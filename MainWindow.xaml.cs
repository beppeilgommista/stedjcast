using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using Stedjcast.Models;
using Stedjcast.Services;

namespace Stedjcast;

public partial class MainWindow : Window
{
    private const int ReconnectBaseSeconds = 5;
    private const int MicrophoneHotKeyId = 0x5301;
    private const int WmHotKey = 0x0312;
    private const uint ModAlt = 0x0001;
    private const uint ModWin = 0x0008;
    private const uint VkK = 0x4B;

    private readonly AudioEngine _audio = new();
    private readonly ShoutcastStreamer _streamer = new();
    private readonly Dictionary<string, ChannelStrip> _strips;
    private readonly Dictionary<(string Channel, int Slot), Window> _parameterWindows = new();
    private readonly DispatcherTimer _meterTimer;
    private AppSettings _settings = new();
    private bool _captureRunning;
    private bool _running;
    private HwndSource? _windowSource;
    private CancellationTokenSource? _reconnectCts;

    private enum StatusKind { Idle, Ready, Live, Warning, Error }

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;

        _strips = new() { ["mic"] = MicStrip, ["loopback"] = LoopbackStrip, ["master"] = MasterStrip };
        foreach (var (key, strip) in _strips)
        {
            strip.SlotClicked += (slot, anchor) => OnSlotClicked(key, slot, anchor);
            strip.BypassClicked += slot => ToggleBypass(key, slot);
            strip.RemoveClicked += slot => RemovePlugin(key, slot);
            strip.MuteClicked += () => SetMuted(key, !_settings.Channel(key).Muted);
            strip.GainChanged += gainDb =>
            {
                _settings.Channel(key).GainDb = gainDb;
                _audio.Channel(key).Volume = (float)Math.Pow(10, gainDb / 20);
            };
        }

        _audio.MixedSamplesAvailable += _streamer.WriteSamples;
        _audio.SystemMicMuteChanged += muted => Dispatcher.BeginInvoke(() =>
        {
            if (_settings.Microphone.Muted != muted)
                SetMuted("mic", muted, syncSystemMute: false);
        });
        _streamer.Disconnected += OnStreamDisconnected;

        // The meters poll the engine's peaks at ~30 fps instead of being pushed from the
        // audio threads on every packet (hundreds of UI dispatches per second).
        _meterTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(33), DispatcherPriority.Render, (_, _) => UpdateMeters(), Dispatcher);

        LoadDevices();
        LoadSettings();
        StartAudioCapture();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _windowSource = HwndSource.FromHwnd(handle);
        _windowSource?.AddHook(WindowMessageHook);

        if (!RegisterHotKey(handle, MicrophoneHotKeyId, ModWin | ModAlt, VkK))
            LoggingService.Write("Unable to register the global Win+Alt+K shortcut.");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmHotKey && wParam.ToInt32() == MicrophoneHotKeyId)
        {
            SetMuted("mic", !_settings.Microphone.Muted);
            handled = true;
        }

        return IntPtr.Zero;
    }

    // ---------------------------------------------------------------- audio

    private void StartAudioCapture()
    {
        try
        {
            _audio.Start(_settings.MicrophoneDeviceId, _settings.LoopbackDeviceId);
            LoadSavedPlugins();

            _captureRunning = true;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SetStatus(StatusKind.Ready, "READY", "Audio is running, press GO LIVE to broadcast");
            _meterTimer.Start();

            if (_settings.AutoStartStream && !_settings.TestMode)
                Start_Click(this, new RoutedEventArgs());
        }
        catch (Exception exception)
        {
            _captureRunning = false;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = false;
            ShowAudioError(exception);
        }
    }

    private void UpdateMeters()
    {
        foreach (var (key, strip) in _strips)
        {
            var channel = _audio.Channel(key);
            var peak = channel.TakePeak();
            strip.SetLevel(channel.Muted ? 0 : peak);
        }
    }

    private void SetMuted(string key, bool muted, bool syncSystemMute = true)
    {
        _settings.Channel(key).Muted = muted;
        _audio.Channel(key).Muted = muted;
        _strips[key].SetMuted(muted);

        if (key == "mic" && syncSystemMute)
            _audio.SetSystemMicMuted(muted);
    }

    private void ShowAudioError(Exception exception)
    {
        LogError("Audio capture", exception);
        SetStatus(StatusKind.Error, "AUDIO ERROR", exception.Message);

        try
        {
            Clipboard.SetText(exception.ToString());
        }
        catch
        {
            // The clipboard may be temporarily locked by another app.
        }

        var openPrivacySettings = MessageBox.Show(
            "Unable to start audio capture. The technical details were copied to the clipboard.\n\n" +
            "Check that the microphone is connected and that Windows allows apps to use it.\n" +
            "Open the Windows microphone privacy settings?",
            "Audio error",
            MessageBoxButton.YesNo,
            MessageBoxImage.Error);

        if (openPrivacySettings == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo("ms-settings:privacy-microphone") { UseShellExecute = true });
    }

    // ---------------------------------------------------------------- VST3 slots

    private void LoadSavedPlugins()
    {
        foreach (var (key, strip) in _strips)
        {
            for (var slot = 0; slot < Vst3Chain.SlotCount; slot++)
            {
                var saved = _settings.Channel(key).Plugins[slot];
                var effect = _audio.Channel(key).Plugins.Slot(slot);
                try
                {
                    effect.Load(saved.Path, AudioEngine.SampleRate);
                    effect.Bypassed = saved.Bypassed;
                }
                catch (Exception exception)
                {
                    // A saved plugin that no longer loads (uninstalled, moved) only empties its slot.
                    LoggingService.Write($"VST3 {key} slot {slot + 1} not loaded ({saved.Path}): {exception.Message}");
                    saved.Path = "";
                    saved.Bypassed = false;
                }

                strip.SetSlot(slot, effect);
            }
        }
    }

    private void OnSlotClicked(string key, int slot, Button anchor)
    {
        if (_audio.Channel(key).Plugins.Slot(slot).IsLoaded)
        {
            OpenParameterWindow(key, slot);
            return;
        }

        if (!_captureRunning)
        {
            MessageBox.Show("Start audio capture before loading a plugin.", "VST3 plugin",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        ShowPluginPicker(key, slot, anchor);
    }

    // Plugins found in the configured folder, plus an entry to browse for a plugin
    // outside that folder.
    private void ShowPluginPicker(string key, int slot, Button anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor };
        var plugins = Vst3Catalog.Scan(_settings.Vst3PluginsFolder);

        if (plugins.Count == 0)
            menu.Items.Add(new MenuItem { Header = "No plugins found (check the folder in Settings)", IsEnabled = false });

        foreach (var plugin in plugins)
        {
            var item = new MenuItem { Header = plugin.DisplayName };
            item.Click += (_, _) => LoadPlugin(key, slot, plugin.Path);
            menu.Items.Add(item);
        }

        menu.Items.Add(new Separator());
        var browseItem = new MenuItem { Header = "Browse..." };
        browseItem.Click += (_, _) => BrowsePlugin(key, slot);
        menu.Items.Add(browseItem);

        menu.IsOpen = true;
    }

    private void BrowsePlugin(string key, int slot)
    {
        // A file picker, not a folder picker: on Windows a VST3 plugin is almost always a
        // single .vst3 file. Vst3Effect.Load rejects anything that isn't a VST3 module.
        var dialog = new OpenFileDialog
        {
            Title = "Select the VST3 plugin",
            Filter = "VST3 plugins (*.vst3;*.dll)|*.vst3;*.dll|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(_settings.Vst3PluginsFolder) ? _settings.Vst3PluginsFolder : ""
        };

        if (dialog.ShowDialog() == true)
            LoadPlugin(key, slot, dialog.FileName);
    }

    private void LoadPlugin(string key, int slot, string path)
    {
        var effect = _audio.Channel(key).Plugins.Slot(slot);
        var saved = _settings.Channel(key).Plugins[slot];
        try
        {
            CloseParameterWindow(key, slot);
            effect.Load(path, AudioEngine.SampleRate);
            saved.Path = path;
            saved.Bypassed = false;
            SettingsService.Save(_settings);
            OpenParameterWindow(key, slot);
        }
        catch (Exception exception)
        {
            LogError($"VST3 plugin {key} slot {slot + 1}", exception);
            MessageBox.Show(exception.Message, "VST3 plugin error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _strips[key].SetSlot(slot, effect);
        }
    }

    private void ToggleBypass(string key, int slot)
    {
        var effect = _audio.Channel(key).Plugins.Slot(slot);
        effect.Bypassed = !effect.Bypassed;
        _settings.Channel(key).Plugins[slot].Bypassed = effect.Bypassed;
        _strips[key].SetSlot(slot, effect);
        SettingsService.Save(_settings);
    }

    private void RemovePlugin(string key, int slot)
    {
        var effect = _audio.Channel(key).Plugins.Slot(slot);
        CloseParameterWindow(key, slot);
        effect.Unload();
        _settings.Channel(key).Plugins[slot] = new PluginSlotSettings();
        _strips[key].SetSlot(slot, effect);
        SettingsService.Save(_settings);
    }

    // Generic parameter panel: the plugins' native GUI can't be used (see Vst3Effect.GetParameters).
    private void OpenParameterWindow(string key, int slot)
    {
        if (_parameterWindows.TryGetValue((key, slot), out var existing))
        {
            existing.Activate();
            return;
        }

        var effect = _audio.Channel(key).Plugins.Slot(slot);
        var parameters = effect.GetParameters();

        var panel = new StackPanel { Margin = new Thickness(18) };
        if (parameters.Count == 0)
            panel.Children.Add(new TextBlock { Text = "This plugin exposes no controllable parameters.", FontSize = 15, TextWrapping = TextWrapping.Wrap });

        foreach (var parameter in parameters)
            panel.Children.Add(BuildParameterRow(effect, parameter));

        var window = new Window
        {
            Title = $"Stedjcast - {_strips[key].Title} #{slot + 1} - {effect.DisplayName}",
            Width = 520,
            Height = 650,
            MinWidth = 380,
            MinHeight = 240,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily,
            Background = App.Brush("#FFFFFF"),
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }
        };

        window.Closed += (_, _) => _parameterWindows.Remove((key, slot));
        _parameterWindows[(key, slot)] = window;
        window.Show();
    }

    private void CloseParameterWindow(string key, int slot)
    {
        if (_parameterWindows.TryGetValue((key, slot), out var window))
            window.Close();
    }

    private static FrameworkElement BuildParameterRow(Vst3Effect effect, Vst3ParameterInfo parameter)
    {
        var label = string.IsNullOrEmpty(parameter.Units) ? parameter.Title : $"{parameter.Title} ({parameter.Units})";
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        row.Children.Add(new TextBlock { Text = label, FontSize = 14, Margin = new Thickness(0, 0, 0, 3) });

        var line = new DockPanel();
        var valueText = new TextBlock { Width = 64, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
        DockPanel.SetDock(valueText, Dock.Right);
        line.Children.Add(valueText);

        if (parameter.StepCount == 1)
        {
            var isOn = parameter.NormalizedValue >= 0.5;
            valueText.Text = isOn ? "on" : "off";
            var check = new CheckBox { IsChecked = isOn, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0) };
            AutomationProperties.SetName(check, label);
            check.Checked += (_, _) => { effect.SetParameterNormalized(parameter.Id, 1); valueText.Text = "on"; };
            check.Unchecked += (_, _) => { effect.SetParameterNormalized(parameter.Id, 0); valueText.Text = "off"; };
            line.Children.Add(check);
        }
        else
        {
            valueText.Text = parameter.NormalizedValue.ToString("0.00");
            var slider = new Slider { Minimum = 0, Maximum = 1, Value = parameter.NormalizedValue, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(slider, label);
            slider.ValueChanged += (_, e) =>
            {
                effect.SetParameterNormalized(parameter.Id, e.NewValue);
                valueText.Text = e.NewValue.ToString("0.00");
            };
            line.Children.Add(slider);
        }

        row.Children.Add(line);
        return row;
    }

    // ---------------------------------------------------------------- settings

    private void LoadDevices()
    {
        try
        {
            MicrophoneCombo.ItemsSource = AudioEngine.GetCaptureDevices();
            LoopbackCombo.ItemsSource = AudioEngine.GetRenderDevices();
        }
        catch (Exception exception)
        {
            ShowAudioError(exception);
        }
    }

    private void LoadSettings()
    {
        _settings = SettingsService.Load();
        LoggingService.Enabled = _settings.LoggingEnabled;

        MicrophoneCombo.SelectedValue = _settings.MicrophoneDeviceId;
        LoopbackCombo.SelectedValue = _settings.LoopbackDeviceId;
        if (MicrophoneCombo.SelectedIndex < 0 && MicrophoneCombo.Items.Count > 0)
            MicrophoneCombo.SelectedIndex = 0;
        if (LoopbackCombo.SelectedIndex < 0 && LoopbackCombo.Items.Count > 0)
            LoopbackCombo.SelectedIndex = 0;
        _settings.MicrophoneDeviceId = MicrophoneCombo.SelectedValue as string ?? "";
        _settings.LoopbackDeviceId = LoopbackCombo.SelectedValue as string ?? "";

        Vst3FolderBox.Text = _settings.Vst3PluginsFolder;
        ServerTypeCombo.SelectedIndex = _settings.ServerType == "Shoutcast v1" ? 0 : 1;
        ServerHostBox.Text = _settings.ServerHost;
        ServerPortBox.Text = _settings.ServerPort.ToString();
        PasswordBox.Password = _settings.SourcePassword;
        StreamIdBox.Text = _settings.StreamId.ToString();
        BitrateCombo.SelectedIndex = _settings.BitrateKbps switch { 96 => 0, 192 => 2, 256 => 3, _ => 1 };
        PublicCheck.IsChecked = _settings.PublicServer;
        StreamNameBox.Text = _settings.StreamName;
        DescriptionBox.Text = _settings.Description;
        UrlBox.Text = _settings.Url;
        GenreBox.Text = _settings.Genre;
        AutoStartCheck.IsChecked = _settings.AutoStartStream;
        TestModeCheck.IsChecked = _settings.TestMode;
        LoggingCheck.IsChecked = _settings.LoggingEnabled;

        foreach (var (key, strip) in _strips)
        {
            var channel = _settings.Channel(key);
            strip.GainDb = channel.GainDb;
            SetMuted(key, channel.Muted, syncSystemMute: false);
        }
    }

    /// <summary>Copies the SETTINGS tab into <see cref="_settings"/>; false if a field is invalid.</summary>
    private bool ReadSettingsForm(bool showErrors)
    {
        string? error = null;
        if (MicrophoneCombo.SelectedValue is not string microphoneId || LoopbackCombo.SelectedValue is not string loopbackId)
            error = "Select the microphone and the audio output.";
        else if (!int.TryParse(ServerPortBox.Text, out var port) || port is < 1 or > 65535)
            error = "Invalid server port.";
        else
        {
            _settings.MicrophoneDeviceId = microphoneId;
            _settings.LoopbackDeviceId = loopbackId;
            _settings.ServerPort = port;
            _settings.StreamId = int.TryParse(StreamIdBox.Text, out var streamId) && streamId > 0 ? streamId : 1;
            _settings.Vst3PluginsFolder = Vst3FolderBox.Text.Trim();
            _settings.ServerType = ServerTypeCombo.SelectedIndex == 0 ? "Shoutcast v1" : "Shoutcast v2";
            _settings.ServerHost = ServerHostBox.Text.Trim();
            _settings.SourcePassword = PasswordBox.Password;
            _settings.BitrateKbps = int.Parse((string)((ComboBoxItem)BitrateCombo.SelectedItem).Content);
            _settings.PublicServer = PublicCheck.IsChecked == true;
            _settings.StreamName = StreamNameBox.Text.Trim();
            _settings.Description = DescriptionBox.Text.Trim();
            _settings.Url = UrlBox.Text.Trim();
            _settings.Genre = GenreBox.Text.Trim();
            _settings.AutoStartStream = AutoStartCheck.IsChecked == true;
            _settings.TestMode = TestModeCheck.IsChecked == true;
            _settings.LoggingEnabled = LoggingCheck.IsChecked == true;
            LoggingService.Enabled = _settings.LoggingEnabled;
            return true;
        }

        if (showErrors)
            MessageBox.Show(error, "Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadSettingsForm(showErrors: true))
            return;

        SettingsService.Save(_settings);
        MessageBox.Show("Settings saved.", "Stedjcast", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Vst3FolderButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Select the VST3 plugin folder",
            InitialDirectory = Directory.Exists(Vst3FolderBox.Text) ? Vst3FolderBox.Text : ""
        };

        if (dialog.ShowDialog() == true)
            Vst3FolderBox.Text = dialog.FolderName;
    }

    // ---------------------------------------------------------------- broadcast

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_running || !_captureRunning || !ReadSettingsForm(showErrors: true))
            return;

        SettingsService.Save(_settings);
        try
        {
            _running = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;

            _audio.SetTestRecording(_settings.TestMode);

            if (_settings.TestMode)
            {
                SetStatus(StatusKind.Live, "LOCAL TEST", $"Recording WAV files to {AudioEngine.TestRecordingFolder}, not broadcasting");
                return;
            }

            _streamer.Start(_settings);
            SetStatus(StatusKind.Live, "ON AIR", "Audio is being sent to the server");
        }
        catch (Exception exception)
        {
            LogError("Go live", exception);
            _streamer.Stop();
            _audio.SetTestRecording(false);
            _running = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            SetStatus(StatusKind.Error, "SERVER NOT CONNECTED", exception.Message);
        }
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        _reconnectCts?.Cancel();
        _streamer.Stop();
        _audio.SetTestRecording(false);
        _running = false;

        StartButton.IsEnabled = _captureRunning;
        StopButton.IsEnabled = false;

        if (_captureRunning)
            SetStatus(StatusKind.Ready, "READY", "Audio is running, press GO LIVE to broadcast");
        else
            SetStatus(StatusKind.Idle, "NOT CONNECTED", "");
    }

    private void OnStreamDisconnected(string reason)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_running)
                return;

            SystemSounds.Exclamation.Play();
            SetStatus(StatusKind.Warning, "BROADCAST INTERRUPTED", $"Reconnecting ({reason})");
            _ = ReconnectLoopAsync();
        });
    }

    private async Task ReconnectLoopAsync()
    {
        _reconnectCts?.Cancel();
        var cts = new CancellationTokenSource();
        _reconnectCts = cts;
        var attempt = 0;

        while (_running && !cts.IsCancellationRequested)
        {
            attempt++;
            var delaySeconds = Math.Min(ReconnectBaseSeconds * attempt, 60);
            SetStatus(StatusKind.Warning, "BROADCAST INTERRUPTED", $"Retrying in {delaySeconds}s (#{attempt})");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (!_running || cts.IsCancellationRequested)
                return;

            try
            {
                _streamer.Start(_settings);
                SetStatus(StatusKind.Live, "ON AIR", "Reconnected to the server");
                return;
            }
            catch (Exception exception)
            {
                LogError("Shoutcast reconnection", exception);
            }
        }
    }

    private void SetStatus(StatusKind kind, string title, string detail)
    {
        StatusText.Text = string.IsNullOrEmpty(detail) ? title : title + Environment.NewLine + detail;
        var (background, foreground) = kind switch
        {
            StatusKind.Ready => ("#DBEAFE", "#1E3A8A"),
            StatusKind.Live => ("#16A34A", "#FFFFFF"),
            StatusKind.Warning => ("#F59E0B", "#1F2933"),
            StatusKind.Error => ("#DC2626", "#FFFFFF"),
            _ => ("#E2E8F0", "#1F2933")
        };
        StatusBanner.Background = App.Brush(background);
        StatusText.Foreground = App.Brush(foreground);
    }

    private static void LogError(string context, Exception exception) =>
        LoggingService.Write($"{context} HResult=0x{exception.HResult:X8}\r\n{exception}\r\n");

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_running &&
            MessageBox.Show("Broadcast in progress. Close anyway?", "Stedjcast",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }

        ReadSettingsForm(showErrors: false);
        SettingsService.Save(_settings);

        _meterTimer.Stop();
        _reconnectCts?.Cancel();
        _audio.Dispose();
        _streamer.Dispose();

        UnregisterHotKey(new WindowInteropHelper(this).Handle, MicrophoneHotKeyId);
        _windowSource?.RemoveHook(WindowMessageHook);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

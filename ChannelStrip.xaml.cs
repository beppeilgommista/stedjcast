using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Stedjcast.Services;

namespace Stedjcast;

public partial class ChannelStrip : UserControl
{
    private static readonly SolidColorBrush MeterGreen = App.Brush("#22C55E");
    private static readonly SolidColorBrush MeterYellow = App.Brush("#EAB308");
    private static readonly SolidColorBrush MeterRed = App.Brush("#EF4444");

    private readonly Button[] _slotButtons = new Button[Vst3Chain.SlotCount];
    private readonly Button[] _bypassButtons = new Button[Vst3Chain.SlotCount];
    private readonly Button[] _removeButtons = new Button[Vst3Chain.SlotCount];
    private string _channelName = "";
    private bool _muted;
    private float _displayedLevel;

    public event Action<int, Button>? SlotClicked;
    public event Action<int>? BypassClicked;
    public event Action<int>? RemoveClicked;
    public event Action? MuteClicked;
    public event Action<double>? GainChanged;

    public ChannelStrip()
    {
        InitializeComponent();

        for (var slot = 0; slot < Vst3Chain.SlotCount; slot++)
        {
            var index = slot;
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            _bypassButtons[slot] = SideButton("B", "Bypass", new Thickness(0, 0, 6, 0), () => BypassClicked?.Invoke(index));
            _removeButtons[slot] = SideButton("✕", "Remove", new Thickness(6, 0, 0, 0), () => RemoveClicked?.Invoke(index));
            DockPanel.SetDock(_bypassButtons[slot], Dock.Left);
            DockPanel.SetDock(_removeButtons[slot], Dock.Right);

            _slotButtons[slot] = new Button { Style = (Style)FindResource("Vst3SlotButtonStyle") };
            _slotButtons[slot].Click += (_, _) => SlotClicked?.Invoke(index, _slotButtons[index]);

            row.Children.Add(_bypassButtons[slot]);
            row.Children.Add(_removeButtons[slot]);
            row.Children.Add(_slotButtons[slot]);
            SlotsPanel.Children.Add(row);
            SetSlot(slot, null);
        }

        UpdateGainText();
        SetMuted(false);
    }

    public string Title
    {
        get => TitleText.Text;
        set => TitleText.Text = value;
    }

    public Brush Accent
    {
        get => Header.Background;
        set => Header.Background = value;
    }

    /// <summary>Channel name read by screen readers ("Microphone", "PC audio", ...).</summary>
    public string ChannelName
    {
        get => _channelName;
        set
        {
            _channelName = value;
            AutomationProperties.SetName(GainBox, $"{value} gain in decibels");
            AutomationProperties.SetName(Fader, $"{value} volume");
            AutomationProperties.SetName(Meter, $"{value} level");
            for (var slot = 0; slot < Vst3Chain.SlotCount; slot++)
            {
                AutomationProperties.SetName(_slotButtons[slot], $"{value} slot {slot + 1}, click to add or open a plugin");
                AutomationProperties.SetName(_bypassButtons[slot], $"{value} slot {slot + 1} bypass");
                AutomationProperties.SetName(_removeButtons[slot], $"{value} slot {slot + 1} remove");
            }
            SetMuted(_muted);
        }
    }

    public double GainDb
    {
        get => Fader.Value;
        set => Fader.Value = Math.Clamp(value, Fader.Minimum, Fader.Maximum);
    }

    // Scale 0..60 = -60..0 dBFS: red above -6 dB, yellow above -18 dB. Peaks rise at once
    // and fall back gradually (~27 dB/s at 30 fps), like a hardware meter, instead of
    // dropping to zero whenever a refresh finds no new audio packet.
    public void SetLevel(float level)
    {
        if (!float.IsFinite(level))
            level = 0;
        _displayedLevel = Math.Max(level, _displayedLevel * 0.9f);

        var value = _displayedLevel > 0 ? Math.Clamp(60 + 20 * Math.Log10(_displayedLevel), 0, 60) : 0;
        Meter.Value = value;
        Meter.Foreground = value > 54 ? MeterRed : value > 42 ? MeterYellow : MeterGreen;
    }

    // State shown by text as well as color, and exposed through the UI Automation name.
    public void SetMuted(bool muted)
    {
        _muted = muted;
        MuteButton.Content = muted ? "🔇  MUTED" : "ON";
        MuteButton.Background = App.Brush(muted ? "#DC2626" : "#DCFCE7");
        MuteButton.Foreground = App.Brush(muted ? "#FFFFFF" : "#166534");
        AutomationProperties.SetName(MuteButton, muted
            ? $"{_channelName} muted, press to unmute"
            : $"{_channelName} on, press to mute");
    }

    public void SetSlot(int slot, Vst3Effect? effect)
    {
        var loaded = effect?.IsLoaded == true;
        var bypassed = loaded && effect!.Bypassed;
        var button = _slotButtons[slot];

        button.Content = loaded ? effect!.DisplayName : "+ VST3";
        button.ToolTip = loaded ? effect!.Path : "Click to add a VST3 plugin";
        button.Background = App.Brush(!loaded ? "#F7F9FB" : bypassed ? "#F3F4F6" : "#DBEAFE");
        button.Foreground = App.Brush(!loaded || bypassed ? "#6B7280" : "#1E3A8A");

        _bypassButtons[slot].IsEnabled = loaded;
        _bypassButtons[slot].Background = App.Brush(bypassed ? "#F59E0B" : "#F7F9FB");
    }

    private Button SideButton(string content, string toolTip, Thickness margin, Action onClick)
    {
        var button = new Button
        {
            Content = content,
            ToolTip = toolTip,
            Margin = margin,
            Style = (Style)FindResource("Vst3SideButtonStyle")
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private void UpdateGainText() => GainBox.Text = $"{Fader.Value:0.0}";

    private void ApplyGainText()
    {
        if (double.TryParse(GainBox.Text, out var value))
            GainDb = value;
        UpdateGainText();
    }

    private void Fader_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainBox is null)
            return;

        UpdateGainText();
        GainChanged?.Invoke(e.NewValue);
    }

    private void Fader_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        GainDb = 0;
        e.Handled = true;
    }

    private void GainBox_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        GainDb = 0;
        e.Handled = true;
    }

    private void GainBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        ApplyGainText();
        e.Handled = true;
    }

    private void GainBox_LostFocus(object sender, RoutedEventArgs e) => ApplyGainText();

    private void MuteButton_Click(object sender, RoutedEventArgs e) => MuteClicked?.Invoke();
}

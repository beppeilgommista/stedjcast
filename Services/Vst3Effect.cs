using System.IO;
using System.Windows;

namespace Stedjcast.Services;

/// <summary>The saved state of a plugin, as the plugin itself serializes it.</summary>
public sealed record Vst3State(byte[] Component, byte[] Controller);

/// <summary>One plugin slot: a VST3 effect with bypass, fed from the audio thread.</summary>
public sealed class Vst3Effect : IDisposable
{
    private const int BlockFrames = 512;

    private readonly object _sync = new();
    private Vst3PluginInstance? _plugin;
    private Vst3EditorWindow? _editor;

    // The input queue accumulates samples until a full block declared to the plugin is
    // available; the output queue holds processed audio not yet returned to the caller.
    // This decouples the WASAPI packet size (variable, ~480-960 frames) from the fixed
    // block size the VST3 plugin expects, instead of padding every call with silence.
    private readonly Queue<float> _pendingInput = new();
    private readonly Queue<float> _pendingOutput = new();

    public bool IsLoaded => _plugin is not null;
    public string Path { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    /// <summary>When true, audio passes through the slot unchanged without calling the plugin.</summary>
    public bool Bypassed { get; set; }

    public void Load(string path, double sampleRate, Vst3State? state = null)
    {
        Unload();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("VST3 plugin not found.", path);

        // A .vst3/.dll file that doesn't export GetPluginFactory is not a VST3 module
        // (typically a legacy VST2 plugin): reject it before loading it into the process.
        var module = Vst3PluginInstance.ResolveModulePath(path);
        if (!PeModuleInspector.ExportsFunction(module, "GetPluginFactory"))
        {
            throw new InvalidOperationException(
                $"The file is not a valid VST3 module (probably a VST2 plugin or another DLL): {path}.");
        }

        var plugin = new Vst3PluginInstance(path, sampleRate, BlockFrames, state?.Component, state?.Controller);
        lock (_sync)
        {
            _plugin = plugin;
            _pendingInput.Clear();
            _pendingOutput.Clear();
            Path = path;
            DisplayName = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        }
    }

    public unsafe void Process(Span<float> stereoSamples)
    {
        if (Bypassed || stereoSamples.Length == 0)
            return;

        // Everything under a single lock: if Unload() (slot removed from the UI) ran
        // between reading _plugin and processing, the audio thread would use an already
        // destroyed native plugin and crash the process.
        lock (_sync)
        {
            var plugin = _plugin;
            if (plugin is null)
                return;

            foreach (var sample in stereoSamples)
                _pendingInput.Enqueue(sample);

            var stereoIn = plugin.InputChannels > 1;
            var stereoOut = plugin.OutputChannels > 1;
            while (_pendingInput.Count >= BlockFrames * 2)
            {
                float* inLeft = plugin.Input(0), inRight = stereoIn ? plugin.Input(1) : null;
                for (var frame = 0; frame < BlockFrames; frame++)
                {
                    var left = _pendingInput.Dequeue();
                    var right = _pendingInput.Dequeue();
                    if (stereoIn)
                    {
                        inLeft[frame] = left;
                        inRight[frame] = right;
                    }
                    else
                    {
                        inLeft[frame] = 0.5f * (left + right);
                    }
                }

                plugin.Process();

                float* outLeft = plugin.Output(0), outRight = stereoOut ? plugin.Output(1) : outLeft;
                for (var frame = 0; frame < BlockFrames; frame++)
                {
                    _pendingOutput.Enqueue(outLeft[frame]);
                    _pendingOutput.Enqueue(outRight[frame]);
                }
            }

            for (var index = 0; index < stereoSamples.Length; index++)
                stereoSamples[index] = _pendingOutput.Count > 0 ? _pendingOutput.Dequeue() : 0f;
        }
    }

    // Parameter, state and editor calls run on the UI thread, as VST3 requires for the
    // edit controller; the VST3 threading model lets them run while the audio thread processes.

    public IReadOnlyList<Vst3ParameterInfo> GetParameters() => _plugin?.GetParameters() ?? [];

    public void SetParameterNormalized(uint id, double normalizedValue) =>
        _plugin?.SetParameterNormalized(id, normalizedValue);

    public Vst3State? GetState()
    {
        if (_plugin is null)
            return null;
        var (component, controller) = _plugin.GetState();
        return new Vst3State(component, controller);
    }

    /// <summary>Shows the plugin's own GUI; false when the plugin has none.</summary>
    public bool ShowEditor(string title, Window owner, Action? closed = null)
    {
        if (_plugin is null)
            return false;
        if (_editor is not null)
        {
            _editor.Activate();
            return true;
        }

        _editor = Vst3EditorWindow.Open(_plugin, title, owner);
        if (_editor is null)
            return false;
        _editor.Closed += (_, _) =>
        {
            _editor = null;
            closed?.Invoke();
        };
        return true;
    }

    public void Dispose() => Unload();

    public void Unload()
    {
        // The editor must be detached from the plugin before the plugin is destroyed.
        _editor?.Close();
        lock (_sync)
        {
            _plugin?.Dispose();
            _plugin = null;
            _pendingInput.Clear();
            _pendingOutput.Clear();
            Path = "";
            DisplayName = "";
            Bypassed = false;
        }
    }
}

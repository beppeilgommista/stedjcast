using Vst3HostSharp;
using static Vst3HostSharp.StructsAndEnums;
using System.IO;

namespace Stedjcast.Services;

/// <summary>A plugin parameter, for the generic parameter panel.</summary>
public sealed record Vst3ParameterInfo(uint Id, string Title, string Units, int StepCount, double NormalizedValue);

public sealed class Vst3Effect : IDisposable
{
    private readonly object _sync = new();
    private Vst3Plugin? _plugin;
    private int _blockFrames;

    // The input queue accumulates samples until a full block declared to the plugin is
    // available; the output queue holds processed audio not yet returned to the caller.
    // This decouples the WASAPI packet size (variable, ~480-960 frames) from the fixed
    // block size the VST3 plugin expects, instead of padding every call with silence.
    private readonly Queue<float> _pendingInput = new();
    private readonly Queue<float> _pendingOutput = new();

    public bool IsLoaded => _plugin?.AudioProcessor is not null;
    public string Path { get; private set; } = "";
    public string DisplayName { get; private set; } = "";

    /// <summary>When true, audio passes through the slot unchanged without calling the plugin.</summary>
    public bool Bypassed { get; set; }

    public void Load(string path, double sampleRate)
    {
        Unload();
        if (string.IsNullOrWhiteSpace(path))
            return;
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException("VST3 plugin not found.", path);

        // A .vst3/.dll file that doesn't export GetPluginFactory is not a VST3 module
        // (typically a legacy VST2 plugin): loading it into the native bridge crashes the
        // whole process, so reject it here with a managed exception.
        if (File.Exists(path) && !PeModuleInspector.ExportsFunction(path, "GetPluginFactory"))
        {
            throw new InvalidOperationException(
                $"The file is not a valid VST3 module (probably a VST2 plugin or another DLL): {path}.");
        }

        const int requestedBlockFrames = 512;
        var plugin = new Vst3Plugin(path)
        {
            PluginType = VstType.VstEffect
        };
        plugin.InitializePlugin(requestedBlockFrames, sampleRate, IntPtr.Zero);
        if (plugin.AudioProcessor is null)
        {
            plugin.Dispose();
            throw new InvalidOperationException(
                $"Unable to initialize the VST3 plugin: {path}.");
        }

        lock (_sync)
        {
            _plugin = plugin;
            _blockFrames = plugin.AudioProcessor.managedProcessData.NumSamples;
            _pendingInput.Clear();
            _pendingOutput.Clear();
            Path = path;
            DisplayName = System.IO.Path.GetFileNameWithoutExtension(path.TrimEnd('\\', '/'));
        }
    }

    public void Process(Span<float> stereoSamples)
    {
        if (Bypassed || stereoSamples.Length == 0)
            return;

        // Everything under a single lock: if Unload() (slot removed from the UI) ran
        // between reading _plugin and PerformProcessData, the audio thread would use an
        // already destroyed native plugin and crash the process.
        lock (_sync)
        {
            var plugin = _plugin;
            var blockFrames = _blockFrames;
            if (plugin?.AudioProcessor is null || blockFrames <= 0)
                return;

            var processData = plugin.AudioProcessor.managedProcessData;
            if (processData.Inputs.Length == 0 || processData.Outputs.Length == 0)
                return;

            var input = processData.Inputs[0].ChannelBuffers32;
            var output = processData.Outputs[0].ChannelBuffers32;
            if (input is null || output is null || input.Length == 0 || output.Length == 0)
                return;

            foreach (var sample in stereoSamples)
                _pendingInput.Enqueue(sample);

            while (_pendingInput.Count >= blockFrames * 2)
            {
                for (var frame = 0; frame < blockFrames; frame++)
                {
                    var left = _pendingInput.Dequeue();
                    var right = _pendingInput.Dequeue();
                    input[0][frame] = left;
                    if (input.Length > 1)
                        input[1][frame] = right;
                }

                plugin.AudioProcessor.PerformProcessData();

                for (var frame = 0; frame < blockFrames; frame++)
                {
                    _pendingOutput.Enqueue(output[0][frame]);
                    _pendingOutput.Enqueue(output.Length > 1 ? output[1][frame] : output[0][frame]);
                }
            }

            for (var index = 0; index < stereoSamples.Length; index++)
                stereoSamples[index] = _pendingOutput.Count > 0 ? _pendingOutput.Dequeue() : 0f;
        }
    }

    public void Dispose() => Unload();

    // The plugin's native GUI (Vst3Plugin.ShowEditor -> CreateAndShowEditor) crashes the
    // process deterministically with this hosting library, so it is never used. Plugins
    // are edited through a generic parameter panel instead, which only makes managed
    // calls and never creates native windows.
    public IReadOnlyList<Vst3ParameterInfo> GetParameters()
    {
        lock (_sync)
        {
            if (_plugin is null)
                return Array.Empty<Vst3ParameterInfo>();

            var count = _plugin.GetParameterCount();
            var result = new List<Vst3ParameterInfo>(count);
            for (var index = 0; index < count; index++)
            {
                var info = _plugin.GetParameterInfo(index);
                result.Add(new Vst3ParameterInfo(
                    info.Id,
                    info.Title,
                    info.Units,
                    info.StepCount,
                    _plugin.GetParameterNormalized(info.Id)));
            }

            return result;
        }
    }

    public void SetParameterNormalized(uint id, double normalizedValue)
    {
        lock (_sync)
            _plugin?.SetParameterNormalized(id, normalizedValue);
    }

    public void Unload()
    {
        lock (_sync)
        {
            _plugin?.Dispose();
            _plugin = null;
            _blockFrames = 0;
            _pendingInput.Clear();
            _pendingOutput.Clear();
            Path = "";
            DisplayName = "";
            Bypassed = false;
        }
    }
}

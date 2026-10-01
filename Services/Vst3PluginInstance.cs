using System.Collections.Concurrent;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace Stedjcast.Services;

/// <summary>A parameter of a loaded plugin, for the generic parameter panel.</summary>
public sealed record Vst3ParameterInfo(uint Id, string Title, string Units, int StepCount, double NormalizedValue);

/// <summary>
/// One VST3 effect instance hosted in-process: component + audio processor + edit
/// controller, wired together the way the VST3 SDK host classes do it. Construction,
/// editor and state calls belong to the UI thread; Process() to the audio thread.
/// </summary>
public sealed unsafe class Vst3PluginInstance : IDisposable
{
    private const int MediaAudio = 0, DirInput = 0, DirOutput = 1;
    private const ulong SpeakerStereo = 0b11;

    // Modules are never unloaded: several plugins crash when their DLL is freed and
    // reloaded within the same process, as most hosts have learned the hard way.
    private static readonly Dictionary<string, ComPtr> Factories = new(StringComparer.OrdinalIgnoreCase);

    private readonly HostApplication _host = new();
    private readonly ComponentHandler _handler;
    private readonly ParameterChanges _parameterChanges = new();
    private readonly ConcurrentQueue<(uint Id, double Value)> _pendingEdits = new();
    private ComPtr _component, _processor, _controller, _componentConnection, _controllerConnection;
    private bool _separateController;

    private ProcessData* _data;
    private AudioBusBuffers* _inputs, _outputs;
    private int _inputBusCount, _outputBusCount;
    private readonly List<IntPtr> _allocations = [];

    private ComPtr _view;
    private PlugFrame? _frame;

    public int BlockFrames { get; }
    public int InputChannels { get; private set; }
    public int OutputChannels { get; private set; }
    public float* Input(int channel) => _inputs[0].ChannelBuffers32[channel];
    public float* Output(int channel) => _outputs[0].ChannelBuffers32[channel];
    public bool HasEditor { get; private set; }

    public Vst3PluginInstance(string path, double sampleRate, int blockFrames, byte[]? componentState = null, byte[]? controllerState = null)
    {
        BlockFrames = blockFrames;
        _handler = new ComponentHandler((id, value) => _pendingEdits.Enqueue((id, value)));
        try
        {
            Create(path);
            if (componentState is { Length: > 0 })
                RestoreState(componentState, controllerState);
            SetupProcessing(sampleRate);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private void Create(string path)
    {
        var factory = GetFactory(path);
        var classInfo = FindAudioEffectClass(factory);

        _component = CreateInstance(factory, classInfo.Cid, Iid.IComponent);
        if (_component.IsNull)
            throw new InvalidOperationException("The plugin did not create its audio component.");
        Check(Call(_component, 3, _host.Pointer), "initialize the component");

        _processor = _component.Query(Iid.IAudioProcessor);
        if (_processor.IsNull)
            throw new InvalidOperationException("The plugin has no audio processor.");

        // Single-component plugins implement the controller on the same object.
        _controller = _component.Query(Iid.IEditController);
        if (_controller.IsNull)
        {
            Guid controllerId;
            if (((delegate* unmanaged<IntPtr, Guid*, int>)_component.Slot(5))(_component.Pointer, &controllerId) == Result.Ok)
            {
                _controller = CreateInstance(factory, controllerId, Iid.IEditController);
                if (!_controller.IsNull)
                {
                    _separateController = true;
                    Check(Call(_controller, 3, _host.Pointer), "initialize the controller");
                }
            }
        }

        if (_controller.IsNull)
            return;

        Call(_controller, 16, _handler.Pointer);  // setComponentHandler

        if (_separateController)
        {
            _componentConnection = _component.Query(Iid.IConnectionPoint);
            _controllerConnection = _controller.Query(Iid.IConnectionPoint);
            if (!_componentConnection.IsNull && !_controllerConnection.IsNull)
            {
                Call(_componentConnection, 3, _controllerConnection.Pointer);
                Call(_controllerConnection, 3, _componentConnection.Pointer);
            }
        }

        SyncControllerWithComponent();
    }

    private void SyncControllerWithComponent()
    {
        if (_controller.IsNull || !_separateController)
            return;
        using var stream = new BStream();
        if (Call(_component, 13, stream.Pointer) != Result.Ok)
            return;
        using var copy = new BStream(stream.ToArray());
        Call(_controller, 5, copy.Pointer);  // setComponentState
    }

    private void SetupProcessing(double sampleRate)
    {
        _inputBusCount = ((delegate* unmanaged<IntPtr, int, int, int>)_component.Slot(7))(_component.Pointer, MediaAudio, DirInput);
        _outputBusCount = ((delegate* unmanaged<IntPtr, int, int, int>)_component.Slot(7))(_component.Pointer, MediaAudio, DirOutput);
        if (_inputBusCount == 0 || _outputBusCount == 0)
            throw new InvalidOperationException("The plugin is not an audio effect (no audio input or output).");

        // Ask for stereo on the main buses; keep what the plugin proposes for the others.
        var inArrangements = stackalloc ulong[_inputBusCount];
        var outArrangements = stackalloc ulong[_outputBusCount];
        for (var i = 0; i < _inputBusCount; i++)
            inArrangements[i] = i == 0 ? SpeakerStereo : GetArrangement(DirInput, i);
        for (var i = 0; i < _outputBusCount; i++)
            outArrangements[i] = i == 0 ? SpeakerStereo : GetArrangement(DirOutput, i);
        ((delegate* unmanaged<IntPtr, ulong*, int, ulong*, int, int>)_processor.Slot(3))(
            _processor.Pointer, inArrangements, _inputBusCount, outArrangements, _outputBusCount);

        _inputs = AllocateBuses(DirInput, _inputBusCount);
        _outputs = AllocateBuses(DirOutput, _outputBusCount);
        InputChannels = _inputs[0].NumChannels;
        OutputChannels = _outputs[0].NumChannels;
        if (InputChannels == 0 || OutputChannels == 0)
            throw new InvalidOperationException("The plugin's main audio bus has no channels.");

        ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)_component.Slot(10))(_component.Pointer, MediaAudio, DirInput, 0, 1);
        ((delegate* unmanaged<IntPtr, int, int, int, byte, int>)_component.Slot(10))(_component.Pointer, MediaAudio, DirOutput, 0, 1);

        var setup = new ProcessSetup { ProcessMode = 0, SymbolicSampleSize = 0, MaxSamplesPerBlock = BlockFrames, SampleRate = sampleRate };
        Check(((delegate* unmanaged<IntPtr, ProcessSetup*, int>)_processor.Slot(7))(_processor.Pointer, &setup), "set up processing");

        _data = (ProcessData*)Allocate(sizeof(ProcessData));
        *_data = new ProcessData
        {
            NumSamples = BlockFrames,
            NumInputs = _inputBusCount,
            NumOutputs = _outputBusCount,
            Inputs = _inputs,
            Outputs = _outputs,
            InputParameterChanges = _parameterChanges.Pointer
        };

        Check(((delegate* unmanaged<IntPtr, byte, int>)_component.Slot(11))(_component.Pointer, 1), "activate the plugin");
        ((delegate* unmanaged<IntPtr, byte, int>)_processor.Slot(8))(_processor.Pointer, 1);  // setProcessing
    }

    private ulong GetArrangement(int direction, int index)
    {
        ulong arrangement = 0;
        ((delegate* unmanaged<IntPtr, int, int, ulong*, int>)_processor.Slot(4))(_processor.Pointer, direction, index, &arrangement);
        return arrangement;
    }

    private AudioBusBuffers* AllocateBuses(int direction, int count)
    {
        var buses = (AudioBusBuffers*)Allocate(sizeof(AudioBusBuffers) * count);
        for (var bus = 0; bus < count; bus++)
        {
            var channels = BitOperations.PopCount(GetArrangement(direction, bus));
            var pointers = (float**)Allocate(sizeof(IntPtr) * Math.Max(channels, 1));
            for (var channel = 0; channel < channels; channel++)
                pointers[channel] = (float*)Allocate(sizeof(float) * BlockFrames);
            buses[bus] = new AudioBusBuffers { NumChannels = channels, ChannelBuffers32 = pointers };
        }
        return buses;
    }

    private void* Allocate(int bytes)
    {
        var block = NativeMemory.AllocZeroed((nuint)bytes);
        _allocations.Add((IntPtr)block);
        return block;
    }

    /// <summary>Processes one block of BlockFrames frames from Input() into Output().</summary>
    public void Process()
    {
        _parameterChanges.Clear();
        while (_pendingEdits.TryDequeue(out var edit) && _parameterChanges.Set(edit.Id, edit.Value)) { }
        ((delegate* unmanaged<IntPtr, ProcessData*, int>)_processor.Slot(9))(_processor.Pointer, _data);
    }

    // ---------------------------------------------------------------- parameters

    public IReadOnlyList<Vst3ParameterInfo> GetParameters()
    {
        if (_controller.IsNull)
            return [];
        var count = ((delegate* unmanaged<IntPtr, int>)_controller.Slot(8))(_controller.Pointer);
        var result = new List<Vst3ParameterInfo>(count);
        for (var index = 0; index < count; index++)
        {
            ParameterInfo info;
            if (((delegate* unmanaged<IntPtr, int, ParameterInfo*, int>)_controller.Slot(9))(_controller.Pointer, index, &info) != Result.Ok)
                continue;
            const int hidden = 1 << 4, readOnly = 1 << 1;
            if ((info.Flags & (hidden | readOnly)) != 0)
                continue;
            var value = ((delegate* unmanaged<IntPtr, uint, double>)_controller.Slot(14))(_controller.Pointer, info.Id);
            result.Add(new Vst3ParameterInfo(info.Id, new string(info.Title), new string(info.Units), info.StepCount, value));
        }
        return result;
    }

    public void SetParameterNormalized(uint id, double value)
    {
        if (!_controller.IsNull)
            ((delegate* unmanaged<IntPtr, uint, double, int>)_controller.Slot(15))(_controller.Pointer, id, value);
        _pendingEdits.Enqueue((id, value));
    }

    // ---------------------------------------------------------------- state

    public (byte[] Component, byte[] Controller) GetState()
    {
        using var component = new BStream();
        Call(_component, 13, component.Pointer);
        using var controller = new BStream();
        if (_separateController)
            Call(_controller, 7, controller.Pointer);
        return (component.ToArray(), controller.ToArray());
    }

    private void RestoreState(byte[] componentState, byte[]? controllerState)
    {
        using (var stream = new BStream(componentState))
            Call(_component, 12, stream.Pointer);
        if (!_separateController)
            return;
        using (var stream = new BStream(componentState))
            Call(_controller, 5, stream.Pointer);
        if (controllerState is { Length: > 0 })
        {
            using var stream = new BStream(controllerState);
            Call(_controller, 6, stream.Pointer);
        }
    }

    // ---------------------------------------------------------------- editor

    /// <summary>Opens the plugin's own GUI inside <paramref name="parent"/>; returns its size in pixels.</summary>
    public (int Width, int Height)? OpenEditor(IntPtr parent, Func<int, int, bool> resize)
    {
        if (_controller.IsNull || !_view.IsNull)
            return null;
        var view = new ComPtr((IntPtr)((delegate* unmanaged<IntPtr, byte*, IntPtr>)_controller.Slot(17))(_controller.Pointer, Ascii("editor")));
        if (view.IsNull)
            return null;

        var hwnd = Ascii("HWND");
        if (((delegate* unmanaged<IntPtr, byte*, int>)view.Slot(3))(view.Pointer, hwnd) != Result.Ok)
        {
            view.Release();
            return null;
        }

        _frame = new PlugFrame(resize);
        ((delegate* unmanaged<IntPtr, IntPtr, int>)view.Slot(12))(view.Pointer, _frame.Pointer);
        if (((delegate* unmanaged<IntPtr, IntPtr, byte*, int>)view.Slot(4))(view.Pointer, parent, hwnd) != Result.Ok)
        {
            ((delegate* unmanaged<IntPtr, IntPtr, int>)view.Slot(12))(view.Pointer, IntPtr.Zero);
            view.Release();
            _frame.Dispose();
            _frame = null;
            return null;
        }

        _view = view;
        ViewRect rect;
        ((delegate* unmanaged<IntPtr, ViewRect*, int>)view.Slot(9))(view.Pointer, &rect);
        return (rect.Width, rect.Height);
    }

    public void CloseEditor()
    {
        if (_view.IsNull)
            return;
        ((delegate* unmanaged<IntPtr, int>)_view.Slot(5))(_view.Pointer);                   // removed
        ((delegate* unmanaged<IntPtr, IntPtr, int>)_view.Slot(12))(_view.Pointer, IntPtr.Zero); // setFrame(null)
        _view.Release();
        _view = default;
        _frame?.Dispose();
        _frame = null;
    }

    // ---------------------------------------------------------------- lifetime

    public void Dispose()
    {
        CloseEditor();
        if (!_processor.IsNull && _data != null)
        {
            ((delegate* unmanaged<IntPtr, byte, int>)_processor.Slot(8))(_processor.Pointer, 0);
            ((delegate* unmanaged<IntPtr, byte, int>)_component.Slot(11))(_component.Pointer, 0);
        }
        if (!_componentConnection.IsNull && !_controllerConnection.IsNull)
        {
            Call(_componentConnection, 4, _controllerConnection.Pointer);
            Call(_controllerConnection, 4, _componentConnection.Pointer);
        }
        _componentConnection.Release();
        _controllerConnection.Release();

        if (!_controller.IsNull)
        {
            Call(_controller, 16, IntPtr.Zero);
            if (_separateController)
                Call(_controller, 4);  // terminate
            _controller.Release();
        }
        _processor.Release();
        if (!_component.IsNull)
        {
            Call(_component, 4);
            _component.Release();
        }
        _component = _processor = _controller = _componentConnection = _controllerConnection = default;

        foreach (var block in _allocations)
            NativeMemory.Free((void*)block);
        _allocations.Clear();
        _data = null;

        _parameterChanges.Dispose();
        _handler.Dispose();
        _host.Dispose();
    }

    // ---------------------------------------------------------------- helpers

    private static int Call(ComPtr target, int slot) =>
        ((delegate* unmanaged<IntPtr, int>)target.Slot(slot))(target.Pointer);

    private static int Call(ComPtr target, int slot, IntPtr argument) =>
        ((delegate* unmanaged<IntPtr, IntPtr, int>)target.Slot(slot))(target.Pointer, argument);

    private static void Check(int result, string action)
    {
        if (result != Result.Ok)
            throw new InvalidOperationException($"The plugin failed to {action} (result 0x{result:X8}).");
    }

    // Interned for the process lifetime: plugins may keep FIDString pointers.
    private static readonly ConcurrentDictionary<string, IntPtr> AsciiStrings = new();
    private static byte* Ascii(string text) =>
        (byte*)AsciiStrings.GetOrAdd(text, t => Marshal.StringToHGlobalAnsi(t));

    private static ComPtr CreateInstance(ComPtr factory, Guid classId, Guid iid)
    {
        IntPtr instance;
        var result = ((delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)factory.Slot(6))(factory.Pointer, &classId, &iid, &instance);
        return result == Result.Ok ? new ComPtr(instance) : default;
    }

    private static PClassInfo FindAudioEffectClass(ComPtr factory)
    {
        var count = ((delegate* unmanaged<IntPtr, int>)factory.Slot(4))(factory.Pointer);
        for (var index = 0; index < count; index++)
        {
            PClassInfo info;
            if (((delegate* unmanaged<IntPtr, int, PClassInfo*, int>)factory.Slot(5))(factory.Pointer, index, &info) != Result.Ok)
                continue;
            var category = Encoding.ASCII.GetString(new ReadOnlySpan<byte>(info.Category, 32)).TrimEnd('\0');
            if (category == "Audio Module Class")
                return info;
        }
        throw new InvalidOperationException("The module contains no VST3 audio processor.");
    }

    /// <summary>The DLL inside a .vst3 bundle folder, or the file itself.</summary>
    public static string ResolveModulePath(string path)
    {
        if (!Directory.Exists(path))
            return path;
        var binary = System.IO.Path.Combine(path, "Contents", "x86_64-win",
            System.IO.Path.GetFileName(path.TrimEnd('\\', '/')));
        return File.Exists(binary) ? binary : throw new FileNotFoundException("No 64-bit Windows binary in the VST3 bundle.", path);
    }

    private static ComPtr GetFactory(string path)
    {
        var module = ResolveModulePath(path);
        lock (Factories)
        {
            if (Factories.TryGetValue(module, out var cached))
                return cached;

            var library = NativeLibrary.Load(module);
            if (NativeLibrary.TryGetExport(library, "InitDll", out var initDll))
                ((delegate* unmanaged<byte>)initDll)();
            var getFactory = NativeLibrary.GetExport(library, "GetPluginFactory");
            var factory = new ComPtr(((delegate* unmanaged<IntPtr>)getFactory)());
            if (factory.IsNull)
                throw new InvalidOperationException("The plugin returned no factory.");
            Factories[module] = factory;
            return factory;
        }
    }
}

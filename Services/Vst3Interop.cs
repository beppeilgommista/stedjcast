using System.Runtime.InteropServices;

namespace Stedjcast.Services;

// Minimal VST3 hosting interop in plain C#. On Windows VST3 interfaces are COM-compatible
// (FUnknown has the IUnknown layout and interface IDs are GUIDs), so plugin objects are
// called through their vtables with function pointers, and the host-side objects the plugin
// calls back are native blocks whose vtables point to [UnmanagedCallersOnly] methods.
// Interface IDs, vtable order and struct layouts follow the Steinberg VST3 interfaces
// (pluginterfaces, MIT license): https://github.com/steinbergmedia/vst3_pluginterfaces

internal static class Result
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NoInterface = unchecked((int)0x80004002);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArgument = unchecked((int)0x80070057);
}

internal static class Iid
{
    // DECLARE_CLASS_IID (Name, l1, l2, l3, l4) -> COM GUID byte layout on Windows.
    private static Guid From(uint l1, uint l2, uint l3, uint l4) =>
        new(l1, (ushort)(l2 >> 16), (ushort)l2,
            (byte)(l3 >> 24), (byte)(l3 >> 16), (byte)(l3 >> 8), (byte)l3,
            (byte)(l4 >> 24), (byte)(l4 >> 16), (byte)(l4 >> 8), (byte)l4);

    public static readonly Guid FUnknown = From(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly Guid IPluginFactory = From(0x7A4D811C, 0x52114A1F, 0xAED9D2EE, 0x0B43BF9F);
    public static readonly Guid IComponent = From(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly Guid IAudioProcessor = From(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly Guid IEditController = From(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly Guid IConnectionPoint = From(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    public static readonly Guid IPlugView = From(0x5BC32507, 0xD06049EA, 0xA6151B52, 0x2B755B29);
    public static readonly Guid IPlugFrame = From(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly Guid IComponentHandler = From(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly Guid IBStream = From(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly Guid IHostApplication = From(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly Guid IParameterChanges = From(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D);
    public static readonly Guid IParamValueQueue = From(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA);
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PClassInfo
{
    public Guid Cid;
    public int Cardinality;
    public fixed byte Category[32];
    public fixed byte Name[64];
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct BusInfo
{
    public int MediaType;
    public int Direction;
    public int ChannelCount;
    public fixed char Name[128];
    public int BusType;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ParameterInfo
{
    public uint Id;
    public fixed char Title[128];
    public fixed char ShortTitle[128];
    public fixed char Units[128];
    public int StepCount;
    public double DefaultNormalizedValue;
    public int UnitId;
    public int Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessSetup
{
    public int ProcessMode;          // 0 = realtime
    public int SymbolicSampleSize;   // 0 = 32-bit float
    public int MaxSamplesPerBlock;
    public double SampleRate;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct AudioBusBuffers
{
    public int NumChannels;
    public ulong SilenceFlags;
    public float** ChannelBuffers32;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct ProcessData
{
    public int ProcessMode;
    public int SymbolicSampleSize;
    public int NumSamples;
    public int NumInputs;
    public int NumOutputs;
    public AudioBusBuffers* Inputs;
    public AudioBusBuffers* Outputs;
    public IntPtr InputParameterChanges;
    public IntPtr OutputParameterChanges;
    public IntPtr InputEvents;
    public IntPtr OutputEvents;
    public IntPtr ProcessContext;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ViewRect
{
    public int Left, Top, Right, Bottom;
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
}

/// <summary>A reference to a plugin-side VST3 object, called through its vtable.</summary>
internal readonly unsafe struct ComPtr(IntPtr pointer)
{
    public IntPtr Pointer { get; } = pointer;
    public bool IsNull => Pointer == IntPtr.Zero;

    public void* Slot(int index) => (*(void***)Pointer)[index];

    public ComPtr Query(Guid iid)
    {
        if (IsNull)
            return default;
        IntPtr result;
        var hr = ((delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)Slot(0))(Pointer, &iid, &result);
        return hr == Result.Ok ? new ComPtr(result) : default;
    }

    public void Release()
    {
        if (!IsNull)
            ((delegate* unmanaged<IntPtr, uint>)Slot(2))(Pointer);
    }
}

/// <summary>
/// Base for objects implemented by the host and handed to the plugin: a native block
/// { vtable, GCHandle } so the static callbacks find the managed instance. Reference
/// counting is nominal; the owner frees the block after the plugin has been terminated.
/// </summary>
internal abstract unsafe class HostObject : IDisposable
{
    private readonly GCHandle _handle;
    public IntPtr Pointer { get; }

    protected HostObject(void** vtable)
    {
        _handle = GCHandle.Alloc(this);
        Pointer = (IntPtr)NativeMemory.Alloc((nuint)(2 * sizeof(IntPtr)));
        ((void**)Pointer)[0] = vtable;
        ((IntPtr*)Pointer)[1] = GCHandle.ToIntPtr(_handle);
    }

    protected abstract bool Implements(Guid iid);

    public static T From<T>(IntPtr self) where T : HostObject =>
        (T)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    protected static void** CreateVtable(int size)
    {
        var vtable = (void**)NativeMemory.Alloc((nuint)(size * sizeof(IntPtr)));
        vtable[0] = (delegate* unmanaged<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged<IntPtr, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged<IntPtr, uint>)&ReleaseRef;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        var target = From<HostObject>(self);
        if (*iid == Iid.FUnknown || target.Implements(*iid))
        {
            *result = self;
            return Result.Ok;
        }
        *result = IntPtr.Zero;
        return Result.NoInterface;
    }

    [UnmanagedCallersOnly] private static uint AddRef(IntPtr self) => 1;
    [UnmanagedCallersOnly] private static uint ReleaseRef(IntPtr self) => 1;

    public void Dispose()
    {
        NativeMemory.Free((void*)Pointer);
        _handle.Free();
    }
}

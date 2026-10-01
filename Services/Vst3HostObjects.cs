using System.IO;
using System.Runtime.InteropServices;

namespace Stedjcast.Services;

/// <summary>IHostApplication: the context passed to the plugin's initialize().</summary>
internal sealed unsafe class HostApplication : HostObject
{
    private static readonly void** Vtable = Build();

    public HostApplication() : base(Vtable) { }

    protected override bool Implements(Guid iid) => iid == Iid.IHostApplication;

    private static void** Build()
    {
        var vtable = CreateVtable(5);
        vtable[3] = (delegate* unmanaged<IntPtr, char*, int>)&GetName;
        vtable[4] = (delegate* unmanaged<IntPtr, Guid*, Guid*, IntPtr*, int>)&CreateInstance;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int GetName(IntPtr self, char* name)
    {
        const string host = "Stedjcast";
        for (var i = 0; i < host.Length; i++)
            name[i] = host[i];
        name[host.Length] = '\0';
        return Result.Ok;
    }

    // ponytail: no host-created IMessage/IAttributeList; plugins that message between their
    // processor and controller through the host fall back to not sending. Add when a plugin needs it.
    [UnmanagedCallersOnly]
    private static int CreateInstance(IntPtr self, Guid* cid, Guid* iid, IntPtr* result)
    {
        *result = IntPtr.Zero;
        return Result.NoInterface;
    }
}

/// <summary>IBStream over a growable byte buffer, used for plugin state.</summary>
internal sealed unsafe class BStream : HostObject
{
    private static readonly void** Vtable = Build();
    private readonly MemoryStream _stream;

    public BStream(byte[]? data = null) : base(Vtable) =>
        _stream = data is null ? new MemoryStream() : new MemoryStream(data, writable: false);

    public byte[] ToArray() => _stream.ToArray();
    public void Rewind() => _stream.Position = 0;

    protected override bool Implements(Guid iid) => iid == Iid.IBStream;

    private static void** Build()
    {
        var vtable = CreateVtable(7);
        vtable[3] = (delegate* unmanaged<IntPtr, byte*, int, int*, int>)&Read;
        vtable[4] = (delegate* unmanaged<IntPtr, byte*, int, int*, int>)&Write;
        vtable[5] = (delegate* unmanaged<IntPtr, long, int, long*, int>)&Seek;
        vtable[6] = (delegate* unmanaged<IntPtr, long*, int>)&Tell;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int Read(IntPtr self, byte* buffer, int count, int* read)
    {
        var n = From<BStream>(self)._stream.Read(new Span<byte>(buffer, count));
        if (read != null)
            *read = n;
        return Result.Ok;
    }

    [UnmanagedCallersOnly]
    private static int Write(IntPtr self, byte* buffer, int count, int* written)
    {
        var stream = From<BStream>(self)._stream;
        if (!stream.CanWrite)
            return Result.False;
        stream.Write(new ReadOnlySpan<byte>(buffer, count));
        if (written != null)
            *written = count;
        return Result.Ok;
    }

    [UnmanagedCallersOnly]
    private static int Seek(IntPtr self, long position, int mode, long* result)
    {
        var stream = From<BStream>(self)._stream;
        var target = mode switch { 1 => stream.Position + position, 2 => stream.Length + position, _ => position };
        if (target < 0)
            return Result.InvalidArgument;
        stream.Position = target;
        if (result != null)
            *result = target;
        return Result.Ok;
    }

    [UnmanagedCallersOnly]
    private static int Tell(IntPtr self, long* position)
    {
        if (position != null)
            *position = From<BStream>(self)._stream.Position;
        return Result.Ok;
    }
}

/// <summary>IComponentHandler: receives edits made in the plugin's own GUI.</summary>
internal sealed unsafe class ComponentHandler(Action<uint, double> performEdit) : HostObject(Vtable)
{
    private readonly Action<uint, double> _performEdit = performEdit;
    private static readonly void** Vtable = Build();

    protected override bool Implements(Guid iid) => iid == Iid.IComponentHandler;

    private static void** Build()
    {
        var vtable = CreateVtable(7);
        vtable[3] = (delegate* unmanaged<IntPtr, uint, int>)&BeginEdit;
        vtable[4] = (delegate* unmanaged<IntPtr, uint, double, int>)&PerformEdit;
        vtable[5] = (delegate* unmanaged<IntPtr, uint, int>)&EndEdit;
        vtable[6] = (delegate* unmanaged<IntPtr, int, int>)&RestartComponent;
        return vtable;
    }

    [UnmanagedCallersOnly] private static int BeginEdit(IntPtr self, uint id) => Result.Ok;
    [UnmanagedCallersOnly] private static int EndEdit(IntPtr self, uint id) => Result.Ok;

    // ponytail: restart requests (latency/bus changes) are acknowledged but not acted on.
    [UnmanagedCallersOnly] private static int RestartComponent(IntPtr self, int flags) => Result.Ok;

    [UnmanagedCallersOnly]
    private static int PerformEdit(IntPtr self, uint id, double value)
    {
        From<ComponentHandler>(self)._performEdit(id, value);
        return Result.Ok;
    }
}

/// <summary>IPlugFrame: lets the plugin's editor ask the host to resize its window.</summary>
internal sealed unsafe class PlugFrame(Func<int, int, bool> resize) : HostObject(Vtable)
{
    private readonly Func<int, int, bool> _resize = resize;
    private static readonly void** Vtable = Build();

    protected override bool Implements(Guid iid) => iid == Iid.IPlugFrame;

    private static void** Build()
    {
        var vtable = CreateVtable(4);
        vtable[3] = (delegate* unmanaged<IntPtr, IntPtr, ViewRect*, int>)&ResizeView;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int ResizeView(IntPtr self, IntPtr view, ViewRect* size)
    {
        if (size == null || !From<PlugFrame>(self)._resize(size->Width, size->Height))
            return Result.False;
        // The host must confirm the new size to the view.
        var rect = *size;
        ((delegate* unmanaged<IntPtr, ViewRect*, int>)new ComPtr(view).Slot(10))(view, &rect);
        return Result.Ok;
    }
}

/// <summary>
/// IParameterChanges for one process() call: one queue per parameter, one point each
/// (the latest value at sample offset 0). Fixed capacity, no allocation on the audio thread.
/// </summary>
internal sealed unsafe class ParameterChanges : HostObject
{
    private const int Capacity = 64;
    private static readonly void** Vtable = Build();
    private readonly ParamValueQueue[] _queues = new ParamValueQueue[Capacity];
    private int _count;

    public ParameterChanges() : base(Vtable)
    {
        for (var i = 0; i < Capacity; i++)
            _queues[i] = new ParamValueQueue();
    }

    public int Count => _count;
    public void Clear() => _count = 0;

    /// <summary>Sets the value of a parameter for the next block; false if full.</summary>
    public bool Set(uint id, double value)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_queues[i].Id == id)
            {
                _queues[i].Value = value;
                return true;
            }
        }
        if (_count == Capacity)
            return false;
        _queues[_count].Id = id;
        _queues[_count].Value = value;
        _count++;
        return true;
    }

    protected override bool Implements(Guid iid) => iid == Iid.IParameterChanges;

    private static void** Build()
    {
        var vtable = CreateVtable(6);
        vtable[3] = (delegate* unmanaged<IntPtr, int>)&GetParameterCount;
        vtable[4] = (delegate* unmanaged<IntPtr, int, IntPtr>)&GetParameterData;
        vtable[5] = (delegate* unmanaged<IntPtr, uint*, int*, IntPtr>)&AddParameterData;
        return vtable;
    }

    [UnmanagedCallersOnly]
    private static int GetParameterCount(IntPtr self) => From<ParameterChanges>(self)._count;

    [UnmanagedCallersOnly]
    private static IntPtr GetParameterData(IntPtr self, int index)
    {
        var changes = From<ParameterChanges>(self);
        return index >= 0 && index < changes._count ? changes._queues[index].Pointer : IntPtr.Zero;
    }

    // Only used for output parameter changes, which this host does not request.
    [UnmanagedCallersOnly]
    private static IntPtr AddParameterData(IntPtr self, uint* id, int* index)
    {
        *index = -1;
        return IntPtr.Zero;
    }

    public new void Dispose()
    {
        foreach (var queue in _queues)
            queue.Dispose();
        base.Dispose();
    }

    private sealed class ParamValueQueue : HostObject
    {
        private static readonly void** QueueVtable = BuildQueue();
        public uint Id;
        public double Value;

        public ParamValueQueue() : base(QueueVtable) { }

        protected override bool Implements(Guid iid) => iid == Iid.IParamValueQueue;

        private static void** BuildQueue()
        {
            var vtable = CreateVtable(7);
            vtable[3] = (delegate* unmanaged<IntPtr, uint>)&GetParameterId;
            vtable[4] = (delegate* unmanaged<IntPtr, int>)&GetPointCount;
            vtable[5] = (delegate* unmanaged<IntPtr, int, int*, double*, int>)&GetPoint;
            vtable[6] = (delegate* unmanaged<IntPtr, int, double, int*, int>)&AddPoint;
            return vtable;
        }

        [UnmanagedCallersOnly] private static uint GetParameterId(IntPtr self) => From<ParamValueQueue>(self).Id;
        [UnmanagedCallersOnly] private static int GetPointCount(IntPtr self) => 1;

        [UnmanagedCallersOnly]
        private static int GetPoint(IntPtr self, int index, int* sampleOffset, double* value)
        {
            if (index != 0)
                return Result.InvalidArgument;
            *sampleOffset = 0;
            *value = From<ParamValueQueue>(self).Value;
            return Result.Ok;
        }

        [UnmanagedCallersOnly]
        private static int AddPoint(IntPtr self, int sampleOffset, double value, int* index)
        {
            *index = 0;
            From<ParamValueQueue>(self).Value = value;
            return Result.Ok;
        }
    }
}

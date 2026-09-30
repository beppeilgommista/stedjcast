namespace Stedjcast.Services;

/// <summary>Up to 3 VST3 plugins in series on one channel.</summary>
public sealed class Vst3Chain : IDisposable
{
    public const int SlotCount = 3;

    private readonly Vst3Effect[] _slots = { new(), new(), new() };

    public Vst3Effect Slot(int index) => _slots[index];

    public void Process(Span<float> stereoSamples)
    {
        foreach (var slot in _slots)
            slot.Process(stereoSamples);
    }

    public void Dispose()
    {
        foreach (var slot in _slots)
            slot.Dispose();
    }
}

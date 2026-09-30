namespace Stedjcast.Services;

public static class Pcm16
{
    /// <summary>Converts float samples to 16-bit little-endian PCM into a reusable buffer
    /// (grown when too small) and returns the number of bytes written.</summary>
    public static int Encode(ReadOnlySpan<float> samples, ref byte[] buffer)
    {
        var length = samples.Length * sizeof(short);
        if (buffer.Length < length)
            buffer = new byte[length];

        for (var index = 0; index < samples.Length; index++)
        {
            var sample = (short)Math.Clamp(samples[index] * short.MaxValue, short.MinValue, short.MaxValue);
            buffer[index * 2] = (byte)(sample & 0xff);
            buffer[index * 2 + 1] = (byte)(sample >> 8);
        }

        return length;
    }
}

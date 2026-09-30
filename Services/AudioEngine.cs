using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Stedjcast.Services;

/// <summary>One mixer channel: VST3 chain -> fader -> mute, plus the peak level for the meter.</summary>
public sealed class MixerChannel
{
    private float _peak;

    public Vst3Chain Plugins { get; } = new();
    public float Volume { get; set; } = 1f;
    public bool Muted { get; set; }

    /// <summary>Highest level since the previous call (read by the UI meter timer).</summary>
    public float TakePeak() => Interlocked.Exchange(ref _peak, 0f);

    // ponytail: unsynchronized max, a concurrent TakePeak can drop one peak; fine for a meter.
    internal void ReportPeak(float peak)
    {
        if (peak > _peak)
            _peak = peak;
    }
}

public sealed class AudioEngine : IDisposable
{
    public const int SampleRate = 48000;

    // Fader range tops out at +12 dB.
    private const float MaxVolume = 3.981f;

    // WASAPI loopback delivers no data at all while nothing plays on the PC (and a failing
    // device can stop too). A source silent for longer than this is mixed as silence
    // instead of stalling the whole mix (and the broadcast) waiting for it.
    private const long IdleSourceMilliseconds = 100;

    // With both sources running on separate clocks, one queue can slowly grow; past 1 s of
    // backlog the oldest samples are dropped. ponytail: not real clock-drift compensation,
    // enough for two sources; add adaptive resampling if long sessions drift audibly.
    private const int MaxQueueBacklogSamples = SampleRate * 2;

    public static readonly string TestRecordingFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Stedjcast");

    private readonly object _mixLock = new();
    private WasapiRecorder? _microphoneRecorder;
    private WasapiRecorder? _loopbackRecorder;
    private readonly Queue<float> _microphoneQueue = new();
    private readonly Queue<float> _loopbackQueue = new();
    private WaveFileWriter? _microphoneTestWriter;
    private WaveFileWriter? _loopbackTestWriter;
    private WaveFileWriter? _mixTestWriter;
    private SystemMicMuteMonitor? _micMuteMonitor;
    private StreamResampler? _microphoneResampler;
    private StreamResampler? _loopbackResampler;
    private float[] _microphoneScratch = new float[0];
    private float[] _loopbackScratch = new float[0];
    private byte[] _microphonePcmScratch = new byte[0];
    private byte[] _loopbackPcmScratch = new byte[0];
    private byte[] _mixPcmScratch = new byte[0];
    private int _callbackErrorLogged;
    private long _lastMicrophoneData;
    private long _lastLoopbackData;

    public MixerChannel Microphone { get; } = new();
    public MixerChannel Loopback { get; } = new();
    public MixerChannel Master { get; } = new();

    public event Action<float[]>? MixedSamplesAvailable;
    public event Action<bool>? SystemMicMuteChanged;

    public MixerChannel Channel(string key) =>
        key switch
        {
            "mic" => Microphone,
            "loopback" => Loopback,
            "master" => Master,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };

    public static IReadOnlyList<AudioDevice> GetCaptureDevices() => GetDevices(DataFlow.Capture);
    public static IReadOnlyList<AudioDevice> GetRenderDevices() => GetDevices(DataFlow.Render);

    private static IReadOnlyList<AudioDevice> GetDevices(DataFlow flow)
    {
        using var enumerator = new MMDeviceEnumerator();
        var defaultId = enumerator.TryGetDefaultAudioEndpoint(flow, Role.Multimedia, out var defaultDevice)
            ? defaultDevice.ID
            : null;
        return enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active)
            .Select(device => new AudioDevice
            {
                ID = device.ID,
                FriendlyName = device.FriendlyName,
                IsDefault = device.ID == defaultId
            })
            .OrderByDescending(device => device.IsDefault)
            .ThenBy(device => device.FriendlyName)
            .ToList();
    }

    public void SetSystemMicMuted(bool muted)
    {
        if (_micMuteMonitor is not null)
            _micMuteMonitor.IsMuted = muted;
    }

    public void Start(string microphoneId, string renderId)
    {
        Stop();
        using var enumerator = new MMDeviceEnumerator();
        var microphoneDevice = enumerator.GetDevice(microphoneId);
        var renderDevice = enumerator.GetDevice(renderId);

        try
        {
            var microphone = BuildRecorder(microphoneDevice, loopback: false);
            var loopback = BuildRecorder(renderDevice, loopback: true);
            _microphoneRecorder = microphone;
            _loopbackRecorder = loopback;
            microphone.DataAvailable += (buffer, flags, _, _) => OnCaptureData(buffer, flags, microphone.WaveFormat, true);
            loopback.DataAvailable += (buffer, flags, _, _) => OnCaptureData(buffer, flags, loopback.WaveFormat, false);
            microphone.RecordingStopped += (_, e) => LogCaptureStopped("Microphone", e);
            loopback.RecordingStopped += (_, e) => LogCaptureStopped("Loopback", e);

            _micMuteMonitor = new SystemMicMuteMonitor(microphoneId);
            _micMuteMonitor.MuteChanged += muted => SystemMicMuteChanged?.Invoke(muted);
            microphone.StartRecording();
            loopback.StartRecording();
            LogAudio($"Capture formats: microphone '{microphoneDevice.FriendlyName}' {microphone.WaveFormat}; " +
                     $"loopback '{renderDevice.FriendlyName}' {loopback.WaveFormat}.");
        }
        catch (Exception exception)
        {
            Stop();
            throw new InvalidOperationException(
                $"Microphone {microphoneDevice.FriendlyName}: {exception.Message}",
                exception);
        }
    }

    public void SetTestRecording(bool enabled)
    {
        lock (_mixLock)
        {
            _microphoneTestWriter?.Dispose();
            _loopbackTestWriter?.Dispose();
            _mixTestWriter?.Dispose();
            _microphoneTestWriter = null;
            _loopbackTestWriter = null;
            _mixTestWriter = null;

            if (!enabled)
                return;

            Directory.CreateDirectory(TestRecordingFolder);
            var format = new WaveFormat(SampleRate, 16, 2);
            _mixTestWriter = new WaveFileWriter(Path.Combine(TestRecordingFolder, "Stedjcast-test.wav"), format);
            _microphoneTestWriter = new WaveFileWriter(Path.Combine(TestRecordingFolder, "Stedjcast-microphone.wav"), format);
            _loopbackTestWriter = new WaveFileWriter(Path.Combine(TestRecordingFolder, "Stedjcast-loopback.wav"), format);
        }
    }

    public void Stop()
    {
        try { _microphoneRecorder?.StopRecording(); } catch { }
        try { _loopbackRecorder?.StopRecording(); } catch { }
        Microphone.Plugins.Dispose();
        Loopback.Plugins.Dispose();
        Master.Plugins.Dispose();
        _micMuteMonitor?.Dispose();
        _micMuteMonitor = null;
        _microphoneResampler = null;
        _loopbackResampler = null;
        SetTestRecording(false);
        _microphoneRecorder?.Dispose();
        _loopbackRecorder?.Dispose();
        _microphoneRecorder = null;
        _loopbackRecorder = null;
        lock (_mixLock)
        {
            _microphoneQueue.Clear();
            _loopbackQueue.Clear();
        }
        _callbackErrorLogged = 0;
    }

    // Shared mode (the capture format is the device's mix format), polling every 100 ms,
    // capture thread registered with MMCSS "Pro Audio" for real-time scheduling.
    private static WasapiRecorder BuildRecorder(MMDevice device, bool loopback)
    {
        var builder = new WasapiRecorderBuilder()
            .WithDevice(device)
            .WithSharedMode()
            .WithPollingSync()
            .WithBufferLength(100)
            .WithMmcssThreadPriority("Pro Audio");
        return (loopback ? builder.WithLoopbackCapture() : builder).Build();
    }

    private void OnCaptureData(ReadOnlySpan<byte> buffer, AudioClientBufferFlags flags, WaveFormat format, bool microphone)
    {
        try
        {
            ProcessSource(buffer, flags.HasFlag(AudioClientBufferFlags.Silent), format, microphone);
        }
        catch (Exception exception)
        {
            // Logged once: the same error would otherwise repeat every 10 ms.
            if (Interlocked.Exchange(ref _callbackErrorLogged, 1) == 0)
                LogAudio($"{(microphone ? "Microphone" : "Loopback")} callback error: {exception}");
        }
    }

    private static void LogCaptureStopped(string source, StoppedEventArgs e)
    {
        if (e.Exception is not null)
            LogAudio($"{source} capture stopped by an error: {e.Exception}");
    }

    // Signal path, per channel: source -> VST chain -> fader -> mute -> bus.
    private void ProcessSource(ReadOnlySpan<byte> buffer, bool silent, WaveFormat format, bool microphone)
    {
        var channel = microphone ? Microphone : Loopback;
        ref var scratch = ref (microphone ? ref _microphoneScratch : ref _loopbackScratch);
        Span<float> samples = ToStereoFloat(buffer, format, ref scratch);

        // WASAPI's "silent" flag means the buffer content must be ignored and treated as silence.
        if (silent)
            samples.Clear();

        ref var resampler = ref (microphone ? ref _microphoneResampler : ref _loopbackResampler);
        var deviceRate = format.SampleRate;
        if (deviceRate != SampleRate)
        {
            resampler ??= new StreamResampler(deviceRate, SampleRate);
            samples = resampler.Process(samples);
        }

        channel.Plugins.Process(samples);

        var volume = Math.Clamp(channel.Volume, 0f, MaxVolume);
        var peak = 0f;
        for (var index = 0; index < samples.Length; index++)
        {
            // A NaN/infinity (misread format, faulty plugin) would poison the mix, the
            // meters and the MP3 stream: replace it with silence.
            var sample = samples[index] * volume;
            samples[index] = float.IsFinite(sample) ? sample : 0f;
            peak = Math.Max(peak, Math.Abs(samples[index]));
        }

        channel.ReportPeak(peak);

        WriteTestSamples(
            samples,
            microphone ? _microphoneTestWriter : _loopbackTestWriter,
            ref (microphone ? ref _microphonePcmScratch : ref _loopbackPcmScratch));

        if (channel.Muted)
            samples.Clear();

        lock (_mixLock)
        {
            var queue = microphone ? _microphoneQueue : _loopbackQueue;
            foreach (var sample in samples)
                queue.Enqueue(sample);
            if (microphone) _lastMicrophoneData = Environment.TickCount64; else _lastLoopbackData = Environment.TickCount64;

            TrimStalledQueue(_microphoneQueue);
            TrimStalledQueue(_loopbackQueue);

            while (NextMixLength() is var length and > 0)
                Mix(length);
        }
    }

    private int NextMixLength()
    {
        var microphone = _microphoneQueue.Count;
        var loopback = _loopbackQueue.Count;
        if (microphone > 0 && loopback > 0)
            return Math.Min(microphone, loopback);

        var now = Environment.TickCount64;
        if (microphone > 0 && now - _lastLoopbackData > IdleSourceMilliseconds)
            return microphone;
        if (loopback > 0 && now - _lastMicrophoneData > IdleSourceMilliseconds)
            return loopback;
        return 0;
    }

    private void Mix(int length)
    {
        static float Take(Queue<float> queue) => queue.Count > 0 ? queue.Dequeue() : 0f;

        var mixed = new float[length];
        for (var index = 0; index < length; index++)
            mixed[index] = Math.Clamp(Take(_microphoneQueue) + Take(_loopbackQueue), -1f, 1f);

        Master.Plugins.Process(mixed);

        var volume = Master.Muted ? 0f : Math.Clamp(Master.Volume, 0f, MaxVolume);
        var peak = 0f;
        for (var index = 0; index < mixed.Length; index++)
        {
            var sample = mixed[index] * volume;
            mixed[index] = float.IsFinite(sample) ? Math.Clamp(sample, -1f, 1f) : 0f;
            peak = Math.Max(peak, Math.Abs(mixed[index]));
        }

        Master.ReportPeak(peak);
        WriteTestSamples(mixed, _mixTestWriter, ref _mixPcmScratch);
        MixedSamplesAvailable?.Invoke(mixed);
    }

    private static void TrimStalledQueue(Queue<float> queue)
    {
        if (queue.Count <= MaxQueueBacklogSamples)
            return;

        var excess = queue.Count - MaxQueueBacklogSamples / 2;
        for (var index = 0; index < excess; index++)
            queue.Dequeue();
    }

    private static Span<float> ToStereoFloat(ReadOnlySpan<byte> buffer, WaveFormat format, ref float[] scratch)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var frameSize = format.Channels * bytesPerSample;
        var length = buffer.Length / frameSize * 2;
        if (scratch.Length < length)
            scratch = new float[length];
        var output = scratch.AsSpan(0, length);

        // With 32-bit WAVE_FORMAT_EXTENSIBLE, samples may be float OR integer (many USB
        // devices): read the SubFormat, don't infer it from the bit depth. Integers read
        // as floats produce garbage and NaN.
        var floatFormat = format.Encoding == WaveFormatEncoding.IeeeFloat ||
                  format is WaveFormatExtensible extensible &&
                  extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT;
        for (var frame = 0; frame < output.Length / 2; frame++)
        {
            var position = frame * frameSize;
            var left = ReadSample(buffer.Slice(position, bytesPerSample), floatFormat);
            var right = format.Channels == 1
                ? left
                : ReadSample(buffer.Slice(position + bytesPerSample, bytesPerSample), floatFormat);
            output[frame * 2] = left;
            output[frame * 2 + 1] = right;
        }

        // A mono microphone plugged into one input of a stereo interface arrives on a
        // single side: when one channel is silent and the other isn't, copy the live one
        // so the voice is centered instead of coming from one speaker only.
        if (format.Channels > 1)
        {
            var leftPeak = 0f;
            var rightPeak = 0f;
            for (var index = 0; index < output.Length; index += 2)
            {
                leftPeak = Math.Max(leftPeak, Math.Abs(output[index]));
                rightPeak = Math.Max(rightPeak, Math.Abs(output[index + 1]));
            }

            if (leftPeak > 0.01f && rightPeak < leftPeak * 0.01f)
            {
                for (var index = 1; index < output.Length; index += 2)
                    output[index] = output[index - 1];
            }
            else if (rightPeak > 0.01f && leftPeak < rightPeak * 0.01f)
            {
                for (var index = 0; index < output.Length; index += 2)
                    output[index] = output[index + 1];
            }
        }

        return output;
    }

    private static float ReadSample(ReadOnlySpan<byte> sample, bool floatFormat)
    {
        switch (sample.Length)
        {
            case 4 when floatFormat:
                return BitConverter.ToSingle(sample);
            case 4:
                return BitConverter.ToInt32(sample) / 2147483648f;
            case 3:
                return ((sample[0] << 8) | (sample[1] << 16) | (sample[2] << 24)) / 2147483648f;
            case 2:
                return BitConverter.ToInt16(sample) / 32768f;
            default:
                throw new InvalidOperationException($"Unsupported sample format ({sample.Length * 8}-bit).");
        }
    }

    private static void WriteTestSamples(ReadOnlySpan<float> samples, WaveFileWriter? writer, ref byte[] pcmScratch)
    {
        if (writer is null)
            return;

        writer.Write(pcmScratch, 0, Pcm16.Encode(samples, ref pcmScratch));
        writer.Flush();
    }

    private static void LogAudio(string message) => LoggingService.Write($"AUDIO {message}");

    public void Dispose() => Stop();

    /// <summary>Resamples a continuous stream to the mixer rate (48 kHz), keeping phase
    /// across calls through a continuously fed buffer.</summary>
    private sealed class StreamResampler
    {
        private readonly BufferedWaveProvider _buffer;
        private readonly WdlResamplingSampleProvider _resampler;
        private readonly int _targetRate;
        private readonly int _sourceRate;
        private float[] _outScratch = new float[0];

        public StreamResampler(int sourceRate, int targetRate)
        {
            _sourceRate = sourceRate;
            _targetRate = targetRate;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(sourceRate, 2);
            _buffer = new BufferedWaveProvider(format, TimeSpan.FromSeconds(2))
            {
                DiscardOnBufferOverflow = true
            };
            _resampler = new WdlResamplingSampleProvider(_buffer.ToSampleProvider(), targetRate);
        }

        public Span<float> Process(ReadOnlySpan<float> stereoIn)
        {
            _buffer.AddSamples(MemoryMarshal.AsBytes(stereoIn));

            var estimatedFrames = (int)Math.Ceiling(stereoIn.Length / 2.0 * _targetRate / _sourceRate) + 4;
            var neededLength = estimatedFrames * 2;
            if (_outScratch.Length < neededLength)
                _outScratch = new float[neededLength];

            var read = _resampler.Read(_outScratch.AsSpan(0, neededLength));
            return _outScratch.AsSpan(0, read);
        }
    }
}

public sealed class AudioDevice
{
    public string ID { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public bool IsDefault { get; init; }
    public override string ToString() => FriendlyName;
}

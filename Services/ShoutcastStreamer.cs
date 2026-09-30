using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Text;
using NAudio.Lame;
using NAudio.Wave;
using Stedjcast.Models;

namespace Stedjcast.Services;

public sealed class ShoutcastStreamer : IDisposable
{
    private readonly object _sync = new();

    private BlockingCollection<float[]>? _samples;
    private TcpClient? _client;
    private NetworkStream? _networkStream;
    private LameMP3FileWriter? _encoder;
    private Task? _worker;
    private bool _started;
    private int _queueFullLogged;
    private byte[] _pcmScratch = new byte[0];

    /// <summary>Raised when the connection drops because of an error (not an explicit
    /// Stop()), so the UI can warn the user and try to reconnect.</summary>
    public event Action<string>? Disconnected;

    public void Start(AppSettings settings)
    {
        Stop();

        if (string.IsNullOrWhiteSpace(settings.ServerHost))
            throw new InvalidOperationException("Enter the Shoutcast server address.");

        var client = new TcpClient
        {
            ReceiveTimeout = 5000,
            SendTimeout = 5000
        };

        try
        {
            client.Connect(settings.ServerHost.Trim(), settings.ServerPort);
        }
        catch (SocketException exception)
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"Unable to reach {settings.ServerHost}:{settings.ServerPort}: " +
                $"{exception.Message}.",
                exception);
        }

        var networkStream = client.GetStream();
        SendSourceHeaders(networkStream, settings);

        var response = ReadResponseLine(networkStream);
        var responseParts = response?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (responseParts is null ||
            responseParts.Length < 2 ||
            !string.Equals(responseParts[1], "200", StringComparison.Ordinal))
        {
            client.Dispose();
            throw new InvalidOperationException(
                $"The Shoutcast server refused the connection: {response ?? "no response"}.");
        }

        var encoder = new LameMP3FileWriter(
            networkStream,
            new WaveFormat(AudioEngine.SampleRate, 16, 2),
            settings.BitrateKbps);
        var samples = new BlockingCollection<float[]>(
            new ConcurrentQueue<float[]>(),
            64);

        lock (_sync)
        {
            _client = client;
            _networkStream = networkStream;
            _encoder = encoder;
            _samples = samples;
            _started = true;
            _worker = Task.Run(() => EncodeLoop(samples, encoder));
            LogStreamer("Connection accepted by the Shoutcast server.");
        }
    }

    public void WriteSamples(float[] samples)
    {
        BlockingCollection<float[]>? queue;

        lock (_sync)
        {
            if (!_started)
                return;

            queue = _samples;
        }

        if (queue is not null &&
            !queue.TryAdd(samples) &&
            Interlocked.Exchange(ref _queueFullLogged, 1) == 0)
        {
            LogStreamer("Audio queue full: the server is not receiving data fast enough.");
        }
    }

    public void Stop()
    {
        BlockingCollection<float[]>? queue;
        Task? worker;
        LameMP3FileWriter? encoder;
        NetworkStream? networkStream;
        TcpClient? client;

        lock (_sync)
        {
            _started = false;
            queue = _samples;
            worker = _worker;
            encoder = _encoder;
            networkStream = _networkStream;
            client = _client;
            _samples = null;
            _worker = null;
            _encoder = null;
            _networkStream = null;
            _client = null;
            _queueFullLogged = 0;
        }

        if (queue is not null)
        {
            queue.CompleteAdding();
            worker?.Wait(TimeSpan.FromSeconds(3));
            queue.Dispose();
        }

        encoder?.Dispose();
        networkStream?.Dispose();
        client?.Dispose();
    }

    private void EncodeLoop(
        BlockingCollection<float[]> samples,
        LameMP3FileWriter encoder)
    {
        try
        {
            foreach (var block in samples.GetConsumingEnumerable())
                encoder.Write(_pcmScratch, 0, Pcm16.Encode(block, ref _pcmScratch));
        }
        catch (Exception exception)
        {
            LogStreamer($"MP3 worker error: {exception}");

            bool wasRunning;
            lock (_sync)
            {
                wasRunning = _started;
                _started = false;
            }

            if (wasRunning)
                Disconnected?.Invoke(exception.Message);
        }
    }

    private static void LogStreamer(string message)
    {
        LoggingService.Write($"STREAM {message}");
    }

    private static void SendSourceHeaders(
        NetworkStream stream,
        AppSettings settings)
    {
        var mount = settings.ServerType.EndsWith(
            "v1",
            StringComparison.OrdinalIgnoreCase)
            ? "/"
            : $"/;stream={settings.StreamId}";

        var headers = new StringBuilder()
            .Append("SOURCE ")
            .Append(settings.SourcePassword)
            .Append(' ')
            .Append(mount)
            .Append(" HTTP/1.0\r\n")
            .Append($"User-Agent: Stedjcast/{typeof(ShoutcastStreamer).Assembly.GetName().Version?.ToString(3)}\r\n")
            .Append("Content-Type: audio/mpeg\r\n")
            .Append("icy-name: ")
            .Append(settings.StreamName)
            .Append("\r\n")
            .Append("icy-description: ")
            .Append(settings.Description)
            .Append("\r\n")
            .Append("icy-genre: ")
            .Append(settings.Genre)
            .Append("\r\n")
            .Append("icy-url: ")
            .Append(settings.Url)
            .Append("\r\n")
            .Append("icy-br: ")
            .Append(settings.BitrateKbps)
            .Append("\r\n")
            .Append("icy-pub: ")
            .Append(settings.PublicServer ? "1" : "0")
            .Append("\r\n\r\n")
            .ToString();

        var bytes = Encoding.ASCII.GetBytes(headers);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();
    }

    private static string? ReadResponseLine(NetworkStream stream)
    {
        var bytes = new List<byte>();

        while (true)
        {
            var value = stream.ReadByte();
            if (value < 0)
                return bytes.Count == 0
                    ? null
                    : Encoding.ASCII.GetString(bytes.ToArray());

            if (value == '\n')
                return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');

            bytes.Add((byte)value);
            if (bytes.Count > 1024)
                throw new InvalidOperationException("Shoutcast server response too long.");
        }
    }

    public void Dispose() => Stop();
}

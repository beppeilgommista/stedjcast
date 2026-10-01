using System.IO;
using System.Text.Json.Serialization;

namespace Stedjcast.Models;

public sealed class AppSettings
{
    public string MicrophoneDeviceId { get; set; } = "";
    public string LoopbackDeviceId { get; set; } = "";

    public string ServerType { get; set; } = "Shoutcast v2";
    public string ServerHost { get; set; } = "";
    public int ServerPort { get; set; } = 8000;
    public int StreamId { get; set; } = 1;
    public int BitrateKbps { get; set; } = 128;
    public bool PublicServer { get; set; }

    /// <summary>Kept in memory only; saved as <see cref="ProtectedPassword"/> (DPAPI, current Windows user).</summary>
    [JsonIgnore]
    public string SourcePassword { get; set; } = "";
    public string ProtectedPassword { get; set; } = "";

    public string StreamName { get; set; } = "";
    public string Description { get; set; } = "";
    public string Url { get; set; } = "";
    public string Genre { get; set; } = "";

    /// <summary>Global shortcut toggling the microphone mute, in KeyGesture text form (e.g. "Ctrl+Alt+K").</summary>
    public string MuteShortcut { get; set; } = "Ctrl+Alt+K";

    public bool TestMode { get; set; }
    public bool AutoStartStream { get; set; }
    public bool LoggingEnabled { get; set; }

    /// <summary>Folder scanned to build the VST3 plugin list.</summary>
    public string Vst3PluginsFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3");

    public ChannelSettings Microphone { get; set; } = new();
    public ChannelSettings Loopback { get; set; } = new();
    public ChannelSettings Master { get; set; } = new();

    public ChannelSettings Channel(string key) =>
        key switch
        {
            "mic" => Microphone,
            "loopback" => Loopback,
            "master" => Master,
            _ => throw new ArgumentOutOfRangeException(nameof(key))
        };
}

public sealed class ChannelSettings
{
    public double GainDb { get; set; }
    public bool Muted { get; set; }
    public PluginSlotSettings[] Plugins { get; set; } = { new(), new(), new() };
}

public sealed class PluginSlotSettings
{
    public string Path { get; set; } = "";
    public bool Bypassed { get; set; }

    /// <summary>The plugin's own saved settings (opaque bytes, stored as base64).</summary>
    public byte[]? State { get; set; }
    public byte[]? ControllerState { get; set; }
}

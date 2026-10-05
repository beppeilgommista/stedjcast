# Stedjcast

A Windows (WPF, .NET 10) app that mixes a microphone with the PC's audio output and
streams the result as MP3 to a Shoutcast server.

## How it works

The microphone and the audio of a Windows output device (loopback) are captured through
WASAPI in shared mode, converted to 48 kHz stereo float and processed per channel:
VST3 plugins -> fader -> mute. The two channels are summed into the master bus, which
has its own VST3 chain, fader and mute. The master output is encoded to MP3 (LAME) and
sent to the server over a Shoutcast source connection.

## Features

- Three channels (MIC, PC AUDIO, MASTER) with fader, level meter and mute.
- Up to 3 VST3 plugins per channel, picked from a scanned folder, with bypass. Each
  plugin opens in its own window, with a generic parameter panel as an alternative.
  Plugin settings are saved and restored at the next start. Legacy VST2 plugins are
  filtered out.
- Microphone mute synchronized with the Windows system mute, plus a configurable global
  shortcut (default Ctrl+Alt+K).
- MP3 streaming to Shoutcast v1/v2 with automatic reconnection when the connection drops.
- Local test mode that records WAV files instead of streaming.
- A small "Update available" indicator links to the GitHub release page when a newer
  version is published (checked once at startup).
- Settings stored in `%LocalAppData%\Stedjcast\settings.json`; the source password is
  encrypted with DPAPI for the current Windows user.

## Limitations

- Plugins run inside the app's process: a plugin that crashes closes the app.
- Plugin windows have a fixed size, and plugins that don't handle display scaling
  themselves may look small on high-DPI screens.
- Some commercial plugins with copy protection may fail to load.
- No server connection test before going live.
- The two capture devices run on separate clocks: no drift compensation beyond dropping
  backlog past 1 second.

## Requirements

- Windows 10/11 x64
- .NET 10 SDK to build

## Build

```powershell
dotnet build -c Release
dotnet run
```

### Installer

Requires [Inno Setup 6](https://jrsoftware.org/isinfo.php). Produces a single
`dist\Stedjcast-<version>-Setup.exe` that installs for all users in Program Files.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
ISCC installer.iss
```

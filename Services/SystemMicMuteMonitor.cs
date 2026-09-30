using NAudio.CoreAudioApi;

namespace Stedjcast.Services;

/// <summary>
/// Mirrors the mute state of the selected microphone's Core Audio endpoint in both
/// directions. Every system mechanism (hardware mic-mute key, Quick Settings, other
/// apps) mutes the microphone by setting this same Windows flag, so hooking it is the
/// only way to stay in sync with "the" system mute, whatever triggered it.
/// </summary>
public sealed class SystemMicMuteMonitor : IDisposable
{
    private readonly MMDeviceEnumerator? _enumerator;
    private readonly MMDevice? _device;
    private bool _updatingInternally;

    public event Action<bool>? MuteChanged;

    public SystemMicMuteMonitor(string deviceId)
    {
        try
        {
            _enumerator = new MMDeviceEnumerator();
            _device = _enumerator.GetDevice(deviceId);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolumeNotification;
        }
        catch (Exception exception)
        {
            LoggingService.Write($"MUTE-SYNC Unable to hook the system mute: {exception.Message}");
            _device = null;
        }
    }

    public bool IsMuted
    {
        get => _device?.AudioEndpointVolume.Mute ?? false;
        set
        {
            if (_device is null || _device.AudioEndpointVolume.Mute == value)
                return;

            _updatingInternally = true;
            try { _device.AudioEndpointVolume.Mute = value; }
            catch (Exception exception) { LoggingService.Write($"MUTE-SYNC Unable to set the system mute: {exception.Message}"); }
            finally { _updatingInternally = false; }
        }
    }

    private void OnVolumeNotification(AudioVolumeNotificationData data)
    {
        if (_updatingInternally)
            return;

        MuteChanged?.Invoke(data.Muted);
    }

    public void Dispose()
    {
        if (_device is not null)
        {
            try { _device.AudioEndpointVolume.OnVolumeNotification -= OnVolumeNotification; }
            catch { }
            _device.Dispose();
        }

        _enumerator?.Dispose();
    }
}

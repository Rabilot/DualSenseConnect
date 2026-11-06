using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

namespace DualSenseConnect.Microphone;

/// <summary>
/// Windows tells us when the default microphone is muted, unmuted or replaced, so the mute
/// state needs no polling. <see cref="Changed"/> is raised on a Windows audio thread.
/// </summary>
sealed class MicWatcher : IDisposable
{
    readonly MMDeviceEnumerator _enumerator = new();
    readonly MMDeviceNotificationClient _notifications;
    readonly object _lock = new();
    MMDevice? _device;

    public event Action? Changed;

    public MicWatcher()
    {
        Attach();
        // Re-attaching is left to the receiver: Windows forbids most audio calls inside these callbacks.
        _notifications = _enumerator.CreateNotificationClient(false);
        _notifications.DefaultDeviceChanged += (_, e) =>
        {
            if (e.Flow == DataFlow.Capture && e.Role == Role.Console)
                Changed?.Invoke();
        };
        _notifications.DeviceStateChanged += (_, e) =>
        {
            if (e.DeviceId == _device?.ID)
                Changed?.Invoke();
        };
    }

    /// <summary>Follows the default microphone; call after <see cref="Changed"/> from a regular thread.</summary>
    public void Attach()
    {
        lock (_lock)
        {
            string? id = null;
            try
            {
                id = _enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console).ID;
            }
            catch (COMException)
            {
                // no microphone at all
            }
            if (id == _device?.ID)
                return;

            Detach();
            if (id == null)
                return;
            _device = _enumerator.GetDevice(id);
            _device.AudioEndpointVolume.OnVolumeNotification += OnVolume;
        }
    }

    void Detach()
    {
        if (_device == null)
            return;
        _device.AudioEndpointVolume.OnVolumeNotification -= OnVolume;
        _device.Dispose();
        _device = null;
    }

    void OnVolume(AudioVolumeNotificationData data) => Changed?.Invoke();

    public void Dispose()
    {
        _notifications.Dispose();
        lock (_lock)
            Detach();
        _enumerator.Dispose();
    }
}

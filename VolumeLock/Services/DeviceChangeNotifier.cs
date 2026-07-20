using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeLock.Services;

/// <summary>
/// Forwards WASAPI endpoint add/remove/default-change notifications to a plain callback.
/// This is treated as a best-effort accelerator, not a load-bearing mechanism: NAudio/WASAPI's
/// endpoint notification callback has known stability issues on some setups (see naudio/NAudio
/// issue #849), so <see cref="AudioService"/> still runs a periodic poll as the reliable
/// fallback path and only uses this to make new devices/apps show up faster.
/// </summary>
internal sealed class DeviceChangeNotifier : IMMNotificationClient
{
    private readonly Action _onChanged;

    public DeviceChangeNotifier(Action onChanged) => _onChanged = onChanged;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _onChanged();
    public void OnDeviceAdded(string pwstrDeviceId) => _onChanged();
    public void OnDeviceRemoved(string deviceId) => _onChanged();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _onChanged();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
}

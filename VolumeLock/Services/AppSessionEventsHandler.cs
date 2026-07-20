using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace VolumeLock.Services;

/// <summary>
/// One instance is registered per live <see cref="AudioSessionControl"/>. WASAPI invokes
/// these callbacks on an MTA worker thread the moment the session's volume or mute state
/// changes, regardless of who changed it (the user dragging a slider, another app calling
/// ISimpleAudioVolume, or our own code). That immediacy is what makes true "locking"
/// possible - polling alone is always a step behind.
/// </summary>
internal sealed class AppSessionEventsHandler : IAudioSessionEventsHandler
{
    private readonly string _key;
    private readonly AudioService _owner;

    public AppSessionEventsHandler(string key, AudioService owner)
    {
        _key = key;
        _owner = owner;
    }

    public void OnVolumeChanged(float volume, bool isMuted)
        => _owner.HandleApplicationVolumeChanged(_key, volume * 100.0, isMuted);

    public void OnDisplayNameChanged(string displayName) { }

    public void OnIconPathChanged(string iconPath) { }

    public void OnChannelVolumeChanged(uint channelCount, nint newVolumes, uint channelIndex) { }

    public void OnGroupingParamChanged(ref Guid groupingId) { }

    public void OnStateChanged(AudioSessionState state)
        => _owner.HandleApplicationStateChanged(_key, state);

    public void OnSessionDisconnected(AudioSessionDisconnectReason disconnectReason)
        => _owner.HandleApplicationDisconnected(_key);
}

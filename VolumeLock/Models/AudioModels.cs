namespace VolumeLock.Models;

/// <summary>Which section of the mixer an item belongs to.</summary>
public enum AudioEndpointKind
{
    Application,
    Microphone
}

/// <summary>
/// Immutable snapshot describing a mixer entry, passed from <see cref="Services.AudioService"/>
/// up to the view models. Keeping this as a plain DTO (rather than handing out NAudio COM
/// wrapper objects) keeps the WASAPI interop fully contained inside the service layer.
/// </summary>
public sealed record AudioItemInfo(
    string Key,
    AudioEndpointKind Kind,
    string DisplayName,
    double VolumePercent,
    bool IsMuted,
    bool IsLocked,
    byte[]? IconPngBytes
);

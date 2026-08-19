using VolumeLock.CoreAudio;

namespace VolumeLock.Services;

/// <summary>
/// Re-applies the user's locked volume levels whenever they drift.
/// Uses COM callbacks for near-instant detection + a fast polling fallback.
/// </summary>
public sealed class AudioLockService : IDisposable
{
    private const float Tolerance = 0.005f;

    private readonly SettingsService _settingsService;
    private readonly AudioManager _audio;
    private readonly Microsoft.UI.Dispatching.DispatcherQueue? _queue;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer? _timer;
    private bool _disposed;

    public event Action<VolumeStatus>? StatusUpdated;

    public AudioLockService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _audio = new AudioManager();

        _queue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        if (_queue != null)
        {
            _timer = _queue.CreateTimer();
            _timer.Interval = TimeSpan.FromMilliseconds(
                Math.Max(500, settingsService.Settings.CheckIntervalSeconds * 1000));
            _timer.Tick += (s, e) => ApplyNow();
        }

        _audio.MicrophoneVolumeChanged += OnExternalVolumeChange;
        _audio.SystemSoundsVolumeChanged += OnExternalVolumeChange;
    }

    public void Start()
    {
        _queue?.TryEnqueue(() =>
        {
            if (_disposed) return;
            _audio.RegisterCallbacks();
            ApplyNow();
        });

        _timer?.Start();
    }

    public void Stop()
    {
        _timer?.Stop();
    }

    private void OnExternalVolumeChange()
    {
        _queue?.TryEnqueue(() =>
        {
            if (!_disposed)
                ApplyNow();
        });
    }

    /// <summary>
    /// Performs a single check-and-enforce pass, then raises StatusUpdated.
    /// </summary>
    public void ApplyNow()
    {
        var status = new VolumeStatus();

        try
        {
            ApplySystemSounds(status);
        }
        catch (Exception ex)
        {
            status.SystemSoundsError = ex.Message;
        }

        try
        {
            ApplyMicrophone(status);
        }
        catch (Exception ex)
        {
            status.MicrophoneError = ex.Message;
        }

        StatusUpdated?.Invoke(status);
    }

    private void ApplySystemSounds(VolumeStatus status)
    {
        var sys = _audio.GetSystemSoundsStatus();
        status.SystemSoundsFound = sys.Found;
        status.SystemSoundsDeviceName = sys.DeviceName;
        status.SystemSoundsLevel = sys.Found ? (int)Math.Round(sys.Level * 100) : null;

        if (!sys.Found)
        {
            status.SystemSoundsError = sys.Error ?? "System Sounds session not found";
            return;
        }

        _audio.TryRegisterSessionEvents();

        if (_settingsService.Settings.SystemSoundsEnabled)
        {
            float target = _settingsService.Settings.SystemSoundsLevel / 100f;
            if (Math.Abs(sys.Level - target) > Tolerance)
            {
                if (!_audio.SetSystemSoundsVolume(target))
                    status.SystemSoundsError = "Failed to apply the locked System Sounds level";
            }
        }
    }

    private void ApplyMicrophone(VolumeStatus status)
    {
        var mic = _audio.GetMicrophoneStatus();
        status.MicrophoneOk = mic.Ok;
        status.MicrophoneDeviceName = mic.DeviceName;
        status.MicrophoneLevel = mic.Ok ? (int)Math.Round(mic.Level * 100) : null;

        if (!mic.Ok)
        {
            status.MicrophoneError = mic.Error ?? "Microphone unavailable";
            return;
        }

        if (_settingsService.Settings.MicrophoneEnabled)
        {
            float target = _settingsService.Settings.MicrophoneLevel / 100f;
            if (Math.Abs(mic.Level - target) > Tolerance)
            {
                if (!_audio.SetMicrophoneVolume(target))
                    status.MicrophoneError = "Failed to apply the locked Microphone level";
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
        _audio.MicrophoneVolumeChanged -= OnExternalVolumeChange;
        _audio.SystemSoundsVolumeChanged -= OnExternalVolumeChange;
        _audio.UnregisterCallbacks();
        _audio.Dispose();
    }
}

/// <summary>Snapshot of the most recent volume check, for the UI.</summary>
public sealed class VolumeStatus
{
    public bool SystemSoundsFound { get; set; }
    public int? SystemSoundsLevel { get; set; }
    public string SystemSoundsDeviceName { get; set; } = "";
    public string? SystemSoundsError { get; set; }

    public bool MicrophoneOk { get; set; }
    public int? MicrophoneLevel { get; set; }
    public string MicrophoneDeviceName { get; set; } = "";
    public string? MicrophoneError { get; set; }
}

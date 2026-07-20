using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using VolumeLock.Models;

namespace VolumeLock.Services;

/// <summary>
/// Owns all WASAPI/Core Audio interop. Nothing outside this class touches NAudio types.
///
/// Design notes (see README.md "Research &amp; design notes" for the full write-up):
///
/// 1. Locking is enforced in <see cref="HandleApplicationVolumeChanged"/> /
///    <see cref="HandleMicrophoneVolumeChanged"/>, which run synchronously on the WASAPI
///    callback thread the instant Windows reports a volume change - not on a poll tick.
///    This is what makes the reset feel instantaneous instead of "flickers back a moment
///    later". A poll loop alone cannot achieve this: even a 50ms poll has a perceptible
///    lag and can miss a volume change that is set and read back within the poll window.
///
/// 2. A slow poll (<see cref="_refreshTimer"/>) still runs alongside the event-driven path.
///    It exists purely as a safety net for two known gaps: (a) new sessions/devices can
///    appear between notifications, and (b) COM event registration in this API surface has
///    occasionally been reported to silently stop firing after certain device changes.
///    Belt-and-suspenders, not the primary mechanism.
///
/// 3. Session identity: a live WASAPI session's own identifiers are not stable across an
///    app's restarts, so the in-memory "sticky lock" map is keyed by process name, while
///    the live UI list is keyed by "processName:pid" so that two processes sharing a name
///    (e.g. two python.exe) still show as separate mixer entries, matching the real Volume
///    Mixer's behavior.
///
/// 4. Every COM wrapper this class hands out (AudioSessionControl event registration,
///    AudioEndpointVolume) is explicitly unregistered/disposed when an item disappears.
///    Leaked event registrations were a real source of instability during earlier related
///    work and are the first thing to check if sessions seem to "stick around" after the
///    app that owns them closes.
/// </summary>
public sealed class AudioService : IDisposable
{
    private const double LockEpsilonPercent = 0.05;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(750);

    private sealed class TrackedApp
    {
        public required string ListKey;
        public required string StickyKey;
        public required string ProcessName;
        public required AudioSessionControl Session;
        public required AppSessionEventsHandler Handler;
    }

    private sealed class TrackedMic
    {
        public required string ListKey; // == device.ID
        public required MMDevice Device;
        public AudioEndpointVolumeNotificationDelegate? Callback;
    }

    private readonly Dispatcher _dispatcher;
    private readonly SettingsService _settings;
    private readonly MMDeviceEnumerator _enumerator = new();
    private readonly object _gate = new();

    private readonly Dictionary<string, TrackedApp> _apps = new();
    private readonly Dictionary<string, TrackedMic> _mics = new();

    // Active lock: list key -> locked percent (0-100). Only exists while the item is live.
    private readonly Dictionary<string, double> _activeLocks = new();

    // Sticky lock: survives an app process restarting so the same lock re-applies
    // automatically. Persisted to settings.json via SettingsService.
    private readonly Dictionary<string, double> _stickyLocks = new();

    private MMDevice? _currentRenderDevice;
    private DeviceChangeNotifier? _deviceNotifier;
    private readonly DispatcherTimer _refreshTimer;
    private bool _pendingRefreshRequest;

    public event Action<AudioItemInfo>? ItemAdded;
    public event Action<string, AudioEndpointKind>? ItemRemoved;
    public event Action<string, double, bool>? VolumeUpdated;

    public AudioService(Dispatcher dispatcher, SettingsService settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = RefreshInterval
        };
        _refreshTimer.Tick += (_, _) => RefreshAll();
    }

    public void Start()
    {
        try
        {
            _deviceNotifier = new DeviceChangeNotifier(RequestPromptRefresh);
            _enumerator.RegisterEndpointNotificationCallback(_deviceNotifier);
        }
        catch
        {
            // Best-effort accelerator only (see class remarks) - the poll timer covers us.
            _deviceNotifier = null;
        }

        foreach (var kvp in _settings.LoadLockedItems())
            _stickyLocks[kvp.Key] = kvp.Value;

        RefreshAll();
        _refreshTimer.Start();
    }

    public IReadOnlyList<AudioItemInfo> GetCurrentItems()
    {
        var items = new List<AudioItemInfo>();
        lock (_gate)
        {
            foreach (var app in _apps.Values)
            {
                double percent = app.Session.SimpleAudioVolume.Volume * 100.0;
                bool muted = app.Session.SimpleAudioVolume.Mute;
                bool isLocked = _activeLocks.ContainsKey(app.ListKey);
                int pid = (int)app.Session.GetProcessID;
                byte[]? icon = TryExtractProcessIconPng(pid);
                items.Add(new AudioItemInfo(app.ListKey, AudioEndpointKind.Application, app.ProcessName, percent, muted, isLocked, icon));
            }
            foreach (var mic in _mics.Values)
            {
                double percent = mic.Device.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0;
                bool muted = mic.Device.AudioEndpointVolume.Mute;
                bool isLocked = _activeLocks.ContainsKey(mic.ListKey);
                items.Add(new AudioItemInfo(mic.ListKey, AudioEndpointKind.Microphone, mic.Device.FriendlyName, percent, muted, isLocked, null));
            }
        }
        return items;
    }

    public void Dispose()
    {
        _refreshTimer.Stop();

        if (_deviceNotifier is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(_deviceNotifier); } catch { /* shutting down anyway */ }
        }

        lock (_gate)
        {
            foreach (var app in _apps.Values)
                UnregisterApp(app);
            _apps.Clear();

            foreach (var mic in _mics.Values)
                UnregisterMic(mic);
            _mics.Clear();
        }

        _currentRenderDevice?.Dispose();
    }

    private void RequestPromptRefresh()
    {
        if (_pendingRefreshRequest) return;
        _pendingRefreshRequest = true;
        _dispatcher.BeginInvoke(() =>
        {
            _pendingRefreshRequest = false;
            RefreshAll();
        });
    }

    // ------------------------------------------------------------------
    // Public control surface (called from view models, always on UI thread)
    // ------------------------------------------------------------------

    public void SetApplicationVolume(string listKey, double percent)
    {
        lock (_gate)
        {
            if (_activeLocks.ContainsKey(listKey)) return; // locked: ignore, defensively
            if (_apps.TryGetValue(listKey, out var app))
            {
                try { app.Session.SimpleAudioVolume.Volume = (float)Clamp01(percent / 100.0); }
                catch { /* session may have just disconnected */ }
            }
        }
    }

    public void SetApplicationMute(string listKey, bool mute)
    {
        lock (_gate)
        {
            if (_apps.TryGetValue(listKey, out var app))
            {
                try { app.Session.SimpleAudioVolume.Mute = mute; }
                catch { }
            }
        }
    }

    public void SetMicrophoneVolume(string listKey, double percent)
    {
        lock (_gate)
        {
            if (_activeLocks.ContainsKey(listKey)) return;
            if (_mics.TryGetValue(listKey, out var mic))
            {
                try { mic.Device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Clamp01(percent / 100.0); }
                catch { }
            }
        }
    }

    public void SetMicrophoneMute(string listKey, bool mute)
    {
        lock (_gate)
        {
            if (_mics.TryGetValue(listKey, out var mic))
            {
                try { mic.Device.AudioEndpointVolume.Mute = mute; }
                catch { }
            }
        }
    }

    /// <summary>Locks or unlocks an item at its current percent. Called from the UI thread.</summary>
    public void SetLocked(string listKey, bool locked, double currentPercent)
    {
        lock (_gate)
        {
            string? stickyKey = _apps.TryGetValue(listKey, out var app) ? app.StickyKey
                : _mics.ContainsKey(listKey) ? listKey
                : null;

            if (locked)
            {
                _activeLocks[listKey] = currentPercent;
                if (stickyKey is not null) _stickyLocks[stickyKey] = currentPercent;
            }
            else
            {
                _activeLocks.Remove(listKey);
                if (stickyKey is not null) _stickyLocks.Remove(stickyKey);
            }

            _settings.SaveLockedItems(_stickyLocks);
        }
    }

    // ------------------------------------------------------------------
    // Event-driven enforcement (called on WASAPI callback threads)
    // ------------------------------------------------------------------

    internal void HandleApplicationVolumeChanged(string listKey, double percent, bool muted)
    {
        double effectivePercent = percent;

        lock (_gate)
        {
            if (_activeLocks.TryGetValue(listKey, out var lockedPercent) &&
                Math.Abs(percent - lockedPercent) > LockEpsilonPercent)
            {
                if (_apps.TryGetValue(listKey, out var app))
                {
                    try { app.Session.SimpleAudioVolume.Volume = (float)Clamp01(lockedPercent / 100.0); }
                    catch { }
                }
                effectivePercent = lockedPercent;
            }
        }

        _dispatcher.BeginInvoke(() => VolumeUpdated?.Invoke(listKey, effectivePercent, muted));
    }

    internal void HandleApplicationStateChanged(string listKey, AudioSessionState state)
    {
        if (state != AudioSessionState.AudioSessionStateExpired) return;
        RemoveAppInternal(listKey);
    }

    internal void HandleApplicationDisconnected(string listKey) => RemoveAppInternal(listKey);

    private void RemoveAppInternal(string listKey)
    {
        TrackedApp? removed = null;
        lock (_gate)
        {
            if (_apps.Remove(listKey, out removed))
            {
                UnregisterApp(removed);
            }
            _activeLocks.Remove(listKey);
        }
        if (removed is not null)
            _dispatcher.BeginInvoke(() => ItemRemoved?.Invoke(listKey, AudioEndpointKind.Application));
    }

    private void HandleMicrophoneVolumeChanged(string listKey, AudioVolumeNotificationData data)
    {
        double percent = data.MasterVolume * 100.0;
        double effectivePercent = percent;

        lock (_gate)
        {
            if (_activeLocks.TryGetValue(listKey, out var lockedPercent) &&
                Math.Abs(percent - lockedPercent) > LockEpsilonPercent)
            {
                if (_mics.TryGetValue(listKey, out var mic))
                {
                    try { mic.Device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Clamp01(lockedPercent / 100.0); }
                    catch { }
                }
                effectivePercent = lockedPercent;
            }
        }

        _dispatcher.BeginInvoke(() => VolumeUpdated?.Invoke(listKey, effectivePercent, data.Muted));
    }

    // ------------------------------------------------------------------
    // Enumeration / refresh (UI thread)
    // ------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshRenderSessions();
        RefreshMicrophones();
    }

    private void RefreshRenderSessions()
    {
        MMDevice? device;
        try
        {
            device = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console);
        }
        catch
        {
            // No active playback device at all.
            device = null;
        }

        if (device is null)
        {
            // Drop everything - nothing is playable right now.
            foreach (var key in _apps.Keys.ToList())
                RemoveAppInternal(key);
            _currentRenderDevice?.Dispose();
            _currentRenderDevice = null;
            return;
        }

        if (_currentRenderDevice is null || _currentRenderDevice.ID != device.ID)
        {
            // Default output device changed - rebuild the app list against the new device.
            foreach (var key in _apps.Keys.ToList())
                RemoveAppInternal(key);

            _currentRenderDevice?.Dispose();
            _currentRenderDevice = device;
        }
        else
        {
            device.Dispose(); // same device, we already hold a reference
        }

        // AudioSessionManager.Sessions is a cached snapshot in NAudio - it is only rebuilt
        // when RefreshSessions() is called, never automatically. Skipping this call means
        // only sessions that existed at the very first read would ever be seen; anything
        // opened afterward (a new app, a new browser tab playing audio) would silently never
        // appear. This is the fix for that.
        _currentRenderDevice.AudioSessionManager.RefreshSessions();
        var sessions = _currentRenderDevice.AudioSessionManager.Sessions;
        var seenKeys = new HashSet<string>();

        for (int i = 0; i < sessions.Count; i++)
        {
            AudioSessionControl session = sessions[i];
            if (session.State == AudioSessionState.AudioSessionStateExpired)
            {
                session.Dispose();
                continue;
            }

            int pid = (int)session.GetProcessID;
            string processName = pid == 0 ? "System Sounds" : TryGetProcessName(pid, out var name) ? name : $"pid-{pid}";
            string listKey = pid == 0 ? "app:system" : $"app:{processName.ToLowerInvariant()}:{pid}";
            string stickyKey = pid == 0 ? "app:system" : $"app:{processName.ToLowerInvariant()}";
            seenKeys.Add(listKey);

            lock (_gate)
            {
                if (_apps.ContainsKey(listKey))
                {
                    session.Dispose();
                    continue;
                }

                var handler = new AppSessionEventsHandler(listKey, this);
                try { session.RegisterEventClient(handler); }
                catch { /* if this fails the poll loop still catches gross drift */ }

                _apps[listKey] = new TrackedApp
                {
                    ListKey = listKey,
                    StickyKey = stickyKey,
                    ProcessName = processName,
                    Session = session,
                    Handler = handler
                };

                double startingPercent = session.SimpleAudioVolume.Volume * 100.0;
                bool startingMuted = session.SimpleAudioVolume.Mute;

                // Sticky-lock re-application: if this process name was previously locked,
                // snap it back to that level immediately and mark it locked again.
                if (_stickyLocks.TryGetValue(stickyKey, out var stickyPercent))
                {
                    _activeLocks[listKey] = stickyPercent;
                    try { session.SimpleAudioVolume.Volume = (float)Clamp01(stickyPercent / 100.0); }
                    catch { }
                    startingPercent = stickyPercent;
                }

                byte[]? icon = TryExtractProcessIconPng(pid);
                bool isLocked = _activeLocks.ContainsKey(listKey);

                _dispatcher.BeginInvoke(() => ItemAdded?.Invoke(new AudioItemInfo(
                    listKey, AudioEndpointKind.Application, processName, startingPercent, startingMuted, isLocked, icon)));

                if (isLocked)
                {
                    double p = startingPercent;
                    _dispatcher.BeginInvoke(() => VolumeUpdated?.Invoke(listKey, p, startingMuted));
                }
            }
        }

        foreach (var staleKey in _apps.Keys.Where(k => !seenKeys.Contains(k)).ToList())
            RemoveAppInternal(staleKey);
    }

    private void RefreshMicrophones()
    {
        MMDeviceCollection devices;
        try
        {
            devices = _enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
        }
        catch
        {
            return;
        }

        var seenKeys = new HashSet<string>();

        foreach (var device in devices)
        {
            string listKey = "mic:" + device.ID;
            seenKeys.Add(listKey);

            lock (_gate)
            {
                if (_mics.ContainsKey(listKey))
                {
                    device.Dispose();
                    continue;
                }

                AudioEndpointVolumeNotificationDelegate callback = data => HandleMicrophoneVolumeChanged(listKey, data);
                try { device.AudioEndpointVolume.OnVolumeNotification += callback; }
                catch { }

                _mics[listKey] = new TrackedMic { ListKey = listKey, Device = device, Callback = callback };

                double startingPercent = device.AudioEndpointVolume.MasterVolumeLevelScalar * 100.0;
                bool startingMuted = device.AudioEndpointVolume.Mute;

                if (_stickyLocks.TryGetValue(listKey, out var stickyPercent))
                {
                    _activeLocks[listKey] = stickyPercent;
                    try { device.AudioEndpointVolume.MasterVolumeLevelScalar = (float)Clamp01(stickyPercent / 100.0); }
                    catch { }
                    startingPercent = stickyPercent;
                }

                bool isLocked = _activeLocks.ContainsKey(listKey);

                _dispatcher.BeginInvoke(() => ItemAdded?.Invoke(new AudioItemInfo(
                    listKey, AudioEndpointKind.Microphone, device.FriendlyName, startingPercent, startingMuted, isLocked, null)));
            }
        }

        foreach (var staleKey in _mics.Keys.Where(k => !seenKeys.Contains(k)).ToList())
        {
            TrackedMic? removed = null;
            lock (_gate)
            {
                if (_mics.Remove(staleKey, out removed))
                    UnregisterMic(removed);
                _activeLocks.Remove(staleKey);
            }
            if (removed is not null)
                _dispatcher.BeginInvoke(() => ItemRemoved?.Invoke(staleKey, AudioEndpointKind.Microphone));
        }
    }

    private static void UnregisterApp(TrackedApp app)
    {
        try { app.Session.UnRegisterEventClient(app.Handler); } catch { }
        try { app.Session.Dispose(); } catch { }
    }

    private static void UnregisterMic(TrackedMic mic)
    {
        try
        {
            if (mic.Callback is not null)
                mic.Device.AudioEndpointVolume.OnVolumeNotification -= mic.Callback;
        }
        catch { }

        try { mic.Device.AudioEndpointVolume.Dispose(); } catch { }
        try { mic.Device.Dispose(); } catch { }
    }

    private static bool TryGetProcessName(int pid, out string name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
            return true;
        }
        catch
        {
            name = string.Empty;
            return false;
        }
    }

    private static byte[]? TryExtractProcessIconPng(int pid)
    {
        if (pid == 0) return null;
        try
        {
            using var process = Process.GetProcessById(pid);
            string? path = process.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return null;

            using Icon? icon = Icon.ExtractAssociatedIcon(path);
            if (icon is null) return null;

            using var bitmap = icon.ToBitmap();
            using var stream = new MemoryStream();
            bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
            return stream.ToArray();
        }
        catch
        {
            // Protected/elevated processes (and some system processes) will deny access here -
            // that's expected and not a bug; the UI just falls back to a generic icon.
            return null;
        }
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.0, 1.0);
}

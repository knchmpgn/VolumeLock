using System.Diagnostics;
using System.Runtime.InteropServices;
using VolumeLock.Interop;

namespace VolumeLock.CoreAudio;

/// <summary>
/// Wraps the Windows Core Audio (WASAPI) APIs needed to read and set the
/// "System Sounds" app-session volume and the default microphone endpoint volume.
/// </summary>
public sealed class AudioManager : IDisposable
{
    private static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private const int CLSCTX_ALL = 23;             // CLSCTX_INPROC_SERVER | CLSCTX_LOCAL_SERVER | CLSCTX_REMOTE_SERVER
    private const int STGM_READ = 0;
    private const uint COINIT_APARTMENTTHREADED = 0x2;
    private const ushort VT_LPWSTR = 31;

    private readonly bool _ownsCom;
    private IMMDeviceEnumerator? _enumerator;

    public AudioManager()
    {
        int hr = NativeMethods.CoInitializeEx(IntPtr.Zero, COINIT_APARTMENTTHREADED);
        // RPC_E_CHANGED_MODE (0x80010106) / RPC_E_ALREADY_INITIALIZED (0x8001010A) mean COM was
        // already initialized on this thread by someone else - that is fine.
        _ownsCom = hr == 0;

        _enumerator = new MMDeviceEnumeratorComObject() as IMMDeviceEnumerator;
        if (_enumerator == null)
            throw new InvalidOperationException("Failed to create the MMDeviceEnumerator COM object.");
    }

    public void Dispose()
    {
        _enumerator = null;
        if (_ownsCom)
            NativeMethods.CoUninitialize();
    }

    // ------------------------------------------------------------------
    // Public surface
    // ------------------------------------------------------------------

    public (bool Found, float Level, string DeviceName, string? Error) GetSystemSoundsStatus()
    {
        try
        {
            IMMDevice? device = GetDefaultRenderDevice();
            if (device == null)
                return (false, 0, "No playback device", "No default playback device found.");

            string deviceName = GetDeviceFriendlyName(device);
            var session = FindSystemSoundsSession(device);

            if (session == null)
            {
                SafeRelease(device);
                return (false, 0, deviceName, "The System Sounds session is not available yet.");
            }

            using (session)
            {
                float level = 0;
                string? error = null;
                int hr = session.Volume.GetMasterVolume(out level);
                if (hr != 0)
                    error = $"Could not read System Sounds volume (0x{hr:X8}).";

                SafeRelease(device);
                return (hr == 0, level, deviceName, error);
            }
        }
        catch (Exception ex)
        {
            return (false, 0, "Default playback device", ex.Message);
        }
    }

    public bool SetSystemSoundsVolume(float level)
    {
        try
        {
            IMMDevice? device = GetDefaultRenderDevice();
            if (device == null)
                return false;

            try
            {
                var session = FindSystemSoundsSession(device);
                if (session == null)
                    return false;

                using (session)
                {
                    Guid context = Guid.Empty;
                    return session.Volume.SetMasterVolume(level, ref context) == 0;
                }
            }
            finally
            {
                SafeRelease(device);
            }
        }
        catch
        {
            return false;
        }
    }

    public (bool Ok, float Level, string DeviceName, string? Error) GetMicrophoneStatus()
    {
        try
        {
            IMMDevice? device = GetDefaultCaptureDevice();
            if (device == null)
                return (false, 0, "No input device", "No default microphone found.");

            string deviceName = GetDeviceFriendlyName(device);

            var endpoint = ActivateAudioEndpointVolume(device);
            SafeRelease(device);
            if (endpoint == null)
                return (false, 0, deviceName, "Could not access microphone volume.");

            using (endpoint)
            {
                int hr = endpoint.Volume.GetMasterVolumeLevelScalar(out float level);
                if (hr != 0)
                    return (false, 0, deviceName, $"Could not read microphone volume (0x{hr:X8}).");
                return (true, level, deviceName, null);
            }
        }
        catch (Exception ex)
        {
            return (false, 0, "Default input device", ex.Message);
        }
    }

    public bool SetMicrophoneVolume(float level)
    {
        try
        {
            IMMDevice? device = GetDefaultCaptureDevice();
            if (device == null)
                return false;

            try
            {
                var endpoint = ActivateAudioEndpointVolume(device);
                if (endpoint == null)
                    return false;

                using (endpoint)
                {
                    Guid context = Guid.Empty;
                    return endpoint.Volume.SetMasterVolumeLevelScalar(level, ref context) == 0;
                }
            }
            finally
            {
                SafeRelease(device);
            }
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------------------------
    // Device resolution
    // ------------------------------------------------------------------

    private IMMDevice? GetDefaultRenderDevice()
        => GetDefaultDeviceAnyRole(EDataFlow.eRender);

    private IMMDevice? GetDefaultCaptureDevice()
        => GetDefaultDeviceAnyRole(EDataFlow.eCapture);

    private IMMDevice? GetDefaultDeviceAnyRole(EDataFlow flow)
    {
        foreach (ERole role in new[] { ERole.eMultimedia, ERole.eConsole, ERole.eCommunications })
        {
            try
            {
                if (_enumerator == null)
                    return null;

                int hr = _enumerator.GetDefaultAudioEndpoint((int)flow, (int)role, out IMMDevice device);
                if (hr == 0)
                    return device;
            }
            catch
            {
                // try the next role
            }
        }
        return null;
    }

    private string GetDeviceFriendlyName(IMMDevice device)
    {
        try
        {
            int hr = device.OpenPropertyStore(STGM_READ, out IPropertyStore store);
            if (hr != 0)
                return "Default device";

            try
            {
                var key = new PROPERTYKEY
                {
                    fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), // PKEY_Device_FriendlyName
                    pid = 14
                };

                hr = store.GetValue(ref key, out PROPVARIANT pv);
                if (hr == 0 && pv.vt == VT_LPWSTR)
                {
                    string? name = Marshal.PtrToStringUni(pv.ptr1);
                    NativeMethods.PropVariantClear(ref pv);
                    if (!string.IsNullOrEmpty(name))
                        return name;
                }

                return "Default device";
            }
            finally
            {
                SafeRelease(store);
            }
        }
        catch
        {
            return "Default device";
        }
    }

    // ------------------------------------------------------------------
    // System Sounds session
    // ------------------------------------------------------------------

    private SystemSoundsSession? FindSystemSoundsSession(IMMDevice renderDevice)
    {
        object? managerObj = null;
        try
        {
            Guid iid = IID_IAudioSessionManager2;
            int hr = renderDevice.Activate(
                ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObj);
            if (hr != 0)
                return null;

            var manager = managerObj as IAudioSessionManager2;
            if (manager == null)
                return null;

            hr = manager.GetSessionEnumerator(out IAudioSessionEnumerator enumerator);
            if (hr != 0)
                return null;

            try
            {
                enumerator.GetCount(out int count);
                for (int i = 0; i < count; i++)
                {
                    try
                    {
                        enumerator.GetSession(i, out IAudioSessionControl control);
                        // control and any 'as' casts of it share a single RCW per COM
                        // object, so we only release it once (at control2) or transfer
                        // ownership on match.
                        var control2 = control as IAudioSessionControl2;
                        if (control2 == null)
                        {
                            SafeRelease(control);
                            continue;
                        }

                        bool isSystemSounds = control2.IsSystemSoundsSession() == 0;

                        if (!isSystemSounds)
                        {
                            // Fallback: match by process or display name.
                            if (control2.GetProcessId(out uint pid) == 0 && pid != 0)
                            {
                                string? processName = null;
                                try { processName = Process.GetProcessById((int)pid).ProcessName; }
                                catch { /* process may have exited */ }

                                isSystemSounds = string.Equals(processName, "SystemSounds", StringComparison.OrdinalIgnoreCase);
                            }

                            if (!isSystemSounds && control2.GetDisplayName(out string? displayName) == 0 &&
                                !string.IsNullOrEmpty(displayName))
                            {
                                isSystemSounds = displayName.Contains("System Sounds", StringComparison.OrdinalIgnoreCase);
                            }
                        }

                        if (isSystemSounds)
                        {
                            var volume = control2 as ISimpleAudioVolume;
                            if (volume != null)
                                return new SystemSoundsSession(control2, volume);
                        }

                        SafeRelease(control2);
                    }
                    catch
                    {
                        // Skip sessions that throw or fail during inspection.
                    }
                }
            }
            finally
            {
                SafeRelease(enumerator);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            SafeRelease(managerObj);
        }
    }

    // ------------------------------------------------------------------
    // Microphone endpoint volume
    // ------------------------------------------------------------------

    private AudioEndpointVolumeControl? ActivateAudioEndpointVolume(IMMDevice device)
    {
        object? endpointObj = null;
        try
        {
            Guid iid = IID_IAudioEndpointVolume;
            int hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out endpointObj);
            if (hr != 0)
            {
                SafeRelease(endpointObj);
                return null;
            }

            var endpoint = endpointObj as IAudioEndpointVolume;
            if (endpoint == null)
            {
                SafeRelease(endpointObj);
                return null;
            }

            // Ownership of the RCW moves to the wrapper; it is released in Dispose().
            return new AudioEndpointVolumeControl(endpoint);
        }
        catch
        {
            SafeRelease(endpointObj);
            return null;
        }
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static void SafeRelease(object? comObject)
    {
        try
        {
            if (comObject != null && Marshal.IsComObject(comObject))
                Marshal.ReleaseComObject(comObject);
        }
        catch
        {
            // already released / invalid - ignore
        }
    }

    private sealed class SystemSoundsSession : IDisposable
    {
        public SystemSoundsSession(IAudioSessionControl2 control, ISimpleAudioVolume volume)
        {
            Control = control;
            Volume = volume;
        }

        public IAudioSessionControl2 Control { get; }
        public ISimpleAudioVolume Volume { get; }

        public void Dispose()
        {
            SafeRelease(Control);
            SafeRelease(Volume);
        }
    }

    private sealed class AudioEndpointVolumeControl : IDisposable
    {
        public AudioEndpointVolumeControl(IAudioEndpointVolume volume)
        {
            Volume = volume;
        }

        public IAudioEndpointVolume Volume { get; }

        public void Dispose()
        {
            SafeRelease(Volume);
        }
    }
}

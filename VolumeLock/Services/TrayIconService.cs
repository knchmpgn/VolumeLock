using System.Runtime.InteropServices;
using VolumeLock.Interop;

namespace VolumeLock.Services;

/// <summary>
/// Implements the notification-area (tray) icon using Win32 Shell_NotifyIcon.
/// Left-click shows the window, right-click opens a context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const int IconId = 1;
    private const uint TrayCallbackMsg = NativeMethods.WM_APP + 1;

    private const int CmdShowHide = 1;
    private const int CmdRunAtStartup = 2;
    private const int CmdHideTrayIcon = 3;
    private const int CmdExit = 4;

    private readonly MainWindow _window;
    private readonly SettingsService _settingsService;
    private readonly string _iconPath;

    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private uint _taskbarCreatedMsg;
    private NativeMethods.WndProcDelegate? _wndProcDelegate;
    private bool _iconVisible;
    private bool _disposed;

    public event EventHandler? ExitRequested;

    public TrayIconService(MainWindow window, SettingsService settingsService)
    {
        _window = window;
        _settingsService = settingsService;

        _iconPath = IconHelper.GetIconPath()
            ?? Path.Combine(AppPaths.ExecutableDirectory, "app.ico");

        EnsureNativeWindow();
    }

    public void Show()
    {
        if (_iconVisible || _disposed)
            return;

        AddIcon();
        _iconVisible = true;
    }

    public void Hide()
    {
        if (!_iconVisible)
            return;

        RemoveIcon();
        _iconVisible = false;
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_iconVisible || _hwnd == IntPtr.Zero)
            return;

        var nid = NewNotifyIconData();
        nid.uFlags = NativeMethods.NIF_INFO | NativeMethods.NIF_MESSAGE;
        nid.dwInfoFlags = NativeMethods.NIIF_INFO;
        nid.szInfoTitle = Truncate(title, 63);
        nid.szInfo = Truncate(message, 255);
        nid.uTimeoutOrVersion = 5000;
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            if (_iconVisible)
                RemoveIcon();

            if (_hIcon != IntPtr.Zero)
            {
                NativeMethods.DestroyIcon(_hIcon);
                _hIcon = IntPtr.Zero;
            }

            if (_hwnd != IntPtr.Zero)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }
        catch
        {
            // best effort during shutdown
        }
    }

    // ------------------------------------------------------------------
    // Native plumbing
    // ------------------------------------------------------------------

    private void EnsureNativeWindow()
    {
        if (_hwnd != IntPtr.Zero)
            return;

        _wndProcDelegate = WndProc;

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
            hInstance = NativeMethods.GetModuleHandleW(null),
            lpszClassName = "VolumeLockTrayWindow"
        };

        if (NativeMethods.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException(
                $"RegisterClassEx failed with Win32 error {Marshal.GetLastWin32Error()}.");

        _hwnd = NativeMethods.CreateWindowEx(
            0,
            "VolumeLockTrayWindow",
            null, // title is never shown; avoids a .NET LPWStr marshaling quirk
            0,
            NativeMethods.CW_USEDEFAULT,
            0,
            0,
            0,
            IntPtr.Zero,
            IntPtr.Zero,
            wc.hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException(
                $"CreateWindowEx failed with Win32 error {Marshal.GetLastWin32Error()}.");

        // Explorer broadcasts this after it restarts; the tray icon must then be re-added.
        _taskbarCreatedMsg = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    private void AddIcon()
    {
        AddIcon("startup");
    }

    private void AddIcon(string caller)
    {
        if (_hIcon == IntPtr.Zero && File.Exists(_iconPath))
        {
            _hIcon = NativeMethods.LoadImage(
                IntPtr.Zero,
                _iconPath,
                NativeMethods.IMAGE_ICON,
                32,
                32,
                NativeMethods.LR_LOADFROMFILE);
        }

        var nid = NewNotifyIconData();
        nid.uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_SHOWTIP;
        nid.hIcon = _hIcon;
        nid.szTip = "VolumeLock";
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid))
            LogTrayError($"NIM_ADD [{caller}]");

        var version = NewNotifyIconData();
        version.uFlags = NativeMethods.NIF_MESSAGE;
        version.uTimeoutOrVersion = NativeMethods.NOTIFYICON_VERSION_4;
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_SETVERSION, ref version))
            LogTrayError($"NIM_SETVERSION [{caller}]");
        else
            LogTrayInfo($"NIM_SETVERSION ok (notification version 4) [{caller}]");
    }

    private void RemoveIcon()
    {
        var nid = NewNotifyIconData();
        if (!NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid))
            LogTrayError("NIM_DELETE");
    }

    private static void LogTrayError(string operation)
    {
        try
        {
            int error = Marshal.GetLastWin32Error();
            File.AppendAllText(
                Path.Combine(AppPaths.ExecutableDirectory, "VolumeLock.log"),
                $"[{DateTime.Now:O}] Shell_NotifyIcon({operation}) failed, Win32 error 0x{error:X8}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }

    private static void LogTrayInfo(string message)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(AppPaths.ExecutableDirectory, "VolumeLock.log"),
                $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // ignore logging failures
        }
    }

    private NativeMethods.NOTIFYICONDATA NewNotifyIconData() => new()
    {
        cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = IconId,
        uCallbackMessage = TrayCallbackMsg
    };

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == TrayCallbackMsg)
        {
            // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, HIWORD(lParam) = icon ID,
            // and wParam is an anchor coordinate (or undefined). Legacy versions put
            // the icon ID in wParam and the event in the low word of lParam.
            uint event_ = (uint)lParam.ToInt64() & 0xFFFF;
            uint iconId = ((uint)lParam.ToInt64() >> 16) & 0xFFFF;
            bool isOurIcon = (uint)wParam.ToInt64() == IconId || iconId == IconId;

            if (isOurIcon)
            {
                LogTrayInfo($"tray callback event 0x{event_:X}");
                switch (event_)
                {
                    case NativeMethods.WM_CONTEXTMENU:
                    case NativeMethods.WM_RBUTTONUP:
                        ShowContextMenu();
                        return IntPtr.Zero;

                    case NativeMethods.WM_RBUTTONDOWN:
                        // Legacy notification versions (0-2) deliver right-click as
                        // WM_RBUTTONDOWN/WM_RBUTTONUP. The window must be foreground
                        // before showing the menu, otherwise TrackPopupMenu won't display.
                        NativeMethods.SetForegroundWindow(_hwnd);
                        return IntPtr.Zero;

                    case NativeMethods.WM_LBUTTONUP:
                    case NativeMethods.WM_LBUTTONDBLCLK:
                    case NativeMethods.NIN_SELECT:
                        _window.ShowMainWindow();
                        return IntPtr.Zero;
                }
            }
        }
        else if (msg == _taskbarCreatedMsg)
        {
            // Explorer restarted and recreated the notification area; re-add the icon.
            if (_iconVisible)
                AddIcon("TaskbarCreated");
            return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        var settings = _settingsService.Settings;
        IntPtr menu = NativeMethods.CreatePopupMenu();

        string showHideLabel = _window.IsWindowVisible ? "Hide" : "Show";
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdShowHide, showHideLabel);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
        NativeMethods.AppendMenu(
            menu,
            NativeMethods.MF_STRING | (settings.RunAtStartup ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
            (IntPtr)CmdRunAtStartup,
            "Run at startup");
        NativeMethods.AppendMenu(
            menu,
            NativeMethods.MF_STRING | (settings.HideTrayIcon ? NativeMethods.MF_CHECKED : NativeMethods.MF_UNCHECKED),
            (IntPtr)CmdHideTrayIcon,
            "Hide tray icon");
        NativeMethods.AppendMenu(menu, NativeMethods.MF_SEPARATOR, IntPtr.Zero, null);
        NativeMethods.AppendMenu(menu, NativeMethods.MF_STRING, (IntPtr)CmdExit, "Exit");

        NativeMethods.GetCursorPos(out NativeMethods.POINT pt);
        NativeMethods.SetForegroundWindow(_hwnd);

        uint command = NativeMethods.TrackPopupMenu(
            menu,
            NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_NONOTIFY,
            pt.X,
            pt.Y,
            0,
            _hwnd,
            IntPtr.Zero);

        NativeMethods.DestroyMenu(menu);
        // Helps the menu dismiss when clicking elsewhere.
        NativeMethods.PostMessage(_hwnd, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);

        switch (command)
        {
            case CmdShowHide:
                if (_window.IsWindowVisible)
                    _window.HideToTray();
                else
                    _window.ShowMainWindow();
                break;

            case CmdRunAtStartup:
                bool runAtStartup = !settings.RunAtStartup;
                StartupService.SetEnabled(runAtStartup);
                settings.RunAtStartup = runAtStartup;
                _settingsService.Save();
                break;

            case CmdHideTrayIcon:
                bool hideTray = !settings.HideTrayIcon;
                settings.HideTrayIcon = hideTray;
                _settingsService.Save();

                if (hideTray)
                {
                    Hide();
                    // Make sure the window is reachable since the tray icon is gone.
                    _window.ShowMainWindow();
                }
                else
                {
                    Show();
                }
                break;

            case CmdExit:
                ExitRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}

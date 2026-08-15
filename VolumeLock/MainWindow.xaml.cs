using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using VolumeLock.Interop;
using VolumeLock.Services;

namespace VolumeLock;

public sealed partial class MainWindow : Window
{
    private readonly SettingsService _settingsService;
    private AudioLockService? _audioLock;
    private TrayIconService? _tray;
    private bool _initializing = true;
    private bool _exiting;
    private bool _syncingSystemSounds;
    private bool _syncingMicrophone;

    public MainWindow(SettingsService settingsService)
    {
        InitializeComponent();
        _settingsService = settingsService;

        Title = "VolumeLock";

        SetupTitleBar();
        PositionWindowBottomRight();

        string? appIconPath = IconHelper.GetIconPath();
        if (!string.IsNullOrEmpty(appIconPath))
            AppIconImage.Source = new BitmapImage(new Uri(appIconPath));

        var s = settingsService.Settings;
        SystemSoundsSlider.Value = s.SystemSoundsLevel;
        MicrophoneSlider.Value = s.MicrophoneLevel;
        SystemSoundsNumberBox.Value = s.SystemSoundsLevel;
        MicrophoneNumberBox.Value = s.MicrophoneLevel;
        RunAtStartupToggle.IsOn = s.RunAtStartup;
        HideTrayIconToggle.IsOn = s.HideTrayIcon;

        UpdateEnabledStates();
        _initializing = false;

        RootGrid.Loaded += (s, e) =>
        {
            PositionWindowBottomRight();
            UpdateDragRegion();
        };
        RootGrid.SizeChanged += (s, e) => UpdateDragRegion();

        AppWindow.Closing += OnAppWindowClosing;
    }

    public bool IsWindowVisible => AppWindow.IsVisible;

    private void SetupTitleBar()
    {
        try
        {
            var titleBar = AppWindow.TitleBar;
            titleBar.ExtendsContentIntoTitleBar = true;
            titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
            titleBar.ButtonBackgroundColor = Colors.Transparent;
            titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

            string? iconPath = IconHelper.GetIconPath();
            if (!string.IsNullOrEmpty(iconPath))
                AppWindow.SetIcon(iconPath);

            AppWindow.Resize(new Windows.Graphics.SizeInt32(560, 760));
        }
        catch
        {
            // Title bar customization is cosmetic; keep the default if anything fails.
        }
    }

    private void UpdateDragRegion()
    {
        try
        {
            double width = RootGrid.ActualWidth;
            double height = TitleBarArea.ActualHeight;
            if (width <= 0 || height <= 0)
                return;

            AppWindow.TitleBar.SetDragRectangles(new[]
            {
                new Windows.Graphics.RectInt32(0, 0, (int)Math.Round(width), (int)Math.Round(height))
            });
        }
        catch
        {
            // non-fatal
        }
    }

    private void PositionWindowBottomRight()
    {
        try
        {
            var displayArea = DisplayArea.Primary;
            var workArea = displayArea.WorkArea;

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            int width = AppWindow.Size.Width;
            int height = AppWindow.Size.Height;

            try
            {
                NativeMethods.GetWindowRect(hwnd, out var rect);
                int outerWidth = rect.Right - rect.Left;
                int outerHeight = rect.Bottom - rect.Top;
                if (outerWidth > 0 && outerHeight > 0)
                {
                    width = outerWidth;
                    height = outerHeight;
                }
            }
            catch
            {
            }

            double scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(hwnd) / 96.0);
            int spacing = (int)Math.Round(20 * scale);

            int x = Math.Max(workArea.X, workArea.X + workArea.Width - width - spacing);
            int y = Math.Max(workArea.Y, workArea.Y + workArea.Height - height - spacing);

            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }
        catch
        {
            // positioning is cosmetic; ignore failures
        }
    }

    public void Attach(AudioLockService audioLock)
    {
        _audioLock = audioLock;
        audioLock.StatusUpdated += OnStatusUpdated;
    }

    public void AttachTray(TrayIconService tray)
    {
        _tray = tray;
    }

    public void ShowMainWindow()
    {
        try
        {
            AppWindow.Show();
            AppWindow.MoveInZOrderAtTop();
            NativeMethods.SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        }
        catch
        {
            // fallback if AppWindow APIs are unavailable
            Activate();
        }
    }

    public void HideToTray()
    {
        AppWindow.Hide();
    }

    public void MarkExiting()
    {
        _exiting = true;
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting)
            return;

        // Closing the window hides it to the tray instead of quitting.
        args.Cancel = true;
        HideToTray();

        if (_settingsService.Settings.HideTrayIcon)
        {
            ShowMainWindow();
        }
        else
        {
            _tray?.ShowBalloon(
                "VolumeLock is still running",
                "Your volume levels are still locked in the background. Right-click the tray icon for options.");
        }
    }

    private void OnStatusUpdated(VolumeStatus status)
    {
        if (!string.IsNullOrEmpty(status.SystemSoundsDeviceName))
            SystemSoundsDeviceNameText.Text = status.SystemSoundsDeviceName;
        if (!string.IsNullOrEmpty(status.MicrophoneDeviceName))
            MicrophoneDeviceNameText.Text = status.MicrophoneDeviceName;

        var errors = new List<string>();
        if (status.SystemSoundsError is not null)
            errors.Add($"System Sounds: {status.SystemSoundsError}");
        if (status.MicrophoneError is not null)
            errors.Add($"Microphone: {status.MicrophoneError}");

        if (errors.Count > 0)
        {
            StatusInfoBar.Severity = InfoBarSeverity.Warning;
            StatusInfoBar.Message = string.Join("   ", errors);
            StatusInfoBar.IsOpen = true;
        }
        else
        {
            StatusInfoBar.IsOpen = false;
        }
    }

    private const string LockGlyph = "\uE72E";
    private const string UnlockGlyph = "\uE785";

    private void UpdateEnabledStates()
    {
        bool systemLocked = _settingsService.Settings.SystemSoundsEnabled;
        SystemSoundsLockIcon.Glyph = systemLocked ? UnlockGlyph : LockGlyph;
        ToolTipService.SetToolTip(SystemSoundsLockButton, systemLocked ? "Unlocked" : "Locked");
        AutomationProperties.SetName(SystemSoundsLockButton, systemLocked ? "System sounds unlocked" : "System sounds locked");
        SystemSoundsSlider.IsEnabled = systemLocked;
        SystemSoundsNumberBox.IsEnabled = systemLocked;

        bool micLocked = _settingsService.Settings.MicrophoneEnabled;
        MicrophoneLockIcon.Glyph = micLocked ? UnlockGlyph : LockGlyph;
        ToolTipService.SetToolTip(MicrophoneLockButton, micLocked ? "Unlocked" : "Locked");
        AutomationProperties.SetName(MicrophoneLockButton, micLocked ? "Microphone unlocked" : "Microphone locked");
        MicrophoneSlider.IsEnabled = micLocked;
        MicrophoneNumberBox.IsEnabled = micLocked;
    }

    private void SystemSoundsLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _settingsService.Settings.SystemSoundsEnabled = !_settingsService.Settings.SystemSoundsEnabled;
        _settingsService.Save();
        UpdateEnabledStates();
        _audioLock?.ApplyNow();
    }

    private void MicrophoneLockButton_Click(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _settingsService.Settings.MicrophoneEnabled = !_settingsService.Settings.MicrophoneEnabled;
        _settingsService.Save();
        UpdateEnabledStates();
        _audioLock?.ApplyNow();
    }

    private void SystemSoundsSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingSystemSounds)
            return; // this change came from the number box

        int value = (int)Math.Round(SystemSoundsSlider.Value);
        _syncingSystemSounds = true;
        SystemSoundsNumberBox.Value = value;
        _syncingSystemSounds = false;

        if (_initializing) return;

        _settingsService.Settings.SystemSoundsLevel = value;
        _settingsService.Save();
        _audioLock?.ApplyNow();
    }

    private void SystemSoundsNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
            return;

        int value = Math.Clamp((int)Math.Round(args.NewValue), 0, 100);
        if (_syncingSystemSounds)
            return;

        _syncingSystemSounds = true;
        SystemSoundsSlider.Value = value;
        _syncingSystemSounds = false;

        if (_initializing) return;

        _settingsService.Settings.SystemSoundsLevel = value;
        _settingsService.Save();
        _audioLock?.ApplyNow();
    }

    private void MicrophoneSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_syncingMicrophone)
            return; // this change came from the number box

        int value = (int)Math.Round(MicrophoneSlider.Value);
        _syncingMicrophone = true;
        MicrophoneNumberBox.Value = value;
        _syncingMicrophone = false;

        if (_initializing) return;

        _settingsService.Settings.MicrophoneLevel = value;
        _settingsService.Save();
        _audioLock?.ApplyNow();
    }

    private void MicrophoneNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue))
            return;

        int value = Math.Clamp((int)Math.Round(args.NewValue), 0, 100);
        if (_syncingMicrophone)
            return;

        _syncingMicrophone = true;
        MicrophoneSlider.Value = value;
        _syncingMicrophone = false;

        if (_initializing) return;

        _settingsService.Settings.MicrophoneLevel = value;
        _settingsService.Save();
        _audioLock?.ApplyNow();
    }

    private void RunAtStartupToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        StartupService.SetEnabled(RunAtStartupToggle.IsOn);
        _settingsService.Settings.RunAtStartup = RunAtStartupToggle.IsOn;
        _settingsService.Save();
    }

    private void HideTrayIconToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        bool hide = HideTrayIconToggle.IsOn;
        _settingsService.Settings.HideTrayIcon = hide;
        _settingsService.Save();

        if (hide)
        {
            _tray?.Hide();
            ShowMainWindow();
        }
        else
        {
            _tray?.Show();
        }
    }
}

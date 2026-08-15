using System.IO;
using Microsoft.UI.Xaml;
using VolumeLock.Services;

namespace VolumeLock;

public partial class App : Application
{
    private readonly SettingsService _settingsService = new();
    private MainWindow? _mainWindow;
    private AudioLockService? _audioLock;
    private TrayIconService? _tray;

    public App()
    {
        InitializeComponent();

        UnhandledException += (s, e) =>
        {
            try
            {
                File.AppendAllText(
                    Path.Combine(AppPaths.ExecutableDirectory, "VolumeLock.log"),
                    $"[{DateTime.Now:O}] {e.Exception}{Environment.NewLine}");
            }
            catch
            {
                // ignore logging failures
            }
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _settingsService.Load();

        _audioLock = new AudioLockService(_settingsService);

        _mainWindow = new MainWindow(_settingsService);
        _mainWindow.Attach(_audioLock);

        _tray = new TrayIconService(_mainWindow, _settingsService);
        _tray.ExitRequested += (s, e) => ExitApp();
        _mainWindow.AttachTray(_tray);

        if (_settingsService.Settings.HideTrayIcon)
            _tray.Hide();
        else
            _tray.Show();

        bool autostart = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        if (autostart)
        {
            // Started from the Run key at login: run quietly in the background (tray only).
            // The window can be reopened from the tray icon.
            _mainWindow.HideToTray();
        }
        else
        {
            _mainWindow.Activate();
        }

        _audioLock.Start();
    }

    private void ExitApp()
    {
        _audioLock?.Dispose();
        _tray?.Dispose();
        _mainWindow?.MarkExiting();
        Application.Current.Exit();
    }
}

using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using VolumeLock.Services;

namespace VolumeLock;

public partial class App : System.Windows.Application
{
    internal static bool IsLightTheme { get; private set; }

    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private MainWindow? _mainWindow;
    private bool _isExiting;
    private ThemeService? _themeService;
    private SettingsService? _settingsService;
    private AudioService? _audioService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        _settingsService = new SettingsService();
        _settingsService.HideTrayIconChanged += OnHideTrayIconChanged;

        _themeService = new ThemeService();
        _themeService.ThemeChanged += OnSystemThemeChanged;
        IsLightTheme = _themeService.IsLightTheme;

        ApplyThemeDictionary(IsLightTheme);

        _audioService = new AudioService(Dispatcher, _settingsService);
        _audioService.Start();

        if (!_settingsService.HideTrayIcon)
        {
            CreateTrayIcon();
        }
    }

    private void OnSystemThemeChanged(bool isLight)
    {
        IsLightTheme = isLight;
        ApplyThemeDictionary(isLight);

        if (_mainWindow is not null)
        {
            _mainWindow.ApplyTheme(isLight);
        }
    }

    private void ApplyThemeDictionary(bool isLight)
    {
        string newSource = isLight
            ? "Themes/Light.xaml"
            : "Themes/Dark.xaml";

        var newDict = new ResourceDictionary { Source = new Uri(newSource, UriKind.Relative) };

        foreach (var key in newDict.Keys)
        {
            Resources[key] = newDict[key];
        }
    }

    private void CreateTrayIcon()
    {
        using var stream = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/app.ico"))!.Stream;
        var icon = new System.Drawing.Icon(stream);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Visible = true,
            Text = "VolumeLock"
        };
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Open VolumeLock", null, (_, _) => ShowMainWindow());
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _trayIcon.ContextMenuStrip = menu;
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null)
        {
            _mainWindow = new MainWindow(_settingsService!, _audioService!);
            _mainWindow.Closing += MainWindow_Closing;
            _mainWindow.ApplyTheme(IsLightTheme);
        }

        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OnHideTrayIconChanged(bool hidden)
    {
        if (hidden)
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
                _trayIcon.Dispose();
                _trayIcon = null;
            }
        }
        else
        {
            if (_trayIcon is null)
            {
                CreateTrayIcon();
            }
        }
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_isExiting) return;

        e.Cancel = true;
        _mainWindow?.Hide();
    }

    private void ExitApplication()
    {
        _isExiting = true;

        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _audioService?.Dispose();

        if (_mainWindow?.DataContext is IDisposable disposable)
            disposable.Dispose();

        _themeService?.Dispose();
        _settingsService?.Dispose();

        Shutdown();
    }
}

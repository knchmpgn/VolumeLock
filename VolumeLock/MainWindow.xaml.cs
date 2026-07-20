using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using VolumeLock.Native;
using VolumeLock.Services;
using VolumeLock.ViewModels;

namespace VolumeLock;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private HwndSource? _hwndSource;
    private bool _isLightTheme;

    public MainWindow(SettingsService settings, AudioService audioService)
    {
        InitializeComponent();

        _viewModel = new MainViewModel(settings, audioService);
        DataContext = _viewModel;

        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        nint hwnd = new WindowInteropHelper(this).Handle;

        _hwndSource = HwndSource.FromHwnd(hwnd);
        if (_hwndSource is not null)
        {
            _hwndSource.AddHook(WndProc);
        }

        _isLightTheme = App.IsLightTheme;
        ApplyDwmSettings(hwnd, darkMode: !_isLightTheme);
    }

    public void ApplyTheme(bool isLight)
    {
        _isLightTheme = isLight;
        nint hwnd = new WindowInteropHelper(this).Handle;
        ApplyDwmSettings(hwnd, darkMode: !isLight);
    }

    private void ApplyDwmSettings(nint hwnd, bool darkMode)
    {
        if (_hwndSource is not null)
            _hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;

        NativeMethods.ExtendFrameIntoClientArea(hwnd);

        NativeMethods.ApplyImmersiveDarkMode(hwnd, darkMode);
        NativeMethods.ApplyRoundedCorners(hwnd);
        bool micaOk = NativeMethods.TryApplyMica(hwnd);

        Background = micaOk
            ? (System.Windows.Media.Brush)FindResource("MicaTransparentBrush")
            : (System.Windows.Media.Brush)FindResource("AppBackgroundBrush");
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_ACTIVATE = 0x0006;
        const int WM_DWMCOMPOSITIONCHANGED = 0x031E;
        const int WM_THEMECHANGED = 0x031A;
        const int WM_SETTINGCHANGE = 0x001A;

        if (msg == WM_ACTIVATE && wParam != 0)
        {
            ApplyDwmSettings(hwnd, !_isLightTheme);
        }
        else if (msg == WM_DWMCOMPOSITIONCHANGED || msg == WM_THEMECHANGED || msg == WM_SETTINGCHANGE)
        {
            ApplyDwmSettings(hwnd, !_isLightTheme);
        }

        return nint.Zero;
    }
}

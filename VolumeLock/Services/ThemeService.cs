using Microsoft.Win32;

namespace VolumeLock.Services;

internal sealed class ThemeService : IDisposable
{
    private const string RegistryKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string RegistryValueName = "AppsUseLightTheme";

    private bool _disposed;

    public event Action<bool>? ThemeChanged;

    public bool IsLightTheme { get; private set; }

    public ThemeService()
    {
        System.Windows.Forms.WindowsFormsSynchronizationContext.AutoInstall = true;

        IsLightTheme = ReadSystemTheme();
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _disposed = true;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.General || e.Category == UserPreferenceCategory.VisualStyle)
        {
            bool nowLight = ReadSystemTheme();
            if (nowLight != IsLightTheme)
            {
                IsLightTheme = nowLight;
                ThemeChanged?.Invoke(nowLight);
            }
        }
    }

    private static bool ReadSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKeyPath);
            if (key is not null && key.GetValue(RegistryValueName) is int value)
                return value == 1;
        }
        catch
        {
        }
        return false;
    }
}

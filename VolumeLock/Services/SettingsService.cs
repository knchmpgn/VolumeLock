using System.Text.Json;

namespace VolumeLock.Services;

public sealed class AppSettings
{
    public bool SystemSoundsEnabled { get; set; } = true;
    public int SystemSoundsLevel { get; set; } = 50;
    public bool MicrophoneEnabled { get; set; } = true;
    public int MicrophoneLevel { get; set; } = 80;
    public bool RunAtStartup { get; set; }
    public bool HideTrayIcon { get; set; }
    public int CheckIntervalSeconds { get; set; } = 5;
}

/// <summary>
/// Loads and saves settings as JSON next to the executable, keeping the app fully portable.
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsFilePath { get; }
    public AppSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        SettingsFilePath = Path.Combine(AppPaths.ExecutableDirectory, "VolumeLock.settings.json");
    }

    public void Load()
    {
        try
        {
            if (File.Exists(SettingsFilePath))
            {
                string json = File.ReadAllText(SettingsFilePath);
                AppSettings? loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    Settings = loaded;
                    Settings.SystemSoundsLevel = Math.Clamp(Settings.SystemSoundsLevel, 0, 100);
                    Settings.MicrophoneLevel = Math.Clamp(Settings.MicrophoneLevel, 0, 100);
                    Settings.CheckIntervalSeconds = Math.Clamp(Settings.CheckIntervalSeconds, 2, 300);
                }
            }
        }
        catch
        {
            // Corrupt or unreadable settings file: fall back to defaults.
        }
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(Settings, JsonOptions));
        }
        catch
        {
            // e.g. folder not writable - ignore, settings just won't persist.
        }
    }
}

using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace VolumeLock.Services;

public sealed class SettingsService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "VolumeLock", "settings.json");

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppName = "VolumeLock";

    private SettingsData _data;

    public event Action<bool>? StartAtBootChanged;
    public event Action<bool>? HideTrayIconChanged;

    public bool StartAtBoot
    {
        get => _data.StartAtBoot;
        set
        {
            if (_data.StartAtBoot == value) return;
            _data.StartAtBoot = value;
            ApplyStartAtBoot(value);
            Save();
            StartAtBootChanged?.Invoke(value);
        }
    }

    public bool HideTrayIcon
    {
        get => _data.HideTrayIcon;
        set
        {
            if (_data.HideTrayIcon == value) return;
            _data.HideTrayIcon = value;
            Save();
            HideTrayIconChanged?.Invoke(value);
        }
    }

    public SettingsService()
    {
        _data = Load();
        ApplyStartAtBoot(_data.StartAtBoot);
    }

    private static SettingsData Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<SettingsData>(json, JsonOptions) ?? new SettingsData();
            }
        }
        catch { }
        return new SettingsData();
    }

    private void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_data, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    private static void ApplyStartAtBoot(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key is null) return;

            if (enabled)
            {
                string exePath = Environment.ProcessPath ?? "";
                key.SetValue(AppName, $"\"{exePath}\"");
            }
            else
            {
                key.DeleteValue(AppName, false);
            }
        }
        catch { }
    }

    public Dictionary<string, double> LoadLockedItems()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<SettingsData>(json, JsonOptions);
                if (data?.LockedItems is not null) return new Dictionary<string, double>(data.LockedItems);
            }
        }
        catch { }
        return new Dictionary<string, double>();
    }

    public void SaveLockedItems(Dictionary<string, double> lockedItems)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (dir is not null) Directory.CreateDirectory(dir);

            SettingsData data;
            if (File.Exists(SettingsPath))
            {
                string existing = File.ReadAllText(SettingsPath);
                data = JsonSerializer.Deserialize<SettingsData>(existing, JsonOptions) ?? new SettingsData();
            }
            else
            {
                data = new SettingsData();
            }

            data.LockedItems = new Dictionary<string, double>(lockedItems);
            string json = JsonSerializer.Serialize(data, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public void Dispose() { }

    private sealed class SettingsData
    {
        public bool StartAtBoot { get; set; }
        public bool HideTrayIcon { get; set; }
        public Dictionary<string, double>? LockedItems { get; set; }
    }
}

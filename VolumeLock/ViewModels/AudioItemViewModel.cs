using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VolumeLock.Models;
using VolumeLock.Services;

namespace VolumeLock.ViewModels;

public sealed class AudioItemViewModel : ViewModelBase
{
    private readonly AudioService _service;

    public string Key { get; }
    public AudioEndpointKind Kind { get; }
    public string DisplayName { get; }
    public ImageSource? Icon { get; }
    public bool HasIcon => Icon is not null;

    /// <summary>Segoe Fluent Icons glyph shown when there's no extracted process icon
    /// (always the case for microphones, and for processes NAudio couldn't read - e.g.
    /// some protected system processes).</summary>
    public string FallbackGlyph => Kind == AudioEndpointKind.Microphone ? "\uE720" : "\uE767";

    private double _volume;
    /// <summary>Volume as a 0-100 percent, bound to both the slider and the number box.</summary>
    public double Volume
    {
        get => _volume;
        set => SetVolumeFromUi(value);
    }

    private string _volumeText = "0";
    /// <summary>Text-box mirror of <see cref="Volume"/>, kept in sync in both directions.</summary>
    public string VolumeText
    {
        get => _volumeText;
        set
        {
            if (SetField(ref _volumeText, value) &&
                double.TryParse(value, out var parsed))
            {
                SetVolumeFromUi(Math.Clamp(parsed, 0, 100), updateText: false);
            }
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (SetField(ref _isMuted, value))
            {
                if (Kind == AudioEndpointKind.Application) _service.SetApplicationMute(Key, value);
                else _service.SetMicrophoneMute(Key, value);
            }
        }
    }

    private bool _isLocked;
    public bool IsLocked
    {
        get => _isLocked;
        private set
        {
            if (SetField(ref _isLocked, value))
            {
                OnPropertyChanged(nameof(IsEditable));
                OnPropertyChanged(nameof(LockGlyph));
                OnPropertyChanged(nameof(LockTooltip));
            }
        }
    }

    /// <summary>Slider/textbox are disabled while locked - this is the UI-side half of the
    /// "cannot be changed by any means" requirement; <see cref="AudioService"/> enforces the
    /// system-side half regardless of what the UI does.</summary>
    public bool IsEditable => !IsLocked;

    public string LockGlyph => IsLocked ? "\uE72E" : "\uE785";        // Segoe Fluent Icons: Lock / Unlock
    public string LockTooltip => IsLocked ? "Locked - click to unlock" : "Click to lock at the current level";

    public RelayCommand ToggleLockCommand { get; }

    public AudioItemViewModel(AudioItemInfo info, AudioService service)
    {
        _service = service;
        Key = info.Key;
        Kind = info.Kind;
        DisplayName = info.DisplayName;
        Icon = TryDecodeIcon(info.IconPngBytes);
        _volume = Math.Round(info.VolumePercent);
        _volumeText = ((int)_volume).ToString();
        _isMuted = info.IsMuted;

        ToggleLockCommand = new RelayCommand(ToggleLock);

        if (info.IsLocked)
            IsLocked = true;
    }

    /// <summary>Called by MainViewModel when AudioService reports a change (from the system,
    /// another app, or our own lock enforcement snapping the value back).</summary>
    public void ApplySystemUpdate(double percent, bool muted)
    {
        double rounded = Math.Round(percent);
        SetField(ref _volume, rounded, nameof(Volume));
        _volumeText = ((int)rounded).ToString();
        OnPropertyChanged(nameof(VolumeText));
        SetField(ref _isMuted, muted, nameof(IsMuted));
    }

    private void SetVolumeFromUi(double value, bool updateText = true)
    {
        value = Math.Clamp(value, 0, 100);
        if (IsLocked) return; // defensive: UI should already be disabled

        SetField(ref _volume, value, nameof(Volume));
        if (updateText)
        {
            _volumeText = ((int)value).ToString();
            OnPropertyChanged(nameof(VolumeText));
        }

        if (Kind == AudioEndpointKind.Application) _service.SetApplicationVolume(Key, value);
        else _service.SetMicrophoneVolume(Key, value);
    }

    private void ToggleLock()
    {
        bool newLocked = !IsLocked;
        _service.SetLocked(Key, newLocked, _volume);
        IsLocked = newLocked;
    }

    private static ImageSource? TryDecodeIcon(byte[]? pngBytes)
    {
        if (pngBytes is null || pngBytes.Length == 0) return null;
        try
        {
            using var stream = new MemoryStream(pngBytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }
}

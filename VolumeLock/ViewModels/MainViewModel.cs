using System.Collections.ObjectModel;
using VolumeLock.Models;
using VolumeLock.Services;

namespace VolumeLock.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    public AudioService Service { get; }
    public SettingsService Settings { get; }

    public ObservableCollection<AudioItemViewModel> Applications { get; } = new();
    public ObservableCollection<AudioItemViewModel> Microphones { get; } = new();

    public bool ApplicationsEmpty => Applications.Count == 0;
    public bool MicrophonesEmpty => Microphones.Count == 0;

    public bool StartAtBoot
    {
        get => Settings.StartAtBoot;
        set => SetField(ref _startAtBoot, value, nameof(StartAtBoot), () => Settings.StartAtBoot = value);
    }
    private bool _startAtBoot;

    public bool HideTrayIcon
    {
        get => Settings.HideTrayIcon;
        set => SetField(ref _hideTrayIcon, value, nameof(HideTrayIcon), () => Settings.HideTrayIcon = value);
    }
    private bool _hideTrayIcon;

    public MainViewModel(SettingsService settings, AudioService audioService)
    {
        Settings = settings;
        _startAtBoot = Settings.StartAtBoot;
        _hideTrayIcon = Settings.HideTrayIcon;

        Service = audioService;

        Applications.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ApplicationsEmpty));
        Microphones.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MicrophonesEmpty));

        PopulateFromService();
    }

    private void PopulateFromService()
    {
        Service.ItemAdded += OnItemAdded;
        Service.ItemRemoved += OnItemRemoved;
        Service.VolumeUpdated += OnVolumeUpdated;

        foreach (var info in Service.GetCurrentItems())
            OnItemAdded(info);
    }

    private void OnItemAdded(AudioItemInfo info)
    {
        var collection = info.Kind == AudioEndpointKind.Application ? Applications : Microphones;
        if (collection.Any(i => i.Key == info.Key)) return;

        var vm = new AudioItemViewModel(info, Service);
        InsertSorted(collection, vm);
    }

    private void OnItemRemoved(string key, AudioEndpointKind kind)
    {
        var collection = kind == AudioEndpointKind.Application ? Applications : Microphones;
        var existing = collection.FirstOrDefault(i => i.Key == key);
        if (existing is not null) collection.Remove(existing);
    }

    private void OnVolumeUpdated(string key, double percent, bool muted)
    {
        var item = Applications.FirstOrDefault(i => i.Key == key)
                   ?? Microphones.FirstOrDefault(i => i.Key == key);
        item?.ApplySystemUpdate(percent, muted);
    }

    private static void InsertSorted(ObservableCollection<AudioItemViewModel> collection, AudioItemViewModel vm)
    {
        // "System Sounds" is always first, matching the real Volume Mixer - everything else
        // is alphabetical.
        if (vm.DisplayName == "System Sounds")
        {
            collection.Insert(0, vm);
            return;
        }

        int index = collection.Count > 0 && collection[0].DisplayName == "System Sounds" ? 1 : 0;
        while (index < collection.Count &&
               string.Compare(collection[index].DisplayName, vm.DisplayName, StringComparison.OrdinalIgnoreCase) < 0)
        {
            index++;
        }
        collection.Insert(index, vm);
    }

    public void Dispose()
    {
        Service.ItemAdded -= OnItemAdded;
        Service.ItemRemoved -= OnItemRemoved;
        Service.VolumeUpdated -= OnVolumeUpdated;
    }
}

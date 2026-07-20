using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace VolumeLock.ViewModels;

public abstract class ViewModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    protected bool SetField<T>(ref T field, T value, string name, Action onChanged)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        onChanged();
        OnPropertyChanged(name);
        return true;
    }
}

using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ForgeManager.Models;

public sealed class ActivePlayer : INotifyPropertyChanged
{
    private string _displayName = "Unknown player";
    private string _playerId = string.Empty;
    private string _address = "Not exposed by log";
    private DateTimeOffset _connectedAt = DateTimeOffset.Now;

    public string Key { get; set; } = string.Empty;

    public string DisplayName
    {
        get => _displayName;
        set => SetField(ref _displayName, value);
    }

    public string PlayerId
    {
        get => _playerId;
        set => SetField(ref _playerId, value);
    }

    public string Address
    {
        get => _address;
        set => SetField(ref _address, value);
    }

    public DateTimeOffset ConnectedAt
    {
        get => _connectedAt;
        set
        {
            if (SetField(ref _connectedAt, value))
                OnPropertyChanged(nameof(ConnectedFor));
        }
    }

    public string ConnectedFor
    {
        get
        {
            var elapsed = DateTimeOffset.Now - ConnectedAt;
            if (elapsed < TimeSpan.Zero)
                elapsed = TimeSpan.Zero;
            return elapsed.TotalDays >= 1
                ? elapsed.ToString(@"d\.hh\:mm\:ss")
                : elapsed.ToString(@"hh\:mm\:ss");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshElapsed() => OnPropertyChanged(nameof(ConnectedFor));

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

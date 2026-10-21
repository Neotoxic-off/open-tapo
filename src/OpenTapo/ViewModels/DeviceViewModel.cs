using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

public abstract partial class DeviceViewModel : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private string _subtitle;

    [ObservableProperty]
    private string _signal = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string _error = "";

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>True once the device state has been read; controls stay disabled until then.</summary>
    [ObservableProperty]
    private bool _isReady;

    /// <summary>Details are shown only for the row the user clicked, to keep the panel small.</summary>
    [ObservableProperty]
    private bool _isExpanded;

    protected DeviceViewModel(TapoDevice device, DeviceSessions sessions)
    {
        Device = device;
        Sessions = sessions;
        _name = device.Model;
        _subtitle = device.Ip;
    }

    public TapoDevice Device { get; }

    public bool HasError => Error.Length > 0;

    protected DeviceSessions Sessions { get; }

    /// <summary>True while device state is copied into properties, so change hooks don't send it back.</summary>
    protected bool IsApplying { get; private set; }

    [RelayCommand]
    public Task RefreshAsync() => RunAsync(action: null);

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>Runs an optional device command then reloads state; any failure becomes the card's error line.</summary>
    protected async Task RunAsync(Func<Task>? action)
    {
        IsBusy = true;
        try
        {
            if (action is not null)
            {
                await action();
            }

            await LoadAsync();
            Error = "";
            IsReady = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Error = "Unreachable on the network";
            IsReady = false;
        }
        catch (TapoException ex)
        {
            Error = ex.Message;
            IsReady = false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    protected abstract Task LoadAsync();

    protected void Apply(Action assign)
    {
        IsApplying = true;
        try
        {
            assign();
        }
        finally
        {
            IsApplying = false;
        }
    }
}

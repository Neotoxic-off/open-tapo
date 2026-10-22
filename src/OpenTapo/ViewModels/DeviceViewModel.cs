using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

public abstract partial class DeviceViewModel : ObservableObject, IRecipient<DeviceExpandedMessage>
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
        // Until the device answers, the address is the only way to tell two identical models apart.
        _name = $"{device.Model} · {device.Ip}";
        _subtitle = device.Ip;
        WeakReferenceMessenger.Default.Register(this);
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

    public void Receive(DeviceExpandedMessage message)
    {
        if (message.Value != this)
        {
            IsExpanded = false;
        }
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value)
        {
            WeakReferenceMessenger.Default.Send(new DeviceExpandedMessage(this));
        }
    }

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
            Error = "Offline or asleep";
            IsReady = false;
        }
        catch (TapoException ex)
        {
            Error = ex.Message;
            IsReady = false;
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            // A field of an unexpected type in an otherwise valid reply.
            Error = "Unexpected reply from the device.";
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

using System.Collections.ObjectModel;
using System.Net;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private static readonly TimeSpan DiscoveryListenTime = TimeSpan.FromSeconds(2.5);

    private readonly CredentialStore _credentials;
    private readonly ManualDeviceStore _manualDevices;
    private readonly DeviceSessions _sessions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    private bool _isSignedIn;

    [ObservableProperty]
    private string _email;

    [ObservableProperty]
    private string _signInError = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddDeviceCommand))]
    private string _manualIp = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private bool _isScanning;

    [ObservableProperty]
    private string _status = "";

    /// <summary>Account row and add-by-IP row stay hidden until their header button is toggled.</summary>
    [ObservableProperty]
    private bool _isAccountOpen;

    [ObservableProperty]
    private bool _isAddOpen;

    public MainViewModel(CredentialStore credentials, ManualDeviceStore manualDevices)
    {
        _credentials = credentials;
        _manualDevices = manualDevices;
        _sessions = new DeviceSessions(credentials);
        TapoAccount? account = credentials.Load();
        _isSignedIn = account is not null;
        _email = account?.Email ?? "";
        Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public bool IsSignedOut => !IsSignedIn;

    public bool IsEmpty => !IsScanning && Devices.Count == 0;

    /// <summary>Pushed by the view from its PasswordBox, which deliberately isn't bindable.</summary>
    public string Password { private get; set; } = "";

    /// <summary>Discovery needs no account; the account only unlocks reading and controlling devices.</summary>
    public Task InitializeAsync() => ScanAsync();

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || Password.Length == 0)
        {
            SignInError = "Email and password are required.";
            return;
        }

        TapoAccount account = new(Email.Trim(), Password);
        SignInError = "";
        Status = "Checking your credentials…";
        AccountCheck check = await AccountVerifier.VerifyAsync(account, Devices.Select(device => device.Device));
        if (check == AccountCheck.Rejected)
        {
            SignInError = "Wrong email or password.";
            Status = "";
            return;
        }

        _credentials.Save(account);
        Password = "";
        IsSignedIn = true;
        await RefreshAsync();
        if (check == AccountCheck.NoDeviceReachable)
        {
            Status = "Signed in, but no device could confirm the password yet.";
        }
    }

    [RelayCommand]
    private async Task SignOutAsync()
    {
        _credentials.Clear();
        SignInError = "";
        IsAccountOpen = false;
        IsSignedIn = false;
        await RefreshAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        IsScanning = true;
        Status = "Scanning for devices…";
        try
        {
            IReadOnlyList<TapoDevice> discovered = await TapoDiscovery.ScanAsync(DiscoveryListenTime);
            foreach (TapoDevice device in discovered.Concat(_manualDevices.Load()))
            {
                AddIfMissing(device);
            }
        }
        finally
        {
            IsScanning = false;
        }

        await RefreshAsync();
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await Task.WhenAll(Devices.Select(device => device.RefreshAsync()));
        Status = $"{Devices.Count} device{(Devices.Count > 1 ? "s" : "")} · updated at {DateTime.Now:HH:mm}";
    }

    [RelayCommand(CanExecute = nameof(CanAddDevice))]
    private async Task AddDeviceAsync()
    {
        string ip = IPAddress.Parse(ManualIp.Trim()).ToString();
        Status = $"Connecting to {ip}…";
        TapoDeviceKind? kind = await TapoDiscovery.IdentifyAsync(ip);
        if (kind is not TapoDeviceKind found)
        {
            Status = $"{ip} is not responding (same network? Wi-Fi client isolation?)";
            return;
        }

        TapoDevice device = new(ip, "", found == TapoDeviceKind.Camera ? "Tapo camera" : "Tapo light", found);
        _manualDevices.Add(device);
        ManualIp = "";
        if (AddIfMissing(device) is DeviceViewModel added)
        {
            await added.RefreshAsync();
        }

        Status = $"{Devices.Count} devices";
    }

    private bool CanAddDevice() => TapoDevice.IsValidAddress(ManualIp);

    private DeviceViewModel? AddIfMissing(TapoDevice device)
    {
        if (Devices.Any(existing => existing.Device.Ip == device.Ip))
        {
            return null;
        }

        DeviceViewModel viewModel = device.Kind == TapoDeviceKind.Camera
            ? new CameraViewModel(device, _sessions)
            : new LightViewModel(device, _sessions);
        Devices.Add(viewModel);
        return viewModel;
    }
}

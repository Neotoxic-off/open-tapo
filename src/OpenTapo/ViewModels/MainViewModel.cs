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
    private readonly KnownDeviceStore _knownDevices;
    private readonly DeviceSessions _sessions;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedOut))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
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

    public MainViewModel(CredentialStore credentials, KnownDeviceStore knownDevices)
    {
        _credentials = credentials;
        _knownDevices = knownDevices;
        _sessions = new DeviceSessions(credentials);
        TapoAccount? account = credentials.Load();
        _isSignedIn = account is not null;
        _email = account?.Email ?? "";
        Devices.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];

    public bool IsSignedOut => !IsSignedIn;

    public bool IsEmpty => IsSignedIn && !IsScanning && Devices.Count == 0;

    /// <summary>Pushed by the view from its PasswordBox, which deliberately isn't bindable.</summary>
    public string Password { private get; set; } = "";

    /// <summary>First run shows only the sign-in setup; scanning starts once an account is stored.</summary>
    public Task InitializeAsync() => IsSignedIn ? ScanAsync() : Task.CompletedTask;

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
        IReadOnlyList<TapoDevice> devices = await DiscoverAsync();
        Status = "Checking your credentials…";
        AccountCheck check = await AccountVerifier.VerifyAsync(account, devices);
        if (check == AccountCheck.Rejected)
        {
            SignInError = "Wrong email or password.";
            Status = "";
            return;
        }

        _credentials.Save(account);
        Password = "";
        IsSignedIn = true;
        Show(devices);
        await RefreshAsync();
        if (check == AccountCheck.NoDeviceReachable)
        {
            Status = "Signed in, but no device could confirm the password yet.";
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        _credentials.Clear();
        Devices.Clear();
        SignInError = "";
        Status = "";
        IsAccountOpen = false;
        IsAddOpen = false;
        IsSignedIn = false;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        Show(await DiscoverAsync());
        await RefreshAsync();
    }

    /// <summary>Scans the network and returns every known device, newly found ones included.</summary>
    private async Task<IReadOnlyList<TapoDevice>> DiscoverAsync()
    {
        IsScanning = true;
        Status = "Scanning for devices…";
        try
        {
            return _knownDevices.Remember(await TapoDiscovery.ScanAsync(DiscoveryListenTime));
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void Show(IEnumerable<TapoDevice> devices)
    {
        foreach (TapoDevice device in devices)
        {
            AddIfMissing(device);
        }
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
        if (await TapoDiscovery.IdentifyAsync(ip) is not TapoDevice device)
        {
            Status = $"{ip} is not responding (same network? Wi-Fi client isolation?)";
            return;
        }

        _knownDevices.Remember([device]);
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

        DeviceViewModel viewModel = device.Kind switch
        {
            TapoDeviceKind.Camera => new CameraViewModel(device, _sessions),
            TapoDeviceKind.Vacuum => new VacuumViewModel(device, _sessions),
            _ => new SmartDeviceViewModel(device, _sessions),
        };
        Devices.Add(viewModel);
        return viewModel;
    }
}

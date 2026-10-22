using System.Text.Json.Nodes;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;
using OpenTapo.ViewModels;

namespace OpenTapo.Tests.Integration;

/// <summary>The whole app minus the window: sign-in, discovery, refresh and add-by-IP against fake devices.</summary>
public sealed class MainViewModelTests : IsolatedTest
{
    [Fact]
    public async Task First_run_waits_for_sign_in()
    {
        MainViewModel viewModel = New();

        await viewModel.InitializeAsync();

        Assert.True(viewModel.IsSignedOut);
        Assert.False(viewModel.IsEmpty);
        Assert.Equal("", viewModel.Email);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData(" ", "secret")]
    [InlineData(Accounts.Email, "")]
    public async Task Sign_in_needs_email_and_password(string email, string password)
    {
        MainViewModel viewModel = New();
        viewModel.Email = email;
        viewModel.Password = password;

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Email and password are required.", viewModel.SignInError);
    }

    [Fact]
    public async Task Wrong_password_is_reported_and_not_stored()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        TapoDiscovery.Hosts = () => [camera.Ip];
        MainViewModel viewModel = New();
        viewModel.Email = Accounts.Email;
        viewModel.Password = "wrong";

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.Equal("Wrong email or password.", viewModel.SignInError);
        Assert.Equal("", viewModel.Status);
        Assert.False(viewModel.IsSignedIn);
        Assert.Null(new CredentialStore().Load());
    }

    [Fact]
    public async Task Sign_in_stores_the_account_and_shows_devices()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        await using FakeCamera camera = await FakeCamera.StartAsync();
        plug.DiscoveryReply = new JsonObject { ["device_type"] = "SMART.TAPOPLUG", ["device_model"] = "P110" };
        TapoDiscovery.Hosts = () => [plug.Ip, camera.Ip];
        MainViewModel viewModel = New();
        viewModel.Email = $"  {Accounts.Email} ";
        viewModel.Password = Accounts.Password;

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSignedIn);
        Assert.Equal(Accounts.Valid, new CredentialStore().Load());
        Assert.Collection(viewModel.Devices, device => Assert.IsType<SmartDeviceViewModel>(device), device => Assert.IsType<CameraViewModel>(device));
        Assert.All(viewModel.Devices, device => Assert.True(device.IsReady));
        Assert.StartsWith("2 devices · updated at", viewModel.Status);
        Assert.False(viewModel.IsEmpty);
    }

    [Fact]
    public async Task Sign_in_without_reachable_devices_says_so()
    {
        MainViewModel viewModel = New();
        viewModel.Email = Accounts.Email;
        viewModel.Password = Accounts.Password;

        await viewModel.SignInCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsSignedIn);
        Assert.True(viewModel.IsEmpty);
        Assert.Equal("Signed in, but no device could confirm the password yet.", viewModel.Status);
    }

    [Fact]
    public async Task Signed_in_start_scans_and_lists_remembered_devices()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync();
        SignIn();
        new KnownDeviceStore().Remember([new TapoDevice(plug.Ip, "", "P110", TapoDeviceKind.Smart)]);
        MainViewModel viewModel = New();

        await viewModel.InitializeAsync();

        Assert.Equal(Accounts.Email, viewModel.Email);
        Assert.Single(viewModel.Devices);
        Assert.StartsWith("1 device · updated at", viewModel.Status);

        await viewModel.ScanCommand.ExecuteAsync(null);
        await viewModel.RefreshCommand.ExecuteAsync(null);
        Assert.Single(viewModel.Devices);
    }

    [Fact]
    public void Sign_out_clears_everything()
    {
        SignIn();
        MainViewModel viewModel = New();
        viewModel.IsAccountOpen = true;
        viewModel.IsAddOpen = true;

        viewModel.SignOutCommand.Execute(null);

        Assert.True(viewModel.IsSignedOut);
        Assert.False(viewModel.IsAccountOpen || viewModel.IsAddOpen);
        Assert.Null(new CredentialStore().Load());
    }

    [Fact]
    public async Task Devices_can_be_added_by_ip()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync();
        SignIn();
        MainViewModel viewModel = New();

        viewModel.ManualIp = "not an ip";
        Assert.False(viewModel.AddDeviceCommand.CanExecute(null));

        viewModel.ManualIp = $" {camera.Ip} ";
        Assert.True(viewModel.AddDeviceCommand.CanExecute(null));
        await viewModel.AddDeviceCommand.ExecuteAsync(null);
        viewModel.ManualIp = plug.Ip;
        await viewModel.AddDeviceCommand.ExecuteAsync(null);
        viewModel.ManualIp = plug.Ip;
        await viewModel.AddDeviceCommand.ExecuteAsync(null);

        Assert.Equal("", viewModel.ManualIp);
        Assert.Equal("2 devices", viewModel.Status);
        Assert.Equal([TapoDeviceKind.Camera, TapoDeviceKind.Smart], viewModel.Devices.Select(device => device.Device.Kind));
        Assert.Equal(2, new KnownDeviceStore().Load().Count);
    }

    [Fact]
    public async Task Silent_ip_is_reported()
    {
        SignIn();
        MainViewModel viewModel = New();
        viewModel.ManualIp = Loopback.DeadIp;

        await viewModel.AddDeviceCommand.ExecuteAsync(null);

        Assert.StartsWith($"{Loopback.DeadIp} is not responding", viewModel.Status);
        Assert.Empty(viewModel.Devices);
    }

    private static MainViewModel New() => new(new CredentialStore(), new KnownDeviceStore());
}

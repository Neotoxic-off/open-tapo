using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;
using OpenTapo.ViewModels;

namespace OpenTapo.Tests.Integration;

/// <summary>Every device family end to end: found by discovery, given the right transport, shown by the right view.</summary>
public sealed class DeviceFamilyTests : IsolatedTest
{
    [Theory]
    [InlineData("SMART.TAPOPLUG", "KLAP", false, TapoDeviceKind.Smart)]
    [InlineData("SMART.TAPOBULB", "AES", false, TapoDeviceKind.Smart)]
    [InlineData("SMART.KASASWITCH", "KLAP", false, TapoDeviceKind.Smart)]
    [InlineData("SMART.TAPOHUB", "KLAP", false, TapoDeviceKind.Smart)]
    [InlineData("SMART.TAPOCHIME", "KLAP", false, TapoDeviceKind.Smart)]
    [InlineData("SMART.TAPOHUB", "AES", true, TapoDeviceKind.Camera)]
    [InlineData("SMART.IPCAMERA", "AES", true, TapoDeviceKind.Camera)]
    [InlineData("SMART.TAPODOORBELL", "AES", true, TapoDeviceKind.Camera)]
    [InlineData("SMART.TAPOROBOVAC", "AES", true, TapoDeviceKind.Vacuum)]
    [InlineData("IOT.SMARTPLUGSWITCH", "XOR", false, TapoDeviceKind.Kasa)]
    [InlineData("IOT.SMARTBULB", "KLAP", false, TapoDeviceKind.Kasa)]
    [InlineData("IOT.IPCAMERA", "XOR", true, TapoDeviceKind.Kasa)]
    public void Every_device_type_gets_its_view(string type, string encryption, bool https, TapoDeviceKind kind)
    {
        Assert.Equal(kind, TapoDevice.KindOf(type, encryption, https));
    }

    [Theory]
    [InlineData(TapoDeviceKind.Smart, "", false, 0, "", typeof(KlapClient))]
    [InlineData(TapoDeviceKind.Smart, "KLAP", true, 2, "", typeof(KlapClient))]
    [InlineData(TapoDeviceKind.Smart, "AES", false, 2, "", typeof(AesClient))]
    [InlineData(TapoDeviceKind.Vacuum, "AES", true, 0, "", typeof(SslClient))]
    [InlineData(TapoDeviceKind.Kasa, "XOR", false, 0, "IOT.SMARTPLUGSWITCH", typeof(KasaClient))]
    [InlineData(TapoDeviceKind.Kasa, "KLAP", false, 2, "IOT.SMARTPLUGSWITCH", typeof(KasaClient))]
    [InlineData(TapoDeviceKind.Kasa, "XOR", true, 0, "IOT.IPCAMERA", typeof(KasaClient))]
    public void Every_transport_gets_its_client(TapoDeviceKind kind, string encryption, bool https, int loginVersion, string type, Type client)
    {
        TapoDevice device = new("10.0.0.2", "", "Model", kind, type, encryption, https, loginVersion);

        Assert.IsType(client, DeviceSessions.CreateSmart(device, Accounts.Valid));
    }

    [Fact]
    public async Task Discovery_reads_the_announced_transport_and_finds_legacy_kasa_devices()
    {
        await using FakeKasaDevice kasa = FakeKasaDevice.Start(answerDiscovery: true);
        await using FakeKlapDevice hub = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        hub.DiscoveryReply = new JsonObject
        {
            ["device_type"] = "SMART.TAPOHUB",
            ["device_model"] = "H200",
            ["mgt_encrypt_schm"] = new JsonObject { ["is_support_https"] = true, ["encrypt_type"] = "AES", ["lv"] = 2 },
        };
        TapoDiscovery.Hosts = () => [kasa.Ip, hub.Ip];

        IReadOnlyList<TapoDevice> found = await TapoDiscovery.ScanAsync(TimeSpan.FromSeconds(1));

        Assert.Contains(new TapoDevice(kasa.Ip, "50:C7:BF:00:00:01", "HS110(EU)", TapoDeviceKind.Kasa, "IOT.SMARTPLUGSWITCH", "XOR"), found);
        Assert.Contains(new TapoDevice(hub.Ip, "", "H200", TapoDeviceKind.Camera, "SMART.TAPOHUB", "AES", true, 2), found);
    }

    [Fact]
    public async Task A_kasa_device_added_by_ip_is_identified_by_its_discovery_reply()
    {
        await using FakeKasaDevice kasa = FakeKasaDevice.Start(answerDiscovery: true);
        kasa.Iot.SysInfo.Remove("mac");
        kasa.Iot.SysInfo["mic_mac"] = "AA";
        kasa.Iot.SysInfo.Remove("type");
        kasa.Iot.SysInfo["mic_type"] = "IOT.SMARTBULB";

        Assert.Equal(new TapoDevice(kasa.Ip, "AA", "HS110(EU)", TapoDeviceKind.Kasa, "IOT.SMARTBULB", "XOR"), await TapoDiscovery.IdentifyAsync(kasa.Ip));
    }

    [Theory]
    [InlineData("""{"system":{"get_sysinfo":{"type":"SMART.TAPOPLUG"}}}""")]
    [InlineData("""{"system":{"get_sysinfo":{"model":"HS100"}}}""")]
    [InlineData("""{"system":{}}""")]
    [InlineData("""null""")]
    [InlineData("""{not json""")]
    public void Unusable_legacy_replies_are_ignored(string reply)
    {
        UdpReceiveResult packet = new(Xor.Encrypt(Encoding.UTF8.GetBytes(reply)), new IPEndPoint(IPAddress.Parse("127.0.0.8"), DevicePorts.Xor));

        Assert.False(TapoDiscovery.TryParseReply(packet, out TapoDevice? device));
        Assert.Null(device);
    }

    [Fact]
    public async Task Account_is_checked_on_an_aes_device_and_never_on_open_kasa_ones()
    {
        await using FakeAesDevice plug = await FakeAesDevice.StartAsync();
        await using FakeKasaDevice kasa = FakeKasaDevice.Start();
        TapoDevice open = new(kasa.Ip, "", "HS100", TapoDeviceKind.Kasa, "IOT.SMARTPLUGSWITCH", "XOR");

        Assert.Equal(AccountCheck.NoDeviceReachable, await AccountVerifier.VerifyAsync(Accounts.Valid, [open]));
        Assert.Equal(AccountCheck.Valid, await AccountVerifier.VerifyAsync(Accounts.Valid, [open, new TapoDevice(plug.Ip, "", "P100", TapoDeviceKind.Smart, "SMART.TAPOPLUG", "AES")]));
        Assert.Equal(AccountCheck.Rejected, await AccountVerifier.VerifyAsync(Accounts.Wrong, [new TapoDevice(plug.Ip, "", "P100", TapoDeviceKind.Smart, "SMART.TAPOPLUG", "AES")]));
    }

    [Fact]
    public async Task Kasa_plug_is_shown_and_switched_through_the_smart_view()
    {
        await using FakeKasaDevice plug = FakeKasaDevice.Start();
        plug.Iot.Realtime = new JsonObject { ["power_mw"] = 3000 };
        SignIn();
        SmartDeviceViewModel viewModel = new(new TapoDevice(plug.Ip, "", "HS110", TapoDeviceKind.Kasa, "IOT.SMARTPLUGSWITCH", "XOR"), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();

        viewModel.IsOn = true;
        await EventuallyAsync(() => (int?)plug.Iot.SysInfo["relay_state"] == 1 && !viewModel.IsBusy);

        Assert.Equal("Desk", viewModel.Name);
        Assert.Equal("On · 3 W", viewModel.State);
    }

    [Fact]
    public async Task Power_strip_outlets_are_switchable()
    {
        await using FakeKlapDevice strip = await FakeKlapDevice.StartAsync();
        strip.Info = new JsonObject { ["model"] = "P300", ["type"] = "SMART.TAPOPLUG" };
        strip.Results["get_child_device_list"] = JsonNode.Parse("""
            {"child_device_list":[
              {"device_id":"01","nickname":"TGFtcA==","device_on":false},
              {"device_id":"02","model":"P300","device_on":true},
              {"device_on":true},
              {"model":"T310","current_temp":21}]}
            """);
        SignIn();
        SmartDeviceViewModel viewModel = new(new TapoDevice(strip.Ip, "", "P300", TapoDeviceKind.Smart), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();

        Assert.Equal(["Lamp", "P300", "Outlet"], viewModel.Outlets.Select(outlet => outlet.Label));
        Assert.Single(viewModel.Children);
        Assert.Equal("4 connected devices", viewModel.State);

        viewModel.Outlets[0].IsOn = true;
        await EventuallyAsync(() => strip.Calls.Any(call => call.Method == "control_child") && !viewModel.IsBusy);

        JsonNode request = strip.Calls.Single(call => call.Method == "control_child").Params!;
        Assert.Equal("01", (string?)request["device_id"]);
        Assert.Equal(true, (bool?)request["requestData"]!["params"]!["requests"]![0]!["params"]!["device_on"]);
    }

    [Fact]
    public async Task Fan_speed_is_shown_and_set()
    {
        await using FakeKlapDevice fan = await FakeKlapDevice.StartAsync();
        fan.Info = new JsonObject { ["device_on"] = true, ["fan_speed_level"] = 2, ["model"] = "S500D", ["type"] = "SMART.TAPOSWITCH" };
        SignIn();
        SmartDeviceViewModel viewModel = new(new TapoDevice(fan.Ip, "", "S500D", TapoDeviceKind.Smart), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();

        Assert.True(viewModel.SupportsFanSpeed);
        Assert.Equal("Speed 2", viewModel.State);

        viewModel.FanSpeed = 4;
        await EventuallyAsync(() => (int?)fan.Info["fan_speed_level"] == 4 && !viewModel.IsBusy);
        Assert.Equal("Speed 4", viewModel.State);
    }

    [Fact]
    public async Task Vacuum_is_shown_and_driven()
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();
        vacuum.Results["getVacStatus"] = new JsonObject { ["status"] = 7 };
        vacuum.Results["getBatteryInfo"] = new JsonObject { ["battery_percentage"] = 80 };
        VacuumViewModel viewModel = await LoadVacuumAsync(vacuum);

        Assert.Equal("Robo", viewModel.Name);
        Assert.Equal($"RV30 Max · {vacuum.Ip}", viewModel.Subtitle);
        Assert.Equal("Paused", viewModel.State);
        Assert.Equal("80 %", viewModel.Battery);

        await viewModel.StartCommand.ExecuteAsync(null);
        vacuum.Results["getVacStatus"] = new JsonObject { ["status"] = 6 };
        await viewModel.RefreshAsync();
        await viewModel.StartCommand.ExecuteAsync(null);
        await viewModel.PauseCommand.ExecuteAsync(null);
        await viewModel.DockCommand.ExecuteAsync(null);

        Assert.Equal(["setRobotPause", "setSwitchClean", "setRobotPause", "setSwitchCharge"], vacuum.Calls.Select(call => call.Method));
        Assert.Equal(false, (bool?)vacuum.Calls.First().Params!["pause"]);
    }

    [Fact]
    public async Task Vacuum_without_state_is_an_error()
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();
        vacuum.Raw = context => context.Response.WriteAsync("""{"error_code":0,"result":{"token":"t","responses":[]}}""");

        VacuumViewModel viewModel = await LoadVacuumAsync(vacuum);

        Assert.Equal("The vacuum did not report its state.", viewModel.Error);
        Assert.Equal("", viewModel.Battery);
    }

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(1, "Cleaning")]
    [InlineData(2, "Mapping")]
    [InlineData(4, "Returning to dock")]
    [InlineData(5, "Charging")]
    [InlineData(6, "Charged")]
    [InlineData(7, "Paused")]
    [InlineData(8, "Off the dock")]
    [InlineData(100, "Needs attention")]
    [InlineData(-1, "")]
    public void Vacuum_status_is_named(int status, string text)
    {
        Assert.Equal(text, VacuumViewModel.StatusText(status));
    }

    [Fact]
    public async Task Camera_hub_lists_its_children_without_a_privacy_switch()
    {
        await using FakeCamera hub = await FakeCamera.StartAsync();
        hub.Results["getDeviceInfo"] = JsonNode.Parse("""{"device_info":{"basic_info":{"device_alias":"Hub","device_model":"H200"}}}""");
        hub.Results["getChildDeviceList"] = JsonNode.Parse("""{"child_device_list":[{"model":"T100","detected":true},{"model":"C410"}],"start_index":0,"sum":2}""");
        SignIn();
        CameraViewModel viewModel = new(new TapoDevice(hub.Ip, "", "H200", TapoDeviceKind.Camera, "SMART.TAPOHUB", "AES", true), new DeviceSessions(new CredentialStore()));

        await viewModel.RefreshAsync();

        Assert.False(viewModel.SupportsPrivacy);
        Assert.Equal(["T100", "C410"], viewModel.Children.Select(child => child.Name));
    }

    [Fact]
    public async Task Remembered_vacuum_gets_the_vacuum_view()
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();
        SignIn();
        new KnownDeviceStore().Remember([new TapoDevice(vacuum.Ip, "", "RV30", TapoDeviceKind.Vacuum, "SMART.TAPOROBOVAC", "AES", true)]);
        MainViewModel viewModel = new(new CredentialStore(), new KnownDeviceStore());

        await viewModel.InitializeAsync();

        Assert.IsType<VacuumViewModel>(Assert.Single(viewModel.Devices));
    }

    private static async Task<VacuumViewModel> LoadVacuumAsync(FakeVacuum vacuum)
    {
        SignIn();
        VacuumViewModel viewModel = new(new TapoDevice(vacuum.Ip, "", "RV30", TapoDeviceKind.Vacuum, "SMART.TAPOROBOVAC", "AES", true), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();
        return viewModel;
    }
}

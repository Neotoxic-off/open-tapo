using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;
using OpenTapo.ViewModels;

namespace OpenTapo.Tests.Integration;

public sealed class SmartDeviceViewModelTests : IsolatedTest
{
    private static readonly Color Off = Color.FromRgb(0x6B, 0x6B, 0x75);

    [Fact]
    public async Task White_bulb_state_is_shown()
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync();
        bulb.Info = new JsonObject
        {
            ["device_on"] = true,
            ["brightness"] = 60,
            ["color_temp"] = 3000,
            ["color_temp_range"] = new JsonArray(2700, 6000),
            ["hue"] = 0,
            ["saturation"] = 100,
            ["nickname"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("Desk")),
            ["model"] = "L530E",
            ["type"] = "SMART.TAPOBULB",
            ["rssi"] = -50,
        };
        bulb.Results["get_device_usage"] = new JsonObject { ["time_usage"] = new JsonObject { ["today"] = 30, ["past7"] = 125, ["past30"] = 600 } };
        bulb.Results["get_on_off_gradually_info"] = new JsonObject { ["enable"] = true };

        SmartDeviceViewModel viewModel = await LoadAsync(bulb);

        Assert.True(viewModel.IsReady);
        Assert.Equal("Desk", viewModel.Name);
        Assert.Equal($"L530E · {bulb.Ip}", viewModel.Subtitle);
        Assert.Equal("60 % · 3000 K", viewModel.State);
        Assert.Equal(DeviceGlyphs.Bulb, viewModel.Glyph);
        Assert.True(viewModel.SupportsPower && viewModel.SupportsBrightness && viewModel.SupportsWhites && viewModel.SupportsColors);
        Assert.Equal((2700, 6000), (viewModel.MinColorTemperature, viewModel.MaxColorTemperature));
        Assert.Equal(3000, viewModel.ColorTemperature);
        Assert.Equal(Color.FromRgb(255, 187, 110), viewModel.Glow);
        Assert.True(viewModel.Fade.IsSupported && viewModel.Fade.IsOn);
        Assert.True(viewModel.HasUsage);
        Assert.Equal(("30 min", "2 h 05", "10 h 00"), (viewModel.UsageToday, viewModel.UsageWeek, viewModel.UsageMonth));
        Assert.Equal("Wi-Fi excellent · -50 dBm", viewModel.Signal);
        Assert.Equal("", viewModel.Energy);
        Assert.Empty(viewModel.Children);
        Assert.Equal(3, viewModel.Whites.Count);
        Assert.Equal(5, viewModel.Colors.Count);
    }

    [Theory]
    [InlineData(0, 255, 0, 0)]
    [InlineData(60, 255, 255, 0)]
    [InlineData(120, 0, 255, 0)]
    [InlineData(180, 0, 255, 255)]
    [InlineData(240, 0, 0, 255)]
    [InlineData(300, 255, 0, 255)]
    public async Task Colour_bulb_glows_in_its_hue(int hue, byte red, byte green, byte blue)
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync();
        bulb.Info = new JsonObject { ["device_on"] = true, ["brightness"] = 80, ["color_temp"] = 0, ["hue"] = hue, ["saturation"] = 100 };

        SmartDeviceViewModel viewModel = await LoadAsync(bulb);

        Assert.Equal("80 % · color", viewModel.State);
        Assert.Equal(Color.FromRgb(red, green, blue), viewModel.Glow);
        Assert.Equal(2700, viewModel.ColorTemperature);
        Assert.False(viewModel.SupportsWhites);
        Assert.Equal((2500, 6500), (viewModel.MinColorTemperature, viewModel.MaxColorTemperature));
    }

    [Fact]
    public async Task Dimmer_shows_brightness_only()
    {
        await using FakeKlapDevice dimmer = await FakeKlapDevice.StartAsync();
        dimmer.Info = new JsonObject { ["device_on"] = true, ["brightness"] = 40, ["model"] = "S500D" };

        SmartDeviceViewModel viewModel = await LoadAsync(dimmer);

        Assert.Equal("40 %", viewModel.State);
        Assert.Equal(Color.FromRgb(255, 183, 98), viewModel.Glow);
        Assert.Equal(100, viewModel.Saturation);
    }

    [Fact]
    public async Task Metering_plug_shows_power_and_energy()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync();
        plug.Info = new JsonObject { ["device_on"] = true, ["model"] = "P110" };
        plug.Results["get_energy_usage"] = new JsonObject { ["current_power"] = 12500, ["today_energy"] = 300, ["month_energy"] = 4500 };

        SmartDeviceViewModel viewModel = await LoadAsync(plug);

        Assert.Equal("On · 12.5 W", viewModel.State);
        Assert.Equal("Now 12.5 W · today 0.3 kWh · month 4.5 kWh", viewModel.Energy);
        Assert.Equal(Color.FromRgb(0xFF, 0xC8, 0x6B), viewModel.Glow);
        Assert.False(viewModel.HasUsage);
        Assert.False(viewModel.Fade.IsSupported);
    }

    [Fact]
    public async Task Plain_switch_shows_on_and_off()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync();
        plug.Info = new JsonObject { ["device_on"] = true };
        SmartDeviceViewModel viewModel = await LoadAsync(plug);
        Assert.Equal("On", viewModel.State);

        plug.Info["device_on"] = false;
        await viewModel.RefreshAsync();

        Assert.Equal("Off", viewModel.State);
        Assert.Equal(Off, viewModel.Glow);
        Assert.Equal("P110", viewModel.Name);
    }

    [Theory]
    [InlineData(1, "1 connected device")]
    [InlineData(2, "2 connected devices")]
    public async Task Hub_lists_its_children(int count, string state)
    {
        await using FakeKlapDevice hub = await FakeKlapDevice.StartAsync();
        hub.Info = new JsonObject { ["nickname"] = "Hub", ["model"] = "H100", ["type"] = "SMART.TAPOHUB" };
        JsonArray children = [];
        for (int i = 0; i < count; i++)
        {
            children.Add(new JsonObject { ["model"] = "T310", ["current_temp"] = 21.5 });
        }

        hub.Results["get_child_device_list"] = new JsonObject { ["child_device_list"] = children };

        SmartDeviceViewModel viewModel = await LoadAsync(hub);

        Assert.Equal("Hub", viewModel.Name);
        Assert.Equal(state, viewModel.State);
        Assert.Equal(count, viewModel.Children.Count);
        Assert.False(viewModel.SupportsPower);
        Assert.Equal(DeviceGlyphs.Hub, viewModel.Glyph);
    }

    [Fact]
    public async Task Unnamed_device_keeps_its_discovered_model()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.Info = new JsonObject { ["device_on"] = false };

        SmartDeviceViewModel viewModel = await LoadAsync(device, new TapoDevice(device.Ip, "", "Tapo", TapoDeviceKind.Smart, "SMART.TAPOPLUG"));

        Assert.Equal("Tapo", viewModel.Name);
        Assert.Equal($" · {device.Ip}", viewModel.Subtitle);
        Assert.Equal(DeviceGlyphs.Power, viewModel.Glyph);
    }

    [Fact]
    public async Task Missing_device_info_is_an_error()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.PlainReply = """{"error_code":0,"result":{"responses":[]}}""";

        SmartDeviceViewModel viewModel = await LoadAsync(device);

        Assert.Equal("The device did not report its state.", viewModel.Error);
        Assert.True(viewModel.HasError);
        Assert.False(viewModel.IsReady);
    }

    [Fact]
    public async Task Unreachable_device_is_offline()
    {
        SignIn();
        SmartDeviceViewModel viewModel = new(new TapoDevice(Loopback.DeadIp, "", "P100", TapoDeviceKind.Smart), new DeviceSessions(new CredentialStore()));

        await viewModel.RefreshAsync();

        Assert.Equal("Offline or asleep", viewModel.Error);
        Assert.Equal($"P100 · {Loopback.DeadIp}", viewModel.Name);
    }

    [Fact]
    public async Task User_changes_are_sent_to_the_device()
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync();
        bulb.Info = new JsonObject { ["device_on"] = false, ["brightness"] = 10, ["hue"] = 0, ["saturation"] = 100, ["color_temp"] = 2700 };
        SmartDeviceViewModel viewModel = await LoadAsync(bulb);

        viewModel.IsOn = true;
        await EventuallyAsync(() => bulb.Calls.Count == 1 && !viewModel.IsBusy);
        viewModel.Brightness = 150;
        await EventuallyAsync(() => bulb.Calls.Count == 2 && !viewModel.IsBusy);
        viewModel.ColorTemperature = 4000.4;
        await EventuallyAsync(() => bulb.Calls.Count == 3 && !viewModel.IsBusy);
        viewModel.Hue = 200;
        await EventuallyAsync(() => bulb.Calls.Count == 4 && !viewModel.IsBusy);
        viewModel.Saturation = 50;
        await EventuallyAsync(() => bulb.Calls.Count == 5 && !viewModel.IsBusy);
        await viewModel.ApplyPresetCommand.ExecuteAsync(LightPreset.Whites[0]);
        await viewModel.ApplyPresetCommand.ExecuteAsync(LightPreset.Colors[3]);
        viewModel.Fade.IsOn = true;
        await EventuallyAsync(() => bulb.Calls.Count == 8 && !viewModel.IsBusy);

        Assert.Equal(
            [
                """{"device_on":true}""",
                """{"brightness":100,"device_on":true}""",
                """{"color_temp":4000,"device_on":true}""",
                """{"hue":200,"saturation":100,"color_temp":0,"device_on":true}""",
                """{"hue":200,"saturation":50,"color_temp":0,"device_on":true}""",
                """{"color_temp":2700,"device_on":true}""",
                """{"hue":220,"saturation":100,"color_temp":0,"device_on":true}""",
                """{"enable":true}""",
            ],
            bulb.Calls.Select(call => call.Params!.ToJsonString()));
        Assert.Equal("set_on_off_gradually_info", bulb.Calls.Last().Method);
    }

    [Fact]
    public async Task Device_rejection_becomes_the_error_line()
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync(password: "other");

        SmartDeviceViewModel viewModel = await LoadAsync(bulb);

        Assert.Equal("The device rejected these Tapo credentials.", viewModel.Error);
    }

    [Fact]
    public async Task Only_one_row_is_expanded_at_a_time()
    {
        SignIn();
        DeviceSessions sessions = new(new CredentialStore());
        SmartDeviceViewModel first = new(new TapoDevice("10.0.0.1", "", "P100", TapoDeviceKind.Smart), sessions);
        SmartDeviceViewModel second = new(new TapoDevice("10.0.0.2", "", "P100", TapoDeviceKind.Smart), sessions);

        first.ToggleExpandedCommand.Execute(null);
        Assert.True(first.IsExpanded);
        second.ToggleExpandedCommand.Execute(null);
        Assert.False(first.IsExpanded);
        Assert.True(second.IsExpanded);
        second.ToggleExpandedCommand.Execute(null);
        Assert.False(second.IsExpanded);
    }

    private static async Task<SmartDeviceViewModel> LoadAsync(FakeKlapDevice device, TapoDevice? discovered = null)
    {
        SignIn();
        SmartDeviceViewModel viewModel = new(discovered ?? new TapoDevice(device.Ip, "", "P110", TapoDeviceKind.Smart, "SMART.TAPOPLUG"), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();
        return viewModel;
    }
}

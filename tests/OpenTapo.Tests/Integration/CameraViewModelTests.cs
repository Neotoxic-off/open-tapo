using System.Text.Json.Nodes;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;
using OpenTapo.ViewModels;

namespace OpenTapo.Tests.Integration;

public sealed class CameraViewModelTests : IsolatedTest
{
    [Fact]
    public async Task Camera_state_is_shown()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.Results["getDeviceInfo"] = BasicInfo(new JsonObject
        {
            ["device_alias"] = "Hall",
            ["device_model"] = "C410",
            ["battery_percent"] = 80,
            ["battery_charging"] = "YES",
            ["low_battery"] = true,
        });
        camera.Results["getSdCardStatus"] = JsonNode.Parse("""{"harddisk_manage":{"hd_info":[{"hd_info_1":{"total_space":"29.7GB","free_space":"512.0MB"}}]}}""");
        camera.Results["getLastAlarmInfo"] = Alarm("motion");
        camera.Results["getConnectionType"] = new JsonObject { ["rssiValue"] = -70 };
        camera.Results["getDayNightModeConfig"] = JsonNode.Parse("""{"image":{"common":{"inf_type":"on"}}}""");
        camera.Results["getRotationStatus"] = JsonNode.Parse("""{"image":{"switch":{"flip_type":"center"}}}""");
        camera.Results["getLensMaskConfig"] = JsonNode.Parse("""{"lens_mask":{"lens_mask_info":{"enabled":"off"}}}""");
        camera.Results["getDetectionConfig"] = JsonNode.Parse("""{"motion_detection":{"motion_det":{"enabled":"on"}}}""");
        camera.Results["getLedStatus"] = JsonNode.Parse("""{"led":{"config":{"enabled":"off"}}}""");

        CameraViewModel viewModel = await LoadAsync(camera);

        Assert.True(viewModel.IsReady);
        Assert.Equal("Hall", viewModel.Name);
        Assert.Equal($"C410 · {camera.Ip}", viewModel.Subtitle);
        Assert.True(viewModel.IsActive);
        Assert.True(viewModel.MotionDetection.IsSupported && viewModel.MotionDetection.IsOn);
        Assert.False(viewModel.Settings[0].IsSupported);
        Assert.True(viewModel.Settings[3].IsSupported);
        Assert.False(viewModel.Settings[3].IsOn);
        Assert.True(viewModel.Flip.IsOn);
        Assert.True(viewModel.HasNightVision);
        Assert.Equal("Always on", viewModel.NightVision?.Label);
        Assert.Equal(3, viewModel.NightVisionModes.Count);
        Assert.Equal("Last alert 2 min ago · motion", viewModel.LastAlarm);
        Assert.Equal("80 % ⚡", viewModel.Battery);
        Assert.True(viewModel.IsBatteryLow);
        Assert.True(viewModel.HasStorage);
        Assert.Equal(98.3, viewModel.StorageUsedPercent, precision: 1);
        Assert.Equal("SD card · 0.5 GB free of 29.7 GB", viewModel.Storage);
        Assert.Equal("Wi-Fi fair · -70 dBm", viewModel.Signal);
    }

    [Theory]
    [InlineData("person", "Last alert 2 min ago · person")]
    [InlineData("doorbell", "Last alert 2 min ago · doorbell")]
    [InlineData("", "Last alert 2 min ago")]
    [InlineData(null, "Last alert 2 min ago")]
    public async Task Last_alert_names_its_type(string? type, string expected)
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.Results["getLastAlarmInfo"] = Alarm(type);

        Assert.Equal(expected, (await LoadAsync(camera)).LastAlarm);
    }

    [Fact]
    public async Task Silent_camera_falls_back_to_defaults()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.Results["getDeviceInfo"] = BasicInfo(new JsonObject { ["battery_percent"] = 50 });
        camera.Results["getLensMaskConfig"] = JsonNode.Parse("""{"lens_mask":{"lens_mask_info":{"enabled":"on"}}}""");

        CameraViewModel viewModel = await LoadAsync(camera);

        Assert.Equal("Camera", viewModel.Name);
        Assert.False(viewModel.IsActive);
        Assert.Equal("50 %", viewModel.Battery);
        Assert.False(viewModel.HasNightVision);
        Assert.Null(viewModel.NightVision);
        Assert.False(viewModel.HasStorage);
        Assert.Equal(0, viewModel.StorageUsedPercent);
        Assert.Equal("", viewModel.Storage);
        Assert.Equal("Last alert none", viewModel.LastAlarm);
        Assert.Equal("", viewModel.Signal);
        Assert.False(viewModel.Flip.IsSupported);
    }

    [Fact]
    public async Task Mains_camera_shows_no_battery()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();

        Assert.Equal("", (await LoadAsync(camera)).Battery);
    }

    [Fact]
    public async Task User_changes_are_sent_to_the_camera()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        CameraViewModel viewModel = await LoadAsync(camera);
        camera.Calls.Clear();

        viewModel.IsActive = false;
        viewModel.NightVision = viewModel.NightVisionModes[0];
        viewModel.NightVision = null;
        viewModel.MotionDetection.IsOn = true;
        viewModel.Flip.IsOn = true;
        await viewModel.StartSirenCommand.ExecuteAsync(null);
        await viewModel.StopSirenCommand.ExecuteAsync(null);
        await EventuallyAsync(() => camera.Calls.Count(call => !call.Method.StartsWith("get", StringComparison.Ordinal)) == 6);

        string[] writes = camera.Calls.Where(call => !call.Method.StartsWith("get", StringComparison.Ordinal))
            .Select(call => $"{call.Method} {call.Params!.ToJsonString()}")
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                """do {"msg_alarm":{"manual_msg_alarm":{"action":"start"}}}""",
                """do {"msg_alarm":{"manual_msg_alarm":{"action":"stop"}}}""",
                """setDayNightModeConfig {"image":{"common":{"inf_type":"auto"}}}""",
                """setDetectionConfig {"motion_detection":{"motion_det":{"enabled":"on"}}}""",
                """setLensMaskConfig {"lens_mask":{"lens_mask_info":{"enabled":"on"}}}""",
                """setRotationStatus {"image":{"switch":{"flip_type":"center"}}}""",
            ],
            writes);
    }

    [Fact]
    public async Task Rejected_password_becomes_the_error_line()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync(password: "other");

        CameraViewModel viewModel = await LoadAsync(camera);

        Assert.StartsWith("The camera rejected this password.", viewModel.Error);
        Assert.False(viewModel.IsReady);
    }

    private static async Task<CameraViewModel> LoadAsync(FakeCamera camera)
    {
        SignIn();
        CameraViewModel viewModel = new(new TapoDevice(camera.Ip, "", "Tapo camera", TapoDeviceKind.Camera), new DeviceSessions(new CredentialStore()));
        await viewModel.RefreshAsync();
        return viewModel;
    }

    private static JsonObject BasicInfo(JsonObject info) =>
        new() { ["device_info"] = new JsonObject { ["basic_info"] = info } };

    private static JsonObject Alarm(string? type)
    {
        JsonObject info = new() { ["last_alarm_time"] = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 150).ToString() };
        if (type is not null)
        {
            info["last_alarm_type"] = type;
        }

        return new JsonObject { ["system"] = new JsonObject { ["last_alarm_info"] = info } };
    }
}

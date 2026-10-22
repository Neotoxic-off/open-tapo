using System.Text;
using System.Text.Json.Nodes;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

/// <summary>Smart API calls translated to the Kasa IOT API, against a fake XOR device.</summary>
public sealed class KasaClientTests
{
    [Fact]
    public async Task Plug_is_read_and_switched()
    {
        await using FakeKasaDevice plug = FakeKasaDevice.Start();
        plug.Iot.Realtime = new JsonObject { ["power_mw"] = 12500 };
        KasaClient client = new(new XorClient(plug.Ip));

        await client.CallAsync("set_device_info", new JsonObject { ["device_on"] = true });
        IReadOnlyDictionary<string, JsonNode?> results = await client.ExecuteAsync("get_device_info", "get_energy_usage", "get_device_usage", "get_child_device_list");

        JsonNode info = results["get_device_info"]!;
        Assert.Equal("Desk", Encoding.UTF8.GetString(Convert.FromBase64String((string)info["nickname"]!)));
        Assert.Equal("IOT.SMARTPLUGSWITCH", (string?)info["type"]);
        Assert.Equal(true, (bool?)info["device_on"]);
        Assert.Null(info["brightness"]);
        Assert.Equal(12500, (int?)results["get_energy_usage"]!["current_power"]);
        Assert.Null(results["get_device_usage"]);
        Assert.Null(results["get_child_device_list"]);
    }

    [Fact]
    public async Task Dimmer_brightness_is_read_and_set()
    {
        await using FakeKasaDevice dimmer = FakeKasaDevice.Start();
        dimmer.Iot.SysInfo["brightness"] = 40;
        KasaClient client = new(new XorClient(dimmer.Ip));

        await client.CallAsync("set_device_info", new JsonObject { ["brightness"] = 70 });

        Assert.Equal(70, (int?)(await client.CallAsync("get_device_info"))["brightness"]);
        Assert.Contains("smartlife.iot.dimmer.set_brightness {\"brightness\":70}", dimmer.Iot.Calls);
    }

    [Fact]
    public async Task First_hs110_reports_watts()
    {
        await using FakeKasaDevice plug = FakeKasaDevice.Start();
        plug.Iot.Realtime = new JsonObject { ["power"] = 7.25 };
        KasaClient client = new(new XorClient(plug.Ip));

        Assert.Equal(7250, (int?)(await client.CallAsync("get_energy_usage"))["current_power"]);

        plug.Iot.Realtime = new JsonObject { ["voltage"] = 230 };
        await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("get_energy_usage"));
    }

    [Fact]
    public async Task Power_strip_outlets_are_listed_and_switched()
    {
        await using FakeKasaDevice strip = FakeKasaDevice.Start();
        strip.Iot.SysInfo = new JsonObject
        {
            ["alias"] = "Strip",
            ["model"] = "HS300(EU)",
            ["type"] = "IOT.SMARTPLUGSWITCH",
            ["children"] = JsonNode.Parse("""[{"id":"8006A1","alias":"Lamp","state":0},{"id":"8006A2","alias":"Radio","state":1}]"""),
        };
        KasaClient client = new(new XorClient(strip.Ip));

        JsonNode info = await client.CallAsync("get_device_info");
        await client.CallAsync("control_child", new JsonObject
        {
            ["device_id"] = "8006A1",
            ["requestData"] = JsonNode.Parse("""{"method":"multipleRequest","params":{"requests":[{"method":"set_device_info","params":{"device_on":true}}]}}"""),
        });
        JsonArray outlets = (await client.CallAsync("get_child_device_list"))["child_device_list"]!.AsArray();

        Assert.Null(info["device_on"]);
        Assert.Equal(["8006A1", "8006A2"], outlets.Select(outlet => (string?)outlet!["device_id"]));
        Assert.All(outlets, outlet => Assert.True((bool?)outlet!["device_on"]));
        Assert.Contains("system.set_relay_state {\"state\":1} [\"8006A1\"]", strip.Iot.Calls);
        await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("control_child", new JsonObject { ["device_id"] = "8006A1" }));
    }

    [Fact]
    public async Task Color_bulb_state_is_read_and_set()
    {
        await using FakeKasaDevice bulb = FakeKasaDevice.Start();
        bulb.Iot.SysInfo = Bulb(JsonNode.Parse("""{"on_off":1,"brightness":50,"color_temp":0,"hue":120,"saturation":80}""")!);
        bulb.Iot.Realtime = new JsonObject { ["power_mw"] = 9000 };
        KasaClient client = new(new XorClient(bulb.Ip));

        JsonNode info = await client.CallAsync("get_device_info");
        await client.CallAsync("set_device_info", new JsonObject { ["device_on"] = false, ["color_temp"] = 2700 });
        await client.CallAsync("get_energy_usage");

        Assert.Equal(true, (bool?)info["device_on"]);
        Assert.Equal(50, (int?)info["brightness"]);
        Assert.Equal(120, (int?)info["hue"]);
        Assert.Equal(80, (int?)info["saturation"]);
        Assert.Equal([2500, 6500], info["color_temp_range"]!.AsArray().Select(value => (int)value!));
        Assert.Contains(bulb.Iot.Calls, call => call.StartsWith("smartlife.iot.smartbulb.lightingservice.transition_light_state {\"ignore_default\":1,\"on_off\":0,\"color_temp\":2700}", StringComparison.Ordinal));
        Assert.Contains(bulb.Iot.Calls, call => call.StartsWith("smartlife.iot.common.emeter.get_realtime", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Off_bulb_shows_the_state_it_returns_with()
    {
        await using FakeKasaDevice bulb = FakeKasaDevice.Start();
        bulb.Iot.SysInfo = Bulb(JsonNode.Parse("""{"on_off":0,"dft_on_state":{"brightness":30,"color_temp":4000,"hue":0,"saturation":0}}""")!);
        KasaClient client = new(new XorClient(bulb.Ip));

        JsonNode info = await client.CallAsync("get_device_info");

        Assert.Equal(false, (bool?)info["device_on"]);
        Assert.Equal(30, (int?)info["brightness"]);
        Assert.Equal(4000, (int?)info["color_temp"]);
    }

    [Fact]
    public async Task Bare_bulb_falls_back_to_defaults()
    {
        await using FakeKasaDevice bulb = FakeKasaDevice.Start();
        bulb.Iot.SysInfo = Bulb(JsonNode.Parse("""{"on_off":0}""")!);
        KasaClient client = new(new XorClient(bulb.Ip));

        JsonNode info = await client.CallAsync("get_device_info");

        Assert.Equal(100, (int?)info["brightness"]);
        Assert.Equal(0, (int?)info["color_temp"]);
        Assert.Equal(0, (int?)info["hue"]);
        Assert.Equal(100, (int?)info["saturation"]);
    }

    [Fact]
    public async Task Light_strip_uses_its_own_service()
    {
        await using FakeKasaDevice strip = FakeKasaDevice.Start();
        strip.Iot.SysInfo = Bulb(JsonNode.Parse("""{"on_off":1,"brightness":50}""")!);
        strip.Iot.SysInfo["lighting_effect_state"] = new JsonObject { ["enable"] = 0 };
        KasaClient client = new(new XorClient(strip.Ip));

        await client.CallAsync("set_device_info", new JsonObject { ["brightness"] = 20 });

        Assert.Contains("smartlife.iot.lightStrip.set_light_state {\"ignore_default\":1,\"brightness\":20}", strip.Iot.Calls);
    }

    [Fact]
    public async Task Kasa_camera_switch_is_read_and_set_over_linkie()
    {
        await using FakeKasaDevice camera = await FakeKasaDevice.StartLinkieAsync();
        KasaClient client = new(new LinkieClient(camera.Ip));

        Assert.Equal(true, (bool?)(await client.CallAsync("get_device_info"))["device_on"]);
        await client.CallAsync("set_device_info", new JsonObject { ["device_on"] = false });
        await client.CallAsync("set_device_info", new JsonObject());

        Assert.Equal("off", camera.Iot.CameraSwitch);
    }

    [Fact]
    public async Task Device_errors_and_unknown_calls_are_reported()
    {
        await using FakeKasaDevice plug = FakeKasaDevice.Start();
        plug.Iot.ErrorCode = -3;
        KasaClient client = new(new XorClient(plug.Ip));

        TapoException error = await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("set_device_info", new JsonObject { ["device_on"] = true }));
        Assert.Equal("set_relay_state: error -3", error.Message);
        await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("get_device_usage"));
        await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("control_child"));
        await client.CallAsync("set_device_info");
    }

    [Fact]
    public async Task Malformed_device_state_is_an_error()
    {
        await using FakeKasaDevice plug = FakeKasaDevice.Start();
        plug.Iot.SysInfo["alias"] = new JsonArray();

        await Assert.ThrowsAsync<TapoException>(() => new KasaClient(new XorClient(plug.Ip)).ExecuteAsync("get_device_info"));
    }

    private static JsonObject Bulb(JsonNode light) => new()
    {
        ["alias"] = "Lamp",
        ["model"] = "KL130(EU)",
        ["mic_type"] = "IOT.SMARTBULB",
        ["mic_mac"] = "50C7BF000002",
        ["is_dimmable"] = 1,
        ["is_variable_color_temp"] = 1,
        ["is_color"] = 1,
        ["light_state"] = light,
    };
}

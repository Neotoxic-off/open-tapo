using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// Kasa IOT devices (plugs, switches, dimmers, power strips, bulbs, light strips, cameras) behind the Tapo
/// smart API: each smart call the panel makes is translated to its IOT query, so Kasa devices use the same
/// view as Tapo ones. Calls with no IOT equivalent fail and are hidden like any unsupported feature.
/// </summary>
public sealed class KasaClient(IIotTransport transport) : SmartClient
{
    private const string BulbService = "smartlife.iot.smartbulb.lightingservice";
    private const string LightStripService = "smartlife.iot.lightStrip";
    private const string DimmerService = "smartlife.iot.dimmer";
    // Kasa camera privacy switch; not covered by python-kasa, from the KC app's API.
    private const string CameraService = "smartlife.cam.ipcamera.switch";
    private static readonly string[] LightKeys = ["brightness", "color_temp", "hue", "saturation"];

    public override async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
    {
        try
        {
            return method switch
            {
                "get_device_info" => await DeviceInfoAsync(),
                "set_device_info" => await SetAsync(parameters ?? []),
                "get_energy_usage" => await EnergyAsync(),
                "get_child_device_list" => await ChildrenAsync(),
                "control_child" => await ControlChildAsync(parameters ?? []),
                _ => throw new TapoException($"{method}: not supported by this device"),
            };
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }
    }

    /// <summary>IOT has no batching: getters run one by one, an unsupported one is reported as null.</summary>
    public override async Task<IReadOnlyDictionary<string, JsonNode?>> ExecuteAsync(params string[] methods)
    {
        Dictionary<string, JsonNode?> results = [];
        foreach (string method in methods)
        {
            try
            {
                results[method] = await CallAsync(method);
            }
            catch (TapoException) when (method != "get_device_info")
            {
                results[method] = null;
            }
        }

        return results;
    }

    private async Task<JsonNode> DeviceInfoAsync()
    {
        JsonNode system = await SystemInfoAsync();
        JsonObject info = new()
        {
            ["nickname"] = Convert.ToBase64String(Bytes.Utf8((string?)system["alias"] ?? "")),
            ["model"] = (string?)system["model"],
            ["type"] = (string?)(system["mic_type"] ?? system["type"]),
            ["rssi"] = (int?)system["rssi"],
        };

        if (system["light_state"] is JsonNode light)
        {
            bool on = (int?)light["on_off"] == 1;
            // An off bulb reports the state it will come back with under dft_on_state.
            JsonNode values = on ? light : light["dft_on_state"] ?? light;
            info["device_on"] = on;
            if ((int?)system["is_dimmable"] == 1)
            {
                info["brightness"] = (int?)values["brightness"] ?? 100;
            }

            if ((int?)system["is_variable_color_temp"] == 1)
            {
                info["color_temp"] = (int?)values["color_temp"] ?? 0;
                // shortcut: Kasa bulbs don't report their range (2500-9000 K on KL130); widen per model if it matters.
                info["color_temp_range"] = new JsonArray(2500, 6500);
            }

            if ((int?)system["is_color"] == 1)
            {
                info["hue"] = (int?)values["hue"] ?? 0;
                info["saturation"] = (int?)values["saturation"] ?? 100;
            }
        }
        else if (IsCamera(system))
        {
            info["device_on"] = (string?)(await QueryAsync(CameraService, "get_is_enable"))["value"] == "on";
        }
        else if (system["children"] is null && system["relay_state"] is JsonNode relay)
        {
            info["device_on"] = (int?)relay == 1;
            if (system["brightness"] is JsonNode brightness)
            {
                info["brightness"] = (int?)brightness;
            }
        }

        return info;
    }

    private async Task<JsonNode> SetAsync(JsonObject parameters)
    {
        JsonNode system = await SystemInfoAsync();
        bool? on = (bool?)parameters["device_on"];
        if (system["light_state"] is not null)
        {
            JsonObject state = new() { ["ignore_default"] = 1 };
            if (on is bool value)
            {
                state["on_off"] = value ? 1 : 0;
            }

            foreach (string key in LightKeys.Where(key => parameters[key] is not null))
            {
                state[key] = (int?)parameters[key];
            }

            return system["lighting_effect_state"] is null
                ? await QueryAsync(BulbService, "transition_light_state", state)
                : await QueryAsync(LightStripService, "set_light_state", state);
        }

        if (IsCamera(system))
        {
            return on is bool value ? await QueryAsync(CameraService, "set_is_enable", new JsonObject { ["value"] = value ? "on" : "off" }) : new JsonObject();
        }

        JsonNode result = new JsonObject();
        if ((int?)parameters["brightness"] is int level)
        {
            result = await QueryAsync(DimmerService, "set_brightness", new JsonObject { ["brightness"] = level });
        }

        if (on is bool power)
        {
            result = await QueryAsync("system", "set_relay_state", new JsonObject { ["state"] = power ? 1 : 0 });
        }

        return result;
    }

    private async Task<JsonNode> EnergyAsync()
    {
        JsonNode system = await SystemInfoAsync();
        JsonNode realtime = await QueryAsync(system["light_state"] is null ? "emeter" : "smartlife.iot.common.emeter", "get_realtime");
        // Newer hardware reports milliwatts, the first HS110 watts.
        int milliwatts = (int?)realtime["power_mw"]
            ?? (int)Math.Round(((double?)realtime["power"] ?? throw new TapoException("get_energy_usage: no power reading")) * 1000);
        return new JsonObject { ["current_power"] = milliwatts };
    }

    private async Task<JsonNode> ChildrenAsync()
    {
        JsonNode system = await SystemInfoAsync();
        JsonArray children = system["children"]?.AsArray() ?? throw new TapoException("get_child_device_list: not a power strip");
        return new JsonObject
        {
            ["child_device_list"] = new JsonArray(children.OfType<JsonNode>().Select(child => (JsonNode)new JsonObject
            {
                ["device_id"] = (string?)child["id"],
                ["nickname"] = Convert.ToBase64String(Bytes.Utf8((string?)child["alias"] ?? "")),
                ["model"] = (string?)system["model"],
                ["device_on"] = (int?)child["state"] == 1,
            }).ToArray()),
        };
    }

    private Task<JsonNode> ControlChildAsync(JsonObject parameters)
    {
        bool on = (bool?)parameters["requestData"]?["params"]?["requests"]?[0]?["params"]?["device_on"] ?? throw new TapoException("control_child: only on / off is supported");
        JsonObject context = new() { ["child_ids"] = new JsonArray((string?)parameters["device_id"]) };
        return QueryAsync("system", "set_relay_state", new JsonObject { ["state"] = on ? 1 : 0 }, context);
    }

    private Task<JsonNode> SystemInfoAsync() => QueryAsync("system", "get_sysinfo");

    private async Task<JsonNode> QueryAsync(string target, string command, JsonObject? arguments = null, JsonObject? context = null)
    {
        JsonObject request = new() { [target] = new JsonObject { [command] = arguments ?? [] } };
        if (context is not null)
        {
            request["context"] = context;
        }

        JsonNode reply = await transport.QueryAsync(request);
        JsonNode result = reply[target]?[command] ?? throw new TapoException($"{command}: not supported by this device");
        int errorCode = (int?)result["err_code"] ?? 0;
        return errorCode == 0 ? result : throw new TapoException($"{command}: error {errorCode}");
    }

    private static bool IsCamera(JsonNode system) => (string?)system["type"] == "IOT.IPCAMERA";
}

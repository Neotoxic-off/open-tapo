using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;
using OpenTapo.ViewModels;

namespace OpenTapo.Tests.Fuzz;

/// <summary>
/// Seeded fuzzing of everything that parses bytes from the LAN. Any host can answer as a device, so the
/// property is always the same: hostile input ends as a handled error, never as an unexpected exception.
/// A failure message carries the seed and iteration to replay it.
/// </summary>
public sealed class FuzzTests : IsolatedTest
{
    private const int Iterations = 300;

    // Field names the parsers read, so random documents actually reach the casts.
    private static readonly string[] Keys =
    [
        "result", "error_code", "method", "responses", "device_type", "mac", "device_model", "nickname", "model", "type",
        "device_on", "brightness", "color_temp", "color_temp_range", "hue", "saturation", "rssi", "time_usage", "today",
        "past7", "past30", "enable", "current_power", "today_energy", "month_energy", "child_device_list", "current_temp",
        "target_temp", "current_humidity", "detected", "open", "battery_percentage", "at_low_battery", "status",
        "device_info", "basic_info", "device_alias", "battery_percent", "battery_charging", "low_battery", "harddisk_manage",
        "hd_info", "hd_info_1", "total_space", "free_space", "system", "last_alarm_info", "last_alarm_type", "last_alarm_time",
        "rssiValue", "image", "common", "inf_type", "switch", "flip_type", "lens_mask", "lens_mask_info", "enabled",
    ];

    [Fact]
    public void Format_helpers_accept_any_string()
    {
        Random random = new(1);
        for (int i = 0; i < Iterations * 10; i++)
        {
            string value = RandomString(random);
            Check(1, i, value, () =>
            {
                Format.Nickname(value);
                Format.Gigabytes(value);
                Format.Ago(value);
            });
        }

        Check(1, -1, "edges", () =>
        {
            Format.Ago(long.MaxValue.ToString());
            Format.Duration(int.MinValue);
            Format.Signal(int.MinValue);
            Format.Watts(int.MinValue);
        });
    }

    [Fact]
    public void Discovery_reply_parser_accepts_any_datagram()
    {
        Random random = new(2);
        IPEndPoint sender = new(IPAddress.Parse("127.0.0.7"), 20002);
        for (int i = 0; i < Iterations * 10; i++)
        {
            byte[] packet = i % 2 == 0
                ? RandomBytes(random, random.Next(0, 64))
                : [.. RandomBytes(random, 16), .. Encoding.UTF8.GetBytes(RandomJson(random, depth: 3).ToJsonString())];
            Check(2, i, Convert.ToBase64String(packet), () => TapoDiscovery.TryParseReply(new UdpReceiveResult(packet, sender), out _));
        }
    }

    [Fact]
    public void Child_reading_accepts_any_document()
    {
        Random random = new(3);
        for (int i = 0; i < Iterations * 10; i++)
        {
            JsonObject child = RandomObject(random, depth: 2);
            // Wrong field types may throw, but only the kinds DeviceViewModel.RunAsync turns into an error line.
            Check(3, i, child.ToJsonString(), () =>
            {
                try
                {
                    ChildReading.From(child);
                }
                catch (Exception ex) when (TapoException.IsMalformedReply(ex))
                {
                }
            });
        }
    }

    [Fact]
    public async Task Klap_device_view_survives_any_reply()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        SignIn();
        SmartDeviceViewModel viewModel = new(new TapoDevice(device.Ip, "", "P110", TapoDeviceKind.Smart), new DeviceSessions(new CredentialStore()));
        Random random = new(4);
        for (int i = 0; i < Iterations; i++)
        {
            device.PlainReply = RandomBatchReply(random, "get_device_info", "get_device_usage", "get_on_off_gradually_info", "get_energy_usage", "get_child_device_list");
            await CheckAsync(4, i, device.PlainReply, viewModel.RefreshAsync);
        }
    }

    [Fact]
    public async Task Camera_view_survives_any_reply()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        SignIn();
        CameraViewModel viewModel = new(new TapoDevice(camera.Ip, "", "Tapo camera", TapoDeviceKind.Camera), new DeviceSessions(new CredentialStore()));
        Random random = new(5);
        for (int i = 0; i < Iterations; i++)
        {
            camera.PlainReply = RandomBatchReply(
                random,
                "getDeviceInfo", "getSdCardStatus", "getLastAlarmInfo", "getConnectionType", "getDayNightModeConfig", "getRotationStatus", "getLensMaskConfig", "getDetectionConfig");
            await CheckAsync(5, i, camera.PlainReply, viewModel.RefreshAsync);
        }
    }

    [Fact]
    public async Task Klap_client_rejects_any_malformed_transport_reply()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        Random random = new(6);
        for (int i = 0; i < Iterations; i++)
        {
            byte[] body = RandomBytes(random, random.Next(0, 96));
            if (i % 2 == 0)
            {
                device.RawRequest = context => context.Response.Body.WriteAsync(body).AsTask();
                device.RawHandshake = null;
            }
            else
            {
                device.RawRequest = null;
                device.RawHandshake = context => context.Response.Body.WriteAsync(body).AsTask();
            }

            KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);
            await CheckAsync(6, i, Convert.ToBase64String(body), () => client.CallAsync("get_device_info"));
        }
    }

    [Fact]
    public async Task Camera_client_rejects_any_malformed_reply()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        Random random = new(7);
        for (int i = 0; i < Iterations; i++)
        {
            string body = i % 3 == 0 ? Encoding.Latin1.GetString(RandomBytes(random, random.Next(0, 64))) : RandomJson(random, depth: 4).ToJsonString();
            camera.Raw = context => context.Response.WriteAsync(body);
            await CheckAsync(7, i, body, () => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
        }

        camera.Raw = null;
        for (int i = 0; i < Iterations; i++)
        {
            camera.PlainReply = i % 3 == 0 ? RandomString(random) : RandomJson(random, depth: 4).ToJsonString();
            await CheckAsync(7, Iterations + i, camera.PlainReply, () => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
        }
    }

    /// <summary>A multipleRequest reply where each known method gets a random result, error code or nothing.</summary>
    private static string RandomBatchReply(Random random, params string[] methods)
    {
        JsonArray responses = [];
        foreach (string method in methods)
        {
            JsonObject response = new() { ["method"] = method, ["error_code"] = random.Next(4) == 0 ? -1 : 0 };
            if (random.Next(5) != 0)
            {
                response["result"] = random.Next(6) == 0 ? RandomJson(random, depth: 1) : RandomObject(random, depth: 4);
            }

            responses.Add(response);
        }

        JsonNode result = random.Next(10) == 0 ? RandomJson(random, depth: 1) : new JsonObject { ["responses"] = responses };
        return new JsonObject { ["error_code"] = random.Next(10) == 0 ? -1 : 0, ["result"] = result }.ToJsonString();
    }

    private static JsonNode RandomJson(Random random, int depth) => random.Next(depth > 0 ? 9 : 7) switch
    {
        0 => JsonValue.Create(random.Next(2) == 0),
        1 => JsonValue.Create(random.Next(-100000, 100000)),
        2 => JsonValue.Create((random.NextDouble() - 0.5) * Math.Pow(10, random.Next(-3, 20))),
        3 => JsonValue.Create(RandomString(random)),
        4 => JsonValue.Create(random.Next(2) == 0 ? long.MaxValue : long.MinValue),
        5 => JsonValue.Create(random.Next(-100, 100).ToString()),
        6 => JsonValue.Create(Convert.ToBase64String(RandomBytes(random, random.Next(0, 12)))),
        7 => new JsonArray(Enumerable.Range(0, random.Next(0, 4)).Select(_ => (JsonNode?)(random.Next(4) == 0 ? null : RandomJson(random, depth - 1))).ToArray()),
        _ => RandomObject(random, depth - 1),
    };

    private static JsonObject RandomObject(Random random, int depth)
    {
        JsonObject result = [];
        for (int i = random.Next(0, 8); i > 0; i--)
        {
            result[Keys[random.Next(Keys.Length)]] = random.Next(8) == 0 ? null : RandomJson(random, Math.Max(depth, 0));
        }

        return result;
    }

    private static string RandomString(Random random)
    {
        const string alphabet = "0123456789.-+eE GBMTKBabcdefxyz=/_\"\\{}[]:,\u00e9\u20ac\ud83d\ude00";
        return new string(Enumerable.Range(0, random.Next(0, 24)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
    }

    private static byte[] RandomBytes(Random random, int length)
    {
        byte[] bytes = new byte[length];
        random.NextBytes(bytes);
        return bytes;
    }

    private static void Check(int seed, int iteration, string input, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Assert.Fail($"seed {seed} iteration {iteration}: {ex.GetType().Name} {ex.Message}\ninput: {input}");
        }
    }

    /// <summary>Protocol failures are the only acceptable outcome.</summary>
    private static async Task CheckAsync(int seed, int iteration, string input, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is TapoException or HttpRequestException)
        {
        }
        catch (Exception ex)
        {
            Assert.Fail($"seed {seed} iteration {iteration}: {ex.GetType().Name} {ex.Message}\ninput: {input}");
        }
    }
}

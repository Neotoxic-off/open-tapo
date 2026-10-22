using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OpenTapo.Tapo;

namespace OpenTapo.Tests.Fakes;

/// <summary>The Kasa IOT API ({target: {command: args}}) of a plug, dimmer, strip, bulb or camera, behind any transport fake.</summary>
internal sealed class FakeIot
{
    public JsonObject SysInfo { get; set; } = new()
    {
        ["alias"] = "Desk",
        ["model"] = "HS110(EU)",
        ["type"] = "IOT.SMARTPLUGSWITCH",
        ["mac"] = "50:C7:BF:00:00:01",
        ["relay_state"] = 0,
        ["rssi"] = -60,
    };

    /// <summary>The emeter get_realtime reply; null = the device has no emeter.</summary>
    public JsonObject? Realtime { get; set; }

    /// <summary>Kasa camera switch value: "on" or "off".</summary>
    public string CameraSwitch { get; set; } = "on";

    /// <summary>err_code of every setter.</summary>
    public int ErrorCode { get; set; }

    /// <summary>"target.command args [child ids]" for every command received.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    public JsonObject Process(JsonObject request)
    {
        JsonArray? childIds = request["context"]?["child_ids"]?.AsArray();
        JsonObject reply = [];
        foreach ((string target, JsonNode? commands) in request.Where(entry => entry.Key != "context"))
        {
            JsonObject answers = [];
            foreach ((string command, JsonNode? arguments) in commands!.AsObject())
            {
                Calls.Enqueue($"{target}.{command} {arguments?.ToJsonString()}{(childIds is null ? "" : " " + childIds.ToJsonString())}");
                if (Handle(target, command, arguments, childIds) is JsonObject answer)
                {
                    answers[command] = answer;
                }
            }

            if (answers.Count > 0)
            {
                reply[target] = answers;
            }
        }

        return reply;
    }

    private JsonObject? Handle(string target, string command, JsonNode? arguments, JsonArray? childIds)
    {
        JsonObject ok = new() { ["err_code"] = ErrorCode };
        switch (target, command)
        {
            case ("system", "get_sysinfo"):
                JsonObject info = SysInfo.DeepClone().AsObject();
                info["err_code"] = 0;
                return info;

            case ("system", "set_relay_state"):
                if (ErrorCode == 0 && childIds is not null)
                {
                    foreach (JsonNode? child in SysInfo["children"]!.AsArray().Where(child => childIds.Any(id => (string?)id == (string?)child!["id"])))
                    {
                        child!["state"] = (int)arguments!["state"]!;
                    }
                }
                else if (ErrorCode == 0)
                {
                    SysInfo["relay_state"] = (int)arguments!["state"]!;
                }

                return ok;

            case ("smartlife.iot.dimmer", "set_brightness"):
                SysInfo["brightness"] = (int)arguments!["brightness"]!;
                return ok;

            case ("smartlife.iot.smartbulb.lightingservice", "transition_light_state"):
            case ("smartlife.iot.lightStrip", "set_light_state"):
                JsonObject light = SysInfo["light_state"]!.AsObject();
                foreach ((string key, JsonNode? value) in arguments!.AsObject().Where(entry => entry.Key != "ignore_default"))
                {
                    light[key] = value?.DeepClone();
                }

                return ok;

            case ("emeter", "get_realtime"):
            case ("smartlife.iot.common.emeter", "get_realtime"):
                if (Realtime is null)
                {
                    return new JsonObject { ["err_code"] = -1, ["err_msg"] = "module not support" };
                }

                JsonObject realtime = Realtime.DeepClone().AsObject();
                realtime["err_code"] = 0;
                return realtime;

            case ("smartlife.cam.ipcamera.switch", "get_is_enable"):
                return new JsonObject { ["err_code"] = 0, ["value"] = CameraSwitch };

            case ("smartlife.cam.ipcamera.switch", "set_is_enable"):
                CameraSwitch = (string)arguments!["value"]!;
                return ok;

            default:
                // Real devices omit unknown targets entirely.
                return null;
        }
    }
}

/// <summary>
/// A legacy Kasa device on a loopback address: XOR over TCP plus the UDP discovery on the XOR port, or
/// (camera) Linkie over HTTPS.
/// </summary>
internal sealed class FakeKasaDevice : IAsyncDisposable
{
    public const string LinkieAuthorization = "Basic YWRtaW46MjEyMzJmMjk3YTU3YTVhNzQzODk0YTBlNGE4MDFmYzM=";

    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _tcp;
    private UdpClient? _udp;
    private WebApplication? _app;

    private FakeKasaDevice(string ip) => Ip = ip;

    public string Ip { get; }

    public FakeIot Iot { get; } = new();

    /// <summary>Replaces the whole reply payload (fuzzing, malformed devices); for XOR, the bytes after the length header.</summary>
    public byte[]? RawReply { get; set; }

    /// <summary>Length header sent before the XOR reply; null = the real length.</summary>
    public int? ReplyLength { get; set; }

    public static FakeKasaDevice Start(bool answerDiscovery = false)
    {
        FakeKasaDevice device = new(Loopback.NextIp());
        device._tcp = new TcpListener(IPAddress.Parse(device.Ip), DevicePorts.Xor);
        device._tcp.Start();
        _ = device.ServeTcpAsync();
        if (answerDiscovery)
        {
            device._udp = new UdpClient(new IPEndPoint(IPAddress.Parse(device.Ip), DevicePorts.Xor));
            _ = device.AnswerDiscoveryAsync();
        }

        return device;
    }

    public static async Task<FakeKasaDevice> StartLinkieAsync()
    {
        FakeKasaDevice device = new(Loopback.NextIp());
        device.Iot.SysInfo = new JsonObject { ["alias"] = "Garden", ["model"] = "KC105", ["type"] = "IOT.IPCAMERA", ["mic_mac"] = "AA" };
        device._app = await Loopback.StartAsync(device.Ip, DevicePorts.Linkie, device.HandleLinkieAsync, options => options.UseHttps(Loopback.CreateCertificate("CN=KASA")));
        return device;
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _tcp?.Stop();
        _udp?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task ServeTcpAsync()
    {
        try
        {
            while (true)
            {
                using TcpClient client = await _tcp!.AcceptTcpClientAsync(_stop.Token);
                try
                {
                    NetworkStream stream = client.GetStream();
                    byte[] header = new byte[4];
                    await stream.ReadExactlyAsync(header);
                    byte[] payload = new byte[BinaryPrimitives.ReadInt32BigEndian(header)];
                    await stream.ReadExactlyAsync(payload);
                    byte[] reply = RawReply ?? Xor.Encrypt(Encoding.UTF8.GetBytes(Iot.Process(JsonNode.Parse(Xor.Decrypt(payload))!.AsObject()).ToJsonString()));
                    BinaryPrimitives.WriteInt32BigEndian(header, ReplyLength ?? reply.Length);
                    await stream.WriteAsync(header);
                    await stream.WriteAsync(reply);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A state the fake cannot process (fuzzing): drop the connection like a crashing device.
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException or IOException)
        {
        }
    }

    private async Task AnswerDiscoveryAsync()
    {
        try
        {
            while (true)
            {
                UdpReceiveResult packet = await _udp!.ReceiveAsync();
                JsonObject request = JsonNode.Parse(Xor.Decrypt(packet.Buffer))!.AsObject();
                await _udp.SendAsync(Xor.Encrypt(Encoding.UTF8.GetBytes(Iot.Process(request).ToJsonString())), packet.RemoteEndPoint);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    private async Task HandleLinkieAsync(HttpContext context)
    {
        if (context.Request.Path != "/data/LINKIE2.json" || context.Request.Headers.Authorization != LinkieAuthorization)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        IFormCollection form = await context.Request.ReadFormAsync();
        JsonObject request = JsonNode.Parse(Xor.Decrypt(Convert.FromBase64String(form["content"]!)))!.AsObject();
        byte[] reply = RawReply ?? Encoding.UTF8.GetBytes(Convert.ToBase64String(Xor.Encrypt(Encoding.UTF8.GetBytes(Iot.Process(request).ToJsonString()))));
        await context.Response.Body.WriteAsync(reply);
    }
}

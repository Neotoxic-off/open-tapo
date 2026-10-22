using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace OpenTapo.Tests.Fakes;

/// <summary>A KLAP v2 device (bulb, plug, hub) on port 80 of a loopback address, speaking the real protocol.</summary>
internal sealed class FakeKlapDevice : IAsyncDisposable
{
    private readonly byte[] _authHash;
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private WebApplication? _app;
    private UdpClient? _udp;
    private Session? _lastSession;

    private FakeKlapDevice(string ip, string email, string password)
    {
        Ip = ip;
        _authHash = SHA256.HashData([.. SHA1.HashData(Encoding.UTF8.GetBytes(email)), .. SHA1.HashData(Encoding.UTF8.GetBytes(password))]);
    }

    public string Ip { get; }

    /// <summary>What get_device_info returns; set_device_info merges into it.</summary>
    public JsonObject Info { get; set; } = new() { ["device_on"] = false, ["model"] = "P110", ["type"] = "SMART.TAPOPLUG" };

    /// <summary>Other getters answered in multipleRequest: a null value is answered with an error code, a missing one too.</summary>
    public Dictionary<string, JsonNode?> Results { get; } = [];

    public ConcurrentQueue<(string Method, JsonNode? Params)> Calls { get; } = new();

    /// <summary>error_code returned by every call other than get_device_info and multipleRequest.</summary>
    public int ErrorCode { get; set; }

    public bool RejectHandshake { get; set; }

    public bool SendCookie { get; set; } = true;

    /// <summary>Replaces the decrypted reply payload of every request.</summary>
    public string? PlainReply { get; set; }

    /// <summary>Replaces the whole /app/request reply (fuzzing, malformed devices).</summary>
    public Func<HttpContext, Task>? RawRequest { get; set; }

    /// <summary>Replaces the whole /app/handshake1 reply.</summary>
    public Func<HttpContext, Task>? RawHandshake { get; set; }

    /// <summary>The "result" this device answers the UDP discovery with; null = silent.</summary>
    public JsonObject? DiscoveryReply { get; set; }

    public bool DiscoveryPacketValid { get; private set; }

    public int Handshakes;

    public static async Task<FakeKlapDevice> StartAsync(string email = Accounts.Email, string password = Accounts.Password, bool answerDiscovery = false)
    {
        FakeKlapDevice device = new(Loopback.NextIp(), email, password);
        device._app = await Loopback.StartAsync(device.Ip, 80, device.HandleAsync);
        if (answerDiscovery)
        {
            device._udp = new UdpClient(new IPEndPoint(IPAddress.Parse(device.Ip), 20002));
            _ = device.AnswerDiscoveryAsync();
        }

        return device;
    }

    public void ExpireSessions()
    {
        _sessions.Clear();
        _lastSession = null;
    }

    public async ValueTask DisposeAsync()
    {
        _udp?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        byte[] body = await Loopback.ReadBodyAsync(context.Request);
        switch (context.Request.Path.Value)
        {
            case "/app/handshake1":
                Interlocked.Increment(ref Handshakes);
                if (RawHandshake is not null)
                {
                    await RawHandshake(context);
                    return;
                }

                if (RejectHandshake)
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                byte[] remoteSeed = RandomNumberGenerator.GetBytes(16);
                Session session = new(body, remoteSeed, _authHash);
                string id = Guid.NewGuid().ToString("N");
                _sessions[id] = session;
                _lastSession = session;
                if (SendCookie)
                {
                    context.Response.Headers.SetCookie = $"TP_SESSIONID={id};TIMEOUT=86400";
                }

                await context.Response.Body.WriteAsync((byte[])[.. remoteSeed, .. SHA256.HashData([.. body, .. remoteSeed, .. _authHash])]);
                return;

            case "/app/handshake2":
                Session? pending = Find(context);
                if (pending is null || !body.SequenceEqual(SHA256.HashData([.. pending.RemoteSeed, .. pending.LocalSeed, .. _authHash])))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                pending.Confirmed = true;
                return;

            case "/app/request":
                Session? current = Find(context);
                if (current is not { Confirmed: true })
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return;
                }

                if (RawRequest is not null)
                {
                    await RawRequest(context);
                    return;
                }

                int sequence = int.Parse(context.Request.Query["seq"]!);
                byte[] cipher = body[32..];
                if (!body[..32].SequenceEqual(SHA256.HashData([.. current.Signature, .. BigEndian(sequence), .. cipher])))
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                byte[] iv = [.. current.IvPrefix, .. BigEndian(sequence)];
                using (Aes aes = Aes.Create())
                {
                    aes.Key = current.Key;
                    JsonNode request = JsonNode.Parse(aes.DecryptCbc(cipher, iv))!;
                    string reply = PlainReply ?? Process((string)request["method"]!, request["params"]).ToJsonString();
                    byte[] encrypted = aes.EncryptCbc(Encoding.UTF8.GetBytes(reply), iv);
                    await context.Response.Body.WriteAsync((byte[])[.. SHA256.HashData([.. current.Signature, .. BigEndian(sequence), .. encrypted]), .. encrypted]);
                }

                return;

            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private JsonObject Process(string method, JsonNode? parameters)
    {
        if (method == "multipleRequest")
        {
            JsonArray responses = [];
            foreach (JsonNode? request in parameters!["requests"]!.AsArray())
            {
                string name = (string)request!["method"]!;
                JsonObject response = new() { ["method"] = name };
                if (name == "get_device_info")
                {
                    response["result"] = Info.DeepClone();
                    response["error_code"] = 0;
                }
                else if (Results.TryGetValue(name, out JsonNode? result) && result is not null)
                {
                    response["result"] = result.DeepClone();
                    response["error_code"] = 0;
                }
                else
                {
                    response["error_code"] = -40210;
                }

                responses.Add(response);
            }

            return new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["responses"] = responses } };
        }

        Calls.Enqueue((method, parameters?.DeepClone()));
        if (method == "get_device_info")
        {
            return new JsonObject { ["error_code"] = 0, ["result"] = Info.DeepClone() };
        }

        if (method == "set_device_info" && ErrorCode == 0)
        {
            foreach ((string key, JsonNode? value) in parameters!.AsObject())
            {
                Info[key] = value?.DeepClone();
            }
        }

        return new JsonObject { ["error_code"] = ErrorCode };
    }

    private Session? Find(HttpContext context)
    {
        string? cookie = context.Request.Headers.Cookie;
        if (cookie is null)
        {
            return _lastSession;
        }

        return _sessions.GetValueOrDefault(cookie.Split('=')[1]);
    }

    private async Task AnswerDiscoveryAsync()
    {
        try
        {
            while (true)
            {
                UdpReceiveResult packet = await _udp!.ReceiveAsync();
                DiscoveryPacketValid = IsValidDiscoveryPacket(packet.Buffer);
                if (DiscoveryReply is not null)
                {
                    byte[] payload = Encoding.UTF8.GetBytes(new JsonObject { ["result"] = DiscoveryReply.DeepClone() }.ToJsonString());
                    await _udp.SendAsync(new byte[16].Concat(payload).ToArray(), packet.RemoteEndPoint);
                }
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SocketException)
        {
        }
    }

    /// <summary>Checks the header the Tapo app sends: version 2, payload length, CRC32 over the packet with a seeded CRC field.</summary>
    private static bool IsValidDiscoveryPacket(byte[] packet)
    {
        if (packet.Length <= 16 || packet[0] != 2 || BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)) != packet.Length - 16)
        {
            return false;
        }

        uint crc = BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(12));
        byte[] seeded = [.. packet];
        BinaryPrimitives.WriteUInt32BigEndian(seeded.AsSpan(12), 0x5A6B7C8D);
        return crc == Crc32(seeded) && JsonNode.Parse(packet.AsSpan(16))?["params"]?["rsa_key"] is not null;
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    private static byte[] BigEndian(int value)
    {
        byte[] buffer = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        return buffer;
    }

    private sealed class Session
    {
        public Session(byte[] localSeed, byte[] remoteSeed, byte[] authHash)
        {
            LocalSeed = localSeed;
            RemoteSeed = remoteSeed;
            byte[] Derive(string label) => SHA256.HashData([.. Encoding.UTF8.GetBytes(label), .. localSeed, .. remoteSeed, .. authHash]);
            Key = Derive("lsk")[..16];
            IvPrefix = Derive("iv")[..12];
            Signature = Derive("ldk")[..28];
        }

        public byte[] LocalSeed { get; }

        public byte[] RemoteSeed { get; }

        public byte[] Key { get; }

        public byte[] IvPrefix { get; }

        public byte[] Signature { get; }

        public bool Confirmed { get; set; }
    }
}

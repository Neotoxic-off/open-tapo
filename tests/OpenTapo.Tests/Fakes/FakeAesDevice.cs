using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using OpenTapo.Tapo;

namespace OpenTapo.Tests.Fakes;

/// <summary>
/// A Tapo device on older firmware (AES transport, HTTP): RSA handshake, login_device inside securePassthrough,
/// then token-authenticated passthrough requests.
/// </summary>
internal sealed class FakeAesDevice : FakeSmartDevice, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private readonly string _username;
    private readonly string _password;
    private readonly int _loginVersion;
    private WebApplication? _app;

    private FakeAesDevice(string ip, int loginVersion)
    {
        Ip = ip;
        _loginVersion = loginVersion;
        _username = Base64(Sha1Hex(Accounts.Email));
        _password = loginVersion == 2 ? Base64(Sha1Hex(Accounts.Password)) : Base64(Accounts.Password);
    }

    public string Ip { get; }

    public int Logins;

    /// <summary>Replaces the decrypted reply payload of every request after login.</summary>
    public string? PlainReply { get; set; }

    /// <summary>Replaces every HTTP reply (fuzzing, malformed devices).</summary>
    public Func<HttpContext, Task>? Raw { get; set; }

    public static async Task<FakeAesDevice> StartAsync(int loginVersion = 1)
    {
        FakeAesDevice device = new(Loopback.NextIp(), loginVersion);
        device._app = await Loopback.StartAsync(device.Ip, DevicePorts.Http, device.HandleAsync);
        return device;
    }

    /// <summary>Forgets every session, as a reboot does: the next passthrough gets error 9999.</summary>
    public void ExpireSessions() => _sessions.Clear();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (Raw is not null)
        {
            await Raw(context);
            return;
        }

        JsonNode request = JsonNode.Parse(await Loopback.ReadBodyAsync(context.Request))!;
        if ((string?)request["method"] == "handshake")
        {
            using RSA client = RSA.Create();
            client.ImportFromPem((string)request["params"]!["key"]!);
            byte[] keyAndIv = RandomNumberGenerator.GetBytes(32);
            string id = Guid.NewGuid().ToString("N");
            _sessions[id] = new Session(keyAndIv[..16], keyAndIv[16..]);
            context.Response.Headers.SetCookie = $"TP_SESSIONID={id};TIMEOUT=1440";
            await Reply(context, new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["key"] = Convert.ToBase64String(client.Encrypt(keyAndIv, RSAEncryptionPadding.Pkcs1)) } });
            return;
        }

        string? cookie = context.Request.Headers.Cookie;
        if (cookie is null || !_sessions.TryGetValue(cookie.Split('=')[1], out Session? session))
        {
            await Reply(context, new JsonObject { ["error_code"] = 9999 });
            return;
        }

        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        JsonNode inner = JsonNode.Parse(aes.DecryptCbc(Convert.FromBase64String((string)request["params"]!["request"]!), session.Iv))!;
        string method = (string)inner["method"]!;
        string reply;
        if (method == "login_device")
        {
            Interlocked.Increment(ref Logins);
            string field = _loginVersion == 2 ? "password2" : "password";
            bool valid = (string?)inner["params"]!["username"] == _username && (string?)inner["params"]![field] == _password;
            session.Token = valid ? Guid.NewGuid().ToString("N") : null;
            reply = (valid
                ? new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["token"] = session.Token } }
                : new JsonObject { ["error_code"] = -1501 }).ToJsonString();
        }
        else if (session.Token is null || context.Request.Query["token"] != session.Token)
        {
            reply = new JsonObject { ["error_code"] = -1501 }.ToJsonString();
        }
        else
        {
            reply = PlainReply ?? Process(method, inner["params"]).ToJsonString();
        }

        string encrypted = Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(reply), session.Iv));
        await Reply(context, new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["response"] = encrypted } });
    }

    private static Task Reply(HttpContext context, JsonObject body) => context.Response.WriteAsync(body.ToJsonString());

    private static string Sha1Hex(string value) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Base64(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private sealed record Session(byte[] Key, byte[] Iv)
    {
        public string? Token { get; set; }
    }
}

/// <summary>A Tapo robot vacuum (SSL transport, HTTPS): login with email and MD5(password), then token-authenticated plain requests.</summary>
internal sealed class FakeVacuum : FakeSmartDevice, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, bool> _tokens = new();
    private WebApplication? _app;

    private FakeVacuum(string ip) => Ip = ip;

    public string Ip { get; }

    public int Logins;

    /// <summary>Replaces every HTTP reply (fuzzing, malformed devices).</summary>
    public Func<HttpContext, Task>? Raw { get; set; }

    public static async Task<FakeVacuum> StartAsync()
    {
        FakeVacuum vacuum = new(Loopback.NextIp());
        vacuum.Info = new JsonObject { ["model"] = "RV30 Max", ["type"] = "SMART.TAPOROBOVAC", ["nickname"] = "Um9ibw==", ["rssi"] = -50 };
        vacuum._app = await Loopback.StartAsync(vacuum.Ip, DevicePorts.Https, vacuum.HandleAsync, options => options.UseHttps(Loopback.CreateCertificate("CN=TAPO-DEVICE")));
        return vacuum;
    }

    /// <summary>Forgets every token, as a reboot does: the next request gets HTTP 401.</summary>
    public void ExpireSessions() => _tokens.Clear();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        if (Raw is not null)
        {
            await Raw(context);
            return;
        }

        JsonNode request = JsonNode.Parse(await Loopback.ReadBodyAsync(context.Request))!;
        string method = (string)request["method"]!;
        string? token = context.Request.Query["token"];
        JsonObject reply;
        if (token is null && method == "login")
        {
            Interlocked.Increment(ref Logins);
            bool valid = (string?)request["params"]!["username"] == Accounts.Email && (string?)request["params"]!["password"] == Loopback.Md5Hex(Accounts.Password);
            string issued = Guid.NewGuid().ToString("N");
            if (valid)
            {
                _tokens[issued] = true;
            }

            reply = valid ? new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["token"] = issued } } : new JsonObject { ["error_code"] = -1501 };
        }
        else if (token is null || !_tokens.ContainsKey(token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }
        else
        {
            reply = Process(method, request["params"]);
        }

        await context.Response.WriteAsync(reply.ToJsonString());
    }
}

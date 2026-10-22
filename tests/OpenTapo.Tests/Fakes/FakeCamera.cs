using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace OpenTapo.Tests.Fakes;

/// <summary>
/// A Tapo camera on HTTPS port 443 of a loopback address: nonce login with device_confirm, then
/// securePassthrough requests checked against the Tapo_tag header and sequence number.
/// </summary>
internal sealed class FakeCamera : IAsyncDisposable
{
    private readonly string _passwordHash;
    private readonly ConcurrentDictionary<string, string> _challenges = new();
    private readonly ConcurrentDictionary<string, Session> _sessions = new();
    private WebApplication? _app;

    private FakeCamera(string ip, string password, bool md5)
    {
        Ip = ip;
        _passwordHash = md5 ? Loopback.Md5Hex(password) : Loopback.Sha256Hex(password);
    }

    public string Ip { get; }

    /// <summary>Getter results by method; a missing or null entry is answered with an error code.</summary>
    public Dictionary<string, JsonNode?> Results { get; } = [];

    public ConcurrentQueue<(string Method, JsonNode? Params)> Calls { get; } = new();

    public int Challenges;

    public int Logins;

    public int TagMismatches;

    /// <summary>Lock sign-in like a camera after 10 failures; the value is sec_left, or null to omit it.</summary>
    public bool Locked { get; set; }

    public int? LockedSecondsLeft { get; set; }

    public bool OmitNonce { get; set; }

    public bool OmitStok { get; set; }

    public bool OmitStartSeq { get; set; }

    /// <summary>Some firmware puts the challenge in a top-level "data" instead of "result.data".</summary>
    public bool DataAtTopLevel { get; set; }

    /// <summary>error_code of every securePassthrough reply.</summary>
    public int ErrorCode { get; set; }

    public bool OmitResponse { get; set; }

    /// <summary>Replaces the decrypted reply payload.</summary>
    public string? PlainReply { get; set; }

    /// <summary>Replaces every HTTP reply (fuzzing, malformed devices).</summary>
    public Func<HttpContext, Task>? Raw { get; set; }

    public static async Task<FakeCamera> StartAsync(string password = Accounts.Password, bool md5 = false, string subject = "CN=TPRI-DEVICE")
    {
        FakeCamera camera = new(Loopback.NextIp(), password, md5);
        X509Certificate2 certificate = Loopback.CreateCertificate(subject);
        camera._app = await Loopback.StartAsync(camera.Ip, 443, camera.HandleAsync, options => options.UseHttps(certificate));
        return camera;
    }

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
        string body = Encoding.UTF8.GetString(await Loopback.ReadBodyAsync(context.Request));
        if (Raw is not null)
        {
            await Raw(context);
            return;
        }

        JsonNode reply = context.Request.Path.Value == "/" ? Login(JsonNode.Parse(body)!["params"]!) : Passthrough(context, body);
        await context.Response.WriteAsync(reply.ToJsonString());
    }

    private JsonNode Login(JsonNode parameters)
    {
        string clientNonce = (string)parameters["cnonce"]!;
        if ((string?)parameters["digest_passwd"] is not string digest)
        {
            Interlocked.Increment(ref Challenges);
            JsonObject data;
            if (Locked)
            {
                data = new JsonObject { ["code"] = -40404 };
                if (LockedSecondsLeft is int seconds)
                {
                    data["sec_left"] = seconds;
                }
            }
            else
            {
                string nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
                _challenges[clientNonce] = nonce;
                data = new JsonObject
                {
                    ["code"] = -40401,
                    ["device_confirm"] = Loopback.Sha256Hex(clientNonce + _passwordHash + nonce) + nonce + clientNonce,
                };
                if (!OmitNonce)
                {
                    data["nonce"] = nonce;
                }
            }

            return DataAtTopLevel
                ? new JsonObject { ["error_code"] = -40413, ["data"] = data }
                : new JsonObject { ["error_code"] = -40413, ["result"] = new JsonObject { ["data"] = data } };
        }

        string deviceNonce = _challenges[clientNonce];
        if (digest != Loopback.Sha256Hex(_passwordHash + clientNonce + deviceNonce) + clientNonce + deviceNonce)
        {
            return new JsonObject { ["error_code"] = -40401, ["result"] = new JsonObject { ["data"] = new JsonObject { ["code"] = -40401 } } };
        }

        Interlocked.Increment(ref Logins);
        if (OmitStok)
        {
            return new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject() };
        }

        string token = Guid.NewGuid().ToString("N");
        string seed = clientNonce + deviceNonce + Loopback.Sha256Hex(clientNonce + _passwordHash + deviceNonce);
        Session session = new(
            SHA256.HashData(Encoding.UTF8.GetBytes("lsk" + seed))[..16],
            SHA256.HashData(Encoding.UTF8.GetBytes("ivb" + seed))[..16],
            Loopback.Sha256Hex(_passwordHash + clientNonce))
        {
            Sequence = OmitStartSeq ? 0 : 41,
        };
        _sessions[token] = session;
        JsonObject result = new() { ["stok"] = token };
        if (!OmitStartSeq)
        {
            result["start_seq"] = session.Sequence;
        }

        return new JsonObject { ["error_code"] = 0, ["result"] = result };
    }

    private JsonNode Passthrough(HttpContext context, string body)
    {
        string token = context.Request.Path.Value!.Split('/')[1]["stok=".Length..];
        if (!_sessions.TryGetValue(token, out Session? session))
        {
            return new JsonObject { ["error_code"] = -40401 };
        }

        string expectedTag = Loopback.Sha256Hex(session.TagPrefix + body + session.Sequence);
        if (context.Request.Headers["Tapo_tag"] != expectedTag || context.Request.Headers["Seq"] != session.Sequence.ToString())
        {
            Interlocked.Increment(ref TagMismatches);
            return new JsonObject { ["error_code"] = -40401 };
        }

        session.Sequence++;
        if (ErrorCode != 0)
        {
            return new JsonObject { ["error_code"] = ErrorCode };
        }

        if (OmitResponse)
        {
            return new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject() };
        }

        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        string encrypted = (string)JsonNode.Parse(body)!["params"]!["request"]!;
        JsonNode request = JsonNode.Parse(aes.DecryptCbc(Convert.FromBase64String(encrypted), session.Iv))!;
        JsonArray responses = [];
        foreach (JsonNode? call in request["params"]!["requests"]!.AsArray())
        {
            string method = (string)call!["method"]!;
            Calls.Enqueue((method, call["params"]?.DeepClone()));
            JsonObject response = new() { ["method"] = method };
            if (method.StartsWith("set", StringComparison.Ordinal) || method == "do")
            {
                response["result"] = new JsonObject();
                response["error_code"] = 0;
            }
            else if (Results.GetValueOrDefault(method) is JsonNode result)
            {
                response["result"] = result.DeepClone();
                response["error_code"] = 0;
            }
            else
            {
                response["error_code"] = -40106;
            }

            responses.Add(response);
        }

        string plain = PlainReply ?? new JsonObject
        {
            ["error_code"] = 0,
            ["result"] = new JsonObject { ["responses"] = responses },
        }.ToJsonString();
        string cipher = Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), session.Iv));
        return new JsonObject { ["error_code"] = 0, ["result"] = new JsonObject { ["response"] = cipher } };
    }

    private sealed record Session(byte[] Key, byte[] Iv, string TagPrefix)
    {
        public int Sequence { get; set; }
    }
}

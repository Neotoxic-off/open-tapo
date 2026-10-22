using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// AES transport of older Tapo firmware (HTTP :80, before KLAP): an RSA handshake hands over an AES-128-CBC
/// key, then login_device and every request travel inside "securePassthrough". Unlike KLAP, the login sends
/// the password (encrypted for the session) before the device has proven anything.
/// </summary>
public sealed class AesClient(string ip, string email, string password, int loginVersion = 1) : SmartClient
{
    private const int LoginFailed = -1501;
    private const int SessionExpired = 9999;
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(6));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private AesSession? _session;

    public override async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
    {
        JsonObject request = Request(method, parameters);
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool reusedSession = _session is not null;
                try
                {
                    _session ??= await LoginAsync();
                    return ResultOf(await PassthroughAsync(_session, request), method);
                }
                catch (Exception ex) when (ex is not TapoException && reusedSession && attempt == 0)
                {
                    // Session expired or the device rebooted: handshake again once.
                    _session = null;
                }
            }
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<AesSession> LoginAsync()
    {
        using RSA rsa = RSA.Create(1024);
        JsonObject handshake = new()
        {
            ["method"] = "handshake",
            ["params"] = new JsonObject { ["key"] = rsa.ExportSubjectPublicKeyInfoPem() + "\n" },
        };
        using HttpResponseMessage response = await Http.PostAsync(Url(token: null), Json(handshake));
        response.EnsureSuccessStatusCode();
        string? cookie = response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            ? cookies.Select(value => value.Split(';')[0]).FirstOrDefault(value => value.StartsWith("TP_SESSIONID=", StringComparison.Ordinal) || value.StartsWith("SESSIONID=", StringComparison.Ordinal))
            : null;
        JsonNode reply = JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new TapoException("Empty handshake reply from the device.");
        byte[] keyAndIv = rsa.Decrypt(Convert.FromBase64String((string)ResultOf(reply, "handshake")["key"]!), RSAEncryptionPadding.Pkcs1);
        AesSession session = new(keyAndIv[..16], keyAndIv[16..32], cookie, Token: null);

        // Login v2 firmware expects the password as a SHA1 hex digest under "password2".
        JsonObject credentials = new() { ["username"] = Base64(Sha1Hex(email)) };
        credentials[loginVersion == 2 ? "password2" : "password"] = Base64(loginVersion == 2 ? Sha1Hex(password) : password);
        JsonObject login = new()
        {
            ["method"] = "login_device",
            ["params"] = credentials,
            ["request_time_milis"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        JsonNode loginReply = await PassthroughAsync(session, login);
        if ((int?)loginReply["error_code"] == LoginFailed)
        {
            throw new TapoAuthException("The device rejected these Tapo credentials.");
        }

        return session with { Token = (string)ResultOf(loginReply, "login_device")["token"]! };
    }

    private async Task<JsonNode> PassthroughAsync(AesSession session, JsonObject request)
    {
        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        JsonObject envelope = new()
        {
            ["method"] = "securePassthrough",
            ["params"] = new JsonObject { ["request"] = Convert.ToBase64String(aes.EncryptCbc(Bytes.Utf8(request.ToJsonString()), session.Iv)) },
        };
        using HttpRequestMessage message = new(HttpMethod.Post, Url(session.Token)) { Content = Json(envelope) };
        if (session.Cookie is not null)
        {
            message.Headers.Add("Cookie", session.Cookie);
        }

        using HttpResponseMessage response = await Http.SendAsync(message);
        response.EnsureSuccessStatusCode();
        JsonNode reply = JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new TapoException("Empty response from the device.");
        if ((int?)reply["error_code"] == SessionExpired)
        {
            throw new HttpRequestException("The device session expired.");
        }

        string encrypted = (string)ResultOf(reply, "securePassthrough")["response"]!;
        return JsonNode.Parse(aes.DecryptCbc(Convert.FromBase64String(encrypted), session.Iv))
            ?? throw new TapoException("Empty response from the device.");
    }

    private string Url(string? token) => $"http://{ip}:{DevicePorts.Http}/app{(token is null ? "" : "?token=" + Uri.EscapeDataString(token))}";

    private static StringContent Json(JsonObject body) => new(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");

    private static string Sha1Hex(string value) => Convert.ToHexStringLower(SHA1.HashData(Bytes.Utf8(value)));

    private static string Base64(string value) => Convert.ToBase64String(Bytes.Utf8(value));

    private sealed record AesSession(byte[] Key, byte[] Iv, string? Cookie, string? Token);
}

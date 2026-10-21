using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

public sealed record CameraRequest(string Method, string Parameters);

/// <summary>
/// Secure camera API (HTTPS :443, encrypt_type 3, user "admin" + account password).
/// Login exchanges a client/device nonce; requests are wrapped in "securePassthrough" (AES-128-CBC)
/// and authenticated with a Tapo_tag header over the body and a sequence number.
/// </summary>
public sealed class CameraClient
{
    // shortcut: cameras use a self-signed "TPRI-DEVICE" / "TAPO-DEVICE" certificate, accepted without pinning; pin its thumbprint if the LAN is untrusted.
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(8), (_, _, _, _) => true);
    private static readonly TapoAuthException Rejection = new("The camera rejected this password. If it is correct, enable Third-Party Compatibility in the Tapo app.");

    private const int LockedCode = -40404;

    private readonly string ip;
    private readonly string password;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CameraSession? _session;
    private (DateTime Until, TapoException Reason)? _blocked;

    /// <param name="knownRejected">The camera already rejected this password in an earlier run: do not spend another attempt on it.</param>
    public CameraClient(string ip, string password, bool knownRejected = false)
    {
        this.ip = ip;
        this.password = password;
        if (knownRejected)
        {
            _blocked = (DateTime.MaxValue, Rejection);
        }
    }

    /// <summary>Raised when the camera rejects the password, so the rejection can outlive this process.</summary>
    public event EventHandler? Rejected;

    /// <summary>Runs several calls in one round trip. Returns method → result, null for calls the camera rejected.</summary>
    public async Task<IReadOnlyDictionary<string, JsonNode?>> ExecuteAsync(params CameraRequest[] requests)
    {
        JsonArray calls = new(requests
            .Select(request => (JsonNode)new JsonObject { ["method"] = request.Method, ["params"] = JsonNode.Parse(request.Parameters) })
            .ToArray());
        JsonObject batch = new()
        {
            ["method"] = "multipleRequest",
            ["params"] = new JsonObject { ["requests"] = calls },
        };

        await _gate.WaitAsync();
        try
        {
            // Every unfinished login challenge counts against the camera's 10 attempts: never retry a known failure.
            if (_blocked is { } blocked && DateTime.UtcNow < blocked.Until)
            {
                throw blocked.Reason;
            }

            for (int attempt = 0; ; attempt++)
            {
                bool reusedSession = _session is not null;
                try
                {
                    _session ??= await LoginAsync();
                    JsonNode reply = await SendAsync(_session, batch);
                    return ParseResponses(reply);
                }
                catch (TapoAuthException ex)
                {
                    _blocked = (DateTime.MaxValue, ex);
                    Rejected?.Invoke(this, EventArgs.Empty);
                    throw;
                }
                catch (TapoLockedException ex)
                {
                    _blocked = (DateTime.UtcNow + ex.RetryAfter, ex);
                    throw;
                }
                catch (Exception) when (reusedSession && attempt == 0)
                {
                    // Token expired (error code reply) or camera rebooted: log in again once.
                    // A failed fresh login is never retried here, because each unfinished challenge costs an attempt.
                    _session = null;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<CameraSession> LoginAsync()
    {
        string clientNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
        JsonNode challenge = await PostAsync($"https://{ip}/", LoginBody(clientNonce, digest: null), headers: null);
        JsonNode? data = challenge["result"]?["data"] ?? challenge["data"];
        if ((int?)data?["code"] == LockedCode)
        {
            throw new TapoLockedException(TimeSpan.FromSeconds((int?)data?["sec_left"] ?? 1800));
        }

        string deviceNonce = (string?)data?["nonce"] ?? throw new TapoException("The camera did not return a nonce.");
        string? deviceConfirm = (string?)data?["device_confirm"];

        // device_confirm tells us locally whether the password is right, so a wrong one never reaches the second login step.
        string passwordHash = FindPasswordHash(clientNonce, deviceNonce, deviceConfirm)
            ?? throw Rejection;

        string digest = Bytes.Sha256Hex(passwordHash + clientNonce + deviceNonce) + clientNonce + deviceNonce;
        JsonNode? login = (await PostAsync($"https://{ip}/", LoginBody(clientNonce, digest), headers: null))["result"];
        string token = (string?)login?["stok"] ?? throw new TapoAuthException("The camera refused the login.");

        // Keys derive from SHA256(cnonce + hash + nonce), not from the password hash directly.
        string seed = clientNonce + deviceNonce + Bytes.Sha256Hex(clientNonce + passwordHash + deviceNonce);
        return new CameraSession(
            Token: token,
            Key: SHA256.HashData(Bytes.Utf8("lsk" + seed))[..16],
            Iv: SHA256.HashData(Bytes.Utf8("ivb" + seed))[..16],
            TagPrefix: Bytes.Sha256Hex(passwordHash + clientNonce))
        {
            Sequence = (int?)login?["start_seq"] ?? 0,
        };
    }

    private string? FindPasswordHash(string clientNonce, string deviceNonce, string? deviceConfirm)
    {
        foreach (string hash in new[] { Bytes.Sha256Hex(password), Bytes.Md5Hex(password) })
        {
            if (deviceConfirm == Bytes.Sha256Hex(clientNonce + hash + deviceNonce) + deviceNonce + clientNonce)
            {
                return hash;
            }
        }

        return null;
    }

    private static string LoginBody(string clientNonce, string? digest)
    {
        JsonObject parameters = new()
        {
            ["cnonce"] = clientNonce,
            ["encrypt_type"] = "3",
            ["username"] = "admin",
        };
        if (digest is not null)
        {
            parameters["digest_passwd"] = digest;
        }

        return new JsonObject { ["method"] = "login", ["params"] = parameters }.ToJsonString();
    }

    private async Task<JsonNode> SendAsync(CameraSession session, JsonObject request)
    {
        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        string encrypted = Convert.ToBase64String(aes.EncryptCbc(Bytes.Utf8(request.ToJsonString()), session.Iv));
        // The camera checks Tapo_tag against the body re-printed the way Python's json.dumps does
        // (", " / ": " separators, '+' unescaped), so the envelope is written in exactly that form.
        string envelope = $$$"""{"method": "securePassthrough", "params": {"request": "{{{encrypted}}}"}}""";
        string tag = Bytes.Sha256Hex(session.TagPrefix + envelope + session.Sequence);
        Dictionary<string, string> headers = new()
        {
            ["Tapo_tag"] = tag,
            ["Seq"] = session.Sequence.ToString(),
        };

        JsonNode reply = await PostAsync($"https://{ip}/stok={session.Token}/ds", envelope, headers);
        session.Sequence++;
        int errorCode = (int?)reply["error_code"] ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException($"Camera: error {errorCode}");
        }

        string response = (string?)reply["result"]?["response"] ?? throw new TapoException("Empty response from the camera.");
        return JsonNode.Parse(aes.DecryptCbc(Convert.FromBase64String(response), session.Iv))
            ?? throw new TapoException("Unreadable response from the camera.");
    }

    private async Task<JsonNode> PostAsync(string url, string body, IReadOnlyDictionary<string, string>? headers)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        request.Headers.TryAddWithoutValidation("Referer", $"https://{ip}");
        request.Headers.TryAddWithoutValidation("requestByApp", "true");
        foreach (KeyValuePair<string, string> header in headers ?? new Dictionary<string, string>())
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        using HttpResponseMessage response = await Http.SendAsync(request);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new TapoException("Empty response from the camera.");
    }

    private static Dictionary<string, JsonNode?> ParseResponses(JsonNode reply)
    {
        Dictionary<string, JsonNode?> results = [];
        foreach (JsonNode? item in reply["result"]?["responses"]?.AsArray() ?? [])
        {
            string? method = (string?)item?["method"];
            if (method is not null)
            {
                results[method] = (int?)item!["error_code"] == 0 ? item["result"] : null;
            }
        }

        return results;
    }

    private sealed record CameraSession(string Token, byte[] Key, byte[] Iv, string TagPrefix)
    {
        public int Sequence { get; set; }
    }
}

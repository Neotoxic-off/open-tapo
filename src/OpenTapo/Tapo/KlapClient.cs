using System.Net.Http;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// KLAP v2 transport used by Tapo lights and plugs (HTTP :80).
/// handshake1/2 exchange 16-byte seeds proving both sides know SHA256(SHA1(email) + SHA1(password)),
/// then each request is AES-128-CBC encrypted and SHA256-signed with a sequence number.
/// </summary>
public sealed class KlapClient(string ip, string email, string password)
{
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(6));

    private readonly SemaphoreSlim _gate = new(1, 1);
    private KlapSession? _session;

    public async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
    {
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool reusedSession = _session is not null;
                try
                {
                    _session ??= await HandshakeAsync();
                    return await SendAsync(_session, method, parameters);
                }
                catch (Exception ex) when (ex is not TapoException && reusedSession && attempt == 0)
                {
                    // Session expired (~24 h) or the device rebooted: handshake again once.
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

    /// <summary>Runs several getters in one round trip. Returns method → result, null for calls the device rejected or lacks.</summary>
    public async Task<IReadOnlyDictionary<string, JsonNode?>> ExecuteAsync(params string[] methods)
    {
        JsonArray requests = new(methods.Select(method => (JsonNode)new JsonObject { ["method"] = method }).ToArray());
        JsonNode reply = await CallAsync("multipleRequest", new JsonObject { ["requests"] = requests });
        Dictionary<string, JsonNode?> results = [];
        foreach (JsonNode? response in reply["responses"]?.AsArray() ?? [])
        {
            if ((string?)response?["method"] is string method)
            {
                results[method] = (int?)response["error_code"] == 0 ? response["result"] : null;
            }
        }

        return results;
    }

    private async Task<KlapSession> HandshakeAsync()
    {
        byte[] authHash = SHA256.HashData([.. SHA1.HashData(Bytes.Utf8(email)), .. SHA1.HashData(Bytes.Utf8(password))]);
        byte[] localSeed = RandomNumberGenerator.GetBytes(16);

        using HttpResponseMessage first = await PostAsync("handshake1", localSeed, cookie: null);
        if (first.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Firmware 1.4+ only speaks TPAP (SPAKE2+) unless the app's compatibility switch re-enables KLAP.
            throw new TapoException("This light requires TP-Link's newer TPAP protocol. Enable Third-Party Compatibility in the Tapo app.");
        }

        first.EnsureSuccessStatusCode();
        string? cookie = first.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            ? cookies.First().Split(';')[0]
            : null;
        byte[] reply = await first.Content.ReadAsByteArrayAsync();
        if (reply.Length != 48)
        {
            throw new TapoException("Unexpected handshake reply from the light.");
        }

        byte[] remoteSeed = reply[..16];
        byte[] expected = SHA256.HashData([.. localSeed, .. remoteSeed, .. authHash]);
        if (!CryptographicOperations.FixedTimeEquals(expected, reply.AsSpan(16)))
        {
            throw new TapoAuthException("The light rejected these Tapo credentials.");
        }

        using HttpResponseMessage second = await PostAsync("handshake2", SHA256.HashData([.. remoteSeed, .. localSeed, .. authHash]), cookie);
        second.EnsureSuccessStatusCode();

        byte[] Derive(string label) => SHA256.HashData([.. Bytes.Utf8(label), .. localSeed, .. remoteSeed, .. authHash]);
        byte[] iv = Derive("iv");
        return new KlapSession(
            Key: Derive("lsk")[..16],
            IvPrefix: iv[..12],
            Signature: Derive("ldk")[..28],
            Cookie: cookie)
        {
            Sequence = BinaryPrimitives.ReadInt32BigEndian(iv.AsSpan(28)),
        };
    }

    private async Task<JsonNode> SendAsync(KlapSession session, string method, JsonObject? parameters)
    {
        JsonObject request = new() { ["method"] = method };
        if (parameters is not null)
        {
            request["params"] = parameters;
        }

        int sequence = unchecked(++session.Sequence);
        byte[] iv = [.. session.IvPrefix, .. Bytes.BigEndian(sequence)];
        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        byte[] cipher = aes.EncryptCbc(Bytes.Utf8(request.ToJsonString()), iv);
        byte[] signature = SHA256.HashData([.. session.Signature, .. Bytes.BigEndian(sequence), .. cipher]);

        using HttpResponseMessage response = await PostAsync($"request?seq={sequence}", [.. signature, .. cipher], session.Cookie);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        JsonNode reply = JsonNode.Parse(aes.DecryptCbc(body.AsSpan(32), iv))
            ?? throw new TapoException("Empty response from the light.");
        int errorCode = (int?)reply["error_code"] ?? 0;
        if (errorCode != 0)
        {
            throw new TapoException($"{method}: error {errorCode}");
        }

        return reply["result"] ?? new JsonObject();
    }

    private Task<HttpResponseMessage> PostAsync(string path, byte[] body, string? cookie)
    {
        HttpRequestMessage request = new(HttpMethod.Post, $"http://{ip}/app/{path}") { Content = new ByteArrayContent(body) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return Http.SendAsync(request);
    }

    private sealed record KlapSession(byte[] Key, byte[] IvPrefix, byte[] Signature, string? Cookie)
    {
        public int Sequence { get; set; }
    }
}

using System.Net.Http;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// KLAP transport used by Tapo lights and plugs (HTTP :80) and Kasa devices on newer firmware.
/// handshake1/2 exchange 16-byte seeds proving both sides know the account hash, then each request is
/// AES-128-CBC encrypted and SHA256-signed with a sequence number.
/// v2 (Tapo): hash SHA256(SHA1(email) + SHA1(password)), seeds hashed together.
/// v1 (Kasa): hash MD5(MD5(email) + MD5(password)), each seed hashed alone. The payload is whatever JSON the
/// device speaks: the smart API for Tapo, the IOT API for Kasa.
/// </summary>
public sealed class KlapClient(string ip, string email, string password, int version = 2, bool https = false) : SmartClient, IIotTransport
{
    // shortcut: devices on HTTPS use a self-signed certificate, accepted without pinning (the KLAP handshake authenticates the device).
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(6), (_, _, _, _) => true);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private KlapSession? _session;

    public override async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null) =>
        ResultOf(await QueryAsync(Request(method, parameters)), method);

    /// <summary>Sends one raw JSON payload and returns the device's decrypted reply.</summary>
    public async Task<JsonNode> QueryAsync(JsonObject request)
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
                    return await SendAsync(_session, request);
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

    private async Task<KlapSession> HandshakeAsync()
    {
        byte[] authHash = version == 1
            ? MD5.HashData([.. MD5.HashData(Bytes.Utf8(email)), .. MD5.HashData(Bytes.Utf8(password))])
            : SHA256.HashData([.. SHA1.HashData(Bytes.Utf8(email)), .. SHA1.HashData(Bytes.Utf8(password))]);
        byte[] localSeed = RandomNumberGenerator.GetBytes(16);

        using HttpResponseMessage first = await PostAsync("handshake1", localSeed, cookie: null);
        if (first.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            // Firmware 1.4+ only speaks TPAP (SPAKE2+) unless the app's compatibility switch re-enables KLAP.
            throw new TapoException("This device requires TP-Link's newer TPAP protocol. Enable Third-Party Compatibility in the Tapo app.");
        }

        first.EnsureSuccessStatusCode();
        string? cookie = first.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? cookies)
            ? cookies.First().Split(';')[0]
            : null;
        byte[] reply = await first.Content.ReadAsByteArrayAsync();
        if (reply.Length != 48)
        {
            throw new TapoException("Unexpected handshake reply from the device.");
        }

        byte[] remoteSeed = reply[..16];
        byte[] expected = version == 1
            ? SHA256.HashData([.. localSeed, .. authHash])
            : SHA256.HashData([.. localSeed, .. remoteSeed, .. authHash]);
        if (!CryptographicOperations.FixedTimeEquals(expected, reply.AsSpan(16)))
        {
            throw new TapoAuthException("The device rejected these Tapo credentials.");
        }

        byte[] confirm = version == 1
            ? SHA256.HashData([.. remoteSeed, .. authHash])
            : SHA256.HashData([.. remoteSeed, .. localSeed, .. authHash]);
        using HttpResponseMessage second = await PostAsync("handshake2", confirm, cookie);
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

    private async Task<JsonNode> SendAsync(KlapSession session, JsonObject request)
    {
        int sequence = unchecked(++session.Sequence);
        byte[] iv = [.. session.IvPrefix, .. Bytes.BigEndian(sequence)];
        using Aes aes = Aes.Create();
        aes.Key = session.Key;
        byte[] cipher = aes.EncryptCbc(Bytes.Utf8(request.ToJsonString()), iv);
        byte[] signature = SHA256.HashData([.. session.Signature, .. Bytes.BigEndian(sequence), .. cipher]);

        using HttpResponseMessage response = await PostAsync($"request?seq={sequence}", [.. signature, .. cipher], session.Cookie);
        response.EnsureSuccessStatusCode();
        byte[] body = await response.Content.ReadAsByteArrayAsync();
        return JsonNode.Parse(aes.DecryptCbc(body.AsSpan(32), iv))
            ?? throw new TapoException("Empty response from the device.");
    }

    private Task<HttpResponseMessage> PostAsync(string path, byte[] body, string? cookie)
    {
        string root = https ? $"https://{ip}:{DevicePorts.Https}" : $"http://{ip}:{DevicePorts.Http}";
        HttpRequestMessage request = new(HttpMethod.Post, $"{root}/app/{path}") { Content = new ByteArrayContent(body) };
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

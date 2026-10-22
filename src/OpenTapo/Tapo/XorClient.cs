using System.Buffers.Binary;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>Carries a Kasa IOT JSON payload ({"system":{"get_sysinfo":{}}}) and returns the device's reply.</summary>
public interface IIotTransport
{
    Task<JsonNode> QueryAsync(JsonObject request);
}

/// <summary>Kasa's autokey XOR cipher (key 171): obfuscation, not encryption.</summary>
internal static class Xor
{
    public static byte[] Encrypt(byte[] plain)
    {
        byte[] result = new byte[plain.Length];
        byte key = 171;
        for (int i = 0; i < plain.Length; i++)
        {
            key ^= plain[i];
            result[i] = key;
        }

        return result;
    }

    public static byte[] Decrypt(ReadOnlySpan<byte> cipher)
    {
        byte[] result = new byte[cipher.Length];
        byte key = 171;
        for (int i = 0; i < cipher.Length; i++)
        {
            result[i] = (byte)(key ^ cipher[i]);
            key = cipher[i];
        }

        return result;
    }
}

/// <summary>
/// Legacy Kasa protocol (HS100, HS110, KL130…): length-prefixed XOR JSON over TCP :9999, no account at all,
/// so anyone on the LAN can control these devices.
/// </summary>
public sealed class XorClient(string ip) : IIotTransport
{
    private const int MaxReplyBytes = 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public async Task<JsonNode> QueryAsync(JsonObject request)
    {
        using CancellationTokenSource timeout = new(Timeout);
        try
        {
            using TcpClient tcp = new();
            await tcp.ConnectAsync(ip, DevicePorts.Xor, timeout.Token);
            NetworkStream stream = tcp.GetStream();
            byte[] payload = Xor.Encrypt(Bytes.Utf8(request.ToJsonString()));
            await stream.WriteAsync(Bytes.BigEndian(payload.Length), timeout.Token);
            await stream.WriteAsync(payload, timeout.Token);

            byte[] header = new byte[4];
            await stream.ReadExactlyAsync(header, timeout.Token);
            int length = BinaryPrimitives.ReadInt32BigEndian(header);
            if (length is < 0 or > MaxReplyBytes)
            {
                throw new TapoException("Unexpected reply from the device.");
            }

            byte[] reply = new byte[length];
            await stream.ReadExactlyAsync(reply, timeout.Token);
            return JsonNode.Parse(Xor.Decrypt(reply)) ?? throw new TapoException("Empty response from the device.");
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
        {
            // Same meaning as an HTTP device that does not answer: offline.
            throw new HttpRequestException(ex.Message, ex);
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }
    }
}

/// <summary>
/// Kasa cameras (KC…): XOR JSON, base64 and form-encoded, POSTed to HTTPS :10443 with the fixed
/// credentials every Kasa camera accepts on the LAN.
/// </summary>
public sealed class LinkieClient(string ip) : IIotTransport
{
    // admin : MD5("admin"), python-kasa's KASACAMERA default.
    private const string Authorization = "YWRtaW46MjEyMzJmMjk3YTU3YTVhNzQzODk0YTBlNGE4MDFmYzM=";

    // shortcut: Kasa cameras use a self-signed certificate, accepted without pinning; pin its thumbprint if the LAN is untrusted.
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(8), (_, _, _, _) => true);

    public async Task<JsonNode> QueryAsync(JsonObject request)
    {
        string command = Convert.ToBase64String(Xor.Encrypt(Bytes.Utf8(request.ToJsonString())));
        using HttpRequestMessage message = new(HttpMethod.Post, $"https://{ip}:{DevicePorts.Linkie}/data/LINKIE2.json")
        {
            Content = new FormUrlEncodedContent([new("content", command)]),
        };
        message.Headers.Authorization = new("Basic", Authorization);
        using HttpResponseMessage response = await Http.SendAsync(message);
        response.EnsureSuccessStatusCode();
        try
        {
            return JsonNode.Parse(Xor.Decrypt(Convert.FromBase64String(await response.Content.ReadAsStringAsync())))
                ?? throw new TapoException("Empty response from the device.");
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }
    }
}

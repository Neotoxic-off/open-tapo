using System.IO;
using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// Finds Tapo devices on the local network: the app's UDP 20002 discovery (lights, plugs),
/// then a TLS fingerprint on 443 for cameras that ignore it.
/// </summary>
public static class TapoDiscovery
{
    private const int DiscoveryPort = 20002;
    private const int HeaderLength = 16;
    private const int SioUdpConnReset = unchecked((int)0x9800000C);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan TlsTimeout = TimeSpan.FromSeconds(4);
    private static readonly string[] CameraCertificateNames = ["CN=TPRI-DEVICE", "CN=TAPO-DEVICE"];

    /// <summary>Tests aim discovery at loopback fakes so a test run never probes the user's real devices.</summary>
    internal static IPAddress BroadcastAddress { get; set; } = IPAddress.Broadcast;

    internal static Func<IEnumerable<string>> Hosts { get; set; } = LocalSubnetHosts;

    public static async Task<IReadOnlyList<TapoDevice>> ScanAsync(TimeSpan listenTime)
    {
        Dictionary<string, TapoDevice> found = [];
        byte[] packet = BuildDiscoveryPacket();
        List<string> hosts = Hosts().ToList();

        using (UdpClient udp = new(0) { EnableBroadcast = true })
        {
            // ICMP "port unreachable" from silent hosts must not abort the receive loop.
            udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
            await udp.SendAsync(packet, new IPEndPoint(BroadcastAddress, DiscoveryPort));
            // Windows Firewall drops replies to a broadcast (source mismatch), so unicast every neighbour too.
            foreach (string host in hosts)
            {
                await udp.SendAsync(packet, new IPEndPoint(IPAddress.Parse(host), DiscoveryPort));
            }

            using CancellationTokenSource listen = new(listenTime);
            try
            {
                while (true)
                {
                    UdpReceiveResult reply = await udp.ReceiveAsync(listen.Token);
                    if (TryParseReply(reply, out TapoDevice? device))
                    {
                        found[device.Ip] = device;
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        List<string> candidates = hosts.Where(host => !found.ContainsKey(host)).ToList();
        bool[] isCamera = await Task.WhenAll(candidates.Select(IsCameraAsync));
        for (int i = 0; i < candidates.Count; i++)
        {
            if (isCamera[i])
            {
                found[candidates[i]] = new TapoDevice(candidates[i], "", "Tapo camera", TapoDeviceKind.Camera);
            }
        }

        return found.Values.OrderBy(device => device.Kind).ThenBy(device => device.Ip).ToList();
    }

    /// <summary>Identifies a device typed in by IP: camera by TLS fingerprint, light if KLAP's HTTP port answers, null if unreachable.</summary>
    public static async Task<TapoDeviceKind?> IdentifyAsync(string ip)
    {
        if (await IsCameraAsync(ip))
        {
            return TapoDeviceKind.Camera;
        }

        try
        {
            using TcpClient tcp = new();
            using CancellationTokenSource connect = new(ConnectTimeout);
            await tcp.ConnectAsync(ip, KlapClient.Port, connect.Token);
            return TapoDeviceKind.Smart;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tapo cameras present a self-signed certificate ("TPRI-DEVICE" on the C225, "TAPO-DEVICE" on newer
    /// models such as the C410); checking it costs no login attempt.
    /// </summary>
    public static async Task<bool> IsCameraAsync(string ip)
    {
        try
        {
            using TcpClient tcp = new();
            using (CancellationTokenSource connect = new(ConnectTimeout))
            {
                await tcp.ConnectAsync(ip, 443, connect.Token);
            }

            using CancellationTokenSource handshake = new(TlsTimeout);
            using SslStream ssl = new(tcp.GetStream());
            SslClientAuthenticationOptions options = new()
            {
                TargetHost = ip,
                RemoteCertificateValidationCallback = (_, _, _, _) => true,
            };
            await ssl.AuthenticateAsClientAsync(options, handshake.Token);
            string subject = ssl.RemoteCertificate?.Subject ?? "";
            return CameraCertificateNames.Any(name => subject.Contains(name, StringComparison.Ordinal));
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or AuthenticationException)
        {
            return false;
        }
    }

    private static byte[] BuildDiscoveryPacket()
    {
        using RSA rsa = RSA.Create(1024);
        JsonObject body = new()
        {
            ["params"] = new JsonObject { ["rsa_key"] = rsa.ExportSubjectPublicKeyInfoPem() + "\n" },
        };
        byte[] payload = Bytes.Utf8(body.ToJsonString());
        byte[] packet = new byte[HeaderLength + payload.Length];
        packet[0] = 2;   // version
        packet[3] = 1;   // op code
        packet[6] = 17;  // flags
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(4), (ushort)payload.Length);
        BinaryPrimitives.WriteInt32BigEndian(packet.AsSpan(8), Random.Shared.Next());
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), 0x5A6B7C8D); // CRC seed, replaced below
        payload.CopyTo(packet, HeaderLength);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(12), Crc32(packet));
        return packet;
    }

    internal static bool TryParseReply(UdpReceiveResult reply, [NotNullWhen(true)] out TapoDevice? device)
    {
        device = null;
        if (reply.Buffer.Length <= HeaderLength)
        {
            return false;
        }

        try
        {
            JsonNode? info = JsonNode.Parse(reply.Buffer.AsSpan(HeaderLength))?["result"];
            if (info is null)
            {
                return false;
            }

            string type = (string?)info["device_type"] ?? "";
            device = new TapoDevice(
                // The sender address, not the payload's "ip": a replier must not choose which host the app contacts.
                reply.RemoteEndPoint.Address.ToString(),
                (string?)info["mac"] ?? "",
                (string?)info["device_model"] ?? "Tapo",
                type.Contains("CAMERA", StringComparison.Ordinal) ? TapoDeviceKind.Camera : TapoDeviceKind.Smart,
                type);
            return true;
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            device = null;
            return false;
        }
    }

    // shortcut: assumes /24 home networks, widen if devices live on a larger subnet.
    internal static IEnumerable<string> LocalSubnetHosts() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.GetIPProperties().GatewayAddresses.Count > 0)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => string.Join('.', address.Address.ToString().Split('.')[..3]))
            .Distinct()
            .SelectMany(prefix => Enumerable.Range(1, 254).Select(host => $"{prefix}.{host}"));

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }
}

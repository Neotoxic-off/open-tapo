using System.Net;
using System.Net.Sockets;

namespace OpenTapo.Tapo;

public enum TapoDeviceKind
{
    /// <summary>Everything that speaks KLAP: bulbs, light strips, plugs, power strips, switches, hubs.</summary>
    Smart,

    /// <summary>Everything that speaks the camera API: cameras and doorbells.</summary>
    Camera,
}

/// <param name="Type">The device's own type string when known, e.g. "SMART.TAPOPLUG"; empty for devices found by TLS fingerprint or IP.</param>
public sealed record TapoDevice(string Ip, string Mac, string Model, TapoDeviceKind Kind, string Type = "")
{
    /// <summary>Device addresses end up as URL hosts, so only plain IPv4 literals are accepted.</summary>
    public static bool IsValidAddress(string? ip) =>
        IPAddress.TryParse(ip?.Trim(), out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;
}

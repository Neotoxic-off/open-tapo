using System.Net;
using System.Net.Sockets;

namespace OpenTapo.Tapo;

/// <summary>Which view a device gets. Stored as a number in devices.json: append new values only.</summary>
public enum TapoDeviceKind
{
    /// <summary>The Tapo smart API (KLAP, AES or SSL transport): bulbs, light strips, plugs, power strips, switches, hubs, fans.</summary>
    Smart,

    /// <summary>The camera API: cameras, doorbells and the H200 hub.</summary>
    Camera,

    /// <summary>Kasa IOT devices (XOR, KLAP v1 or Linkie transport), shown through the smart view.</summary>
    Kasa,

    /// <summary>Robot vacuums: the smart API with cleaning commands.</summary>
    Vacuum,
}

/// <param name="Type">The device's own type string when known, e.g. "SMART.TAPOPLUG"; empty for devices found by TLS fingerprint or IP.</param>
/// <param name="Encryption">Transport announced in discovery: "KLAP", "AES" or "XOR"; empty when unknown (KLAP is assumed).</param>
/// <param name="Https">The device announced HTTPS (port 4433 for KLAP / SSL).</param>
/// <param name="LoginVersion">Login variant announced in discovery ("lv"), 0 when unknown.</param>
public sealed record TapoDevice(
    string Ip,
    string Mac,
    string Model,
    TapoDeviceKind Kind,
    string Type = "",
    string Encryption = "",
    bool Https = false,
    int LoginVersion = 0)
{
    /// <summary>Device addresses end up as URL hosts, so only plain IPv4 literals are accepted.</summary>
    public static bool IsValidAddress(string? ip) =>
        IPAddress.TryParse(ip?.Trim(), out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;

    /// <summary>The view for a device type and the transport it announced.</summary>
    public static TapoDeviceKind KindOf(string type, string encryption, bool https) => type switch
    {
        _ when type.StartsWith("IOT.", StringComparison.Ordinal) => TapoDeviceKind.Kasa,
        _ when type.Contains("ROBOVAC", StringComparison.Ordinal) => TapoDeviceKind.Vacuum,
        // Doorbells and the H200 hub speak the camera API too; the H200 only says so through AES over HTTPS.
        _ when type.Contains("CAMERA", StringComparison.Ordinal) || type.Contains("DOORBELL", StringComparison.Ordinal) || (https && encryption == "AES") => TapoDeviceKind.Camera,
        _ => TapoDeviceKind.Smart,
    };
}

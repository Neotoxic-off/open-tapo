using System.Net;
using System.Net.Sockets;

namespace OpenTapo.Tapo;

public enum TapoDeviceKind
{
    Light,
    Camera,
}

public sealed record TapoDevice(string Ip, string Mac, string Model, TapoDeviceKind Kind)
{
    /// <summary>Device addresses end up as URL hosts, so only plain IPv4 literals are accepted.</summary>
    public static bool IsValidAddress(string? ip) =>
        IPAddress.TryParse(ip?.Trim(), out IPAddress? address) && address.AddressFamily == AddressFamily.InterNetwork;
}

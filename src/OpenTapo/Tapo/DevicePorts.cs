namespace OpenTapo.Tapo;

/// <summary>Fixed device ports; settable only so tests can avoid ports the machine already uses (CI keeps 80 for HTTP.sys).</summary>
internal static class DevicePorts
{
    /// <summary>KLAP and AES over HTTP.</summary>
    public static int Http { get; set; } = 80;

    /// <summary>KLAP and SSL over HTTPS (robot vacuums, some newer devices).</summary>
    public static int Https { get; set; } = 4433;

    /// <summary>Legacy Kasa XOR protocol (TCP) and its discovery (UDP).</summary>
    public static int Xor { get; set; } = 9999;

    /// <summary>Kasa cameras (Linkie).</summary>
    public static int Linkie { get; set; } = 10443;
}

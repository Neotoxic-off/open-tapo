namespace OpenTapo.ViewModels;

/// <summary>Segoe Fluent Icons glyph for a device, chosen from the type string the device reports.</summary>
internal static class DeviceGlyphs
{
    public const string Power = "";
    public const string Bulb = "";
    public const string Hub = "";
    public const string Camera = "";

    public static string For(string type) => type switch
    {
        _ when type.Contains("BULB", StringComparison.Ordinal) || type.Contains("STRIP", StringComparison.Ordinal) => Bulb,
        _ when type.Contains("HUB", StringComparison.Ordinal) => Hub,
        _ when type.Contains("CAMERA", StringComparison.Ordinal) => Camera,
        _ => Power,
    };
}

namespace OpenTapo.ViewModels;

public sealed record NightVisionMode(string Label, string Value)
{
    public static IReadOnlyList<NightVisionMode> All { get; } =
    [
        new("Auto", "auto"),
        new("Always on", "on"),
        new("Off", "off"),
    ];
}

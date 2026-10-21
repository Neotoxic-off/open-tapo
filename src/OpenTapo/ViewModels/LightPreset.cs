using System.Windows.Media;

namespace OpenTapo.ViewModels;

/// <summary>A one-click light setting: a white temperature (Kelvin) or a saturated hue.</summary>
public sealed record LightPreset(string Name, Color Color, int? Kelvin = null, int? Hue = null)
{
    public SolidColorBrush Swatch { get; } = Freeze(new SolidColorBrush(Color));

    public static IReadOnlyList<LightPreset> Whites { get; } =
    [
        new("Warm white", Color.FromRgb(0xFF, 0xB8, 0x6B), Kelvin: 2700),
        new("Neutral white", Color.FromRgb(0xFF, 0xE4, 0xC4), Kelvin: 4000),
        new("Cool white", Color.FromRgb(0xDD, 0xEB, 0xFF), Kelvin: 6500),
    ];

    public static IReadOnlyList<LightPreset> Colors { get; } =
    [
        new("Red", Color.FromRgb(0xFF, 0x45, 0x45), Hue: 0),
        new("Orange", Color.FromRgb(0xFF, 0x9A, 0x2E), Hue: 30),
        new("Green", Color.FromRgb(0x3D, 0xD6, 0x6B), Hue: 120),
        new("Blue", Color.FromRgb(0x3A, 0x7B, 0xFF), Hue: 220),
        new("Violet", Color.FromRgb(0xA8, 0x55, 0xF7), Hue: 280),
    ];

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}

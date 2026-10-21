using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

/// <summary>
/// Any KLAP device (bulb, light strip, plug, power strip, switch, hub). Everything shown is driven by what
/// the device reports: a feature it does not answer for is simply hidden.
/// </summary>
public sealed partial class SmartDeviceViewModel : DeviceViewModel
{
    private const string DeviceInfo = "get_device_info";
    private const string DeviceUsage = "get_device_usage";
    private const string FadeInfo = "get_on_off_gradually_info";
    private const string EnergyUsage = "get_energy_usage";
    private const string ChildDevices = "get_child_device_list";
    private static readonly Color OffColor = Color.FromRgb(0x6B, 0x6B, 0x75);
    private static readonly Color OnColor = Color.FromRgb(0xFF, 0xC8, 0x6B);

    [ObservableProperty]
    private string _glyph = DeviceGlyphs.Power;

    [ObservableProperty]
    private bool _supportsPower;

    [ObservableProperty]
    private bool _isOn;

    [ObservableProperty]
    private double _brightness;

    [ObservableProperty]
    private double _colorTemperature = 2700;

    [ObservableProperty]
    private int _minColorTemperature = 2500;

    [ObservableProperty]
    private int _maxColorTemperature = 6500;

    [ObservableProperty]
    private double _hue;

    [ObservableProperty]
    private double _saturation = 100;

    [ObservableProperty]
    private string _state = "";

    [ObservableProperty]
    private Color _glow = OffColor;

    [ObservableProperty]
    private bool _supportsBrightness;

    [ObservableProperty]
    private bool _supportsWhites;

    [ObservableProperty]
    private bool _supportsColors;

    [ObservableProperty]
    private bool _hasUsage;

    [ObservableProperty]
    private string _usageToday = "–";

    [ObservableProperty]
    private string _usageWeek = "–";

    [ObservableProperty]
    private string _usageMonth = "–";

    [ObservableProperty]
    private string _energy = "";

    public SmartDeviceViewModel(TapoDevice device, DeviceSessions sessions)
        : base(device, sessions)
    {
        Fade = new SettingToggle("Fade on / off", "", enabled => SendAsync("set_on_off_gradually_info", new JsonObject { ["enable"] = enabled }));
        Glyph = DeviceGlyphs.For(device.Type);
    }

    public SettingToggle Fade { get; }

    /// <summary>Sensors and other child devices of a hub.</summary>
    public ObservableCollection<ChildReading> Children { get; } = [];

    public IReadOnlyList<LightPreset> Whites => LightPreset.Whites;

    public IReadOnlyList<LightPreset> Colors => LightPreset.Colors;

    private KlapClient Client => Sessions.Smart(Device.Ip);

    partial void OnIsOnChanged(bool value) => SetStateIfUserChange(new JsonObject { ["device_on"] = value });

    partial void OnBrightnessChanged(double value) =>
        SetStateIfUserChange(new JsonObject { ["brightness"] = Math.Clamp((int)Math.Round(value), 1, 100), ["device_on"] = true });

    partial void OnColorTemperatureChanged(double value) =>
        SetStateIfUserChange(new JsonObject { ["color_temp"] = (int)Math.Round(value), ["device_on"] = true });

    partial void OnHueChanged(double value) => SetStateIfUserChange(ColorState());

    partial void OnSaturationChanged(double value) => SetStateIfUserChange(ColorState());

    [RelayCommand]
    private Task ApplyPresetAsync(LightPreset preset)
    {
        JsonObject state = preset.Kelvin is int kelvin
            ? new JsonObject { ["color_temp"] = kelvin }
            : new JsonObject { ["hue"] = preset.Hue ?? 0, ["saturation"] = 100, ["color_temp"] = 0 };
        state["device_on"] = true;
        return SendAsync("set_device_info", state);
    }

    protected override async Task LoadAsync()
    {
        IReadOnlyDictionary<string, JsonNode?> results = await Client.ExecuteAsync(DeviceInfo, DeviceUsage, FadeInfo, EnergyUsage, ChildDevices);
        JsonNode info = results.GetValueOrDefault(DeviceInfo) ?? throw new TapoException("The device did not report its state.");
        JsonNode? usage = results.GetValueOrDefault(DeviceUsage)?["time_usage"];
        JsonNode? energy = results.GetValueOrDefault(EnergyUsage);
        JsonArray? children = results.GetValueOrDefault(ChildDevices)?["child_device_list"]?.AsArray();
        JsonArray? temperatureRange = info["color_temp_range"]?.AsArray();
        bool supportsPower = info["device_on"] is not null;
        bool isOn = (bool?)info["device_on"] ?? false;
        int brightness = (int?)info["brightness"] ?? 0;
        int colorTemp = (int?)info["color_temp"] ?? 0;
        int hue = (int?)info["hue"] ?? 0;
        int saturation = (int?)info["saturation"] ?? 100;
        bool supportsBrightness = info["brightness"] is not null;
        bool supportsColors = info["hue"] is not null;
        // shortcut: only the first page of a hub's children is read (the device default, 10 children); page with start_index for more.
        List<ChildReading> readings = children?.Select(child => ChildReading.From(child!)).ToList() ?? [];

        Apply(() =>
        {
            Name = Format.Nickname((string?)info["nickname"]) ?? (string?)info["model"] ?? Device.Model;
            Subtitle = $"{info["model"]} · {Device.Ip}";
            Glyph = DeviceGlyphs.For((string?)info["type"] ?? Device.Type);
            SupportsPower = supportsPower;
            IsOn = isOn;
            Brightness = brightness;
            SupportsBrightness = supportsBrightness;
            SupportsWhites = temperatureRange is not null;
            SupportsColors = supportsColors;
            MinColorTemperature = (int?)temperatureRange?[0] ?? 2500;
            MaxColorTemperature = (int?)temperatureRange?[1] ?? 6500;
            ColorTemperature = colorTemp > 0 ? colorTemp : ColorTemperature;
            Hue = hue;
            Saturation = saturation;
            Energy = energy is null ? "" : Format.Energy((int?)energy["current_power"], (int?)energy["today_energy"], (int?)energy["month_energy"]);
            State = !supportsPower ? $"{readings.Count} connected device{(readings.Count == 1 ? "" : "s")}"
                : !isOn ? "Off"
                : colorTemp > 0 ? $"{brightness} % · {colorTemp} K"
                : supportsColors ? $"{brightness} % · color"
                : supportsBrightness ? $"{brightness} %"
                : energy is not null ? $"On · {Format.Watts((int?)energy["current_power"])}"
                : "On";
            Glow = !isOn ? OffColor
                : supportsColors && colorTemp == 0 ? HsvColor(hue, saturation / 100.0)
                : supportsBrightness ? KelvinColor(colorTemp > 0 ? colorTemp : 2700)
                : OnColor;
            Fade.Load((bool?)results.GetValueOrDefault(FadeInfo)?["enable"]);
            HasUsage = usage is not null;
            UsageToday = Format.Duration((int?)usage?["today"]);
            UsageWeek = Format.Duration((int?)usage?["past7"]);
            UsageMonth = Format.Duration((int?)usage?["past30"]);
            Signal = Format.Signal((int?)info["rssi"]);
            Children.Clear();
            foreach (ChildReading reading in readings)
            {
                Children.Add(reading);
            }
        });
    }

    private JsonObject ColorState() => new()
    {
        ["hue"] = (int)Math.Round(Hue),
        ["saturation"] = (int)Math.Round(Saturation),
        ["color_temp"] = 0,
        ["device_on"] = true,
    };

    private void SetStateIfUserChange(JsonObject state)
    {
        if (!IsApplying)
        {
            _ = SendAsync("set_device_info", state);
        }
    }

    private Task SendAsync(string method, JsonObject parameters) => RunAsync(() => Client.CallAsync(method, parameters));

    // Approximate warm→cool white ramp, used only to tint the icon.
    private static Color KelvinColor(int kelvin)
    {
        double t = Math.Clamp((kelvin - 2500) / 4000.0, 0, 1);
        return Color.FromRgb(255, (byte)(180 + (60 * t)), (byte)(90 + (160 * t)));
    }

    private static Color HsvColor(int hue, double saturation)
    {
        double chroma = saturation;
        double x = chroma * (1 - Math.Abs((hue / 60.0 % 2) - 1));
        double m = 1 - chroma;
        (double r, double g, double b) = (hue % 360) switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}

using System.Text;
using System.Text.Json.Nodes;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.ViewModels;

public sealed partial class LightViewModel : DeviceViewModel
{
    private const string DeviceInfo = "get_device_info";
    private const string DeviceUsage = "get_device_usage";
    private const string FadeInfo = "get_on_off_gradually_info";
    private static readonly Color OffColor = Color.FromRgb(0x6B, 0x6B, 0x75);

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

    public LightViewModel(TapoDevice device, DeviceSessions sessions)
        : base(device, sessions)
    {
        Fade = new SettingToggle("Fade on / off", "", enabled => SendAsync("set_on_off_gradually_info", new JsonObject { ["enable"] = enabled }));
    }

    public SettingToggle Fade { get; }

    public IReadOnlyList<LightPreset> Whites => LightPreset.Whites;

    public IReadOnlyList<LightPreset> Colors => LightPreset.Colors;

    private KlapClient Client => Sessions.Light(Device.Ip);

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
        IReadOnlyDictionary<string, JsonNode?> results = await Client.ExecuteAsync(DeviceInfo, DeviceUsage, FadeInfo);
        JsonNode info = results.GetValueOrDefault(DeviceInfo) ?? throw new TapoException("The light did not report its state.");
        JsonNode? usage = results.GetValueOrDefault(DeviceUsage)?["time_usage"];
        JsonArray? temperatureRange = info["color_temp_range"]?.AsArray();
        bool isOn = (bool?)info["device_on"] ?? false;
        int brightness = (int?)info["brightness"] ?? 0;
        int colorTemp = (int?)info["color_temp"] ?? 0;
        int hue = (int?)info["hue"] ?? 0;
        int saturation = (int?)info["saturation"] ?? 100;
        bool supportsColors = info["hue"] is not null;

        Apply(() =>
        {
            Name = DecodeNickname((string?)info["nickname"]) ?? (string?)info["model"] ?? "Light";
            Subtitle = $"{info["model"]} · {Device.Ip}";
            IsOn = isOn;
            Brightness = brightness;
            SupportsBrightness = info["brightness"] is not null;
            SupportsWhites = temperatureRange is not null;
            SupportsColors = supportsColors;
            MinColorTemperature = (int?)temperatureRange?[0] ?? 2500;
            MaxColorTemperature = (int?)temperatureRange?[1] ?? 6500;
            ColorTemperature = colorTemp > 0 ? colorTemp : ColorTemperature;
            Hue = hue;
            Saturation = saturation;
            State = !isOn ? "Off"
                : colorTemp > 0 ? $"{brightness} % · {colorTemp} K"
                : supportsColors ? $"{brightness} % · color"
                : SupportsBrightness ? $"{brightness} %"
                : "On";
            Glow = !isOn ? OffColor
                : colorTemp > 0 || !supportsColors ? KelvinColor(colorTemp > 0 ? colorTemp : 2700)
                : HsvColor(hue, saturation / 100.0);
            Fade.Load((bool?)results.GetValueOrDefault(FadeInfo)?["enable"]);
            HasUsage = usage is not null;
            UsageToday = Format.Duration((int?)usage?["today"]);
            UsageWeek = Format.Duration((int?)usage?["past7"]);
            UsageMonth = Format.Duration((int?)usage?["past30"]);
            Signal = Format.Signal((int?)info["rssi"]);
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

    private static string? DecodeNickname(string? nickname)
    {
        if (nickname is null)
        {
            return null;
        }

        byte[] buffer = new byte[nickname.Length];
        return Convert.TryFromBase64String(nickname, buffer, out int length)
            ? Encoding.UTF8.GetString(buffer, 0, length)
            : nickname;
    }

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

using System.Globalization;
using System.Windows;
using System.Text.Json.Nodes;
using OpenTapo.Tapo;
using OpenTapo.ViewModels;
using OpenTapo.Views;

namespace OpenTapo.Tests.Unit;

public sealed class FormatTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("TGl2aW5n", "Living")]
    [InlineData("Living room", "Living room")]
    public void Nickname_is_decoded_when_base64(string? raw, string? expected) => Assert.Equal(expected, Format.Nickname(raw));

    [Theory]
    [InlineData(null, "–")]
    [InlineData(1500, "1.5 W")]
    public void Watts_from_milliwatts(int? milliwatts, string expected) => Assert.Equal(expected, Format.Watts(milliwatts));

    [Fact]
    public void Energy_line_tolerates_missing_totals() =>
        Assert.Equal("Now – · today – · month 1.25 kWh", Format.Energy(null, null, 1250));

    [Theory]
    [InlineData(null, "–")]
    [InlineData(59, "59 min")]
    [InlineData(61, "1 h 01")]
    public void Duration_in_minutes_or_hours(int? minutes, string expected) => Assert.Equal(expected, Format.Duration(minutes));

    [Theory]
    [InlineData(null, "")]
    [InlineData(-55, "Wi-Fi excellent · -55 dBm")]
    [InlineData(-67, "Wi-Fi good · -67 dBm")]
    [InlineData(-75, "Wi-Fi fair · -75 dBm")]
    [InlineData(-76, "Wi-Fi weak · -76 dBm")]
    public void Signal_quality_tiers(int? dbm, string expected) => Assert.Equal(expected, Format.Signal(dbm));

    [Theory]
    [InlineData(null, "none")]
    [InlineData("abc", "none")]
    [InlineData("0", "none")]
    [InlineData("-5", "none")]
    [InlineData("99999999999999", "none")]
    public void Ago_without_a_valid_time(string? value, string expected) => Assert.Equal(expected, Format.Ago(value));

    [Theory]
    [InlineData(10, "just now")]
    [InlineData(5 * 60, "5 min ago")]
    [InlineData(3 * 3600, "3 h ago")]
    [InlineData(2 * 86400, "2 d ago")]
    public void Ago_in_the_largest_unit(int secondsAgo, string expected) =>
        Assert.Equal(expected, Format.Ago((DateTimeOffset.UtcNow.ToUnixTimeSeconds() - secondsAgo).ToString(CultureInfo.InvariantCulture)));

    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("GB", 0)]
    [InlineData("1.2.3GB", 0)]
    [InlineData("29.7GB", 29.7)]
    [InlineData("512MB", 0.5)]
    [InlineData("2TB", 2048)]
    public void Gigabytes_from_camera_sizes(string? size, double expected) => Assert.Equal(expected, Format.Gigabytes(size), precision: 3);
}

public sealed class ChildReadingTests
{
    [Fact]
    public void Thermostat_with_humidity_and_battery()
    {
        ChildReading reading = ChildReading.From(JsonNode.Parse("""
            {"nickname":"QmVkcm9vbQ==","current_temp":19.5,"target_temp":21,"current_humidity":45,"battery_percentage":80}
            """)!);

        Assert.Equal(new ChildReading("Bedroom", $"{19.5.ToString("0.#", CultureInfo.CurrentCulture)} °C → 21 °C · 45 % · battery 80 %", false), reading);
    }

    [Theory]
    [InlineData("""{"model":"T100","detected":true}""", "T100", "Motion", true)]
    [InlineData("""{"model":"T100","detected":false}""", "T100", "No motion", false)]
    [InlineData("""{"model":"T110","open":true}""", "T110", "Open", true)]
    [InlineData("""{"model":"T110","open":false}""", "T110", "Closed", false)]
    [InlineData("""{"model":"T110","open":false,"at_low_battery":true}""", "T110", "Closed · low battery", true)]
    [InlineData("""{"model":"S200B","status":"offline"}""", "S200B", "offline", true)]
    [InlineData("""{"model":"S200B","status":"online"}""", "S200B", "S200B", false)]
    [InlineData("""{"current_temp":null}""", "Sensor", "", false)]
    [InlineData("""{}""", "Sensor", "", false)]
    public void Readings_come_from_whatever_the_child_reports(string json, string name, string text, bool alert) =>
        Assert.Equal(new ChildReading(name, text, alert), ChildReading.From(JsonNode.Parse(json)!));
}

public sealed class CameraApiTests
{
    private static readonly Dictionary<string, JsonNode?> Results = new()
    {
        ["getLensMaskConfig"] = JsonNode.Parse("""{"lens_mask":{"lens_mask_info":{"enabled":"on"}}}"""),
        ["getDayNightModeConfig"] = JsonNode.Parse("""{"image":{"common":{"inf_type":"auto"}}}"""),
        ["getRotationStatus"] = JsonNode.Parse("""{"image":{"switch":{"flip_type":"off"}}}"""),
    };

    [Fact]
    public void Switch_requests_follow_the_section_key_pattern()
    {
        Assert.Equal(new CameraRequest("getLensMaskConfig", """{"lens_mask":{"name":["lens_mask_info"]}}"""), CameraSwitch.Privacy.Get);
        Assert.Equal(new CameraRequest("setLedStatus", """{"led":{"config":{"enabled":"off"}}}"""), CameraSwitch.StatusLed.Set(false));
        Assert.True(CameraSwitch.Privacy.Read(Results));
        Assert.Null(CameraSwitch.AutoTracking.Read(Results));
    }

    [Fact]
    public void Night_vision_and_flip_round_trip()
    {
        Assert.Equal("""{"image":{"common":{"inf_type":"off"}}}""", CameraApi.SetNightVision("off").Parameters);
        Assert.Equal("""{"image":{"switch":{"flip_type":"off"}}}""", CameraApi.SetFlipped(false).Parameters);
        Assert.Equal("auto", CameraApi.ReadNightVision(Results));
        Assert.False(CameraApi.ReadFlipped(Results));
        Assert.Null(CameraApi.ReadFlipped(new Dictionary<string, JsonNode?>()));
        Assert.Null(CameraApi.ReadNightVision(new Dictionary<string, JsonNode?>()));
    }

    [Fact]
    public void Lockout_message_rounds_minutes_up() =>
        Assert.Equal("Camera locked after too many sign-in attempts. Try again in 2 min.", new TapoLockedException(TimeSpan.FromSeconds(61)).Message);
}

public sealed class PresentationTests
{
    [Theory]
    [InlineData(null, false, Visibility.Visible)]
    [InlineData("", false, Visibility.Visible)]
    [InlineData("", true, Visibility.Collapsed)]
    [InlineData("text", false, Visibility.Collapsed)]
    public void Placeholder_shows_only_when_empty_and_unfocused(string? text, bool focused, Visibility expected) =>
        Assert.Equal(expected, new PlaceholderVisibilityConverter().Convert([text!, focused], typeof(Visibility), null!, CultureInfo.InvariantCulture));

    [Fact]
    public void Placeholder_is_one_way() =>
        Assert.Throws<NotSupportedException>(() => new PlaceholderVisibilityConverter().ConvertBack(Visibility.Visible, [], null!, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("SMART.TAPOBULB", DeviceGlyphs.Bulb)]
    [InlineData("SMART.LIGHTSTRIP", DeviceGlyphs.Bulb)]
    [InlineData("SMART.TAPOHUB", DeviceGlyphs.Hub)]
    [InlineData("SMART.IPCAMERA", DeviceGlyphs.Camera)]
    [InlineData("SMART.TAPOPLUG", DeviceGlyphs.Power)]
    [InlineData("", DeviceGlyphs.Power)]
    public void Glyph_follows_the_device_type(string type, string glyph) => Assert.Equal(glyph, DeviceGlyphs.For(type));

    [Fact]
    public void Preset_swatches_are_frozen_for_cross_thread_use() =>
        Assert.All(LightPreset.Whites.Concat(LightPreset.Colors), preset => Assert.True(preset.Swatch.IsFrozen));
}

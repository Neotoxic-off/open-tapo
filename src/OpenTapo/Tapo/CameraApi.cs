using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>An on/off camera setting stored as {section: {key: {enabled: "on" | "off"}}}.</summary>
public sealed record CameraSwitch(string GetMethod, string SetMethod, string Section, string Key)
{
    public static readonly CameraSwitch Privacy = new("getLensMaskConfig", "setLensMaskConfig", "lens_mask", "lens_mask_info");
    public static readonly CameraSwitch MotionDetection = new("getDetectionConfig", "setDetectionConfig", "motion_detection", "motion_det");
    public static readonly CameraSwitch PersonDetection = new("getPersonDetectionConfig", "setPersonDetectionConfig", "people_detection", "detection");
    public static readonly CameraSwitch AutoTracking = new("getTargetTrackConfig", "setTargetTrackConfig", "target_track", "target_track_info");
    public static readonly CameraSwitch AlarmOnDetection = new("getAlarmConfig", "setAlarmConfig", "msg_alarm", "chn1_msg_alarm_info");
    public static readonly CameraSwitch StatusLed = new("getLedStatus", "setLedStatus", "led", "config");

    public CameraRequest Get => new(GetMethod, new JsonObject { [Section] = new JsonObject { ["name"] = new JsonArray(Key) } }.ToJsonString());

    public CameraRequest Set(bool enabled) => new(
        SetMethod,
        new JsonObject { [Section] = new JsonObject { [Key] = new JsonObject { ["enabled"] = enabled ? "on" : "off" } } }.ToJsonString());

    /// <summary>The current value, or null when the camera doesn't support this setting.</summary>
    public bool? Read(IReadOnlyDictionary<string, JsonNode?> results) =>
        results.GetValueOrDefault(GetMethod)?[Section]?[Key]?["enabled"] is JsonNode enabled ? (string?)enabled == "on" : null;
}

/// <summary>Camera calls that don't fit the on/off pattern.</summary>
public static class CameraApi
{
    public static readonly CameraRequest DeviceInfo = new("getDeviceInfo", """{"device_info":{"name":["basic_info"]}}""");
    public static readonly CameraRequest SdCard = new("getSdCardStatus", """{"harddisk_manage":{"table":["hd_info"]}}""");
    public static readonly CameraRequest LastAlarm = new("getLastAlarmInfo", """{"system":{"name":["last_alarm_info"]}}""");
    public static readonly CameraRequest Connection = new("getConnectionType", """{"network":{"get_connection_type":[]}}""");
    public static readonly CameraRequest NightVision = new("getDayNightModeConfig", """{"image":{"name":"common"}}""");
    public static readonly CameraRequest Rotation = new("getRotationStatus", """{"image":{"name":["switch"]}}""");
    public static readonly CameraRequest StartSiren = new("do", """{"msg_alarm":{"manual_msg_alarm":{"action":"start"}}}""");
    public static readonly CameraRequest StopSiren = new("do", """{"msg_alarm":{"manual_msg_alarm":{"action":"stop"}}}""");

    /// <summary>Night vision mode: "auto", "on" or "off".</summary>
    public static CameraRequest SetNightVision(string mode) => new(
        "setDayNightModeConfig",
        new JsonObject { ["image"] = new JsonObject { ["common"] = new JsonObject { ["inf_type"] = mode } } }.ToJsonString());

    public static CameraRequest SetFlipped(bool flipped) => new(
        "setRotationStatus",
        new JsonObject { ["image"] = new JsonObject { ["switch"] = new JsonObject { ["flip_type"] = flipped ? "center" : "off" } } }.ToJsonString());

    public static string? ReadNightVision(IReadOnlyDictionary<string, JsonNode?> results) =>
        (string?)results.GetValueOrDefault(NightVision.Method)?["image"]?["common"]?["inf_type"];

    public static bool? ReadFlipped(IReadOnlyDictionary<string, JsonNode?> results) =>
        results.GetValueOrDefault(Rotation.Method)?["image"]?["switch"]?["flip_type"] is JsonNode flip ? (string?)flip != "off" : null;
}

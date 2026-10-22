using System.Globalization;
using System.Text.Json.Nodes;

namespace OpenTapo.ViewModels;

/// <summary>One line for a hub child (sensor, button, thermostat), built from whichever fields it reports.</summary>
public sealed record ChildReading(string Name, string Reading, bool IsAlert)
{
    public static ChildReading From(JsonNode child)
    {
        string name = Format.Nickname((string?)child["nickname"]) ?? (string?)child["model"] ?? "Sensor";
        List<string> parts = [];
        bool alert = false;

        if (child["current_temp"] is JsonNode temperature)
        {
            string reading = $"{Number(temperature)} °C";
            if (child["target_temp"] is JsonNode target)
            {
                reading += $" → {Number(target)} °C";
            }

            parts.Add(reading);
        }

        if (child["current_humidity"] is JsonNode humidity)
        {
            parts.Add($"{Number(humidity)} %");
        }

        if ((bool?)child["detected"] is bool detected)
        {
            parts.Add(detected ? "Motion" : "No motion");
            alert |= detected;
        }

        if ((bool?)child["open"] is bool open)
        {
            parts.Add(open ? "Open" : "Closed");
            alert |= open;
        }

        // T300 water leak sensor.
        if ((bool?)child["in_alarm"] is bool leak)
        {
            parts.Add(leak ? "Leak" : "Dry");
            alert |= leak;
        }

        if ((int?)child["battery_percentage"] is int battery)
        {
            parts.Add($"battery {battery} %");
        }

        if ((bool?)child["at_low_battery"] == true)
        {
            parts.Add("low battery");
            alert = true;
        }

        if ((string?)child["status"] is "offline")
        {
            parts.Add("offline");
            alert = true;
        }

        return new ChildReading(name, parts.Count > 0 ? string.Join(" · ", parts) : (string?)child["model"] ?? "", alert);
    }

    private static string Number(JsonNode value) =>
        ((double?)value ?? 0).ToString("0.#", CultureInfo.CurrentCulture);
}

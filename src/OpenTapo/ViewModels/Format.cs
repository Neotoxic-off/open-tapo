using System.Globalization;
using System.Text;

namespace OpenTapo.ViewModels;

internal static class Format
{
    /// <summary>KLAP devices report their nickname base64-encoded; older firmware sends it in clear.</summary>
    public static string? Nickname(string? nickname)
    {
        if (string.IsNullOrEmpty(nickname))
        {
            return null;
        }

        byte[] buffer = new byte[nickname.Length];
        return Convert.TryFromBase64String(nickname, buffer, out int length)
            ? Encoding.UTF8.GetString(buffer, 0, length)
            : nickname;
    }

    /// <summary>Plugs report current_power in milliwatts.</summary>
    public static string Watts(int? milliwatts) =>
        milliwatts is int value ? $"{value / 1000.0:0.#} W" : "–";

    /// <summary>Energy totals are reported in watt-hours.</summary>
    public static string Energy(int? currentMilliwatts, int? todayWh, int? monthWh) =>
        $"Now {Watts(currentMilliwatts)} · today {KilowattHours(todayWh)} · month {KilowattHours(monthWh)}";

    private static string KilowattHours(int? wattHours) =>
        wattHours is int value ? $"{value / 1000.0:0.##} kWh" : "–";

    public static string Duration(int? minutes) => minutes switch
    {
        null => "–",
        < 60 => $"{minutes} min",
        _ => $"{minutes / 60} h {minutes % 60:00}",
    };

    public static string Signal(int? dbm) => dbm switch
    {
        null => "",
        >= -55 => $"Wi-Fi excellent · {dbm} dBm",
        >= -67 => $"Wi-Fi good · {dbm} dBm",
        >= -75 => $"Wi-Fi fair · {dbm} dBm",
        _ => $"Wi-Fi weak · {dbm} dBm",
    };

    public static string Ago(string? unixSeconds)
    {
        // The value comes from the device: anything past year 9999 would make FromUnixTimeSeconds throw.
        if (!long.TryParse(unixSeconds, out long seconds) || seconds <= 0 || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return "none";
        }

        TimeSpan elapsed = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return elapsed.TotalMinutes < 1 ? "just now"
            : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} min ago"
            : elapsed.TotalDays < 1 ? $"{(int)elapsed.TotalHours} h ago"
            : $"{(int)elapsed.TotalDays} d ago";
    }

    /// <summary>Parses camera sizes such as "29.7GB" or "512.0MB" into gigabytes.</summary>
    public static double Gigabytes(string? size)
    {
        if (string.IsNullOrEmpty(size))
        {
            return 0;
        }

        string number = new(size.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray());
        if (!double.TryParse(number, CultureInfo.InvariantCulture, out double value))
        {
            return 0;
        }

        return size.Contains("TB", StringComparison.Ordinal) ? value * 1024
            : size.Contains("MB", StringComparison.Ordinal) ? value / 1024
            : value;
    }
}

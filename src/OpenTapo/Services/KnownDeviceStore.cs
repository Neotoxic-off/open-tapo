using System.IO;
using System.Text.Json;
using OpenTapo.Tapo;

namespace OpenTapo.Services;

/// <summary>
/// Every device ever discovered or added by IP, saved in %LOCALAPPDATA%\OpenTapo, so battery cameras
/// that are asleep during a scan (and devices on other segments) stay in the list.
/// </summary>
public sealed class KnownDeviceStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTapo", "devices.json");

    public IReadOnlyList<TapoDevice> Load()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        List<TapoDevice>? devices;
        try
        {
            devices = JsonSerializer.Deserialize<List<TapoDevice>>(File.ReadAllText(FilePath));
        }
        catch (JsonException)
        {
            // A corrupted list must not stop the app from starting; it is rewritten on the next save.
            return [];
        }

        return (devices ?? []).Where(device => TapoDevice.IsValidAddress(device.Ip)).ToList();
    }

    /// <summary>Adds devices not already known and returns the full list; a known address keeps its first entry.</summary>
    public IReadOnlyList<TapoDevice> Remember(IEnumerable<TapoDevice> devices)
    {
        List<TapoDevice> known = Load().ToList();
        List<TapoDevice> added = devices
            .Where(device => known.All(existing => existing.Ip != device.Ip))
            .DistinctBy(device => device.Ip)
            .ToList();
        if (added.Count > 0)
        {
            known.AddRange(added);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(known));
        }

        return known;
    }
}

using System.IO;
using System.Text.Json;
using OpenTapo.Tapo;

namespace OpenTapo.Services;

/// <summary>Devices added by IP (not discoverable, e.g. on another segment), saved in %LOCALAPPDATA%\OpenTapo.</summary>
public sealed class ManualDeviceStore
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
            // A corrupted list must not stop the app from starting; it is rewritten on the next add.
            return [];
        }

        return (devices ?? []).Where(device => TapoDevice.IsValidAddress(device.Ip)).ToList();
    }

    public void Add(TapoDevice device)
    {
        List<TapoDevice> devices = Load().Where(existing => existing.Ip != device.Ip).ToList();
        devices.Add(device);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(devices));
    }
}

using System.IO;
using System.Text.Json.Nodes;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.Diagnostics;

/// <summary>`OpenTapo.exe --probe`: discovery plus one raw read per device, to check the protocols against real hardware.</summary>
public static class DeviceProbe
{
    public static async Task RunAsync(TextWriter output)
    {
        CredentialStore credentials = new();
        DeviceSessions sessions = new(credentials);
        TapoAccount? account = credentials.Load();
        await output.WriteLineAsync(account is null ? "No account stored: sign in from the panel first." : $"Account: {account.Email}");

        foreach (TapoDevice device in await TapoDiscovery.ScanAsync(TimeSpan.FromSeconds(2.5)))
        {
            await output.WriteLineAsync($"{device.Ip,-15} {device.Kind,-6} {device.Model} {device.Mac} {device.Type} {device.Encryption}".TrimEnd());
            if (account is null)
            {
                continue;
            }

            try
            {
                string state = device.Kind == TapoDeviceKind.Camera
                    ? string.Join(", ", (await sessions.Camera(device.Ip).ExecuteAsync(
                            new CameraRequest("getDeviceInfo", """{"device_info":{"name":["basic_info"]}}""")))
                        .Select(result => $"{result.Key}={result.Value?.ToJsonString()}"))
                    : (await sessions.Smart(device).CallAsync("get_device_info")).ToJsonString();
                await output.WriteLineAsync("  " + state);
            }
            catch (Exception ex)
            {
                await output.WriteLineAsync($"  {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}

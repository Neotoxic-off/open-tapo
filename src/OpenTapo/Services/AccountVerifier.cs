using System.Net.Http;
using OpenTapo.Tapo;

namespace OpenTapo.Services;

public enum AccountCheck
{
    Valid,
    Rejected,
    NoDeviceReachable,
}

/// <summary>
/// Tests an account against a reachable device before it is saved. Both checks are lockout-safe:
/// KLAP only compares hashes, and cameras validate the password through device_confirm before any login attempt.
/// </summary>
public static class AccountVerifier
{
    private static readonly CameraRequest Probe = new("getDeviceInfo", """{"device_info":{"name":["basic_info"]}}""");

    public static async Task<AccountCheck> VerifyAsync(TapoAccount account, IEnumerable<TapoDevice> devices)
    {
        // Lights first: they have no attempt counter at all.
        foreach (TapoDevice device in devices.OrderBy(device => device.Kind == TapoDeviceKind.Camera))
        {
            try
            {
                if (device.Kind == TapoDeviceKind.Camera)
                {
                    await new CameraClient(device.Ip, account.Password).ExecuteAsync(Probe);
                }
                else
                {
                    await new KlapClient(device.Ip, account.Email, account.Password).CallAsync("get_device_info");
                }

                return AccountCheck.Valid;
            }
            catch (TapoAuthException)
            {
                return AccountCheck.Rejected;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or TapoException)
            {
                // Unreachable or misbehaving device: try the next one.
            }
        }

        return AccountCheck.NoDeviceReachable;
    }
}

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
/// Tests an account against a reachable device before it is saved. KLAP devices come first: they only compare
/// hashes. AES and SSL devices log in with the password, and cameras validate it through device_confirm before
/// any login attempt. Kasa devices on XOR or Linkie have no account to check.
/// </summary>
public static class AccountVerifier
{
    private static readonly CameraRequest Probe = new("getDeviceInfo", """{"device_info":{"name":["basic_info"]}}""");

    public static async Task<AccountCheck> VerifyAsync(TapoAccount account, IEnumerable<TapoDevice> devices)
    {
        foreach (TapoDevice device in devices.Where(UsesAccount).OrderBy(Order))
        {
            try
            {
                if (device.Kind == TapoDeviceKind.Camera)
                {
                    await new CameraClient(device.Ip, account.Password).ExecuteAsync(Probe);
                }
                else
                {
                    await DeviceSessions.CreateSmart(device, account).CallAsync("get_device_info");
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

    private static bool UsesAccount(TapoDevice device) => device.Kind != TapoDeviceKind.Kasa || device.Encryption == "KLAP";

    private static int Order(TapoDevice device) => device switch
    {
        { Kind: TapoDeviceKind.Camera } => 2,
        { Encryption: "AES" } => 1,
        _ => 0,
    };
}

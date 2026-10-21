using System.Net.Http;
using System.Net.Security;

namespace OpenTapo.Tapo;

/// <summary>HttpClient policy shared by every device connection.</summary>
internal static class DeviceHttp
{
    private const int MaxResponseBytes = 1024 * 1024;

    /// <summary>
    /// Tapo devices never redirect, so a 3xx can only come from a rogue peer trying to aim the user's host
    /// at another URL (loopback, VPN-only services): redirects are refused. Responses are capped because
    /// any LAN host can answer as a device.
    /// </summary>
    public static HttpClient Create(TimeSpan timeout, RemoteCertificateValidationCallback? certificateValidation = null)
    {
        SocketsHttpHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
        };
        if (certificateValidation is not null)
        {
            handler.SslOptions.RemoteCertificateValidationCallback = certificateValidation;
        }

        return new HttpClient(handler)
        {
            Timeout = timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
    }
}

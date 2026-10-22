using System.Net.Http;
using System.Text.Json.Nodes;

namespace OpenTapo.Tapo;

/// <summary>
/// SSL transport of Tapo robot vacuums (HTTPS :4433): a login with the email and MD5(password) returns a token,
/// then plain smart API requests go to /app?token=. TLS is the only encryption.
/// </summary>
public sealed class SslClient(string ip, string email, string password) : SmartClient
{
    private const int LoginFailed = -1501;

    // shortcut: vacuums use a self-signed certificate, accepted without pinning; pin its thumbprint if the LAN is untrusted.
    private static readonly HttpClient Http = DeviceHttp.Create(TimeSpan.FromSeconds(8), (_, _, _, _) => true);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;

    public override async Task<JsonNode> CallAsync(string method, JsonObject? parameters = null)
    {
        JsonObject request = Request(method, parameters);
        await _gate.WaitAsync();
        try
        {
            for (int attempt = 0; ; attempt++)
            {
                bool reusedSession = _token is not null;
                try
                {
                    _token ??= await LoginAsync();
                    return ResultOf(await PostAsync(request, _token), method);
                }
                catch (Exception ex) when (ex is not TapoException && reusedSession && attempt == 0)
                {
                    // Token expired (~24 h) or the vacuum rebooted: log in again once.
                    _token = null;
                }
            }
        }
        catch (Exception ex) when (TapoException.IsMalformedReply(ex))
        {
            throw new TapoException("Unexpected reply from the device.");
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> LoginAsync()
    {
        JsonObject login = Request("login", new JsonObject { ["username"] = email, ["password"] = Bytes.Md5Hex(password) });
        JsonNode reply = await PostAsync(login, token: null);
        if ((int?)reply["error_code"] == LoginFailed)
        {
            throw new TapoAuthException("The device rejected these Tapo credentials.");
        }

        return (string)ResultOf(reply, "login")["token"]!;
    }

    private async Task<JsonNode> PostAsync(JsonObject request, string? token)
    {
        string url = $"https://{ip}:{DevicePorts.Https}/app{(token is null ? "" : "?token=" + Uri.EscapeDataString(token))}";
        using StringContent content = new(request.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.PostAsync(url, content);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync()) ?? throw new TapoException("Empty response from the device.");
    }
}

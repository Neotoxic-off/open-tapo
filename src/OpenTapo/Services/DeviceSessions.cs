using OpenTapo.Tapo;

namespace OpenTapo.Services;

/// <summary>One live protocol session per device, dropped whenever the account changes.</summary>
public sealed class DeviceSessions
{
    private readonly CredentialStore _credentials;
    private readonly RejectedCameraStore _rejectedCameras = new();
    private readonly Dictionary<string, object> _clients = [];

    public DeviceSessions(CredentialStore credentials)
    {
        _credentials = credentials;
        _credentials.Changed += (_, _) =>
        {
            lock (_clients)
            {
                _clients.Clear();
                // A new account deserves one fresh attempt on every camera.
                _rejectedCameras.Clear();
            }
        };
    }

    /// <summary>The smart API client for any non-camera device, over the transport it announced.</summary>
    public SmartClient Smart(TapoDevice device) =>
        GetOrCreate(device.Ip, account => CreateSmart(device, account));

    public CameraClient Camera(string ip) =>
        GetOrCreate(ip, account =>
        {
            CameraClient client = new(ip, account.Password, _rejectedCameras.Contains(ip));
            client.Rejected += (_, _) => _rejectedCameras.Add(ip);
            return client;
        });

    internal static SmartClient CreateSmart(TapoDevice device, TapoAccount account) => device switch
    {
        { Kind: TapoDeviceKind.Kasa, Encryption: "KLAP" } => new KasaClient(new KlapClient(device.Ip, account.Email, account.Password, device.LoginVersion >= 2 ? 2 : 1)),
        { Kind: TapoDeviceKind.Kasa, Type: "IOT.IPCAMERA" } => new KasaClient(new LinkieClient(device.Ip)),
        { Kind: TapoDeviceKind.Kasa } => new KasaClient(new XorClient(device.Ip)),
        { Encryption: "AES", Https: true } => new SslClient(device.Ip, account.Email, account.Password),
        { Encryption: "AES" } => new AesClient(device.Ip, account.Email, account.Password, device.LoginVersion),
        _ => new KlapClient(device.Ip, account.Email, account.Password, https: device.Https),
    };

    private T GetOrCreate<T>(string ip, Func<TapoAccount, T> create)
        where T : class
    {
        lock (_clients)
        {
            // Loaded under the same lock as the Changed purge, so a refresh racing a sign-out
            // cannot re-cache a client that still holds the previous account.
            TapoAccount account = _credentials.Load() ?? throw new TapoAuthException("Sign in with your Tapo account to control this device.");
            if (_clients.TryGetValue(ip, out object? existing) && existing is T client)
            {
                return client;
            }

            T created = create(account);
            _clients[ip] = created;
            return created;
        }
    }
}

using OpenTapo.Tapo;

namespace OpenTapo.Services;

/// <summary>One live protocol session per device, dropped whenever the account changes.</summary>
public sealed class DeviceSessions
{
    private readonly CredentialStore _credentials;
    private readonly Dictionary<string, object> _clients = [];

    public DeviceSessions(CredentialStore credentials)
    {
        _credentials = credentials;
        _credentials.Changed += (_, _) =>
        {
            lock (_clients)
            {
                _clients.Clear();
            }
        };
    }

    public KlapClient Light(string ip) =>
        GetOrCreate(ip, account => new KlapClient(ip, account.Email, account.Password));

    public CameraClient Camera(string ip) =>
        GetOrCreate(ip, account => new CameraClient(ip, account.Password));

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

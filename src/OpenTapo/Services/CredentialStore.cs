using Windows.Security.Credentials;

namespace OpenTapo.Services;

public sealed record TapoAccount(string Email, string Password);

/// <summary>Keeps the Tapo account in the Windows Credential Locker (DPAPI-protected, per user).</summary>
public sealed class CredentialStore
{
    private const string Resource = "OpenTapo";

    public event EventHandler? Changed;

    public TapoAccount? Load()
    {
        PasswordCredential? credential = Find().FirstOrDefault();
        if (credential is null)
        {
            return null;
        }

        credential.RetrievePassword();
        return new TapoAccount(credential.UserName, credential.Password);
    }

    public void Save(TapoAccount account)
    {
        RemoveAll();
        new PasswordVault().Add(new PasswordCredential(Resource, account.Email, account.Password));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        RemoveAll();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void RemoveAll()
    {
        PasswordVault vault = new();
        foreach (PasswordCredential credential in Find())
        {
            vault.Remove(credential);
        }
    }

    // RetrieveAll instead of FindAllByResource: the latter throws when nothing is stored yet.
    private static IReadOnlyList<PasswordCredential> Find() =>
        new PasswordVault().RetrieveAll().Where(credential => credential.Resource == Resource).ToList();
}

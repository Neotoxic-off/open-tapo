using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using OpenTapo.Services;
using OpenTapo.Tapo;

// Tests share static state (credential vault entry, app folder, discovery targets) and fixed device ports.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace OpenTapo.Tests;

internal static class Accounts
{
    public const string Email = "user@example.com";
    public const string Password = "correct horse";
    public static readonly TapoAccount Valid = new(Email, Password);
    public static readonly TapoAccount Wrong = new(Email, "wrong password");
}

internal static class TestEnvironment
{
    /// <summary>Runs before any test: nothing in a test run may touch the user's real data, account or LAN.</summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        AppFolder.Root = Path.Combine(Path.GetTempPath(), "OpenTapo.Tests", Guid.NewGuid().ToString("N"));
        CredentialStore.Resource = "OpenTapo.Tests";
        TapoDiscovery.BroadcastAddress = IPAddress.Loopback;
        TapoDiscovery.Hosts = () => [];
        KlapClient.Port = 28080;
    }
}

/// <summary>Base for tests that touch the stores, the account or discovery: each test starts clean.</summary>
public abstract class IsolatedTest : IDisposable
{
    protected IsolatedTest() => Reset();

    public void Dispose()
    {
        Reset();
        GC.SuppressFinalize(this);
    }

    protected static void SignIn(TapoAccount? account = null) => new CredentialStore().Save(account ?? Accounts.Valid);

    /// <summary>Waits for fire-and-forget work (setting toggles, property hooks) to land.</summary>
    protected static async Task EventuallyAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }

    private static void Reset()
    {
        new CredentialStore().Clear();
        if (Directory.Exists(AppFolder.Root))
        {
            Directory.Delete(AppFolder.Root, recursive: true);
        }

        TapoDiscovery.Hosts = () => [];
    }
}

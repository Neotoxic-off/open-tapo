using System.IO;
using OpenTapo.Diagnostics;
using OpenTapo.Services;
using OpenTapo.Tapo;

namespace OpenTapo.Tests.Integration;

public sealed class StoreTests : IsolatedTest
{
    private static readonly TapoDevice Bulb = new("192.168.1.10", "AA", "L530E", TapoDeviceKind.Smart, "SMART.TAPOBULB");
    private static readonly TapoDevice Camera = new("192.168.1.20", "", "Tapo camera", TapoDeviceKind.Camera);

    [Fact]
    public void Known_devices_start_empty_and_keep_the_first_entry_per_address()
    {
        KnownDeviceStore store = new();
        Assert.Empty(store.Load());

        store.Remember([Bulb, Bulb with { Model = "duplicate" }]);
        IReadOnlyList<TapoDevice> known = store.Remember([Bulb with { Model = "renamed" }, Camera]);

        Assert.Equal([Bulb, Camera], known);
        Assert.Equal([Bulb, Camera], new KnownDeviceStore().Load());
    }

    [Fact]
    public void Remembering_nothing_new_does_not_write()
    {
        new KnownDeviceStore().Remember([]);

        Assert.False(File.Exists(AppFolder.File("devices.json")));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Unreadable_device_list_is_empty(string content)
    {
        Write("devices.json", content);

        Assert.Empty(new KnownDeviceStore().Load());
    }

    [Fact]
    public void Stored_addresses_that_are_not_ipv4_are_dropped()
    {
        Write("devices.json", """[{"Ip":"evil.example","Mac":"","Model":"x","Kind":0,"Type":""},{"Ip":"10.0.0.2","Mac":"","Model":"y","Kind":1,"Type":""}]""");

        Assert.Equal([new TapoDevice("10.0.0.2", "", "y", TapoDeviceKind.Camera)], new KnownDeviceStore().Load());
    }

    [Fact]
    public void Rejected_cameras_persist_until_cleared()
    {
        RejectedCameraStore store = new();
        store.Clear();
        Assert.False(store.Contains("10.0.0.5"));

        store.Add("10.0.0.5");
        store.Add("10.0.0.5");

        Assert.True(new RejectedCameraStore().Contains("10.0.0.5"));
        store.Clear();
        Assert.False(store.Contains("10.0.0.5"));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    public void Unreadable_rejection_list_is_empty(string content)
    {
        Write("rejected-cameras.json", content);

        Assert.False(new RejectedCameraStore().Contains("10.0.0.5"));
    }

    [Fact]
    public void Credentials_round_trip_through_the_locker()
    {
        CredentialStore store = new();
        int changes = 0;
        store.Changed += (_, _) => changes++;
        Assert.Null(store.Load());

        store.Save(Accounts.Wrong);
        store.Save(Accounts.Valid);
        Assert.Equal(Accounts.Valid, new CredentialStore().Load());

        store.Clear();
        Assert.Null(store.Load());
        Assert.Equal(3, changes);
    }

    [Fact]
    public void Error_log_appends_exceptions()
    {
        ErrorLog.Write(new InvalidOperationException("first"));
        ErrorLog.Write(new InvalidOperationException("second"));

        string log = File.ReadAllText(ErrorLog.FilePath);
        Assert.Contains("first", log);
        Assert.Contains("second", log);
    }

    private static void Write(string name, string content)
    {
        Directory.CreateDirectory(AppFolder.Root);
        File.WriteAllText(AppFolder.File(name), content);
    }
}

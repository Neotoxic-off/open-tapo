using System.IO;
using System.Text.Json.Nodes;
using OpenTapo.Diagnostics;
using OpenTapo.Services;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

public sealed class ServiceTests : IsolatedTest
{
    [Fact]
    public async Task Account_is_checked_on_lights_before_cameras()
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync();
        await using FakeCamera camera = await FakeCamera.StartAsync();

        AccountCheck check = await AccountVerifier.VerifyAsync(Accounts.Valid, [Device(camera.Ip, TapoDeviceKind.Camera), Device(bulb.Ip, TapoDeviceKind.Smart)]);

        Assert.Equal(AccountCheck.Valid, check);
        Assert.Equal(0, camera.Challenges);
    }

    [Fact]
    public async Task Account_is_checked_on_a_camera_when_no_light_answers()
    {
        await using FakeKlapDevice tpapOnly = await FakeKlapDevice.StartAsync();
        tpapOnly.RejectHandshake = true;
        await using FakeCamera camera = await FakeCamera.StartAsync();

        AccountCheck check = await AccountVerifier.VerifyAsync(
            Accounts.Valid,
            [Device(Loopback.DeadIp, TapoDeviceKind.Smart), Device(tpapOnly.Ip, TapoDeviceKind.Smart), Device(camera.Ip, TapoDeviceKind.Camera)]);

        Assert.Equal(AccountCheck.Valid, check);
    }

    [Fact]
    public async Task Wrong_account_is_rejected()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();

        Assert.Equal(AccountCheck.Rejected, await AccountVerifier.VerifyAsync(Accounts.Wrong, [Device(camera.Ip, TapoDeviceKind.Camera)]));
    }

    [Fact]
    public async Task No_reachable_device_cannot_confirm_the_account()
    {
        Assert.Equal(AccountCheck.NoDeviceReachable, await AccountVerifier.VerifyAsync(Accounts.Valid, [Device(Loopback.DeadIp, TapoDeviceKind.Camera)]));
    }

    [Fact]
    public void Sessions_need_an_account()
    {
        DeviceSessions sessions = new(new CredentialStore());

        Assert.Throws<TapoAuthException>(() => sessions.Smart("10.0.0.1"));
    }

    [Fact]
    public void Sessions_are_reused_until_the_account_changes()
    {
        SignIn();
        CredentialStore credentials = new();
        DeviceSessions sessions = new(credentials);

        KlapClient first = sessions.Smart("10.0.0.1");
        Assert.Same(first, sessions.Smart("10.0.0.1"));
        Assert.NotSame(first, sessions.Camera("10.0.0.1"));

        credentials.Save(Accounts.Valid);
        Assert.NotSame(first, sessions.Smart("10.0.0.1"));
    }

    [Fact]
    public async Task Camera_rejection_survives_new_sessions_until_sign_in()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        SignIn(Accounts.Wrong);
        CredentialStore credentials = new();

        await Assert.ThrowsAsync<TapoAuthException>(() => new DeviceSessions(credentials).Camera(camera.Ip).ExecuteAsync(CameraApi.DeviceInfo));
        await Assert.ThrowsAsync<TapoAuthException>(() => new DeviceSessions(credentials).Camera(camera.Ip).ExecuteAsync(CameraApi.DeviceInfo));
        Assert.Equal(1, camera.Challenges);

        DeviceSessions sessions = new(credentials);
        credentials.Save(Accounts.Valid);
        await sessions.Camera(camera.Ip).ExecuteAsync(CameraApi.DeviceInfo);
        Assert.Equal(1, camera.Logins);
    }

    [Fact]
    public async Task Probe_without_account_only_lists_devices()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        plug.DiscoveryReply = new JsonObject { ["device_type"] = "SMART.TAPOPLUG", ["device_model"] = "P110", ["mac"] = "AA" };
        TapoDiscovery.Hosts = () => [plug.Ip];
        StringWriter output = new();

        await DeviceProbe.RunAsync(output);

        Assert.Contains("No account stored", output.ToString());
        Assert.Contains($"{plug.Ip,-15} Smart  P110 AA", output.ToString());
        Assert.Equal(0, plug.Handshakes);
    }

    [Fact]
    public async Task Probe_reads_each_device_and_reports_failures()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        await using FakeCamera camera = await FakeCamera.StartAsync();
        await using FakeCamera locked = await FakeCamera.StartAsync();
        plug.DiscoveryReply = new JsonObject { ["device_type"] = "SMART.TAPOPLUG" };
        camera.Results["getDeviceInfo"] = new JsonObject { ["ok"] = true };
        locked.Locked = true;
        TapoDiscovery.Hosts = () => [plug.Ip, camera.Ip, locked.Ip];
        SignIn();
        StringWriter output = new();

        await DeviceProbe.RunAsync(output);

        string text = output.ToString();
        Assert.Contains($"Account: {Accounts.Email}", text);
        Assert.Contains("\"model\":\"P110\"", text);
        Assert.Contains("getDeviceInfo={\"ok\":true}", text);
        Assert.Contains("TapoLockedException", text);
    }

    private static TapoDevice Device(string ip, TapoDeviceKind kind) => new(ip, "", "test", kind);
}

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

public sealed class DiscoveryTests : IsolatedTest
{
    [Fact]
    public async Task Scan_finds_udp_devices_and_cameras_by_certificate()
    {
        await using FakeKlapDevice bulb = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        await using FakeKlapDevice doorbell = await FakeKlapDevice.StartAsync(answerDiscovery: true);
        await using FakeCamera camera = await FakeCamera.StartAsync();
        await using FakeCamera router = await FakeCamera.StartAsync(subject: "CN=router");
        // The payload's "ip" is ignored: only the sender address is trusted.
        bulb.DiscoveryReply = new JsonObject { ["device_type"] = "SMART.TAPOBULB", ["mac"] = "AA-BB", ["device_model"] = "L530E", ["ip"] = "10.9.9.9" };
        doorbell.DiscoveryReply = new JsonObject { ["device_type"] = "SMART.IPCAMERA" };
        TapoDiscovery.Hosts = () => [bulb.Ip, doorbell.Ip, camera.Ip, router.Ip, Loopback.DeadIp];

        IReadOnlyList<TapoDevice> found = await TapoDiscovery.ScanAsync(TimeSpan.FromSeconds(1));

        Assert.True(bulb.DiscoveryPacketValid);
        List<TapoDevice> expected =
        [
            new(bulb.Ip, "AA-BB", "L530E", TapoDeviceKind.Smart, "SMART.TAPOBULB"),
            new(camera.Ip, "", "Tapo camera", TapoDeviceKind.Camera),
            new(doorbell.Ip, "", "Tapo", TapoDeviceKind.Camera, "SMART.IPCAMERA"),
        ];
        Assert.Equal(expected.OrderBy(device => device.Kind).ThenBy(device => device.Ip), found);
    }

    [Fact]
    public async Task Identify_tells_cameras_from_klap_devices()
    {
        await using FakeKlapDevice plug = await FakeKlapDevice.StartAsync();
        await using FakeCamera camera = await FakeCamera.StartAsync(subject: "CN=TAPO-DEVICE");

        Assert.Equal(TapoDeviceKind.Camera, await TapoDiscovery.IdentifyAsync(camera.Ip));
        Assert.Equal(TapoDeviceKind.Smart, await TapoDiscovery.IdentifyAsync(plug.Ip));
        Assert.Null(await TapoDiscovery.IdentifyAsync(Loopback.DeadIp));
    }

    [Fact]
    public async Task A_host_that_drops_tls_is_not_a_camera()
    {
        string ip = Loopback.NextIp();
        TcpListener listener = new(IPAddress.Parse(ip), 443);
        listener.Start();
        Task drop = Task.Run(async () =>
        {
            using TcpClient peer = await listener.AcceptTcpClientAsync();
        });

        Assert.False(await TapoDiscovery.IsCameraAsync(ip));
        await drop;
        listener.Stop();
    }

    [Theory]
    [InlineData("")]
    [InlineData("0123456789abcdef")]
    [InlineData("0123456789abcdef{}")]
    [InlineData("0123456789abcdef{\"result\":null}")]
    [InlineData("0123456789abcdef{not json")]
    public void Malformed_replies_are_ignored(string packet) =>
        Assert.False(TapoDiscovery.TryParseReply(Reply(packet), out _));

    [Fact]
    public void Reply_fields_default_when_missing()
    {
        Assert.True(TapoDiscovery.TryParseReply(Reply("0123456789abcdef{\"result\":{}}"), out TapoDevice? device));
        Assert.Equal(new TapoDevice("127.0.0.7", "", "Tapo", TapoDeviceKind.Smart, ""), device);
    }

    [Fact]
    public void Subnet_hosts_cover_each_ipv4_slash_24()
    {
        List<string> hosts = TapoDiscovery.LocalSubnetHosts().ToList();

        Assert.Equal(0, hosts.Count % 254);
        Assert.All(hosts, host => Assert.True(TapoDevice.IsValidAddress(host)));
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData(" 10.0.0.1 ", true)]
    [InlineData("::1", false)]
    [InlineData("example.com", false)]
    [InlineData(null, false)]
    public void Only_ipv4_literals_are_valid_addresses(string? ip, bool valid) =>
        Assert.Equal(valid, TapoDevice.IsValidAddress(ip));

    private static UdpReceiveResult Reply(string packet) =>
        new(Encoding.UTF8.GetBytes(packet), new IPEndPoint(IPAddress.Parse("127.0.0.7"), 20002));
}

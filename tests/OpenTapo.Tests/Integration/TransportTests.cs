using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

/// <summary>The transports added for older Tapo firmware, robot vacuums and Kasa devices, against fakes speaking each protocol.</summary>
public sealed class TransportTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Aes_client_logs_in_and_calls(int loginVersion)
    {
        await using FakeAesDevice device = await FakeAesDevice.StartAsync(loginVersion);
        AesClient client = new(device.Ip, Accounts.Email, Accounts.Password, loginVersion);

        await client.CallAsync("set_device_info", new JsonObject { ["device_on"] = true });
        IReadOnlyDictionary<string, JsonNode?> results = await client.ExecuteAsync("get_device_info", "get_device_usage");

        Assert.Equal(true, (bool?)results["get_device_info"]!["device_on"]);
        Assert.Null(results["get_device_usage"]);
        Assert.Equal(1, device.Logins);
    }

    [Fact]
    public async Task Aes_client_reports_a_wrong_password()
    {
        await using FakeAesDevice device = await FakeAesDevice.StartAsync();

        await Assert.ThrowsAsync<TapoAuthException>(() => new AesClient(device.Ip, Accounts.Email, "wrong").CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Aes_client_logs_in_again_after_a_reboot()
    {
        await using FakeAesDevice device = await FakeAesDevice.StartAsync();
        AesClient client = new(device.Ip, Accounts.Email, Accounts.Password);
        await client.CallAsync("get_device_info");

        device.ExpireSessions();
        await client.CallAsync("get_device_info");

        Assert.Equal(2, device.Logins);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[1]")]
    [InlineData("""{"error_code":0,"result":{"key":"bm90IGEga2V5"}}""")]
    public async Task Aes_client_rejects_a_malformed_handshake(string body)
    {
        await using FakeAesDevice device = await FakeAesDevice.StartAsync();
        device.Raw = context => context.Response.WriteAsync(body);

        await Assert.ThrowsAsync<TapoException>(() => new AesClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Aes_client_rejects_a_malformed_reply()
    {
        await using FakeAesDevice device = await FakeAesDevice.StartAsync();
        device.PlainReply = "[]";

        await Assert.ThrowsAsync<TapoException>(() => new AesClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Ssl_client_logs_in_and_calls()
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();
        SslClient client = new(vacuum.Ip, Accounts.Email, Accounts.Password);

        JsonNode info = await client.CallAsync("get_device_info");
        vacuum.ExpireSessions();
        await client.CallAsync("setSwitchCharge", new JsonObject { ["switch_charge"] = true });

        Assert.Equal("RV30 Max", (string?)info["model"]);
        Assert.Equal(2, vacuum.Logins);
        Assert.Contains(vacuum.Calls, call => call.Method == "setSwitchCharge");
    }

    [Fact]
    public async Task Ssl_client_reports_a_wrong_password()
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();

        await Assert.ThrowsAsync<TapoAuthException>(() => new SslClient(vacuum.Ip, Accounts.Email, "wrong").CallAsync("get_device_info"));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"error_code":0,"result":[]}""")]
    public async Task Ssl_client_rejects_a_malformed_reply(string body)
    {
        await using FakeVacuum vacuum = await FakeVacuum.StartAsync();
        vacuum.Raw = context => context.Response.WriteAsync(body);

        await Assert.ThrowsAsync<TapoException>(() => new SslClient(vacuum.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Klap_v2_runs_over_https()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync(https: true);

        JsonNode info = await new KlapClient(device.Ip, Accounts.Email, Accounts.Password, https: true).CallAsync("get_device_info");

        Assert.Equal("P110", (string?)info["model"]);
    }

    [Fact]
    public async Task Klap_v1_carries_kasa_payloads()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync(version: 1);
        device.Kasa = new FakeIot();

        JsonNode reply = await new KlapClient(device.Ip, Accounts.Email, Accounts.Password, version: 1).QueryAsync(new JsonObject { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject() } });

        Assert.Equal("Desk", (string?)reply["system"]!["get_sysinfo"]!["alias"]);
    }

    [Fact]
    public async Task Klap_v1_rejects_a_wrong_password()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync(version: 1);

        await Assert.ThrowsAsync<TapoAuthException>(() => new KlapClient(device.Ip, Accounts.Email, "wrong", version: 1).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Xor_client_queries_a_legacy_device()
    {
        await using FakeKasaDevice device = FakeKasaDevice.Start();

        JsonNode reply = await new XorClient(device.Ip).QueryAsync(new JsonObject { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject() } });

        Assert.Equal("HS110(EU)", (string?)reply["system"]!["get_sysinfo"]!["model"]);
    }

    [Fact]
    public async Task Xor_client_reports_an_unreachable_device_as_offline()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => new XorClient(Loopback.DeadIp).QueryAsync([]));
    }

    [Fact]
    public async Task Xor_client_rejects_malformed_replies()
    {
        await using FakeKasaDevice device = FakeKasaDevice.Start();
        XorClient client = new(device.Ip);

        device.ReplyLength = -1;
        await Assert.ThrowsAsync<TapoException>(() => client.QueryAsync([]));
        device.ReplyLength = null;
        device.RawReply = Xor.Encrypt(Encoding.UTF8.GetBytes("{not json"));
        await Assert.ThrowsAsync<TapoException>(() => client.QueryAsync([]));
        device.RawReply = Xor.Encrypt(Encoding.UTF8.GetBytes("null"));
        await Assert.ThrowsAsync<TapoException>(() => client.QueryAsync([]));
    }

    [Fact]
    public async Task Linkie_client_queries_a_kasa_camera()
    {
        await using FakeKasaDevice camera = await FakeKasaDevice.StartLinkieAsync();

        JsonNode reply = await new LinkieClient(camera.Ip).QueryAsync(new JsonObject { ["system"] = new JsonObject { ["get_sysinfo"] = new JsonObject() } });

        Assert.Equal("KC105", (string?)reply["system"]!["get_sysinfo"]!["model"]);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("bnVsbA==")]
    public async Task Linkie_client_rejects_malformed_replies(string body)
    {
        await using FakeKasaDevice camera = await FakeKasaDevice.StartLinkieAsync();
        camera.RawReply = Encoding.UTF8.GetBytes(body == "bnVsbA==" ? Convert.ToBase64String(Xor.Encrypt(Encoding.UTF8.GetBytes("null"))) : body);

        await Assert.ThrowsAsync<TapoException>(() => new LinkieClient(camera.Ip).QueryAsync([]));
    }

    [Fact]
    public void Xor_cipher_round_trips()
    {
        byte[] plain = Encoding.UTF8.GetBytes("""{"system":{"get_sysinfo":{}}}""");

        Assert.Equal(plain, Xor.Decrypt(Xor.Encrypt(plain)));
        Assert.Equal(0xD0, Xor.Encrypt(plain)[0]);
    }
}

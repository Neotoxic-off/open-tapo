using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

public sealed class KlapClientTests
{
    [Fact]
    public async Task Handshakes_then_reads_and_writes_state()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);

        JsonNode info = await client.CallAsync("get_device_info");
        await client.CallAsync("set_device_info", new JsonObject { ["device_on"] = true });

        Assert.Equal("P110", (string?)info["model"]);
        Assert.True((bool?)device.Info["device_on"]);
        Assert.Equal(1, device.Handshakes);
    }

    [Fact]
    public async Task Call_without_result_returns_an_empty_object()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        JsonNode result = await new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("set_led_info");

        Assert.Empty(result.AsObject());
    }

    [Fact]
    public async Task Batches_getters_and_maps_failures_to_null()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.Results["get_device_usage"] = new JsonObject { ["time_usage"] = new JsonObject { ["today"] = 5 } };

        IReadOnlyDictionary<string, JsonNode?> results = await new KlapClient(device.Ip, Accounts.Email, Accounts.Password)
            .ExecuteAsync("get_device_info", "get_device_usage", "get_energy_usage");

        Assert.NotNull(results["get_device_info"]);
        Assert.Equal(5, (int?)results["get_device_usage"]?["time_usage"]?["today"]);
        Assert.Null(results["get_energy_usage"]);
    }

    [Fact]
    public async Task Batch_reply_without_responses_is_empty()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);
        await client.CallAsync("get_device_info");
        device.PlainReply = """{"error_code":0,"result":{}}""";

        Assert.Empty(await client.ExecuteAsync("get_device_info"));
    }

    [Fact]
    public async Task Batch_skips_responses_without_a_method()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);
        await client.CallAsync("get_device_info");
        device.PlainReply = """{"error_code":0,"result":{"responses":[{"error_code":0},null]}}""";

        Assert.Empty(await client.ExecuteAsync("get_device_info"));
    }

    [Fact]
    public async Task Wrong_credentials_are_rejected_locally()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();

        await Assert.ThrowsAsync<TapoAuthException>(() => new KlapClient(device.Ip, Accounts.Email, "wrong").CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Tpap_only_firmware_is_reported()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.RejectHandshake = true;

        TapoException error = await Assert.ThrowsAsync<TapoException>(() => new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
        Assert.Contains("TPAP", error.Message);
    }

    [Fact]
    public async Task Handshake_http_error_is_an_http_failure()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.RawHandshake = context =>
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return Task.CompletedTask;
        };

        await Assert.ThrowsAsync<HttpRequestException>(() => new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Short_handshake_reply_is_rejected()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.RawHandshake = context => context.Response.Body.WriteAsync(new byte[20]).AsTask();

        await Assert.ThrowsAsync<TapoException>(() => new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Works_without_a_session_cookie()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.SendCookie = false;

        JsonNode info = await new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("get_device_info");

        Assert.NotNull(info["model"]);
    }

    [Fact]
    public async Task Device_error_code_is_raised()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        device.ErrorCode = -1008;

        TapoException error = await Assert.ThrowsAsync<TapoException>(
            () => new KlapClient(device.Ip, Accounts.Email, Accounts.Password).CallAsync("set_device_info", new JsonObject { ["brightness"] = 5 }));
        Assert.Equal("set_device_info: error -1008", error.Message);
    }

    [Fact]
    public async Task Empty_reply_is_rejected()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);
        await client.CallAsync("get_device_info");
        device.PlainReply = "null";

        await Assert.ThrowsAsync<TapoException>(() => client.CallAsync("get_device_info"));
    }

    [Fact]
    public async Task Expired_session_handshakes_again_once()
    {
        await using FakeKlapDevice device = await FakeKlapDevice.StartAsync();
        KlapClient client = new(device.Ip, Accounts.Email, Accounts.Password);
        await client.CallAsync("get_device_info");
        device.ExpireSessions();

        await client.CallAsync("get_device_info");

        Assert.Equal(2, device.Handshakes);
    }

    [Fact]
    public async Task Unreachable_device_is_an_http_failure()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => new KlapClient(Loopback.DeadIp, Accounts.Email, Accounts.Password).CallAsync("get_device_info"));
    }
}

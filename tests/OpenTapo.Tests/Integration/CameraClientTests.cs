using System.Net.Http;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using OpenTapo.Tapo;
using OpenTapo.Tests.Fakes;

namespace OpenTapo.Tests.Integration;

public sealed class CameraClientTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Logs_in_and_batches_requests(bool md5, bool dataAtTopLevel)
    {
        await using FakeCamera camera = await FakeCamera.StartAsync(md5: md5);
        camera.DataAtTopLevel = dataAtTopLevel;
        camera.Results["getDeviceInfo"] = new JsonObject { ["device_info"] = new JsonObject { ["basic_info"] = new JsonObject { ["device_alias"] = "Hall" } } };
        CameraClient client = new(camera.Ip, Accounts.Password);

        IReadOnlyDictionary<string, JsonNode?> results = await client.ExecuteAsync(CameraApi.DeviceInfo, CameraApi.SdCard);
        await client.ExecuteAsync(CameraSwitch.Privacy.Set(true));

        Assert.Equal("Hall", (string?)results["getDeviceInfo"]?["device_info"]?["basic_info"]?["device_alias"]);
        Assert.Null(results["getSdCardStatus"]);
        Assert.Equal(1, camera.Logins);
        Assert.Equal(0, camera.TagMismatches);
        Assert.Contains(camera.Calls, call => call.Method == "setLensMaskConfig");
    }

    [Fact]
    public async Task Sequence_defaults_to_zero_without_start_seq()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.OmitStartSeq = true;
        CameraClient client = new(camera.Ip, Accounts.Password);

        await client.ExecuteAsync(CameraApi.DeviceInfo);
        await client.ExecuteAsync(CameraApi.DeviceInfo);

        Assert.Equal(0, camera.TagMismatches);
    }

    [Fact]
    public async Task Wrong_password_is_caught_before_login_and_never_retried()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        CameraClient client = new(camera.Ip, "wrong");
        int rejections = 0;
        client.Rejected += (_, _) => rejections++;

        await Assert.ThrowsAsync<TapoAuthException>(() => client.ExecuteAsync(CameraApi.DeviceInfo));
        await Assert.ThrowsAsync<TapoAuthException>(() => client.ExecuteAsync(CameraApi.DeviceInfo));

        Assert.Equal(1, camera.Challenges);
        Assert.Equal(0, camera.Logins);
        Assert.Equal(1, rejections);
    }

    [Fact]
    public async Task Known_rejection_spends_no_attempt()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();

        await Assert.ThrowsAsync<TapoAuthException>(() => new CameraClient(camera.Ip, Accounts.Password, knownRejected: true).ExecuteAsync(CameraApi.DeviceInfo));

        Assert.Equal(0, camera.Challenges);
    }

    [Theory]
    [InlineData(600, 600)]
    [InlineData(null, 1800)]
    public async Task Lockout_blocks_further_attempts(int? secondsLeft, int expectedSeconds)
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.Locked = true;
        camera.LockedSecondsLeft = secondsLeft;
        CameraClient client = new(camera.Ip, Accounts.Password);

        TapoLockedException error = await Assert.ThrowsAsync<TapoLockedException>(() => client.ExecuteAsync(CameraApi.DeviceInfo));
        await Assert.ThrowsAsync<TapoLockedException>(() => client.ExecuteAsync(CameraApi.DeviceInfo));

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), error.RetryAfter);
        Assert.Equal(1, camera.Challenges);
    }

    [Fact]
    public async Task Missing_nonce_is_rejected()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.OmitNonce = true;

        TapoException error = await Assert.ThrowsAsync<TapoException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
        Assert.Contains("nonce", error.Message);
    }

    [Fact]
    public async Task Missing_token_is_an_auth_failure()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.OmitStok = true;

        await Assert.ThrowsAsync<TapoAuthException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
    }

    [Fact]
    public async Task Error_code_reply_is_raised()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.ErrorCode = -40210;

        TapoException error = await Assert.ThrowsAsync<TapoException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
        Assert.Equal("Camera: error -40210", error.Message);
    }

    [Fact]
    public async Task Missing_response_is_rejected()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.OmitResponse = true;

        await Assert.ThrowsAsync<TapoException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
    }

    [Fact]
    public async Task Null_payload_is_rejected()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.PlainReply = "null";

        await Assert.ThrowsAsync<TapoException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
    }

    [Fact]
    public async Task Null_http_body_is_rejected()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        camera.Raw = context => context.Response.WriteAsync("null");

        await Assert.ThrowsAsync<TapoException>(() => new CameraClient(camera.Ip, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
    }

    [Fact]
    public async Task Responses_without_method_or_list_are_skipped()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        CameraClient client = new(camera.Ip, Accounts.Password);

        camera.PlainReply = """{"result":{"responses":[{"error_code":0},null]}}""";
        Assert.Empty(await client.ExecuteAsync(CameraApi.DeviceInfo));
        camera.PlainReply = """{"result":{}}""";
        Assert.Empty(await client.ExecuteAsync(CameraApi.DeviceInfo));
    }

    [Fact]
    public async Task Expired_session_logs_in_again_once()
    {
        await using FakeCamera camera = await FakeCamera.StartAsync();
        CameraClient client = new(camera.Ip, Accounts.Password);
        await client.ExecuteAsync(CameraApi.DeviceInfo);
        camera.ExpireSessions();

        await client.ExecuteAsync(CameraApi.DeviceInfo);

        Assert.Equal(2, camera.Logins);
    }

    [Fact]
    public async Task Unreachable_camera_is_an_http_failure()
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => new CameraClient(Loopback.DeadIp, Accounts.Password).ExecuteAsync(CameraApi.DeviceInfo));
    }
}

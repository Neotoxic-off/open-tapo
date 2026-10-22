using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging;

namespace OpenTapo.Tests.Fakes;

/// <summary>Fake devices live on their own 127.0.0.x address, because the clients use fixed ports.</summary>
internal static class Loopback
{
    /// <summary>Nothing ever listens here: connections are refused at once.</summary>
    public const string DeadIp = "127.0.9.9";

    private static int _next = 1;

    public static string NextIp()
    {
        int host = (Interlocked.Increment(ref _next) % 250) + 2;
        return $"127.0.0.{host}";
    }

    public static async Task<WebApplication> StartAsync(string ip, int port, RequestDelegate handle, Action<ListenOptions>? configure = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Parse(ip), port, options => configure?.Invoke(options)));
        WebApplication app = builder.Build();
        app.Run(handle);
        await app.StartAsync();
        return app;
    }

    public static async Task<byte[]> ReadBodyAsync(HttpRequest request)
    {
        using MemoryStream buffer = new();
        await request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    public static string Sha256Hex(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string Md5Hex(string value) => Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(value)));
}

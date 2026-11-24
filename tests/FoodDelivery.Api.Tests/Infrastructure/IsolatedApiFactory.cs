using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Api.Tests.Infrastructure;

/// <summary>
/// A separate app instance with its own settings (and optionally replaced services), pointing
/// at the shared, already migrated test database. Create one per test so in-memory state such
/// as rate-limit counters or a fake clock is never shared between tests.
/// </summary>
public class IsolatedApiFactory(
    string connectionString,
    IDictionary<string, string?> settings,
    Action<IServiceCollection>? configureServices = null)
    : WebApplicationFactory<Program>
{
    // TEST-ONLY: lets a test choose the client IP. Never do this in production code:
    // it would let clients pick their own IP and bypass per-IP rate limits.
    public const string ClientIpHeader = "X-Test-Client-Ip";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Default", connectionString);
        builder.UseSetting("Jwt:Key", ApiFactory.TestJwtKey);
        foreach (var (key, value) in settings) builder.UseSetting(key, value);

        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, FakeClientIpStartupFilter>();
            configureServices?.Invoke(services);
        });
    }

    public HttpClient CreateClientFromIp(string ip)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ClientIpHeader, ip);
        return client;
    }

    // A startup filter's middleware runs before everything in Program.cs, including the rate limiter.
    private class FakeClientIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (IPAddress.TryParse(context.Request.Headers[ClientIpHeader], out var ip))
                    context.Connection.RemoteIpAddress = ip;
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Api.Tests.Infrastructure;

/// <summary>
/// A second app instance with small rate limits, pointing at the shared (already migrated)
/// test database. Create one per test so every test starts with empty rate-limit counters.
/// </summary>
public class RateLimitedApiFactory(string connectionString, IDictionary<string, string?> settings)
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
            services.AddSingleton<IStartupFilter, FakeClientIpStartupFilter>());
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

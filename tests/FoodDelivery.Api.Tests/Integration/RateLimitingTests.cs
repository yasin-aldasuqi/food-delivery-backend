using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class RateLimitingTests : IDisposable
{
    private const int LoginLimit = 3;
    private const int RegisterLimit = 2;
    private const int WindowSeconds = 60;

    private readonly ApiFactory _shared;
    private readonly IsolatedApiFactory _limited;

    public RateLimitingTests(ApiFactory shared)
    {
        _shared = shared;
        // A fresh app per test => fresh rate-limit counters, no interference between tests.
        _limited = CreateLimitedFactory(LoginLimit, RegisterLimit);
    }

    public void Dispose() => _limited.Dispose();

    [Fact]
    public async Task Login_attempts_over_the_limit_get_429_with_retry_after()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        var client = _limited.CreateClientFromIp("203.0.113.10");

        for (var i = 0; i < LoginLimit; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(client, user.Email, "wrong-password")).StatusCode);

        var blocked = await LoginAsync(client, user.Email, "wrong-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, blocked.StatusCode);
        var retryAfter = int.Parse(blocked.Headers.GetValues("Retry-After").Single());
        Assert.InRange(retryAfter, 1, WindowSeconds);
        Assert.Equal("Too many requests", (await blocked.ReadAsync<ProblemDetails>()).Title);
    }

    [Fact]
    public async Task Correct_password_is_also_blocked_once_the_limit_is_reached()
    {
        // An attacker who guesses the right password after the limit still doesn't get in.
        var user = await _shared.RegisterAsync(UserRole.Customer);
        var client = _limited.CreateClientFromIp("203.0.113.11");

        for (var i = 0; i < LoginLimit; i++)
            await LoginAsync(client, user.Email, "wrong-password");

        var correct = await LoginAsync(client, user.Email, TestHelpers.Password);

        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
    }

    [Fact]
    public async Task Each_client_ip_has_its_own_limit()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        var attacker = _limited.CreateClientFromIp("203.0.113.12");
        var someoneElse = _limited.CreateClientFromIp("198.51.100.7");

        for (var i = 0; i <= LoginLimit; i++)
            await LoginAsync(attacker, user.Email, "wrong-password");

        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(attacker, user.Email, TestHelpers.Password)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(someoneElse, user.Email, TestHelpers.Password)).StatusCode);
    }

    [Fact]
    public async Task Registration_is_limited()
    {
        var client = _limited.CreateClientFromIp("203.0.113.13");

        for (var i = 0; i < RegisterLimit; i++)
            Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client)).StatusCode);

        Assert.Equal(HttpStatusCode.TooManyRequests, (await RegisterAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Login_and_register_limits_are_independent()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        var client = _limited.CreateClientFromIp("203.0.113.14");

        for (var i = 0; i <= LoginLimit; i++)
            await LoginAsync(client, user.Email, "wrong-password");

        Assert.Equal(HttpStatusCode.Created, (await RegisterAsync(client)).StatusCode);
    }

    [Fact]
    public async Task Other_endpoints_are_not_rate_limited()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        var client = _limited.CreateClientFromIp("203.0.113.15");

        for (var i = 0; i <= LoginLimit; i++)
            await LoginAsync(client, user.Email, "wrong-password");

        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/restaurants")).StatusCode);
    }

    [Fact]
    public void Invalid_rate_limit_settings_prevent_startup()
    {
        using var misconfigured = CreateLimitedFactory(loginLimit: 0, registerLimit: RegisterLimit);

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());

        Assert.Contains("RateLimiting", error.ToString());
    }

    private IsolatedApiFactory CreateLimitedFactory(int loginLimit, int registerLimit) =>
        new(_shared.GetConnectionString(), new Dictionary<string, string?>
        {
            ["RateLimiting:Login:PermitLimit"] = loginLimit.ToString(),
            ["RateLimiting:Login:WindowSeconds"] = WindowSeconds.ToString(),
            ["RateLimiting:Register:PermitLimit"] = registerLimit.ToString(),
            ["RateLimiting:Register:WindowSeconds"] = WindowSeconds.ToString()
        });

    private static Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private static Task<HttpResponseMessage> RegisterAsync(HttpClient client) =>
        client.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Rate Test",
            email = TestHelpers.UniqueEmail("rate"),
            password = TestHelpers.Password
        });
}

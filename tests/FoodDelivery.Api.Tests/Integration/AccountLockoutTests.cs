using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class AccountLockoutTests : IDisposable
{
    private const int MaxFailedAttempts = 3;
    private static readonly TimeSpan LockoutDuration = TimeSpan.FromMinutes(15);
    private const string WrongPassword = "wrong-password";

    private readonly ApiFactory _shared;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly IsolatedApiFactory _app;
    private readonly HttpClient _client;

    public AccountLockoutTests(ApiFactory shared)
    {
        _shared = shared;
        _app = CreateApp(MaxFailedAttempts, (int)LockoutDuration.TotalMinutes);
        _client = _app.CreateClient();
    }

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Account_locks_after_max_failed_attempts_even_for_the_correct_password()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);

        for (var i = 0; i < MaxFailedAttempts; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, WrongPassword)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);
        Assert.NotNull(await GetLockoutEndAsync(user.Id));
    }

    [Fact]
    public async Task A_locked_account_looks_exactly_like_a_wrong_password_or_unknown_email()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        for (var i = 0; i < MaxFailedAttempts; i++)
            await LoginAsync(user.Email, WrongPassword);

        var lockedCorrectPassword = await LoginAsync(user.Email, TestHelpers.Password);
        var otherUser = await _shared.RegisterAsync(UserRole.Customer);
        var wrongPassword = await LoginAsync(otherUser.Email, WrongPassword);
        var unknownEmail = await LoginAsync(TestHelpers.UniqueEmail("nobody"), WrongPassword);

        var responses = new[] { lockedCorrectPassword, wrongPassword, unknownEmail };
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        var titles = await Task.WhenAll(responses.Select(async r => (await r.ReadAsync<ProblemDetails>()).Title));
        Assert.Single(titles.Distinct());
        Assert.All(responses, r => Assert.False(r.Headers.Contains("Retry-After")));
    }

    [Fact]
    public async Task Lockout_ends_by_itself_after_the_configured_duration()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        for (var i = 0; i < MaxFailedAttempts; i++)
            await LoginAsync(user.Email, WrongPassword);

        _clock.Advance(LockoutDuration - TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);
        Assert.Null(await GetLockoutEndAsync(user.Id));
    }

    [Fact]
    public async Task Attempts_during_a_lockout_do_not_extend_it()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);
        for (var i = 0; i < MaxFailedAttempts; i++)
            await LoginAsync(user.Email, WrongPassword);

        _clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < MaxFailedAttempts * 2; i++)
            await LoginAsync(user.Email, WrongPassword);

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1)); // 15m01s after the lockout began
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_count()
    {
        var user = await _shared.RegisterAsync(UserRole.Customer);

        for (var i = 0; i < MaxFailedAttempts - 1; i++) await LoginAsync(user.Email, WrongPassword);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);

        for (var i = 0; i < MaxFailedAttempts - 1; i++) await LoginAsync(user.Email, WrongPassword);
        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(user.Email, TestHelpers.Password)).StatusCode);
    }

    [Fact]
    public async Task Lockout_only_affects_the_targeted_account()
    {
        var victim = await _shared.RegisterAsync(UserRole.Customer);
        var bystander = await _shared.RegisterAsync(UserRole.Customer);

        for (var i = 0; i < MaxFailedAttempts; i++)
            await LoginAsync(victim.Email, WrongPassword);

        Assert.Equal(HttpStatusCode.OK, (await LoginAsync(bystander.Email, TestHelpers.Password)).StatusCode);
    }

    [Fact]
    public async Task Parallel_failed_attempts_are_all_counted()
    {
        // With a read-modify-write counter, concurrent failures overwrite each other and the
        // count never reaches the limit. With an atomic UPDATE, exactly the limit locks it.
        const int limit = 10;
        using var app = CreateApp(limit, (int)LockoutDuration.TotalMinutes);
        var client = app.CreateClient();
        var user = await _shared.RegisterAsync(UserRole.Customer);

        await Task.WhenAll(Enumerable.Range(0, limit).Select(_ =>
            client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password = WrongPassword })));

        Assert.NotNull(await GetLockoutEndAsync(user.Id));
        var correct = await client.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = TestHelpers.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, correct.StatusCode);
    }

    [Fact]
    public void Invalid_lockout_settings_prevent_startup()
    {
        using var misconfigured = CreateApp(maxFailedAttempts: 0, durationMinutes: 15);

        var error = Assert.ThrowsAny<Exception>(() => misconfigured.CreateClient());

        Assert.Contains("MaxFailedAttempts", error.ToString());
    }

    private IsolatedApiFactory CreateApp(int maxFailedAttempts, int durationMinutes) =>
        new(_shared.GetConnectionString(),
            new Dictionary<string, string?>
            {
                ["Lockout:MaxFailedAttempts"] = maxFailedAttempts.ToString(),
                ["Lockout:DurationMinutes"] = durationMinutes.ToString(),
                // Rate limits off here, so a rejection is always caused by the lockout.
                ["RateLimiting:Login:PermitLimit"] = "100000",
                ["RateLimiting:Login:WindowSeconds"] = "60",
                ["RateLimiting:Register:PermitLimit"] = "100000",
                ["RateLimiting:Register:WindowSeconds"] = "60"
            },
            services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(_clock)));

    private Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        _client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private async Task<DateTime?> GetLockoutEndAsync(int userId)
    {
        using var scope = _shared.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Users.Where(u => u.Id == userId).Select(u => u.LockoutEnd).SingleAsync();
    }
}

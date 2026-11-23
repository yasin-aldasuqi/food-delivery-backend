using System.Net;
using System.Net.Http.Json;
using System.Text;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class AuthTests(ApiFactory factory)
{
    private readonly HttpClient _anonymous = factory.CreateClient();

    [Fact]
    public async Task Register_normalizes_email_and_login_accepts_any_case()
    {
        var mixedCase = $"Mixed.Case-{Guid.NewGuid():N}@Test.COM";

        var user = await factory.RegisterAsync(UserRole.Customer, mixedCase);

        Assert.Equal(mixedCase.ToLowerInvariant(), user.Email);

        var login = await _anonymous.PostAsJsonAsync("/api/auth/login",
            new { email = mixedCase.ToUpperInvariant(), password = TestHelpers.Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Password_is_stored_as_a_hash_not_plain_text()
    {
        var user = await factory.RegisterAsync(UserRole.Customer);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var hash = await db.Users.Where(u => u.Id == user.Id).Select(u => u.PasswordHash).SingleAsync();

        Assert.NotEqual(TestHelpers.Password, hash);
        Assert.DoesNotContain(TestHelpers.Password, hash);
        Assert.StartsWith("AQAAAA", hash); // ASP.NET Core PasswordHasher v3 format (PBKDF2)
    }

    [Fact]
    public async Task Cannot_self_register_as_admin()
    {
        var response = await _anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Sneaky",
            email = TestHelpers.UniqueEmail(),
            password = TestHelpers.Password,
            role = "Admin"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Duplicate_email_with_different_case_returns_409()
    {
        var email = TestHelpers.UniqueEmail();
        await factory.RegisterAsync(UserRole.Customer, email);

        var response = await _anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Again",
            email = email.ToUpperInvariant(),
            password = TestHelpers.Password
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_email_give_identical_errors()
    {
        var user = await factory.RegisterAsync(UserRole.Customer);

        var wrongPassword = await _anonymous.PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = "wrong-password" });
        var unknownEmail = await _anonymous.PostAsJsonAsync("/api/auth/login",
            new { email = TestHelpers.UniqueEmail("nobody"), password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);

        var a = await wrongPassword.ReadAsync<ProblemDetails>();
        var b = await unknownEmail.ReadAsync<ProblemDetails>();
        Assert.Equal(a.Title, b.Title); // no hint about which part was wrong
    }

    [Fact]
    public async Task Protected_endpoint_without_token_returns_401()
    {
        var response = await _anonymous.GetAsync("/api/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Me_returns_the_user_from_the_token()
    {
        var user = await factory.RegisterAsync(UserRole.RestaurantOwner);

        var me = await (await user.Client.GetAsync("/api/auth/me")).ReadAsync<UserResponse>();

        Assert.Equal(user.Id, me.Id);
        Assert.Equal("RestaurantOwner", me.Role);
    }

    [Fact]
    public async Task Token_with_edited_payload_is_rejected()
    {
        // Get a real customer token...
        var response = await _anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = "Customer",
            email = TestHelpers.UniqueEmail(),
            password = TestHelpers.Password
        });
        var token = (await response.ReadAsync<AuthResponse>()).Token;

        // ...decode the payload (anyone can — it's NOT encrypted), promote ourselves to Admin,
        // re-encode it, and keep the original signature.
        var parts = token.Split('.');
        var payload = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
        Assert.Contains("\"role\":\"Customer\"", payload);

        var forgedPayload = payload.Replace("\"role\":\"Customer\"", "\"role\":\"Admin\"");
        var forgedToken = $"{parts[0]}.{Base64UrlEncode(Encoding.UTF8.GetBytes(forgedPayload))}.{parts[2]}";

        var me = await factory.ClientWithToken(forgedToken).GetAsync("/api/auth/me");

        // The signature no longer matches the payload.
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

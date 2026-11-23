using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Api.Tests.Infrastructure;

public record TestUser(int Id, string Email, string Password, HttpClient Client);

public record TestRestaurant(int Id, int BurgerId, int FriesId, int UnavailableId);

public static class TestHelpers
{
    public const string Password = "Passw0rd!";

    // Same JSON rules as the API (camelCase, enums as strings).
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T> ReadAsync<T>(this HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(Json))!;

    // Unique email per call so tests never collide on the shared database.
    public static string UniqueEmail(string prefix = "user") => $"{prefix}-{Guid.NewGuid():N}@test.com";

    public static async Task<TestUser> RegisterAsync(this ApiFactory factory, UserRole role, string? email = null)
    {
        email ??= UniqueEmail(role.ToString().ToLowerInvariant());
        var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/auth/register", new
        {
            fullName = $"Test {role}",
            email,
            password = Password,
            role = role.ToString()
        });
        response.EnsureSuccessStatusCode();
        var auth = (await response.Content.ReadFromJsonAsync<AuthResponse>())!;

        return new TestUser(auth.User.Id, auth.User.Email, Password, factory.ClientWithToken(auth.Token));
    }

    // Admins can't self-register (by design), so promote a customer in the database, then log in.
    public static async Task<TestUser> CreateAdminAsync(this ApiFactory factory)
    {
        var user = await factory.RegisterAsync(UserRole.Customer);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.Users.Where(u => u.Id == user.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, UserRole.Admin));
        }

        var login = await factory.CreateClient().PostAsJsonAsync("/api/auth/login",
            new { email = user.Email, password = Password });
        login.EnsureSuccessStatusCode();
        var auth = (await login.Content.ReadFromJsonAsync<AuthResponse>())!;
        return user with { Client = factory.ClientWithToken(auth.Token) };
    }

    public static HttpClient ClientWithToken(this ApiFactory factory, string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    // POST /api/orders with any body and an explicit Idempotency-Key (null = send no header).
    public static Task<HttpResponseMessage> PostOrderAsync(this TestUser customer, object body, string? idempotencyKey)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/orders")
        {
            Content = JsonContent.Create(body)
        };
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        return customer.Client.SendAsync(request);
    }

    public static object OrderBody(int restaurantId, params (int MenuItemId, int Quantity)[] items) => new
    {
        restaurantId,
        deliveryAddress = "Olaya St, Riyadh",
        items = items.Select(i => new { menuItemId = i.MenuItemId, quantity = i.Quantity })
    };

    // Places an order with a fresh Idempotency-Key (a new, distinct order every call).
    public static Task<HttpResponseMessage> PlaceOrderAsync(
        this TestUser customer, int restaurantId, params (int MenuItemId, int Quantity)[] items) =>
        customer.PostOrderAsync(OrderBody(restaurantId, items), Guid.NewGuid().ToString());

    // Polls PostgreSQL until some query is waiting on a lock (deterministic; no sleeps).
    public static async Task WaitForBlockedQueryAsync(string connectionString)
    {
        await using var conn = new Npgsql.NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            await using var cmd = new Npgsql.NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE NOT granted", conn);
            if ((long)(await cmd.ExecuteScalarAsync())! > 0) return;
            await Task.Delay(20);
        }

        throw new TimeoutException("The API request never blocked on the lock.");
    }

    public static string GetConnectionString(this ApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
    }

    public static async Task<OrderResponse> PlaceOrderOkAsync(
        this TestUser customer, int restaurantId, params (int MenuItemId, int Quantity)[] items)
    {
        var response = await customer.PlaceOrderAsync(restaurantId, items);
        response.EnsureSuccessStatusCode();
        return await response.ReadAsync<OrderResponse>();
    }

    public static Task<HttpResponseMessage> SetStatusAsync(this TestUser user, int orderId, string status) =>
        user.Client.PatchAsJsonAsync($"/api/orders/{orderId}/status", new { status });

    // A restaurant with Burger (25.50), Fries (10.00) and an unavailable item.
    public static async Task<TestRestaurant> CreateRestaurantWithMenuAsync(this TestUser owner)
    {
        var restaurant = await owner.Client.PostAsJsonAsync("/api/restaurants",
            new { name = "Test Grill", address = "Riyadh" });
        restaurant.EnsureSuccessStatusCode();
        var restaurantId = (await restaurant.Content.ReadFromJsonAsync<RestaurantResponse>())!.Id;

        async Task<int> AddItem(string name, decimal price, bool available = true)
        {
            var r = await owner.Client.PostAsJsonAsync($"/api/restaurants/{restaurantId}/menu-items",
                new { name, price, isAvailable = available });
            r.EnsureSuccessStatusCode();
            return (await r.Content.ReadFromJsonAsync<MenuItemResponse>())!.Id;
        }

        return new TestRestaurant(
            restaurantId,
            BurgerId: await AddItem("Burger", 25.50m),
            FriesId: await AddItem("Fries", 10.00m),
            UnavailableId: await AddItem("Old Soup", 5m, available: false));
    }
}

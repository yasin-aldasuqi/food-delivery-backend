using System.Net;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Controllers;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class IdempotencyTests(ApiFactory factory)
{
    [Fact]
    public async Task Retry_with_same_key_returns_the_original_order_without_a_duplicate()
    {
        var (customer, restaurant) = await SetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 2));

        var first = await customer.PostOrderAsync(body, key);
        var retry = await customer.PostOrderAsync(body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.False(first.Headers.Contains(OrdersController.IdempotentReplayedHeader));
        Assert.Equal("true", retry.Headers.GetValues(OrdersController.IdempotentReplayedHeader).Single());
        Assert.Equal((await first.ReadAsync<OrderResponse>()).Id, (await retry.ReadAsync<OrderResponse>()).Id);
        Assert.Equal(1, await CountOrdersAsync(customer.Id));
    }

    [Fact]
    public async Task Same_items_in_a_different_order_count_as_the_same_request()
    {
        var (customer, restaurant) = await SetupAsync();
        var key = Guid.NewGuid().ToString();

        var first = await customer.PostOrderAsync(
            TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1), (restaurant.FriesId, 2)), key);
        var retry = await customer.PostOrderAsync(
            TestHelpers.OrderBody(restaurant.Id, (restaurant.FriesId, 2), (restaurant.BurgerId, 1)), key);

        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal((await first.ReadAsync<OrderResponse>()).Id, (await retry.ReadAsync<OrderResponse>()).Id);
    }

    [Fact]
    public async Task Same_key_with_a_different_request_returns_422()
    {
        var (customer, restaurant) = await SetupAsync();
        var key = Guid.NewGuid().ToString();

        await customer.PostOrderAsync(TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1)), key);
        var different = await customer.PostOrderAsync(TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 5)), key);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, different.StatusCode);
        Assert.Equal(1, await CountOrdersAsync(customer.Id));
    }

    [Theory]
    [InlineData(null)]   // header missing
    [InlineData("   ")]  // blank
    [InlineData("TOO_LONG")]
    public async Task Missing_or_invalid_key_returns_400(string? key)
    {
        var (customer, restaurant) = await SetupAsync();
        if (key == "TOO_LONG") key = new string('k', IdempotencyKey.MaxKeyLength + 1);

        var response = await customer.PostOrderAsync(TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1)), key);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await CountOrdersAsync(customer.Id));
    }

    [Fact]
    public async Task Keys_are_scoped_per_user()
    {
        var (customerA, restaurant) = await SetupAsync();
        var customerB = await factory.RegisterAsync(UserRole.Customer);
        var sharedKey = Guid.NewGuid().ToString();
        var body = TestHelpers.OrderBody(restaurant.Id, (restaurant.FriesId, 1));

        var a = await customerA.PostOrderAsync(body, sharedKey);
        var b = await customerB.PostOrderAsync(body, sharedKey);

        Assert.Equal(HttpStatusCode.Created, b.StatusCode);
        Assert.False(b.Headers.Contains(OrdersController.IdempotentReplayedHeader));
        Assert.NotEqual((await a.ReadAsync<OrderResponse>()).Id, (await b.ReadAsync<OrderResponse>()).Id);
    }

    [Fact]
    public async Task A_failed_request_does_not_use_up_the_key()
    {
        var (customer, restaurant) = await SetupAsync();
        var key = Guid.NewGuid().ToString();

        var failed = await customer.PostOrderAsync(TestHelpers.OrderBody(restaurant.Id, (restaurant.UnavailableId, 1)), key);
        var corrected = await customer.PostOrderAsync(TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1)), key);

        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Equal(HttpStatusCode.Created, corrected.StatusCode);
        Assert.False(corrected.Headers.Contains(OrdersController.IdempotentReplayedHeader));
    }

    [Fact]
    public async Task Parallel_requests_with_the_same_key_create_exactly_one_order()
    {
        var (customer, restaurant) = await SetupAsync();
        var key = Guid.NewGuid().ToString();
        var body = TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1));

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 10).Select(_ => customer.PostOrderAsync(body, key)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));
        var ids = await Task.WhenAll(responses.Select(async r => (await r.ReadAsync<OrderResponse>()).Id));
        Assert.Single(ids.Distinct());
        Assert.Equal(1, await CountOrdersAsync(customer.Id));
    }

    [Fact]
    public async Task Losing_a_race_on_the_key_rolls_back_the_duplicate_and_replays_the_winner()
    {
        var (customer, restaurant) = await SetupAsync();
        var winner = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.FriesId, 1));
        var key = Guid.NewGuid().ToString();
        var request = new CreateOrderRequest
        {
            RestaurantId = restaurant.Id,
            DeliveryAddress = "Olaya St, Riyadh",
            Items = [new CreateOrderItemRequest { MenuItemId = restaurant.BurgerId, Quantity = 1 }]
        };
        var connectionString = factory.GetConnectionString();

        // 1) A "concurrent request" claims the key but hasn't committed yet.
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            """
            INSERT INTO "IdempotencyKeys" ("UserId", "Key", "RequestHash", "OrderId", "CreatedAt")
            VALUES (@user, @key, @hash, @order, now())
            """, conn, tx))
        {
            cmd.Parameters.AddWithValue("user", customer.Id);
            cmd.Parameters.AddWithValue("key", key);
            cmd.Parameters.AddWithValue("hash", OrdersController.ComputeRequestHash(request));
            cmd.Parameters.AddWithValue("order", winner.Id);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2) The API doesn't see the uncommitted key, creates its own order,
        //    and blocks when inserting the same (UserId, Key).
        var apiCall = customer.PostOrderAsync(
            TestHelpers.OrderBody(restaurant.Id, (restaurant.BurgerId, 1)), key);
        await TestHelpers.WaitForBlockedQueryAsync(connectionString);

        // 3) Commit: the API's insert now fails with a unique violation.
        await tx.CommitAsync();
        var response = await apiCall;

        // The API replays the winner, and its own order was rolled back.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("true", response.Headers.GetValues(OrdersController.IdempotentReplayedHeader).Single());
        Assert.Equal(winner.Id, (await response.ReadAsync<OrderResponse>()).Id);
        Assert.Equal(1, await CountOrdersAsync(customer.Id));
    }

    private async Task<(TestUser Customer, TestRestaurant Restaurant)> SetupAsync()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        return (customer, await owner.CreateRestaurantWithMenuAsync());
    }

    private async Task<int> CountOrdersAsync(int customerId)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Orders.CountAsync(o => o.CustomerId == customerId);
    }
}

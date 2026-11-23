using System.Net;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class ConcurrencyTests(ApiFactory factory)
{
    [Fact]
    public async Task Second_writer_with_stale_data_gets_a_concurrency_exception()
    {
        var orderId = await CreatePendingOrderAsync();

        // Two independent DbContexts = two users who loaded the same order.
        using var scope1 = factory.Services.CreateScope();
        using var scope2 = factory.Services.CreateScope();
        var db1 = scope1.ServiceProvider.GetRequiredService<AppDbContext>();
        var db2 = scope2.ServiceProvider.GetRequiredService<AppDbContext>();

        var ownerView = await db1.Orders.SingleAsync(o => o.Id == orderId);
        var customerView = await db2.Orders.SingleAsync(o => o.Id == orderId);

        ownerView.Status = OrderStatus.Confirmed;
        await db1.SaveChangesAsync(); // first write wins, xmin changes

        customerView.Status = OrderStatus.Cancelled; // based on stale data
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => db2.SaveChangesAsync());

        using var verify = factory.Services.CreateScope();
        var status = await verify.ServiceProvider.GetRequiredService<AppDbContext>()
            .Orders.Where(o => o.Id == orderId).Select(o => o.Status).SingleAsync();
        Assert.Equal(OrderStatus.Confirmed, status); // the first write was not overwritten
    }

    [Fact]
    public async Task Api_returns_409_when_order_changes_while_it_is_being_updated()
    {
        var (orderId, owner) = await CreatePendingOrderWithOwnerAsync();
        var connectionString = factory.GetConnectionString();

        // 1) Another writer changes the order and holds the row lock (no commit yet).
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var cmd = new NpgsqlCommand(
            """UPDATE "Orders" SET "Status" = 'Cancelled' WHERE "Id" = @id""", conn, tx))
        {
            cmd.Parameters.AddWithValue("id", orderId);
            await cmd.ExecuteNonQueryAsync();
        }

        // 2) The owner confirms via the API. It reads "Pending" (the committed version)
        //    and its UPDATE blocks on our lock.
        var apiCall = owner.SetStatusAsync(orderId, "Confirmed");

        // 3) Wait until PostgreSQL itself reports the API's UPDATE waiting on a lock.
        //    No sleeps/guessing: this is deterministic.
        await TestHelpers.WaitForBlockedQueryAsync(connectionString);

        // 4) Commit: xmin changes, so the API's "WHERE xmin = <old>" matches no row.
        await tx.CommitAsync();
        var response = await apiCall;

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Order was modified", (await response.ReadAsync<ProblemDetails>()).Title);
    }

    private async Task<int> CreatePendingOrderAsync() => (await CreatePendingOrderWithOwnerAsync()).OrderId;

    private async Task<(int OrderId, TestUser Owner)> CreatePendingOrderWithOwnerAsync()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var order = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.FriesId, 1));
        return (order.Id, owner);
    }
}

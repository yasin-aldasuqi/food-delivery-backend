using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class OrdersTests(ApiFactory factory)
{
    [Fact]
    public async Task Server_uses_database_prices_and_ignores_client_amounts()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();

        // A tampered request trying to pay almost nothing.
        var response = await customer.PostOrderAsync(new
        {
            restaurantId = restaurant.Id,
            deliveryAddress = "Home",
            totalAmount = 1,
            items = new object[]
            {
                new { menuItemId = restaurant.BurgerId, quantity = 2, unitPrice = 0.01 },
                new { menuItemId = restaurant.FriesId, quantity = 1 }
            }
        }, Guid.NewGuid().ToString());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var order = await response.ReadAsync<OrderResponse>();
        Assert.Equal(61.00m, order.TotalAmount); // 2 x 25.50 + 10.00
        Assert.Equal(25.50m, order.Items.Single(i => i.MenuItemId == restaurant.BurgerId).UnitPrice);
        Assert.Equal(OrderStatus.Pending, order.Status);
    }

    public enum BadOrder { ItemFromAnotherRestaurant, UnavailableItem, DuplicateLines, NoItems, ZeroQuantity }

    [Theory]
    [InlineData(BadOrder.ItemFromAnotherRestaurant)]
    [InlineData(BadOrder.UnavailableItem)]
    [InlineData(BadOrder.DuplicateLines)]
    [InlineData(BadOrder.NoItems)]
    [InlineData(BadOrder.ZeroQuantity)]
    public async Task Invalid_orders_are_rejected_with_400(BadOrder kind)
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var otherOwner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var other = await otherOwner.CreateRestaurantWithMenuAsync();

        (int, int)[] items = kind switch
        {
            BadOrder.ItemFromAnotherRestaurant => [(other.BurgerId, 1)],
            BadOrder.UnavailableItem => [(restaurant.UnavailableId, 1)],
            BadOrder.DuplicateLines => [(restaurant.BurgerId, 1), (restaurant.BurgerId, 1)],
            BadOrder.NoItems => [],
            BadOrder.ZeroQuantity => [(restaurant.BurgerId, 0)],
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        var response = await customer.PlaceOrderAsync(restaurant.Id, items);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Restaurant_owner_cannot_place_orders()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();

        var response = await owner.PlaceOrderAsync(restaurant.Id, (restaurant.BurgerId, 1));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Orders_are_visible_only_to_customer_restaurant_owner_and_admin()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var otherOwner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var otherCustomer = await factory.RegisterAsync(UserRole.Customer);
        var admin = await factory.CreateAdminAsync();
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var order = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.FriesId, 1));

        var url = $"/api/orders/{order.Id}";
        Assert.Equal(HttpStatusCode.OK, (await customer.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await owner.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.Client.GetAsync(url)).StatusCode);
        // 404, not 403: a private order's existence isn't revealed.
        Assert.Equal(HttpStatusCode.NotFound, (await otherCustomer.Client.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherOwner.Client.GetAsync(url)).StatusCode);

        var otherCustomersList = await (await otherCustomer.Client.GetAsync("/api/orders"))
            .ReadAsync<PagedResponse<OrderResponse>>();
        Assert.DoesNotContain(otherCustomersList.Items, o => o.Id == order.Id);

        var ownersList = await (await owner.Client.GetAsync("/api/orders"))
            .ReadAsync<PagedResponse<OrderResponse>>();
        Assert.Contains(ownersList.Items, o => o.Id == order.Id);
    }

    [Fact]
    public async Task Owner_moves_order_through_the_full_workflow()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var order = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.BurgerId, 1));

        foreach (var next in new[] { "Confirmed", "Preparing", "OutForDelivery", "Delivered" })
            Assert.Equal(HttpStatusCode.NoContent, (await owner.SetStatusAsync(order.Id, next)).StatusCode);

        var final = await (await customer.Client.GetAsync($"/api/orders/{order.Id}")).ReadAsync<OrderResponse>();
        Assert.Equal(OrderStatus.Delivered, final.Status);

        // Delivered is final.
        Assert.Equal(HttpStatusCode.Conflict, (await owner.SetStatusAsync(order.Id, "Cancelled")).StatusCode);
    }

    [Fact]
    public async Task Skipping_workflow_steps_returns_409()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var order = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.BurgerId, 1));

        var response = await owner.SetStatusAsync(order.Id, "Delivered");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Customer_can_cancel_only_while_pending_and_cannot_advance()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();

        var pending = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.FriesId, 1));
        Assert.Equal(HttpStatusCode.NoContent, (await customer.SetStatusAsync(pending.Id, "Cancelled")).StatusCode);

        var confirmed = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.FriesId, 1));
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.SetStatusAsync(confirmed.Id, "Confirmed")).StatusCode);
        await owner.SetStatusAsync(confirmed.Id, "Confirmed");
        Assert.Equal(HttpStatusCode.Forbidden, (await customer.SetStatusAsync(confirmed.Id, "Cancelled")).StatusCode);
    }

    [Fact]
    public async Task Menu_changes_do_not_rewrite_past_orders()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        var order = await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.BurgerId, 1));

        await owner.Client.PutAsJsonAsync($"/api/restaurants/{restaurant.Id}/menu-items/{restaurant.BurgerId}",
            new { name = "Chicken Burger", price = 99m });

        var reloaded = await (await customer.Client.GetAsync($"/api/orders/{order.Id}")).ReadAsync<OrderResponse>();
        var line = Assert.Single(reloaded.Items);
        Assert.Equal("Burger", line.Name);
        Assert.Equal(25.50m, line.UnitPrice);
        Assert.Equal(25.50m, reloaded.TotalAmount);
    }

    [Fact]
    public async Task Restaurant_and_menu_item_with_orders_cannot_be_deleted()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var customer = await factory.RegisterAsync(UserRole.Customer);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        await customer.PlaceOrderOkAsync(restaurant.Id, (restaurant.BurgerId, 1));

        var deleteItem = await owner.Client.DeleteAsync(
            $"/api/restaurants/{restaurant.Id}/menu-items/{restaurant.BurgerId}");
        var deleteRestaurant = await owner.Client.DeleteAsync($"/api/restaurants/{restaurant.Id}");

        Assert.Equal(HttpStatusCode.Conflict, deleteItem.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, deleteRestaurant.StatusCode);
    }
}

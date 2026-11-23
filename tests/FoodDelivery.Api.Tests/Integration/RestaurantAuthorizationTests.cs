using System.Net;
using System.Net.Http.Json;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using FoodDelivery.Api.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FoodDelivery.Api.Tests.Integration;

[Collection(ApiCollection.Name)]
public class RestaurantAuthorizationTests(ApiFactory factory)
{
    [Fact]
    public async Task Anyone_can_list_restaurants_without_logging_in()
    {
        var response = await factory.CreateClient().GetAsync("/api/restaurants");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Owner_is_taken_from_the_token_not_the_body()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);

        // Try to create the restaurant on someone else's behalf.
        var response = await owner.Client.PostAsJsonAsync("/api/restaurants",
            new { name = "Mine", address = "Riyadh", ownerId = 999_999 });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var restaurant = await response.ReadAsync<RestaurantResponse>();

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storedOwnerId = await db.Restaurants
            .Where(r => r.Id == restaurant.Id).Select(r => r.OwnerId).SingleAsync();

        Assert.Equal(owner.Id, storedOwnerId);
    }

    [Fact]
    public async Task Customer_cannot_create_a_restaurant()
    {
        var customer = await factory.RegisterAsync(UserRole.Customer);

        var response = await customer.Client.PostAsJsonAsync("/api/restaurants",
            new { name = "Nope", address = "Riyadh" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Owner_cannot_modify_another_owners_restaurant_or_menu()
    {
        var ownerA = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var ownerB = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var restaurant = await ownerA.CreateRestaurantWithMenuAsync();

        var update = await ownerB.Client.PutAsJsonAsync($"/api/restaurants/{restaurant.Id}",
            new { name = "Hacked", address = "x", isActive = true });
        var addItem = await ownerB.Client.PostAsJsonAsync($"/api/restaurants/{restaurant.Id}/menu-items",
            new { name = "Fake", price = 1 });
        var deleteItem = await ownerB.Client.DeleteAsync(
            $"/api/restaurants/{restaurant.Id}/menu-items/{restaurant.BurgerId}");

        Assert.Equal(HttpStatusCode.Forbidden, update.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, addItem.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deleteItem.StatusCode);
    }

    [Fact]
    public async Task Admin_can_modify_any_restaurant()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var admin = await factory.CreateAdminAsync();
        var restaurant = await owner.CreateRestaurantWithMenuAsync();

        var response = await admin.Client.PutAsJsonAsync($"/api/restaurants/{restaurant.Id}",
            new { name = "Renamed by admin", address = "Riyadh", isActive = true });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Inactive_restaurant_is_hidden_from_public_but_visible_to_owner()
    {
        var owner = await factory.RegisterAsync(UserRole.RestaurantOwner);
        var restaurant = await owner.CreateRestaurantWithMenuAsync();
        await owner.Client.PutAsJsonAsync($"/api/restaurants/{restaurant.Id}",
            new { name = "Closed", address = "Riyadh", isActive = false });

        var asPublic = await factory.CreateClient().GetAsync($"/api/restaurants/{restaurant.Id}");
        var asOwner = await owner.Client.GetAsync($"/api/restaurants/{restaurant.Id}");

        Assert.Equal(HttpStatusCode.NotFound, asPublic.StatusCode);
        Assert.Equal(HttpStatusCode.OK, asOwner.StatusCode);
    }
}

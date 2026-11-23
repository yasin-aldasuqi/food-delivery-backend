using FoodDelivery.Api.Auth;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/restaurants/{restaurantId:int}/menu-items")]
public class MenuItemsController(AppDbContext db) : ControllerBase
{
    // GET /api/restaurants/5/menu-items  (public)
    [HttpGet]
    [AllowAnonymous]
    public async Task<ActionResult<List<MenuItemResponse>>> GetAll(int restaurantId, CancellationToken ct)
    {
        if (!await CanViewRestaurantAsync(restaurantId, ct)) return NotFound();

        var items = await db.MenuItems
            .Where(m => m.RestaurantId == restaurantId)
            .OrderBy(m => m.Category).ThenBy(m => m.Name)
            .Select(m => ToResponse(m))
            .ToListAsync(ct);

        return Ok(items);
    }

    // GET /api/restaurants/5/menu-items/12  (public)
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult<MenuItemResponse>> GetById(int restaurantId, int id, CancellationToken ct)
    {
        if (!await CanViewRestaurantAsync(restaurantId, ct)) return NotFound();

        // Filter by BOTH ids so an item can't be read through another restaurant's URL.
        var item = await db.MenuItems
            .Where(m => m.Id == id && m.RestaurantId == restaurantId)
            .Select(m => ToResponse(m))
            .FirstOrDefaultAsync(ct);

        return item is null ? NotFound() : Ok(item);
    }

    // POST /api/restaurants/5/menu-items  (owner of this restaurant, or admin)
    [HttpPost]
    [Authorize(Roles = $"{Roles.RestaurantOwner},{Roles.Admin}")]
    public async Task<ActionResult<MenuItemResponse>> Create(
        int restaurantId, MenuItemRequest request, CancellationToken ct)
    {
        var access = await CheckManageAccessAsync(restaurantId, ct);
        if (access is not null) return access;

        var item = new MenuItem
        {
            RestaurantId = restaurantId, // taken from the URL, never from the body
            Name = request.Name,
            Description = request.Description,
            Price = request.Price,
            Category = request.Category,
            IsAvailable = request.IsAvailable
        };

        db.MenuItems.Add(item);
        await db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(GetById), new { restaurantId, id = item.Id }, ToResponse(item));
    }

    // PUT /api/restaurants/5/menu-items/12  (owner of this restaurant, or admin)
    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{Roles.RestaurantOwner},{Roles.Admin}")]
    public async Task<IActionResult> Update(
        int restaurantId, int id, MenuItemRequest request, CancellationToken ct)
    {
        var access = await CheckManageAccessAsync(restaurantId, ct);
        if (access is not null) return access;

        var item = await db.MenuItems
            .FirstOrDefaultAsync(m => m.Id == id && m.RestaurantId == restaurantId, ct);
        if (item is null) return NotFound();

        item.Name = request.Name;
        item.Description = request.Description;
        item.Price = request.Price;
        item.Category = request.Category;
        item.IsAvailable = request.IsAvailable;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // DELETE /api/restaurants/5/menu-items/12  (owner of this restaurant, or admin)
    [HttpDelete("{id:int}")]
    [Authorize(Roles = $"{Roles.RestaurantOwner},{Roles.Admin}")]
    public async Task<IActionResult> Delete(int restaurantId, int id, CancellationToken ct)
    {
        var access = await CheckManageAccessAsync(restaurantId, ct);
        if (access is not null) return access;

        var item = await db.MenuItems
            .FirstOrDefaultAsync(m => m.Id == id && m.RestaurantId == restaurantId, ct);
        if (item is null) return NotFound();

        if (await db.OrderItems.AnyAsync(oi => oi.MenuItemId == id, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Menu item has orders",
                detail: "An item that appears in past orders can't be deleted. Set IsAvailable to false instead.");
        }

        db.MenuItems.Remove(item);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // Returns null when the caller may manage the restaurant, otherwise 404 or 403.
    private async Task<ActionResult?> CheckManageAccessAsync(int restaurantId, CancellationToken ct)
    {
        var ownerId = await db.Restaurants
            .Where(r => r.Id == restaurantId)
            .Select(r => (int?)r.OwnerId)
            .FirstOrDefaultAsync(ct);

        if (ownerId is null) return NotFound();
        if (!User.CanManageRestaurant(ownerId.Value)) return Forbid();
        return null;
    }

    // Active restaurants are public; inactive ones only to their owner or an admin.
    private async Task<bool> CanViewRestaurantAsync(int restaurantId, CancellationToken ct)
    {
        var restaurant = await db.Restaurants
            .Where(r => r.Id == restaurantId)
            .Select(r => new { r.OwnerId, r.IsActive })
            .FirstOrDefaultAsync(ct);

        if (restaurant is null) return false;
        if (restaurant.IsActive) return true;
        return User.Identity?.IsAuthenticated == true && User.CanManageRestaurant(restaurant.OwnerId);
    }

    private static MenuItemResponse ToResponse(MenuItem m) =>
        new(m.Id, m.RestaurantId, m.Name, m.Description, m.Price, m.Category, m.IsAvailable);
}

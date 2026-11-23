using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/restaurants/{restaurantId:int}/menu-items")]
public class MenuItemsController(AppDbContext db) : ControllerBase
{
    // GET /api/restaurants/5/menu-items
    [HttpGet]
    public async Task<ActionResult<List<MenuItemResponse>>> GetAll(int restaurantId, CancellationToken ct)
    {
        if (!await db.Restaurants.AnyAsync(r => r.Id == restaurantId, ct))
            return NotFound();

        var items = await db.MenuItems
            .Where(m => m.RestaurantId == restaurantId)
            .OrderBy(m => m.Category).ThenBy(m => m.Name)
            .Select(m => ToResponse(m))
            .ToListAsync(ct);

        return Ok(items);
    }

    // GET /api/restaurants/5/menu-items/12
    [HttpGet("{id:int}")]
    public async Task<ActionResult<MenuItemResponse>> GetById(int restaurantId, int id, CancellationToken ct)
    {
        // Filter by BOTH ids so an item can't be read through another restaurant's URL.
        var item = await db.MenuItems
            .Where(m => m.Id == id && m.RestaurantId == restaurantId)
            .Select(m => ToResponse(m))
            .FirstOrDefaultAsync(ct);

        return item is null ? NotFound() : Ok(item);
    }

    // POST /api/restaurants/5/menu-items
    [HttpPost]
    public async Task<ActionResult<MenuItemResponse>> Create(
        int restaurantId, MenuItemRequest request, CancellationToken ct)
    {
        if (!await db.Restaurants.AnyAsync(r => r.Id == restaurantId, ct))
            return NotFound();

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

    // PUT /api/restaurants/5/menu-items/12
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int restaurantId, int id, MenuItemRequest request, CancellationToken ct)
    {
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

    // DELETE /api/restaurants/5/menu-items/12
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int restaurantId, int id, CancellationToken ct)
    {
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

    private static MenuItemResponse ToResponse(MenuItem m) =>
        new(m.Id, m.RestaurantId, m.Name, m.Description, m.Price, m.Category, m.IsAvailable);
}

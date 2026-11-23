using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/restaurants")]
public class RestaurantsController(AppDbContext db) : ControllerBase
{
    private const int MaxPageSize = 100;

    // GET /api/restaurants?page=1&pageSize=20
    [HttpGet]
    public async Task<ActionResult<PagedResponse<RestaurantResponse>>> GetAll(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = db.Restaurants.Where(r => r.IsActive);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(r => r.Id) // stable order is required for correct paging
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(r => new RestaurantResponse(
                r.Id, r.Name, r.Description, r.Address, r.PhoneNumber, r.IsActive, r.CreatedAt))
            .ToListAsync(ct);

        return Ok(new PagedResponse<RestaurantResponse>(items, page, pageSize, totalCount));
    }

    // GET /api/restaurants/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<RestaurantResponse>> GetById(int id, CancellationToken ct)
    {
        var restaurant = await db.Restaurants
            .Where(r => r.Id == id)
            .Select(r => new RestaurantResponse(
                r.Id, r.Name, r.Description, r.Address, r.PhoneNumber, r.IsActive, r.CreatedAt))
            .FirstOrDefaultAsync(ct);

        return restaurant is null ? NotFound() : Ok(restaurant);
    }

    // POST /api/restaurants
    [HttpPost]
    public async Task<ActionResult<RestaurantResponse>> Create(
        CreateRestaurantRequest request, CancellationToken ct)
    {
        var ownerExists = await db.Users.AnyAsync(u => u.Id == request.OwnerId, ct);
        if (!ownerExists)
        {
            ModelState.AddModelError(nameof(request.OwnerId), "Owner does not exist.");
            return ValidationProblem(ModelState);
        }

        var restaurant = new Restaurant
        {
            Name = request.Name,
            Description = request.Description,
            Address = request.Address,
            PhoneNumber = request.PhoneNumber,
            OwnerId = request.OwnerId
        };

        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync(ct);

        var response = new RestaurantResponse(
            restaurant.Id, restaurant.Name, restaurant.Description, restaurant.Address,
            restaurant.PhoneNumber, restaurant.IsActive, restaurant.CreatedAt);

        // 201 Created + Location header pointing to GET /api/restaurants/{id}
        return CreatedAtAction(nameof(GetById), new { id = restaurant.Id }, response);
    }

    // PUT /api/restaurants/5
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, UpdateRestaurantRequest request, CancellationToken ct)
    {
        var restaurant = await db.Restaurants.FindAsync([id], ct);
        if (restaurant is null) return NotFound();

        restaurant.Name = request.Name;
        restaurant.Description = request.Description;
        restaurant.Address = request.Address;
        restaurant.PhoneNumber = request.PhoneNumber;
        restaurant.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // DELETE /api/restaurants/5
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var restaurant = await db.Restaurants.FindAsync([id], ct);
        if (restaurant is null) return NotFound();

        // Orders are financial records (FK is ON DELETE RESTRICT).
        if (await db.Orders.AnyAsync(o => o.RestaurantId == id, ct))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Restaurant has orders",
                detail: "A restaurant with order history can't be deleted. Set IsActive to false instead.");
        }

        db.Restaurants.Remove(restaurant); // menu items are removed by ON DELETE CASCADE
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

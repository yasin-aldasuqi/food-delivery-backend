using FoodDelivery.Api.Auth;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/restaurants")]
public class RestaurantsController(AppDbContext db) : ControllerBase
{
    private const int MaxPageSize = 100;

    // GET /api/restaurants?page=1&pageSize=20  (public)
    [HttpGet]
    [AllowAnonymous]
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

    // GET /api/restaurants/5  (public; inactive restaurants visible only to owner/admin)
    [HttpGet("{id:int}")]
    [AllowAnonymous]
    public async Task<ActionResult<RestaurantResponse>> GetById(int id, CancellationToken ct)
    {
        var restaurant = await db.Restaurants
            .Where(r => r.Id == id)
            .Select(r => new
            {
                r.OwnerId,
                Response = new RestaurantResponse(
                    r.Id, r.Name, r.Description, r.Address, r.PhoneNumber, r.IsActive, r.CreatedAt)
            })
            .FirstOrDefaultAsync(ct);

        if (restaurant is null) return NotFound();

        var canSeeInactive = User.Identity?.IsAuthenticated == true
                             && User.CanManageRestaurant(restaurant.OwnerId);
        if (!restaurant.Response.IsActive && !canSeeInactive) return NotFound();

        return Ok(restaurant.Response);
    }

    // POST /api/restaurants  (restaurant owners only; owner = the caller)
    [HttpPost]
    [Authorize(Roles = Roles.RestaurantOwner)]
    public async Task<ActionResult<RestaurantResponse>> Create(
        CreateRestaurantRequest request, CancellationToken ct)
    {
        var restaurant = new Restaurant
        {
            Name = request.Name,
            Description = request.Description,
            Address = request.Address,
            PhoneNumber = request.PhoneNumber,
            OwnerId = User.GetUserId() // from the verified token, never from the body
        };

        db.Restaurants.Add(restaurant);
        await db.SaveChangesAsync(ct);

        var response = new RestaurantResponse(
            restaurant.Id, restaurant.Name, restaurant.Description, restaurant.Address,
            restaurant.PhoneNumber, restaurant.IsActive, restaurant.CreatedAt);

        // 201 Created + Location header pointing to GET /api/restaurants/{id}
        return CreatedAtAction(nameof(GetById), new { id = restaurant.Id }, response);
    }

    // PUT /api/restaurants/5  (owner of this restaurant, or admin)
    [HttpPut("{id:int}")]
    [Authorize(Roles = $"{Roles.RestaurantOwner},{Roles.Admin}")]
    public async Task<IActionResult> Update(int id, UpdateRestaurantRequest request, CancellationToken ct)
    {
        var restaurant = await db.Restaurants.FindAsync([id], ct);
        if (restaurant is null) return NotFound();
        if (!User.CanManageRestaurant(restaurant.OwnerId)) return Forbid();

        restaurant.Name = request.Name;
        restaurant.Description = request.Description;
        restaurant.Address = request.Address;
        restaurant.PhoneNumber = request.PhoneNumber;
        restaurant.IsActive = request.IsActive;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    // DELETE /api/restaurants/5  (owner of this restaurant, or admin)
    [HttpDelete("{id:int}")]
    [Authorize(Roles = $"{Roles.RestaurantOwner},{Roles.Admin}")]
    public async Task<IActionResult> Delete(int id, CancellationToken ct)
    {
        var restaurant = await db.Restaurants.FindAsync([id], ct);
        if (restaurant is null) return NotFound();
        if (!User.CanManageRestaurant(restaurant.OwnerId)) return Forbid();

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

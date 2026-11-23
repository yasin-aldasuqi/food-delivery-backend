using System.Linq.Expressions;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using FoodDelivery.Api.Auth;
using FoodDelivery.Api.Contracts;
using FoodDelivery.Api.Data;
using FoodDelivery.Api.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FoodDelivery.Api.Controllers;

[ApiController]
[Route("api/orders")]
[Authorize] // explicit, although the fallback policy already requires login
public class OrdersController(AppDbContext db) : ControllerBase
{
    private const int MaxPageSize = 100;
    private const decimal MaxOrderTotal = 99_999_999.99m; // fits numeric(10,2)

    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    // POST /api/orders  (customers only)
    // Requires an Idempotency-Key header: retrying with the same key returns the
    // original order instead of creating a duplicate.
    [HttpPost]
    [Authorize(Roles = Roles.Customer)]
    public async Task<ActionResult<OrderResponse>> Create(
        CreateOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > IdempotencyKey.MaxKeyLength)
        {
            ModelState.AddModelError(IdempotencyKeyHeader,
                $"The {IdempotencyKeyHeader} header is required (1-{IdempotencyKey.MaxKeyLength} characters). " +
                "Send a new unique value (e.g. a UUID) per order, and the same value when retrying it.");
            return ValidationProblem(ModelState);
        }

        var userId = User.GetUserId();
        var requestHash = ComputeRequestHash(request);

        // Fast path: a retry of a request that already succeeded.
        var replay = await TryReplayAsync(userId, idempotencyKey, requestHash, ct);
        if (replay is not null) return replay;

        var requestedIds = request.Items.Select(i => i.MenuItemId).ToList();
        if (requestedIds.Count != requestedIds.Distinct().Count())
        {
            ModelState.AddModelError(nameof(request.Items), "Each menu item may appear only once; use Quantity instead.");
            return ValidationProblem(ModelState);
        }

        var restaurant = await db.Restaurants
            .Where(r => r.Id == request.RestaurantId && r.IsActive)
            .Select(r => new { r.Id, r.Name })
            .FirstOrDefaultAsync(ct);
        if (restaurant is null)
        {
            ModelState.AddModelError(nameof(request.RestaurantId), "Restaurant does not exist or is not accepting orders.");
            return ValidationProblem(ModelState);
        }

        // Prices come from the database, never from the client.
        // Items must belong to THIS restaurant and be available.
        var menuItems = await db.MenuItems
            .Where(m => requestedIds.Contains(m.Id)
                        && m.RestaurantId == restaurant.Id
                        && m.IsAvailable)
            .ToDictionaryAsync(m => m.Id, ct);

        var invalidIds = requestedIds.Where(id => !menuItems.ContainsKey(id)).ToList();
        if (invalidIds.Count > 0)
        {
            ModelState.AddModelError(nameof(request.Items),
                $"Menu items not available at this restaurant: {string.Join(", ", invalidIds)}.");
            return ValidationProblem(ModelState);
        }

        var order = new Order
        {
            CustomerId = userId,
            RestaurantId = restaurant.Id,
            DeliveryAddress = request.DeliveryAddress.Trim(),
            Status = OrderStatus.Pending
        };

        foreach (var line in request.Items)
        {
            var menuItem = menuItems[line.MenuItemId];
            order.Items.Add(new OrderItem
            {
                MenuItemId = menuItem.Id,
                MenuItemName = menuItem.Name,  // snapshot
                UnitPrice = menuItem.Price,    // snapshot
                Quantity = line.Quantity
            });
        }

        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);
        if (order.TotalAmount > MaxOrderTotal)
        {
            ModelState.AddModelError(nameof(request.Items), "Order total is too large.");
            return ValidationProblem(ModelState);
        }

        db.Orders.Add(order);
        db.IdempotencyKeys.Add(new IdempotencyKey
        {
            UserId = userId,
            Key = idempotencyKey,
            RequestHash = requestHash,
            Order = order
        });

        // One SaveChanges = one transaction: the order, its lines and the idempotency key
        // are saved together, or none of them are.
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
                                           {
                                               SqlState: PostgresErrorCodes.UniqueViolation,
                                               ConstraintName: "PK_IdempotencyKeys"
                                           })
        {
            // A concurrent request with the same key committed first. PostgreSQL rolled back
            // our whole transaction (including our duplicate order), so replay theirs.
            db.ChangeTracker.Clear();
            return await TryReplayAsync(userId, idempotencyKey, requestHash, ct)
                   ?? throw new InvalidOperationException("Idempotency key conflict, but no stored key was found.");
        }

        var response = new OrderResponse(
            order.Id, restaurant.Id, restaurant.Name, order.CustomerId, order.Status,
            order.DeliveryAddress, order.TotalAmount, order.CreatedAt,
            order.Items.Select(i => new OrderItemResponse(
                i.MenuItemId, i.MenuItemName, i.Quantity, i.UnitPrice, i.UnitPrice * i.Quantity)).ToList());

        return CreatedAtAction(nameof(GetById), new { id = order.Id }, response);
    }

    // GET /api/orders?status=Pending&page=1&pageSize=20
    // Customers: their orders. Owners: orders of their restaurants. Admins: all orders.
    [HttpGet]
    public async Task<ActionResult<PagedResponse<OrderResponse>>> GetAll(
        [FromQuery] OrderStatus? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = VisibleTo(User);
        if (status is not null) query = query.Where(o => o.Status == status);

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderByDescending(o => o.CreatedAt).ThenByDescending(o => o.Id) // newest first, stable
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(ToResponse)
            .ToListAsync(ct);

        return Ok(new PagedResponse<OrderResponse>(items, page, pageSize, totalCount));
    }

    // GET /api/orders/5
    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderResponse>> GetById(int id, CancellationToken ct)
    {
        // Orders are private: someone else's order returns 404, not 403,
        // so callers can't even learn that it exists.
        var order = await VisibleTo(User)
            .Where(o => o.Id == id)
            .Select(ToResponse)
            .FirstOrDefaultAsync(ct);

        return order is null ? NotFound() : Ok(order);
    }

    // PATCH /api/orders/5/status   { "status": "Confirmed" }
    [HttpPatch("{id:int}/status")]
    public async Task<IActionResult> UpdateStatus(int id, UpdateOrderStatusRequest request, CancellationToken ct)
    {
        var order = await VisibleTo(User)
            .Include(o => o.Restaurant)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return NotFound();

        if (!order.CanTransitionTo(request.Status))
        {
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Invalid status change",
                detail: $"An order can't go from {order.Status} to {request.Status}.");
        }

        var managesRestaurant = User.CanManageRestaurant(order.Restaurant.OwnerId);
        var isCustomerCancellingPending = order.CustomerId == User.GetUserId()
                                          && order.Status == OrderStatus.Pending
                                          && request.Status == OrderStatus.Cancelled;

        if (!managesRestaurant && !isCustomerCancellingPending) return Forbid();

        order.Status = request.Status;
        try
        {
            // EF adds "WHERE xmin = <value we read>" to the UPDATE.
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Someone else changed this order after we read it (e.g. owner confirmed
            // while the customer cancelled). Don't overwrite their change.
            return Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Order was modified",
                detail: "The order was changed by someone else. Reload it and try again.");
        }

        return NoContent();
    }

    // Returns null if the key is unused; otherwise the original order (same request)
    // or 422 (the key was already used for a different request).
    private async Task<ActionResult?> TryReplayAsync(
        int userId, string key, string requestHash, CancellationToken ct)
    {
        var stored = await db.IdempotencyKeys
            .Where(k => k.UserId == userId && k.Key == key)
            .Select(k => new { k.RequestHash, k.OrderId })
            .FirstOrDefaultAsync(ct);

        if (stored is null) return null;

        if (stored.RequestHash != requestHash)
        {
            return Problem(
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency key reused",
                detail: $"This {IdempotencyKeyHeader} was already used for a different order request. " +
                        "Use a new key for a new order.");
        }

        var order = await db.Orders
            .Where(o => o.Id == stored.OrderId)
            .Select(ToResponse)
            .SingleAsync(ct);

        Response.Headers[IdempotentReplayedHeader] = "true";
        return CreatedAtAction(nameof(GetById), new { id = order.Id }, order);
    }

    // Fingerprint of what the order request means, independent of item order and stray whitespace.
    // Public so integration tests can reproduce the exact fingerprint.
    public static string ComputeRequestHash(CreateOrderRequest request)
    {
        var canonical = new
        {
            request.RestaurantId,
            DeliveryAddress = request.DeliveryAddress.Trim(),
            Items = request.Items
                .OrderBy(i => i.MenuItemId).ThenBy(i => i.Quantity)
                .Select(i => new { i.MenuItemId, i.Quantity })
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    // The single place that decides which orders a user may see.
    private IQueryable<Order> VisibleTo(ClaimsPrincipal user)
    {
        if (user.IsInRole(Roles.Admin)) return db.Orders;

        var userId = user.GetUserId();
        if (user.IsInRole(Roles.RestaurantOwner)) return db.Orders.Where(o => o.Restaurant.OwnerId == userId);
        if (user.IsInRole(Roles.Customer)) return db.Orders.Where(o => o.CustomerId == userId);

        return db.Orders.Where(_ => false); // other roles (e.g. Courier) see nothing for now
    }

    // An expression tree (not a plain method) so EF can translate it into a single SQL query.
    private static readonly Expression<Func<Order, OrderResponse>> ToResponse = o => new OrderResponse(
        o.Id,
        o.RestaurantId,
        o.Restaurant.Name,
        o.CustomerId,
        o.Status,
        o.DeliveryAddress,
        o.TotalAmount,
        o.CreatedAt,
        o.Items
            .OrderBy(i => i.Id)
            .Select(i => new OrderItemResponse(
                i.MenuItemId, i.MenuItemName, i.Quantity, i.UnitPrice, i.UnitPrice * i.Quantity))
            .ToList());
}

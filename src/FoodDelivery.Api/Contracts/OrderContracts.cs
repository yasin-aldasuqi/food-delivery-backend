using System.ComponentModel.DataAnnotations;
using FoodDelivery.Api.Entities;

namespace FoodDelivery.Api.Contracts;

public record CreateOrderRequest
{
    [Range(1, int.MaxValue)]
    public int RestaurantId { get; init; }

    [Required, StringLength(300)]
    public required string DeliveryAddress { get; init; }

    // Only ids and quantities: prices and totals are NEVER accepted from the client.
    [Required, Length(1, 50)]
    public required List<CreateOrderItemRequest> Items { get; init; }
}

public record CreateOrderItemRequest
{
    [Range(1, int.MaxValue)]
    public int MenuItemId { get; init; }

    [Range(1, 100)]
    public int Quantity { get; init; }
}

public record UpdateOrderStatusRequest
{
    [EnumDataType(typeof(OrderStatus))]
    public OrderStatus Status { get; init; }
}

public record OrderItemResponse(
    int MenuItemId,
    string Name,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public record OrderResponse(
    int Id,
    int RestaurantId,
    string RestaurantName,
    int CustomerId,
    OrderStatus Status,
    string DeliveryAddress,
    decimal TotalAmount,
    DateTime CreatedAt,
    IReadOnlyList<OrderItemResponse> Items);

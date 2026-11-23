using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Api.Contracts;

public record MenuItemResponse(
    int Id,
    int RestaurantId,
    string Name,
    string? Description,
    decimal Price,
    string? Category,
    bool IsAvailable);

public record MenuItemRequest
{
    [Required, StringLength(150)]
    public required string Name { get; init; }

    [StringLength(1000)]
    public string? Description { get; init; }

    [Range(typeof(decimal), "0", "99999999.99")]
    public decimal Price { get; init; }

    [StringLength(50)]
    public string? Category { get; init; }

    public bool IsAvailable { get; init; } = true;
}

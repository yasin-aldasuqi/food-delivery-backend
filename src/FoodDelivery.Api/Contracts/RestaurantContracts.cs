using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Api.Contracts;

public record RestaurantResponse(
    int Id,
    string Name,
    string? Description,
    string Address,
    string? PhoneNumber,
    bool IsActive,
    DateTime CreatedAt);

public record CreateRestaurantRequest
{
    [Required, StringLength(150)]
    public required string Name { get; init; }

    [StringLength(1000)]
    public string? Description { get; init; }

    [Required, StringLength(300)]
    public required string Address { get; init; }

    [Phone, StringLength(20)]
    public string? PhoneNumber { get; init; }

    // TEMPORARY: once authentication is added, the owner will come
    // from the logged-in user's token, not from the request body.
    [Range(1, int.MaxValue)]
    public int OwnerId { get; init; }
}

public record UpdateRestaurantRequest
{
    [Required, StringLength(150)]
    public required string Name { get; init; }

    [StringLength(1000)]
    public string? Description { get; init; }

    [Required, StringLength(300)]
    public required string Address { get; init; }

    [Phone, StringLength(20)]
    public string? PhoneNumber { get; init; }

    public bool IsActive { get; init; }
}

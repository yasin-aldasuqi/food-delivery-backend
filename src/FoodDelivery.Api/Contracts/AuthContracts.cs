using System.ComponentModel.DataAnnotations;
using FoodDelivery.Api.Entities;

namespace FoodDelivery.Api.Contracts;

public record RegisterRequest
{
    [Required, StringLength(100)]
    public required string FullName { get; init; }

    [Required, EmailAddress, StringLength(256)]
    public required string Email { get; init; }

    [Phone, StringLength(20)]
    public string? PhoneNumber { get; init; }

    [Required, StringLength(128, MinimumLength = 8)]
    public required string Password { get; init; }

    // Only Customer or RestaurantOwner are accepted; checked in the controller.
    public UserRole Role { get; init; } = UserRole.Customer;
}

public record LoginRequest
{
    [Required, EmailAddress]
    public required string Email { get; init; }

    [Required]
    public required string Password { get; init; }
}

public record UserResponse(int Id, string FullName, string Email, string Role);

public record AuthResponse(string Token, DateTime ExpiresAt, UserResponse User);

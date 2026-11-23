using FoodDelivery.Api.Entities;

namespace FoodDelivery.Api.Auth;

// Constant role names for [Authorize(Roles = ...)], which needs compile-time strings.
public static class Roles
{
    public const string Customer = nameof(UserRole.Customer);
    public const string RestaurantOwner = nameof(UserRole.RestaurantOwner);
    public const string Admin = nameof(UserRole.Admin);
}

using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;

namespace FoodDelivery.Api.Auth;

public static class AuthClaimTypes
{
    public const string Role = "role";
}

public static class ClaimsPrincipalExtensions
{
    public static int GetUserId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirstValue(JwtRegisteredClaimNames.Sub);
        return int.TryParse(sub, out var id)
            ? id
            : throw new InvalidOperationException("Authenticated user has no valid 'sub' claim.");
    }

    // Owners may manage their own restaurants; admins may manage any.
    public static bool CanManageRestaurant(this ClaimsPrincipal user, int ownerId) =>
        user.IsInRole(Roles.Admin) || user.GetUserId() == ownerId;
}

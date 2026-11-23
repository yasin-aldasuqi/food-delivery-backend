using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Api.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    // HS256 needs at least 256 bits (32 bytes) of key material.
    [Required, MinLength(32)]
    public string Key { get; init; } = string.Empty;

    [Required]
    public string Issuer { get; init; } = string.Empty;

    [Required]
    public string Audience { get; init; } = string.Empty;

    [Range(1, 24 * 60)]
    public int ExpiryMinutes { get; init; } = 60;
}

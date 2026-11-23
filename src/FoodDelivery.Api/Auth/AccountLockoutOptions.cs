using System.ComponentModel.DataAnnotations;

namespace FoodDelivery.Api.Auth;

public class AccountLockoutOptions
{
    public const string SectionName = "Lockout";

    // Consecutive failed logins that lock the account.
    [Range(1, 100)]
    public int MaxFailedAttempts { get; init; } = 5;

    // Kept short on purpose: anyone who knows an email can trigger a lockout (lockout DoS).
    [Range(1, 24 * 60)]
    public int DurationMinutes { get; init; } = 15;
}

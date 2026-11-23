namespace FoodDelivery.Api.Entities;

/// <summary>
/// Remembers which order a client-supplied Idempotency-Key created,
/// so a retried POST returns the original order instead of creating a duplicate.
/// </summary>
public class IdempotencyKey
{
    public const int MaxKeyLength = 100;

    // Composite primary key (UserId, Key): keys are scoped per user.
    public int UserId { get; set; }
    public required string Key { get; set; }

    // SHA-256 (hex) of the normalized request, to detect a key reused for a different request.
    public required string RequestHash { get; set; }

    public int OrderId { get; set; }
    public Order Order { get; set; } = null!;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

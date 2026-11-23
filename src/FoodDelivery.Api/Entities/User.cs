namespace FoodDelivery.Api.Entities;

public class User
{
    public int Id { get; set; }
    public required string FullName { get; set; }
    public required string Email { get; set; }
    public string? PhoneNumber { get; set; }

    // Never store the raw password — only its hash.
    public required string PasswordHash { get; set; }

    public UserRole Role { get; set; } = UserRole.Customer;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation properties
    public ICollection<Restaurant> OwnedRestaurants { get; set; } = new List<Restaurant>();
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}

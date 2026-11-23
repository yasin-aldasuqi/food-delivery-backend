using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace FoodDelivery.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Restaurant> Restaurants => Set<Restaurant>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Picks up every IEntityTypeConfiguration<T> class in this project.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}

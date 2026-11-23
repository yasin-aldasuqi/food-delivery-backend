using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoodDelivery.Api.Data.Configurations;

public class MenuItemConfiguration : IEntityTypeConfiguration<MenuItem>
{
    public void Configure(EntityTypeBuilder<MenuItem> builder)
    {
        builder.Property(m => m.Name).HasMaxLength(150);
        builder.Property(m => m.Description).HasMaxLength(1000);
        builder.Property(m => m.Category).HasMaxLength(50);
        builder.Property(m => m.Price).HasPrecision(10, 2);

        builder.ToTable(t => t.HasCheckConstraint("CK_MenuItems_Price", "\"Price\" >= 0"));

        // Deleting a restaurant removes its menu.
        builder.HasOne(m => m.Restaurant)
            .WithMany(r => r.MenuItems)
            .HasForeignKey(m => m.RestaurantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

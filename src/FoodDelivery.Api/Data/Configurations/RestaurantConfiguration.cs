using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoodDelivery.Api.Data.Configurations;

public class RestaurantConfiguration : IEntityTypeConfiguration<Restaurant>
{
    public void Configure(EntityTypeBuilder<Restaurant> builder)
    {
        builder.Property(r => r.Name).HasMaxLength(150);
        builder.Property(r => r.Description).HasMaxLength(1000);
        builder.Property(r => r.Address).HasMaxLength(300);
        builder.Property(r => r.PhoneNumber).HasMaxLength(20);

        // A user who owns restaurants can't be deleted until they're reassigned.
        builder.HasOne(r => r.Owner)
            .WithMany(u => u.OwnedRestaurants)
            .HasForeignKey(r => r.OwnerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

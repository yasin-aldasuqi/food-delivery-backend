using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoodDelivery.Api.Data.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.Property(u => u.FullName).HasMaxLength(100);
        builder.Property(u => u.Email).HasMaxLength(256);
        builder.Property(u => u.PhoneNumber).HasMaxLength(20);
        builder.Property(u => u.PasswordHash).HasMaxLength(512);

        // Store enum as readable text ("Customer") instead of a number.
        builder.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);

        // One account per email, enforced by the database itself.
        builder.HasIndex(u => u.Email).IsUnique();
    }
}

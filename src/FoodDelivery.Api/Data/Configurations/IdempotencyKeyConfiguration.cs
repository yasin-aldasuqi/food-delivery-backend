using FoodDelivery.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FoodDelivery.Api.Data.Configurations;

public class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        // The primary key is also what makes concurrent duplicates impossible:
        // the second insert of the same (UserId, Key) fails and its transaction rolls back.
        builder.HasKey(k => new { k.UserId, k.Key });

        builder.Property(k => k.Key).HasMaxLength(IdempotencyKey.MaxKeyLength);
        builder.Property(k => k.RequestHash).HasMaxLength(64).IsFixedLength();

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(k => k.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(k => k.Order)
            .WithMany()
            .HasForeignKey(k => k.OrderId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

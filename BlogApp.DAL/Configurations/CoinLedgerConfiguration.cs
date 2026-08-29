using BlogApp.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations;

public class CoinLedgerConfiguration : IEntityTypeConfiguration<CoinLedger>
{
    public void Configure(EntityTypeBuilder<CoinLedger> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Amount)
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.Type)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.ReferenceId)
            .HasMaxLength(100);

        builder.HasIndex(x => new { x.UserId, x.ReferenceId, x.Type })
            .IsUnique()
            .HasFilter("[ReferenceId] IS NOT NULL");

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

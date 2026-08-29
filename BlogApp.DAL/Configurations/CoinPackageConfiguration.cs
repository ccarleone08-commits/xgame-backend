using BlogApp.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations;

public class CoinPackageConfiguration : IEntityTypeConfiguration<CoinPackage>
{
    public void Configure(EntityTypeBuilder<CoinPackage> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.CoinAmount)
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.PriceAmount)
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.PriceCurrency)
            .IsRequired()
            .HasMaxLength(10)
            .HasDefaultValue("usd");

        builder.Property(x => x.IsActive)
            .HasDefaultValue(true);

        var seedDate = new DateTime(2026, 5, 15, 0, 0, 0, DateTimeKind.Utc);

        builder.HasData(
            new CoinPackage { Id = 1, Name = "Starter", CoinAmount = 100, PriceAmount = 5, PriceCurrency = "usd", IsActive = true, CreateDate = seedDate },
            new CoinPackage { Id = 2, Name = "Plus", CoinAmount = 250, PriceAmount = 10, PriceCurrency = "usd", IsActive = true, CreateDate = seedDate },
            new CoinPackage { Id = 3, Name = "Pro", CoinAmount = 600, PriceAmount = 20, PriceCurrency = "usd", IsActive = true, CreateDate = seedDate }
        );
    }
}

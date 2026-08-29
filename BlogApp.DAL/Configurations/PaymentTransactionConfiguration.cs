using BlogApp.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations;

public class PaymentTransactionConfiguration : IEntityTypeConfiguration<PaymentTransaction>
{
    public void Configure(EntityTypeBuilder<PaymentTransaction> builder)
    {
        builder.HasKey(x => x.Id);

        builder.Property(x => x.OrderId)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(x => x.NowPaymentsPaymentId)
            .HasMaxLength(100);

        builder.Property(x => x.NowPaymentsInvoiceId)
            .HasMaxLength(100);

        builder.Property(x => x.Status)
            .IsRequired()
            .HasMaxLength(50);

        builder.Property(x => x.PayCurrency)
            .HasMaxLength(20);

        builder.Property(x => x.CoinAmount)
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.PriceAmount)
            .HasColumnType("decimal(18,2)");

        builder.Property(x => x.PriceCurrency)
            .IsRequired()
            .HasMaxLength(10);

        builder.Property(x => x.ActuallyPaid)
            .HasColumnType("decimal(18,8)");

        builder.Property(x => x.PayAddress)
            .HasMaxLength(200);

        builder.Property(x => x.PaymentUrl)
            .HasMaxLength(500);

        builder.HasIndex(x => x.OrderId)
            .IsUnique();

        builder.HasIndex(x => x.NowPaymentsPaymentId)
            .IsUnique()
            .HasFilter("[NowPaymentsPaymentId] IS NOT NULL");

        builder.HasOne(x => x.User)
            .WithMany()
            .HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(x => x.CoinPackage)
            .WithMany(x => x.PaymentTransactions)
            .HasForeignKey(x => x.CoinPackageId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

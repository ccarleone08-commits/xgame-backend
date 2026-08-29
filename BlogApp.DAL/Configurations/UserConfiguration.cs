using BlogApp.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations;

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder
            .HasKey(x => x.Id);

        builder
            .HasIndex(x => x.UserName)
            .IsUnique();

        builder
            .HasIndex(x => x.Email)
            .IsUnique();

        builder
            .Property(x => x.UserName)
            .IsRequired()
            .HasMaxLength(32);

        builder
            .Property(x => x.Email)
            .IsRequired()
            .HasMaxLength(62);

        builder.Property(x => x.Image)
            .IsRequired()
            .HasMaxLength(128);

        builder.Property(x => x.Balance)
            .HasColumnType("decimal(18,2)");
    }
}

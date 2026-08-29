using BlogApp.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder
            .HasKey(x => x.Id);

        builder
            .HasIndex(x => x.Name)
            .IsUnique();

        builder
            .Property(x => x.Name)
            .IsRequired()
            .HasMaxLength(64);

        builder
            .Property(x => x.CreateDate)
            .HasDefaultValueSql("GETDATE()");

        builder
            .Property(x => x.Icon)
            .HasMaxLength(128);
    }
}

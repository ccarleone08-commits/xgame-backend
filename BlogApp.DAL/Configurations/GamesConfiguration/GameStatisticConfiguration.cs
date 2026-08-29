using BlogApp.Core.Entities.GamesEntitiy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations.GamesConfiguration
{
    public class GameStatisticConfiguration : IEntityTypeConfiguration<GameStatistic>
    {
        public void Configure(EntityTypeBuilder<GameStatistic> builder)
        {
            builder.ToTable("GameStatistics");

            builder.HasKey(gs => gs.Id);

            // UserId unique index
            builder.HasIndex(gs => gs.UserId)
                   .IsUnique()
                   .HasDatabaseName("IX_GameStatistics_UserId");

            // WeekStart indexi
            builder.HasIndex(gs => gs.WeekStart)
                   .HasDatabaseName("IX_GameStatistics_WeekStart");

            // Default dəyərlər
            builder.Property(gs => gs.GameBreakdown)
                   .HasDefaultValue("{}");

            builder.Property(gs => gs.CreatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            builder.Property(gs => gs.LastUpdated)
                   .HasDefaultValueSql("GETUTCDATE()");

            builder.Property(gs => gs.WeekStart)
                   .HasDefaultValueSql("GETUTCDATE()");

            // Foreign Key
            builder.HasOne(gs => gs.User)
                   .WithMany()
                   .HasForeignKey(gs => gs.UserId)
                   .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

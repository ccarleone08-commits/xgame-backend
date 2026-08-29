using BlogApp.Core.Entities.GamesEntitiy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BlogApp.DAL.Configurations.GamesConfiguration
{
    public class PlayerRankConfiguration : IEntityTypeConfiguration<PlayerRank>
    {
        public void Configure(EntityTypeBuilder<PlayerRank> builder)
        {
            builder.ToTable("PlayerRanks");

            builder.HasKey(pr => pr.Id);

            // UserId və GameType kombinasiyası unikal olmalıdır
            builder.HasIndex(pr => new { pr.UserId, pr.GameType })
                   .IsUnique()
                   .HasDatabaseName("IX_PlayerRanks_UserId_GameType");

            // Rank indexi
            builder.HasIndex(pr => pr.CurrentRank)
                   .HasDatabaseName("IX_PlayerRanks_CurrentRank");

            // XP indexi
            builder.HasIndex(pr => pr.ExperiencePoints)
                   .HasDatabaseName("IX_PlayerRanks_ExperiencePoints");

            // GameType indexi
            builder.HasIndex(pr => pr.GameType)
                   .HasDatabaseName("IX_PlayerRanks_GameType");

            // Default dəyərlər
            builder.Property(pr => pr.CurrentRank)
                   .HasDefaultValue("Beginner");

            builder.Property(pr => pr.RankLevel)
                   .HasDefaultValue(1);

            builder.Property(pr => pr.ExperiencePoints)
                   .HasDefaultValue(0);

            builder.Property(pr => pr.RequiredXPForNextRank)
                   .HasDefaultValue(100);

            builder.Property(pr => pr.UnlockedAchievements)
                   .HasDefaultValue("[]");

            builder.Property(pr => pr.CreatedAt)
                   .HasDefaultValueSql("GETUTCDATE()");

            builder.Property(pr => pr.RankLastUpdated)
                   .HasDefaultValueSql("GETUTCDATE()");

            // Foreign Key
            builder.HasOne(pr => pr.User)
                   .WithMany()
                   .HasForeignKey(pr => pr.UserId)
                   .OnDelete(DeleteBehavior.Cascade);
        }
    }

}

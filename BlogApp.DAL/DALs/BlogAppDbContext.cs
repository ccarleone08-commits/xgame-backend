using BlogApp.Core.Entities;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.Configurations.GamesConfiguration;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.DAL.DALs;

public class BlogAppDbContext : DbContext
{
    public BlogAppDbContext(DbContextOptions opt) : base(opt) { }
    public DbSet<Category> Categories { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<SupportTicket> SupportTickets => Set<SupportTicket>();
    public DbSet<SupportTicketMessage> SupportTicketMessages => Set<SupportTicketMessage>(); public DbSet<ChatMessage> ChatMessages { get; set; }
    public DbSet<GameStatistic> GameStatistics { get; set; }
    public DbSet<PlayerRank> PlayerRanks { get; set; }
    public DbSet<GameSession> GameSessions { get; set; }
    public DbSet<DepositRequest> DepositRequests { get; set; }
    public DbSet<WithdrawRequest> WithdrawRequests { get; set; }
    public DbSet<CoinPackage> CoinPackages { get; set; }
    public DbSet<PaymentTransaction> PaymentTransactions { get; set; }
    public DbSet<CoinLedger> CoinLedgers { get; set; }
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BlogAppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<GameSession>(entity =>
        {
            entity.HasKey(gs => gs.Id);
            entity.Property(gs => gs.SessionEarnings).HasColumnType("decimal(18,2)");
            entity.Property(gs => gs.SessionLossAmount).HasColumnType("decimal(18,2)");
            entity.HasIndex(gs => new { gs.UserId, gs.GameType });
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(e => e.Id);

            entity.Property(e => e.Sender)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.Receiver)
                .IsRequired()
                .HasMaxLength(100);

            entity.Property(e => e.Content)
                .HasMaxLength(2000);

            entity.Property(e => e.ImageUrl)
                .HasMaxLength(500);

            entity.Property(e => e.Type)
                .IsRequired()
                .HasMaxLength(20)
                .HasDefaultValue("text");

            entity.Property(e => e.Timestamp)
                .IsRequired()
                .HasDefaultValueSql("GETUTCDATE()");

            entity.Property(e => e.IsRead)
                .IsRequired()
                .HasDefaultValue(false);

            // Indexlər - performans üçün
            entity.HasIndex(e => e.Sender)
                .HasDatabaseName("IX_ChatMessages_Sender");

            entity.HasIndex(e => e.Receiver)
                .HasDatabaseName("IX_ChatMessages_Receiver");

            entity.HasIndex(e => e.Timestamp)
                .HasDatabaseName("IX_ChatMessages_Timestamp");

            entity.HasIndex(e => new { e.Sender, e.Receiver })
                .HasDatabaseName("IX_ChatMessages_Sender_Receiver");
        });

        modelBuilder.Entity<SupportTicket>(e =>
        {
            e.HasKey(t => t.Id);

            e.Property(t => t.TicketNumber)
             .IsRequired()
             .HasMaxLength(20);

            e.Property(t => t.FullName).IsRequired().HasMaxLength(150);
            e.Property(t => t.Email).IsRequired().HasMaxLength(200);
            e.Property(t => t.Subject).IsRequired().HasMaxLength(300);
            e.Property(t => t.Message).IsRequired().HasMaxLength(2000);

            // Ticket açan user
            e.HasOne(t => t.User)
             .WithMany(u => u.OpenedTickets)
             .HasForeignKey(t => t.UserId)
             .OnDelete(DeleteBehavior.NoAction);

            // Claim edən worker
            e.HasOne(t => t.AssignedWorker)
             .WithMany(u => u.AssignedTickets)
             .HasForeignKey(t => t.AssignedWorkerId)
             .OnDelete(DeleteBehavior.NoAction);

            // Mesajlar
            e.HasMany(t => t.Messages)
             .WithOne(m => m.Ticket)
             .HasForeignKey(m => m.TicketId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SupportTicketMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).IsRequired().HasMaxLength(2000);

            e.HasOne(m => m.Sender)
             .WithMany()
             .HasForeignKey(m => m.SenderId)
             .OnDelete(DeleteBehavior.Restrict);
        });


        modelBuilder.Entity<DepositRequest>()
            .Property(d => d.Amount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<DepositRequest>()
            .HasOne(d => d.User)
            .WithMany()
            .HasForeignKey(d => d.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DepositRequest>()
            .HasOne(d => d.ReviewedByWorker)
            .WithMany()
            .HasForeignKey(d => d.ReviewedByWorkerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DepositRequest>()
            .HasOne(d => d.ReviewedByBank)
            .WithMany()
            .HasForeignKey(d => d.ReviewedByBankId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<WithdrawRequest>()
            .Property(w => w.Amount)
            .HasColumnType("decimal(18,2)");

        //Games
        modelBuilder.ApplyConfiguration(new GameStatisticConfiguration());
        modelBuilder.ApplyConfiguration(new PlayerRankConfiguration());
    }
}

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlogApp.Core.Entities.GamesEntitiy
{
    public class GameStatistic
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        // Ümumi statistika (ALL TIME)
        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalWinnings { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalLosses { get; set; } = 0;

        public int TotalGamesPlayed { get; set; } = 0;
        public int TotalGamesWon { get; set; } = 0;

        // Həftəlik statistika
        [Column(TypeName = "decimal(18,2)")]
        public decimal WeeklyWinnings { get; set; } = 0;

        public int WeeklyGamesPlayed { get; set; } = 0;
        public int WeeklyGamesWon { get; set; } = 0;
        public DateTime WeekStart { get; set; }

        // Oyun növlərinə görə breakdown (JSON format)
        // Məsələn: {"Poker": 150.50, "Blackjack": 75.00, "Roulette": 200.00}
        public string GameBreakdown { get; set; } = "{}";

        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [ForeignKey("UserId")]
        public virtual User User { get; set; }
    }
}

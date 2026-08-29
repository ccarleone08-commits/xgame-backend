using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlogApp.Core.Entities.GamesEntitiy
{
    public class PlayerRank
    {
        [Key]
        public int Id { get; set; }

        [Required]
        public int UserId { get; set; }

        [Required]
        public GameType GameType { get; set; }

        // Rank məlumatları
        [Required]
        [StringLength(50)]
        public string CurrentRank { get; set; } = "Beginner"; // Bronze, Silver, Gold, Platinum, Diamond, Master, Grandmaster

        public int RankLevel { get; set; } = 1; // 1-10 arası səviyyə

        public int ExperiencePoints { get; set; } = 0; // XP

        public int RequiredXPForNextRank { get; set; } = 100;

        // Ümumi statistika
        public int TotalGamesPlayed { get; set; } = 0;
        public int TotalWins { get; set; } = 0;
        public int TotalLosses { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalEarnings { get; set; } = 0;

        // Win rate (faiz)
        [Column(TypeName = "decimal(5,2)")]
        public decimal WinRate { get; set; } = 0;

        // Streak məlumatları
        public int CurrentWinStreak { get; set; } = 0;
        public int BestWinStreak { get; set; } = 0;

        [Column(TypeName = "decimal(18,2)")]
        public decimal TotalLossAmount { get; set; } = 0;    // Ziyar məbləği

        // Sona çıxma məlumatları
        public int Top3Finishes { get; set; } = 0; // Top 3-ə girdiyi oyunlar
        public int FirstPlaceFinishes { get; set; } = 0; // 1-ci olduğu oyunlar

        // Achievements (JSON format)
        public string UnlockedAchievements { get; set; } = "[]"; // ["first_win", "10_wins", "master_rank"]

        // Tarixlər
        public DateTime LastGamePlayed { get; set; }
        public DateTime RankLastUpdated { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // Navigation
        [ForeignKey("UserId")]
        public virtual User User { get; set; }
    }
}



public class LeaderboardEntry
{
    public int Position { get; set; }
    public int UserId { get; set; }
    public string Username { get; set; }
    public string GameType { get; set; }
    public string CurrentRank { get; set; }
    public int RankLevel { get; set; }
    public int ExperiencePoints { get; set; }
    public decimal WinRate { get; set; }
    public int TotalGamesPlayed { get; set; }
    public int TotalWins { get; set; }
    public decimal TotalEarnings { get; set; }
    public decimal TotalLosses { get; set; } = 0;
    public int BestWinStreak { get; set; }
    public string Image { get; set; } // 👈 MÜTLƏQ

}

public class PlayerRankDetails
{
    public string GameType { get; set; }
    public string CurrentRank { get; set; }
    public string RankIcon { get; set; }
    public string RankColor { get; set; }
    public int RankLevel { get; set; }
    public int ExperiencePoints { get; set; }
    public int RequiredXPForNextRank { get; set; }
    public decimal ProgressPercentage { get; set; }
    public string NextRank { get; set; }
    public int GlobalPosition { get; set; }
    public int TotalPlayers { get; set; }
    public decimal WinRate { get; set; }
    public int TotalGamesPlayed { get; set; }
    public int TotalWins { get; set; }
    public int BestWinStreak { get; set; }
    public decimal TotalEarnings { get; set; }
    public decimal TotalLossAmount { get; set; } = 0;

    public DateTime LastGamePlayed { get; set; }
    public DateTime RankLastUpdated { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public decimal LastSessionEarnings { get; set; } = 0;
    public List<GameSession> RecentSessions { get; set; } = new List<GameSession>();

}

public class GameSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int UserId { get; set; }
    public string GameType { get; set; }
    public decimal SessionEarnings { get; set; }
    public decimal SessionLossAmount { get; set; }
    public bool IsWin { get; set; }
    public int XpGained { get; set; }
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
}


public class RankConfig
{
    public int MinXP { get; set; }
    public int MaxLevel { get; set; }
    public string Icon { get; set; }
    public string Color { get; set; }

    public RankConfig(int minXP, int maxLevel, string icon, string color)
    {
        MinXP = minXP;
        MaxLevel = maxLevel;
        Icon = icon;
        Color = color;
    }
}
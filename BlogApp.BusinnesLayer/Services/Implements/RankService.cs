using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace BlogApp.BusinnesLayer.Services.Implements
{
    public class RankService : IRankService
    {
        private readonly BlogAppDbContext _db;

        // Rank sistemi konfiqurasiyası
        private static readonly Dictionary<string, (int minXP, int maxLevel)> RankTiers = new()
        {
            { "Beginner", (0, 2) },
            { "Bronze", (100, 5) },
            { "Silver", (500, 8) },
            { "Gold", (1500, 10) },
            { "Platinum", (3000, 10) },
            { "Diamond", (5000, 10) },
            { "Master", (8000, 10) },
            { "Grandmaster", (12000, 10) }
        };

        public RankService(BlogAppDbContext db)
        {
            _db = db;
        }

        public async Task<PlayerRank> GetOrCreatePlayerRank(int userId, GameType gameType)
        {
            var rank = _db.PlayerRanks
                .FirstOrDefault(pr => pr.UserId == userId && pr.GameType == gameType);

            if (rank == null)
            {
                rank = new PlayerRank
                {
                    UserId = userId,
                    GameType = gameType,
                    CurrentRank = "Beginner",
                    RankLevel = 1,
                    ExperiencePoints = 0,
                    RequiredXPForNextRank = 100,
                    TotalGamesPlayed = 0,
                    TotalWins = 0,
                    TotalLosses = 0,
                    TotalEarnings = 0,
                    BestWinStreak = 0,
                    CurrentWinStreak = 0,
                    WinRate = 0,
                    LastGamePlayed = DateTime.UtcNow,
                    RankLastUpdated = DateTime.UtcNow,
                    CreatedAt = DateTime.UtcNow
                };

                _db.PlayerRanks.Add(rank);
                await _db.SaveChangesAsync();
            }

            return rank;
        }

        public async Task UpdateRankAfterGame(int userId, GameType gameType, bool isWin, decimal amount)
        {
            var rank = await GetOrCreatePlayerRank(userId, gameType);

            rank.TotalGamesPlayed++;
            rank.LastGamePlayed = DateTime.UtcNow;

            int xpGain = CalculateXPGain(amount, isWin);

            // ✅ DÜZƏLTMƏ 1: isWin TRUE ise - earnings ekle, loss = 0
            if (isWin)
            {
                rank.TotalWins++;
                rank.CurrentWinStreak++;
                if (rank.CurrentWinStreak > rank.BestWinStreak)
                    rank.BestWinStreak = rank.CurrentWinStreak;

                rank.TotalEarnings += amount;
                rank.ExperiencePoints += xpGain;
                Console.WriteLine($"✅ Win! +{xpGain} XP | Earnings: +{amount}₼ | Total: {rank.TotalEarnings}₼");

                // ✅ GameSession'a DOĞRU şekilde kaydet
                _db.GameSessions.Add(new GameSession
                {
                    UserId = userId,
                    GameType = gameType.ToString(),
                    SessionEarnings = amount,      // ← Kazanç
                    SessionLossAmount = 0,         // ← Loss = 0 (yok)
                    IsWin = true,
                    XpGained = xpGain,
                    PlayedAt = DateTime.UtcNow
                });
            }
            // ✅ DÜZƏLTMƏ 2: isWin FALSE ise - loss ekle, earnings = 0
            else
            {
                rank.TotalLosses++;
                rank.CurrentWinStreak = 0;
                rank.TotalLossAmount += amount;   // ← Loss amount'ı ekle
                rank.ExperiencePoints += xpGain;
                Console.WriteLine($"❌ Loss. +{xpGain} XP | Loss Amount: +{amount}₼ | Total: {rank.TotalLossAmount}₼");

                // ✅ GameSession'a DOĞRU şekilde kaydet
                _db.GameSessions.Add(new GameSession
                {
                    UserId = userId,
                    GameType = gameType.ToString(),
                    SessionEarnings = -amount,     // ← Loss history-də görünməsi üçün mənfi saxla
                    SessionLossAmount = amount,    // ← Loss (POZİTİF!)
                    IsWin = false,
                    XpGained = xpGain,
                    PlayedAt = DateTime.UtcNow
                });
            }

            rank.WinRate = rank.TotalGamesPlayed > 0
                ? (decimal)rank.TotalWins / rank.TotalGamesPlayed * 100
                : 0;

            await CheckRankPromotion(rank);
            rank.RankLastUpdated = DateTime.UtcNow;

            await _db.SaveChangesAsync();

            Console.WriteLine($"📊 Rank: {rank.CurrentRank} Level {rank.RankLevel}");
            Console.WriteLine($"💰 Earnings: {rank.TotalEarnings}₼ | Losses: {rank.TotalLossAmount}₼");
        }

        private int CalculateXPGain(decimal earnings, bool isWin)
        {
            // Base XP
            int baseXP = isWin ? 50 : 10;

            // Earnings-ə görə bonus XP
            int earningsBonus = (int)(earnings / 10); // Hər 10 AZN üçün 1 XP

            return baseXP + earningsBonus;
        }

        private async Task CheckRankPromotion(PlayerRank rank)
        {
            while (rank.ExperiencePoints >= rank.RequiredXPForNextRank)
            {
                bool promoted = false;

                // Səviyyə artır
                if (rank.RankLevel < RankTiers[rank.CurrentRank].maxLevel)
                {
                    rank.RankLevel++;
                    rank.ExperiencePoints -= rank.RequiredXPForNextRank;
                    rank.RequiredXPForNextRank = CalculateRequiredXP(rank.RankLevel, rank.CurrentRank);

                    Console.WriteLine($"⬆️ Level UP! Now Level {rank.RankLevel}");
                }
                else
                {
                    // Növbəti rank-a keç
                    string nextRank = GetNextRankTier(rank.CurrentRank);
                    if (nextRank != rank.CurrentRank)
                    {
                        rank.CurrentRank = nextRank;
                        rank.RankLevel = 1;
                        rank.ExperiencePoints = 0;
                        rank.RequiredXPForNextRank = CalculateRequiredXP(1, nextRank);
                        promoted = true;

                        Console.WriteLine($"🎉 RANK PROMOTION! New rank: {nextRank}");

                        // Achievement unlock
                        await UnlockAchievement(rank, $"rank_{nextRank.ToLower()}");
                    }
                    else
                    {
                        // Max rank - artıq yüksələ bilməz
                        break;
                    }
                }

                if (!promoted && rank.RankLevel >= RankTiers[rank.CurrentRank].maxLevel)
                {
                    break;
                }
            }
        }

        private string GetNextRankTier(string currentRank)
        {
            var ranks = RankTiers.Keys.ToList();
            int currentIndex = ranks.IndexOf(currentRank);

            if (currentIndex < ranks.Count - 1)
            {
                return ranks[currentIndex + 1];
            }

            return currentRank; // Max rank
        }

        private int CalculateRequiredXP(int level, string rank)
        {
            // Hər səviyyə üçün tələb olunan XP artır
            int baseXP = RankTiers[rank].minXP;
            return baseXP + (level * 50);
        }

        public async Task<string> CalculateNextRank(PlayerRank rank)
        {
            if (rank.RankLevel < RankTiers[rank.CurrentRank].maxLevel)
            {
                return $"{rank.CurrentRank} Level {rank.RankLevel + 1}";
            }
            else
            {
                string nextTier = GetNextRankTier(rank.CurrentRank);
                return nextTier != rank.CurrentRank ? nextTier : "MAX RANK";
            }
        }


        private async Task UnlockAchievement(PlayerRank rank, string achievementId)
        {
            var achievements = JsonSerializer.Deserialize<List<string>>(rank.UnlockedAchievements) ?? new List<string>();

            if (!achievements.Contains(achievementId))
            {
                achievements.Add(achievementId);
                rank.UnlockedAchievements = JsonSerializer.Serialize(achievements);

                Console.WriteLine($"🏆 Achievement unlocked: {achievementId}");
            }
        }

        public async Task<List<LeaderboardEntry>> GetCombinedLeaderboard(int top = 100, bool isWeekly = false)
        {
            var query = _db.PlayerRanks.Include(pr => pr.User).AsQueryable();

            if (isWeekly)
            {
                var weekStart = DateTime.UtcNow.AddDays(-7);
                query = query.Where(pr => pr.LastGamePlayed >= weekStart);
            }

            var allRanks = await query
                .OrderByDescending(pr => pr.ExperiencePoints)
                .ThenByDescending(pr => pr.WinRate)
                .Take(top)
                .ToListAsync();

            return allRanks.Select((r, index) => new LeaderboardEntry
            {
                Position = index + 1,
                UserId = r.UserId,
                Username = r.User.UserName,
                GameType = r.GameType.ToString(),
                CurrentRank = r.CurrentRank,
                RankLevel = r.RankLevel,
                ExperiencePoints = r.ExperiencePoints,
                WinRate = r.WinRate,
                TotalGamesPlayed = r.TotalGamesPlayed,
                TotalWins = r.TotalWins,
                TotalEarnings = r.TotalEarnings,
                BestWinStreak = r.BestWinStreak
            }).ToList();
        }

        /// <summary>
        /// Xüsusi oyun üçün leaderboard
        /// </summary>
        public async Task<List<LeaderboardEntry>> GetLeaderboard(GameType gameType, int top = 100, bool isWeekly = false)
        {
            var query = _db.PlayerRanks
                .Include(pr => pr.User)
                .Where(pr => pr.GameType == gameType);

            if (isWeekly)
            {
                var weekStart = DateTime.UtcNow.AddDays(-7);
                query = query.Where(pr => pr.LastGamePlayed >= weekStart);
            }

            var ranks = await query
                .OrderByDescending(pr => pr.ExperiencePoints)
                .ThenByDescending(pr => pr.WinRate)
                .Take(top)
                .ToListAsync();

            return ranks.Select((r, index) =>
            {
                return new LeaderboardEntry
                {
                    Position = index + 1,
                    UserId = r.UserId,
                    Username = r.User.UserName,
                    GameType = gameType.ToString(),
                    CurrentRank = r.CurrentRank,
                    RankLevel = r.RankLevel,
                    ExperiencePoints = r.ExperiencePoints,
                    WinRate = r.WinRate,
                    Image = !string.IsNullOrEmpty(r.User.Image)
                        ? r.User.Image
                        : "/assets/characters/default.png",
                    TotalGamesPlayed = r.TotalGamesPlayed,
                    TotalWins = r.TotalWins,
                    TotalEarnings = r.TotalEarnings,
                    TotalLosses = r.TotalLossAmount,  // ✅ Doğrudan kullan
                    BestWinStreak = r.BestWinStreak
                };
            }).ToList();
        }
        /// <summary>
        /// İstifadəçinin xüsusi oyundakı rank məlumatı
        /// </summary>
        public async Task<PlayerRankDetails> GetPlayerRankDetails(int userId, GameType gameType)
        {
            try
            {
                var rank = await GetOrCreatePlayerRank(userId, gameType);

                if (rank == null)
                {
                    throw new Exception($"Rank not found for user {userId}");
                }

                var nextRank = await CalculateNextRank(rank);

                // ✅ DÜZƏLTMƏ: Basitçe TotalLossAmount kullan
                decimal displayLosses = rank.TotalLossAmount;
                decimal netProfit = rank.TotalEarnings - displayLosses;

                // Global position hesabla
                var allRanks = await _db.PlayerRanks
                    .Where(pr => pr.GameType == gameType)
                    .OrderByDescending(pr => pr.ExperiencePoints)
                    .ThenByDescending(pr => pr.WinRate)
                    .ToListAsync();

                int position = allRanks.FindIndex(r => r.UserId == userId) + 1;
                int totalPlayers = allRanks.Count;

                Console.WriteLine($"📊 Rank Details: User={userId}, Game={gameType}");
                Console.WriteLine($"  TotalEarnings: {rank.TotalEarnings}");
                Console.WriteLine($"  TotalLossAmount: {displayLosses}");
                Console.WriteLine($"  NetProfit: {netProfit}");

                var sessions = await _db.GameSessions
                    .Where(s => s.UserId == userId && s.GameType == gameType.ToString())
                    .OrderByDescending(s => s.PlayedAt)
                    .Take(10)
                    .ToListAsync();

                var latestSession = sessions.FirstOrDefault();

                return new PlayerRankDetails
                {
                    GameType = gameType.ToString(),
                    CurrentRank = rank.CurrentRank,
                    RankLevel = rank.RankLevel,
                    ExperiencePoints = rank.ExperiencePoints,
                    RequiredXPForNextRank = rank.RequiredXPForNextRank,
                    ProgressPercentage = rank.RequiredXPForNextRank > 0
                        ? (decimal)rank.ExperiencePoints / rank.RequiredXPForNextRank * 100
                        : 100,
                    NextRank = nextRank,
                    GlobalPosition = position,
                    TotalPlayers = totalPlayers,
                    WinRate = Math.Round(rank.WinRate, 2),
                    TotalGamesPlayed = rank.TotalGamesPlayed,
                    TotalWins = rank.TotalWins,
                    BestWinStreak = rank.BestWinStreak,
                    LastGamePlayed = rank.LastGamePlayed,
                    RankLastUpdated = rank.RankLastUpdated,
                    CreatedAt = rank.CreatedAt,
                    TotalEarnings = rank.TotalEarnings,
                    TotalLossAmount = displayLosses,  // ✅ Doğru
                    LastSessionEarnings = latestSession == null
                        ? 0
                        : latestSession.IsWin
                            ? latestSession.SessionEarnings
                            : -latestSession.SessionLossAmount,
                    RecentSessions = sessions
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetPlayerRankDetails error: {ex.Message}");
                throw;
            }
        }
        /// </summary>
        public async Task<List<PlayerRankDetails>> GetPlayerAllGameRanks(int userId)
        {
            try
            {
                // ✅ 1. OYUNCU RANKS'İ ÇƏKƏ
                var userRanks = await _db.PlayerRanks
                    .Where(pr => pr.UserId == userId)
                    .ToListAsync();

                Console.WriteLine($"📊 User {userId} ranks found: {userRanks.Count}");

                // ✅ 2. HƏR GAME ÜÇÜ DETAIL'İ AL
                var results = new List<PlayerRankDetails>();

                foreach (var rank in userRanks)
                {
                    try
                    {
                        var details = await GetPlayerRankDetails(userId, rank.GameType);
                        results.Add(details);
                        Console.WriteLine($"✅ {rank.GameType}: {details.CurrentRank}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Error loading {rank.GameType}: {ex.Message}");
                    }
                }

                Console.WriteLine($"✅ Total game ranks loaded: {results.Count}");
                return results;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetPlayerAllGameRanks error: {ex.Message}");
                Console.WriteLine($"❌ StackTrace: {ex.StackTrace}");
                throw;
            }
        }
    }
}

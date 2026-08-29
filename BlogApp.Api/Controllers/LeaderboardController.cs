using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.Api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class LeaderboardController : ControllerBase
    {
        private readonly IRankService _rankService;
        private readonly BlogAppDbContext _context;

        public LeaderboardController(IRankService rankService, BlogAppDbContext context)
        {
            _rankService = rankService;
            _context = context;
        }

        /// <summary>
        /// Bütün oyunlar və ya xüsusi oyun üçün leaderboard
        /// </summary>
        [HttpGet("{gameType}/{period}")]
        public async Task<IActionResult> GetLeaderboard(string gameType, string period)
        {
            try
            {
                if (gameType.ToLower() == "all")
                {
                    // Bütün oyunlar üçün birləşdirilmiş leaderboard
                    var allRanks = period.ToLower() == "weekly"
                        ? await _rankService.GetCombinedLeaderboard(top: 100, isWeekly: true)
                        : await _rankService.GetCombinedLeaderboard(top: 100, isWeekly: false);
                    return Ok(allRanks);
                }
                else
                {
                    // Xüsusi oyun üçün leaderboard
                    GameType parsedGameType = gameType.ToLower() switch
                    {
                        "poker" => GameType.Poker,
                        "okey" => GameType.Okey,
                        "backgammon" => GameType.BackGammon,
                        "seka" => GameType.Seka,
                        "durak" => GameType.Durak,
                        "loto" => GameType.Loto,
                        "domino" => GameType.Domino,
                        _ => throw new ArgumentException("Invalid game type")
                    };

                    var isWeekly = period.ToLower() == "weekly";
                    var gameRanks = await _rankService.GetLeaderboard(parsedGameType, top: 100, isWeekly);
                    return Ok(gameRanks);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Leaderboard error: {ex.Message}");
                return StatusCode(500, new { error = "Leaderboard yüklənmədi" });
            }
        }

        /// <summary>
        /// İstifadəçinin xüsusi oyundakı rank məlumatı
        /// </summary>
        [HttpGet("player/{gameType}")]
        public async Task<IActionResult> GetPlayerRank(string gameType)
        {
            try
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                {
                    return Unauthorized();
                }

                GameType parsedGameType = gameType.ToLower() switch
                {
                    "poker" => GameType.Poker,
                    "okey" => GameType.Okey,
                    "backgammon" => GameType.BackGammon,
                    "seka" => GameType.Seka,
                    "durak" => GameType.Durak,
                    "loto" => GameType.Loto,
                    "domino" => GameType.Domino,
                    _ => throw new ArgumentException("Invalid game type")
                };

                var rankDetails = await _rankService.GetPlayerRankDetails(userId, parsedGameType);

                return Ok(rankDetails);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Player rank error: {ex.Message}");
                return StatusCode(500, new { error = "Rank məlumatı yüklənmədi" });
            }
        }

        /// <summary>
        /// İstifadəçinin bütün oyunlardakı rank məlumatları
        /// </summary>
        [HttpGet("player/all")]
        public async Task<IActionResult> GetPlayerAllRanks()
        {
            try
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                {
                    return Unauthorized();
                }
                var user = await _context.Users.Where(u => u.Id == userId).FirstOrDefaultAsync();
                var allRanks = await _rankService.GetPlayerAllGameRanks(userId);

                var result = allRanks.Select(rank => new
                {
                    gameType = rank.GameType,
                    currentRank = rank.CurrentRank,
                    rankLevel = rank.RankLevel,
                    experiencePoints = rank.ExperiencePoints,
                    requiredXPForNextRank = rank.RequiredXPForNextRank,
                    progressPercentage = Math.Round(rank.ProgressPercentage, 2),
                    nextRank = rank.NextRank,
                    globalPosition = rank.GlobalPosition,
                    totalPlayers = rank.TotalPlayers,
                    Image = user.Image,
                    lastGamePlayed = rank.LastGamePlayed,
                    rankLastUpdated = rank.RankLastUpdated,
                    createdAt = rank.CreatedAt,
                    totalGamesPlayed = rank.TotalGamesPlayed,
                    totalWins = rank.TotalWins,
                    winRate = Math.Round(rank.WinRate, 2),
                    bestWinStreak = rank.BestWinStreak,
                    totalEarnings = Math.Round(rank.TotalEarnings, 2),
                    totalLossAmount = Math.Round(rank.TotalLossAmount, 2),

                    // ✅ YENİ
                    lastSessionEarnings = Math.Round(rank.LastSessionEarnings, 2),
                    recentSessions = rank.RecentSessions.Select(s => new
                    {
                        sessionEarnings = Math.Round(s.SessionEarnings, 2),
                        sessionLossAmount = Math.Round(s.SessionLossAmount, 2),
                        isWin = s.IsWin,
                        xpGained = s.XpGained,
                        playedAt = s.PlayedAt
                    }).ToList()

                }).ToList();
                return Ok(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetPlayerAllRanks error: {ex.Message}");
                return StatusCode(500, new { error = ex.Message });
            }
        }



        /// <summary>
        /// Məhvər leaderboard məlumatlarını al
        /// </summary>
        /// <returns>Bütün oyunlardakı top 100 oyunçu</returns>
        [HttpGet("ForAdmin")]
        [AllowAnonymous]
        public async Task<IActionResult> GetMehvareLeaderboard()
        {
            try
            {
                // Database-dən rank məlumatlarını çək
                var leaderboard = await _context.PlayerRanks
                    .Where(r => r.User != null)
                    .OrderByDescending(r => r.RankLevel)
                    .ThenByDescending(r => r.ExperiencePoints)
                .Take(100)
                    .Select(r => new
                    {
                        position = _context.PlayerRanks
                            .Where(x => x.User != null &&
                                   (x.RankLevel > r.RankLevel ||
                                    (x.RankLevel == r.RankLevel && x.ExperiencePoints > r.ExperiencePoints)))
                            .Count() + 1,
                        userId = r.UserId,
                        username = r.User.Name,
                        currentRank = r.CurrentRank,
                        rankLevel = r.RankLevel,
                        experiencePoints = r.ExperiencePoints,
                        winRate = r.WinRate,
                        totalGamesPlayed = r.TotalGamesPlayed,
                        totalWins = r.TotalWins,
                        totalEarnings = r.TotalEarnings,
                        lastGamePlayed = r.LastGamePlayed,
                        totalLose = r.TotalLosses,
                        bestWinStreak = r.BestWinStreak,
                        gameType = r.GameType.ToString(),
                        totalLossAmount = Math.Round(r.TotalLossAmount, 2),
                    })
                    .ToListAsync();

                if (!leaderboard.Any())
                    return Ok(new { message = "Leaderboard boşdur", data = new List<object>() });

                return Ok(new
                {
                    message = "Butun Oyuncularin  leaderboard məlumatları",
                    totalPlayers = leaderboard.Count,
                    data = leaderboard
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Leaderboard yüklənmədi", details = ex.Message });
            }
        }

        /// <summary>
        /// Konkret oyunçunun bütün oyunlardakı məlumatlarını al
        /// </summary>
        /// <returns>Oyunçunun bütün oyunlardakı rank məlumatları</returns>
        [HttpGet("player-summary")]
        [Authorize]
        public async Task<IActionResult> GetPlayerAllGamesSummary()
        {
            try
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                {
                    return Unauthorized(new { error = "İstifadəçi tanınmadı" });
                }

                // İstifadəçi məlumatı
                var user = await _context.Users.FindAsync(userId);
                if (user == null)
                    return NotFound(new { error = "İstifadəçi tapılmadı" });

                // Bütün oyunlardakı rank məlumatları
                var playerRanks = await _context.PlayerRanks
                    .Where(r => r.UserId == userId)
                    .Select(r => new
                    {
                        gameType = r.GameType.ToString(),
                        currentRank = r.CurrentRank,
                        rankLevel = r.RankLevel,
                        experiencePoints = r.ExperiencePoints,
                        winRate = r.WinRate,
                        totalGamesPlayed = r.TotalGamesPlayed,
                        totalWins = r.TotalWins,
                        totalEarnings = r.TotalEarnings,
                        bestWinStreak = r.BestWinStreak,
                        globalPosition = _context.PlayerRanks
                            .Where(x => x.GameType == r.GameType &&
                                   (x.RankLevel > r.RankLevel ||
                                    (x.RankLevel == r.RankLevel && x.ExperiencePoints > r.ExperiencePoints)))
                            .Count() + 1
                    })
                    .ToListAsync();

                // Cəmi məlumatlar
                var totalStats = new
                {
                    totalExperiencePoints = playerRanks.Sum(p => p.experiencePoints),
                    totalEarnings = playerRanks.Sum(p => p.totalEarnings),
                    highestRank = playerRanks.OrderByDescending(p => p.rankLevel).FirstOrDefault()?.currentRank ?? "N/A",
                    totalGamesPlayed = playerRanks.Sum(p => p.totalGamesPlayed),
                    totalWins = playerRanks.Sum(p => p.totalWins),
                    averageWinRate = playerRanks.Count > 0 ? playerRanks.Average(p => p.winRate) : 0
                };

                return Ok(new
                {
                    message = "Oyunçunun bütün oyunlardakı məlumatları",
                    userId = userId,
                    username = user.Name,
                    totalStats = totalStats,
                    gameRanks = playerRanks
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Məlumatlar yüklənmədi", details = ex.Message });
            }
        }

        /// <summary>
        /// Konkret oyunda oyunçunun məlumatlarını al
        /// </summary>
        /// <param name="gameType">Oyun tipi: Poker, Okey, BackGammon, Seka, Durak, Loto, Domino</param>
        /// <returns>Oyunçunun xüsusi oyundakı detalı məlumatları</returns>
        [HttpGet("player-game/{gameType}")]
        [Authorize]
        public async Task<IActionResult> GetPlayerGameRank(string gameType)
        {
            try
            {
                var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
                if (userIdClaim == null || !int.TryParse(userIdClaim.Value, out int userId))
                {
                    return Unauthorized(new { error = "İstifadəçi tanınmadı" });
                }

                // GameType parse et
                if (!System.Enum.TryParse<GameType>(gameType, true, out var parsedGameType))
                {
                    return BadRequest(new { error = "Səhv oyun tipi" });
                }

                // Oyunçunun bu oyundakı rank məlumatı
                var playerRank = await _context.PlayerRanks
                    .Where(r => r.UserId == userId && r.GameType == parsedGameType)
                    .FirstOrDefaultAsync();

                if (playerRank == null)
                {
                    return NotFound(new { error = "Bu oyunda oyunçu məlumatı tapılmadı" });
                }

                // Qlobal mövqe
                var globalPosition = await _context.PlayerRanks
                    .Where(r => r.GameType == parsedGameType &&
                           (r.RankLevel > playerRank.RankLevel ||
                            (r.RankLevel == playerRank.RankLevel && r.ExperiencePoints > playerRank.ExperiencePoints)))
                    .CountAsync() + 1;

                // Cəmi oyunçu sayı
                var totalPlayers = await _context.PlayerRanks
                    .Where(r => r.GameType == parsedGameType)
                    .CountAsync();

                return Ok(new
                {
                    message = "Oyunçunun xüsusi oyundakı məlumatları",
                    userId = userId,
                    gameType = parsedGameType.ToString(),
                    currentRank = playerRank.CurrentRank,
                    rankLevel = playerRank.RankLevel,
                    experiencePoints = playerRank.ExperiencePoints,
                    winRate = playerRank.WinRate,
                    totalGamesPlayed = playerRank.TotalGamesPlayed,
                    totalWins = playerRank.TotalWins,
                    totalEarnings = playerRank.TotalEarnings,
                    bestWinStreak = playerRank.BestWinStreak,
                    globalPosition = globalPosition,
                    totalPlayers = totalPlayers
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Məlumatlar yüklənmədi", details = ex.Message });
            }
        }

        /// <summary>
        /// Konkret oyunun leaderboard-ını al
        /// </summary>
        /// <param name="gameType">Oyun tipi: Poker, Okey, BackGammon, Seka, Durak, Loto, Domino</param>
        /// <returns>Oyunun top 100 oyunçusu</returns>
        [HttpGet("game/{gameType}")]
        [AllowAnonymous]
        public async Task<IActionResult> GetGameLeaderboard(string gameType)
        {
            try
            {
                // GameType parse et
                if (!System.Enum.TryParse<GameType>(gameType, true, out var parsedGameType))
                {
                    return BadRequest(new { error = "Səhv oyun tipi" });
                }

                // Bu oyunun leaderboard-ı
                var leaderboard = await _context.PlayerRanks
                    .Where(r => r.GameType == parsedGameType && r.User != null)
                    .OrderByDescending(r => r.RankLevel)
                    .ThenByDescending(r => r.ExperiencePoints)
                .Take(100)
                    .Select(r => new
                    {
                        position = _context.PlayerRanks
                            .Where(x => x.GameType == parsedGameType && x.User != null &&
                                   (x.RankLevel > r.RankLevel ||
                                    (x.RankLevel == r.RankLevel && x.ExperiencePoints > r.ExperiencePoints)))
                            .Count() + 1,
                        userId = r.UserId,
                        username = r.User.Name,
                        currentRank = r.CurrentRank,
                        rankLevel = r.RankLevel,
                        experiencePoints = r.ExperiencePoints,
                        winRate = r.WinRate,
                        totalGamesPlayed = r.TotalGamesPlayed,
                        totalWins = r.TotalWins,
                        totalEarnings = r.TotalEarnings,
                        bestWinStreak = r.BestWinStreak
                    })
                    .ToListAsync();

                if (!leaderboard.Any())
                    return Ok(new { message = $"{gameType} leaderboard boşdur", data = new List<object>() });

                return Ok(new
                {
                    message = $"{gameType} leaderboard məlumatları",
                    gameType = gameType,
                    totalPlayers = leaderboard.Count,
                    data = leaderboard
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Leaderboard yüklənmədi", details = ex.Message });
            }
        }
    }
}

// =============================================================================
// DTO CLASSLAR - LeaderboardModels.cs adlı yeni fayl yarat və buraları əlavə et
// =============================================================================

namespace BlogApp.Api.Models
{
    public class LeaderboardEntry
    {
        public int Position { get; set; }
        public int UserId { get; set; }
        public string Username { get; set; }
        public string? GameType { get; set; }
        public string CurrentRank { get; set; }
        public int RankLevel { get; set; }
        public int ExperiencePoints { get; set; }
        public decimal WinRate { get; set; }
        public int TotalGamesPlayed { get; set; }
        public int TotalWins { get; set; }
        public decimal TotalEarnings { get; set; }
        public int BestWinStreak { get; set; }
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
    }
}
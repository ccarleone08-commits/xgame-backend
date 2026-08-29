using BlogApp.Core.Entities.GamesEntitiy;

namespace BlogApp.BusinnesLayer.Services.Interfaces
{
    public interface IRankService
    {
        Task<PlayerRank> GetOrCreatePlayerRank(int userId, GameType gameType);
        Task UpdateRankAfterGame(int userId, GameType gameType, bool isWin, decimal earnings);

        // Leaderboard metodları - isWeekly parametri əlavə edildi
        Task<List<LeaderboardEntry>> GetLeaderboard(GameType gameType, int top = 100, bool isWeekly = false);
        Task<List<LeaderboardEntry>> GetCombinedLeaderboard(int top = 100, bool isWeekly = false);
        Task<PlayerRankDetails> GetPlayerRankDetails(int userId, GameType gameType);
        Task<List<PlayerRankDetails>> GetPlayerAllGameRanks(int userId);
        Task<string> CalculateNextRank(PlayerRank rank);
    }
}

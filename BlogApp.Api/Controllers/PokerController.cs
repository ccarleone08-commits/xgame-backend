//using BlogApp.BusinnesLayer.Services.Interfaces;
//using BlogApp.Core.Entities.GamesEntitiy;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;

//namespace BlogApp.Api.Controllers
//{
//    [Route("api/[controller]")]
//    [ApiController]
//    public class PokerController : ControllerBase
//    {
//        private readonly IRankService _rankService;

//        public PokerController(IRankService rankService)
//        {
//            _rankService = rankService;
//        }

//        [HttpGet("leaderboard/{period}")]
//        public async Task<IActionResult> GetLeaderboard(string period)
//        {
//            try
//            {
//                var leaderboard = await _rankService.GetLeaderboard(GameType.Poker, top: 100);

//                var result = leaderboard.Select(p => new
//                {
//                    name = p.User?.Name + " " + p.User?.Surname,
//                    rank = p.CurrentRank,
//                    rankLevel = p.RankLevel,
//                    totalEarnings = p.TotalEarnings,
//                    totalGamesPlayed = p.TotalGamesPlayed,
//                    totalWins = p.TotalWins,
//                    winRate = p.WinRate
//                }).ToList();

//                return Ok(result);
//            }
//            catch (Exception ex)
//            {
//                return StatusCode(500, new { error = ex.Message });
//            }
//        }

//        [Authorize]
//        [HttpGet("myrank")]
//        public async Task<IActionResult> GetMyRank()
//        {
//            try
//            {
//                var userIdStr = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
//                if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
//                {
//                    return Unauthorized();
//                }

//                var rank = await _rankService.GetOrCreatePlayerRank(userId, GameType.Poker);

//                return Ok(new
//                {
//                    currentRank = rank.CurrentRank,
//                    rankLevel = rank.RankLevel,
//                    experiencePoints = rank.ExperiencePoints,
//                    requiredXPForNextRank = rank.RequiredXPForNextRank,
//                    totalGamesPlayed = rank.TotalGamesPlayed,
//                    totalWins = rank.TotalWins,
//                    totalEarnings = rank.TotalEarnings,
//                    winRate = rank.WinRate,
//                    currentWinStreak = rank.CurrentWinStreak,
//                    bestWinStreak = rank.BestWinStreak
//                });
//            }
//            catch (Exception ex)
//            {
//                return StatusCode(500, new { error = ex.Message });
//            }
//        }
//    }
//}

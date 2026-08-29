using BlogApp.Api.Hubs.Services.BlogApp.Api.Hubs.Services;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.Api.Hubs.Services
{
    public interface ILotoGameService
    {
        Task<WinResult?> ProcessWinner(string roomId, string ticketId, int userId, bool isAutoWin = false);
        RoomPlayer? CheckForWinner(LotoRoom room);
    }

    public class WinResult
    {
        public string WinnerName { get; set; } = "";
        public int UserId { get; set; }
        public decimal Prize { get; set; }
        public decimal NetPrize { get; set; }
        public string Message { get; set; } = "";
        public bool IsValid { get; set; }
        public string? ErrorMessage { get; set; }
    }

    public class LotoGameService : ILotoGameService
    {
        private readonly BlogAppDbContext _db;
        private readonly LotoRoomManager _roomManager;
        private readonly IRankService _rankService;
        private readonly IHubContext<LotoHub> _hubContext;
        private readonly ILogger<LotoGameService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly BotBudgetService _botBudgetService;

        public LotoGameService(
            BlogAppDbContext db,
            LotoRoomManager roomManager,
            IRankService rankService,
            IHubContext<LotoHub> hubContext,
            ILogger<LotoGameService> logger,
            IServiceProvider serviceProvider,
            BotBudgetService botBudgetService)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
            _hubContext = hubContext;
            _logger = logger;
            _serviceProvider = serviceProvider;
            _botBudgetService = botBudgetService;
        }

        public async Task<WinResult?> ProcessWinner(string roomId, string ticketId, int userId, bool isAutoWin = false)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                return new WinResult { IsValid = false, ErrorMessage = "Otaq tapılmadı" };
            }

            RoomPlayer? winnerTicket = null;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    return new WinResult { IsValid = false, ErrorMessage = "Oyun artıq bitib" };
                }

                winnerTicket = room.Players.FirstOrDefault(p => p.TicketId == ticketId);
                if (winnerTicket == null)
                {
                    return new WinResult { IsValid = false, ErrorMessage = "Bilet tapılmadı" };
                }

                // ✅ OTAĞIN QAYDASINA GÖRƏ YOXLA
                var drawnSet = new HashSet<int>(room.DrawnNumbers);
                bool isValid = false;

                if (room.RequiresFullCard)
                {
                    // 0.20₼ - TAM KART
                    isValid = IsFullCardCompleted(winnerTicket.Card, drawnSet);
                    _logger.LogInformation($"🔍 TAM KART yoxlanılır: {winnerTicket.Name} → {isValid}");
                }
                else
                {
                    // Digər otaqlar - BİR XƏTT
                    isValid = IsAnyLineCompleted(winnerTicket.Card, drawnSet);
                    _logger.LogInformation($"🔍 BİR XƏTT yoxlanılır: {winnerTicket.Name} → {isValid}");
                }

                if (!isValid)
                {
                    string rule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT";
                    _logger.LogWarning($"❌ Şərt yerinə yetirilməyib: {winnerTicket.Name} (Qayda: {rule})");
                    return new WinResult
                    {
                        IsValid = false,
                        ErrorMessage = $"Yanlış BINGO! Qayda: {rule}"
                    };
                }

                room.IsGameFinished = true;
                room.IsGameStarted = false;
                winnerTicket.HasWon = true;
            }

            // ✅ Ödənişi hesabla
            decimal totalPot = room.JackpotPool;
            decimal commission = totalPot * 0.20m;
            decimal netPot = totalPot - commission;

            // ✅ Bot deyilsə balans əlavə et
            if (!winnerTicket.IsBot)
            {
                using var scope = _serviceProvider.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();

                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user != null)
                {
                    user.Balance += netPot;
                    await db.SaveChangesAsync();
                    _logger.LogInformation($"💰 Prize verildi: {winnerTicket.Name} → {netPot} coin (Komisiya: {commission})");
                }

                // ✅ Rank yenilə
                var rankService = scope.ServiceProvider.GetRequiredService<IRankService>();
                await rankService.UpdateRankAfterGame(userId, GameType.Loto, true, netPot);
                _logger.LogInformation($"📊 Rank yeniləndi: {winnerTicket.Name}");

                // ✅ Uduzan oyunçuların rank-ini yenilə (yalnız real oyunçular)
                await UpdateLosersRank(roomId, userId, room);
            }
            else
            {
                await _botBudgetService.AddBotWinnings(netPot, $"Bot won in {room.RoomName}");
                _logger.LogInformation($"🤖💰 Bot qazandı: {winnerTicket.Name} → {netPot}");
            }

            // ✅ Client-lərə bildir
            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winnerTicket.Name,
                prize = totalPot,
                netPrize = netPot,
                isBot = winnerTicket.IsBot,
                winType = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT",
                winningCard = winnerTicket.Card,
                winningTicketId = winnerTicket.TicketId,
                drawnNumbers = room.DrawnNumbers
            });

            _logger.LogInformation($"🎊 AUTO-BINGO WINNER: {winnerTicket.Name} → {totalPot} coin");

            return new WinResult
            {
                IsValid = true,
                WinnerName = winnerTicket.Name,
                UserId = userId,
                Prize = totalPot,
                NetPrize = netPot,
                Message = "Təbrik edirik!"
            };
        }

        public RoomPlayer? CheckForWinner(LotoRoom room)
        {
            if (room.DrawnNumbers.Count < 15 || room.IsGameFinished)
            {
                return null;
            }

            var drawnSet = new HashSet<int>(room.DrawnNumbers);

            foreach (var player in room.Players)
            {
                if (player.HasWon) continue;

                bool isWinner = false;

                // ✅ Otağın qaydasına görə yoxla
                if (room.RequiresFullCard)
                {
                    isWinner = IsFullCardCompleted(player.Card, drawnSet);
                }
                else
                {
                    isWinner = IsAnyLineCompleted(player.Card, drawnSet);
                }

                if (isWinner)
                {
                    _logger.LogInformation("🏆 Qalib tapıldı: {Name} (ID: {UserId})", player.Name, player.UserId);
                    return player;
                }
            }

            return null;
        }

        // ✅ TAM KART yoxlama
        private bool IsFullCardCompleted(int?[][] card, HashSet<int> drawnSet)
        {
            int totalNumbers = 0;
            int matchedNumbers = 0;

            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 9; c++)
                {
                    if (card[r][c].HasValue)
                    {
                        totalNumbers++;
                        if (drawnSet.Contains(card[r][c].Value))
                        {
                            matchedNumbers++;
                        }
                    }
                }
            }

            return totalNumbers == 15 && matchedNumbers == 15;
        }

        // ✅ BİR XƏTT yoxlama
        private bool IsAnyLineCompleted(int?[][] card, HashSet<int> drawnSet)
        {
            for (int r = 0; r < 3; r++)
            {
                bool lineComplete = true;
                bool hasNumbers = false;

                for (int c = 0; c < 9; c++)
                {
                    if (card[r][c].HasValue)
                    {
                        hasNumbers = true;
                        if (!drawnSet.Contains(card[r][c].Value))
                        {
                            lineComplete = false;
                            break;
                        }
                    }
                }

                if (hasNumbers && lineComplete)
                {
                    return true;
                }
            }

            return false;
        }

        private async Task UpdateLosersRank(string roomId, int winnerId, LotoRoom room)
        {
            try
            {
                var losers = room.Players
                    .Where(p => p.UserId != winnerId && !p.IsBot) // ✅ Botları keç
                    .Select(p => p.UserId)
                    .Distinct()
                    .ToList();

                foreach (var loserId in losers)
                {
                    int ticketCount = room.Players.Count(p => p.UserId == loserId && !p.IsBot);
                    decimal totalLoss = ticketCount * room.EntryFee;

                    Console.WriteLine($"🔍 LOSS: userId={loserId}, tickets={ticketCount}, loss={totalLoss}₼");

                    await _rankService.UpdateRankAfterGame(
                        userId: loserId,
                        gameType: GameType.Loto,
                        isWin: false,
                        earnings: totalLoss  // ✅ Bütün biletlərin məbləği
                    );
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ UpdateLosersRank xətası");
            }
        }
    }
}
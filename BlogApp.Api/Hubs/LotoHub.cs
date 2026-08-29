using BlogApp.Api.Hubs.Services;
using BlogApp.Api.Hubs.Services.BlogApp.Api.Hubs.Services;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace BlogApp.Api.Hubs
{
    public class LotoHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly LotoRoomManager _roomManager;
        private readonly IRankService _rankService;
        private readonly ILotoGameService _gameService;
        private readonly BotManager _botManager;
        private readonly BotBudgetService _botBudgetService;
        private readonly IHubContext<LotoHub> _hubContext;
        private readonly IServiceProvider _serviceProvider;

        private static readonly ConcurrentDictionary<string, HashSet<string>> _userRooms = new();
        private DateTime _lastBroadcast = DateTime.MinValue;


        public LotoHub(
            BlogAppDbContext db,
            LotoRoomManager roomManager,
            IRankService rankService,
            ILotoGameService gameService,
            BotManager botManager,
            BotBudgetService botBudgetService,
            IHubContext<LotoHub> hubContext,
            IServiceProvider serviceProvider)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
            _gameService = gameService;
            _botManager = botManager;
            _botBudgetService = botBudgetService;
            _hubContext = hubContext;
            _serviceProvider = serviceProvider;
        }

        public override async Task OnConnectedAsync()
        {
            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Context.Abort();
                return;
            }

            var userId = GetUserId();
            if (userId == 0)
            {
                Context.Abort();
                return;
            }

            try
            {
                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName,
                    balance = user.Balance
                });

                Console.WriteLine($"✅ Connected: {fullName} (Balance: {user.Balance})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnConnectedAsync error: {ex.Message}");
                Context.Abort();
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connId = Context.ConnectionId;
            var userId = GetUserId();

            if (_userRooms.TryRemove(connId, out var roomIds))
            {
                foreach (var roomId in roomIds)
                {
                    var room = _roomManager.GetRoom(roomId);
                    if (room != null && !room.IsGameStarted)
                    {
                        lock (room.StateLock)
                        {
                            var tickets = room.Players.Where(p => p.ConnectionId == connId).ToList();

                            // ✅ Balansı geri qaytar (YENİ SCOPE)
                            if (tickets.Any())
                            {
                                var totalRefund = tickets.Count * room.EntryFee;

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var scope = _serviceProvider.CreateScope();
                                        var scopedDb = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                                        var user = await scopedDb.Users.FirstOrDefaultAsync(u => u.Id == userId);
                                        if (user != null)
                                        {
                                            user.Balance += totalRefund;
                                            await scopedDb.SaveChangesAsync();
                                            Console.WriteLine($"💰 Disconnect refund: {user.UserName} +{totalRefund}₼");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Console.WriteLine($"❌ Disconnect refund error: {ex.Message}");
                                    }
                                });

                                foreach (var ticket in tickets)
                                {
                                    room.Players.Remove(ticket);
                                    room.JackpotPool -= room.EntryFee;
                                }

                                Clients.Group(roomId).SendAsync("PlayerLeft", tickets[0].Name, tickets.Count);
                                BroadcastRoomUpdate(roomId);
                            }
                        }
                    }

                    await Groups.RemoveFromGroupAsync(connId, roomId);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task<List<RoomListItems>> GetRoomList()
        {
            return _roomManager.GetAvailableRooms();
        }

        public async Task<object> BuyTicket(string roomId)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                return new { success = false, message = "İstifadəçi tapılmadı" };
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                return new { success = false, message = "Room tapılmadı" };
            }

            // ✅ ƏSAS FİX: Oyun başladıqdan sonra bilet almağa icazə vermə
            if (room.IsGameStarted)
            {
                return new { success = false, message = "Oyun artıq başlayıb, bilet ala bilməzsiniz" };
            }

            using var scope = _serviceProvider.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();

            var user = await scopedDb.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                return new { success = false, message = "İstifadəçi tapılmadı" };
            }

            var existingTickets = room.Players.Count(p => p.UserId == userId);
            if (existingTickets >= room.MaxTicketsPerPlayer)
            {
                return new { success = false, message = $"Maksimum {room.MaxTicketsPerPlayer} bilet ala bilərsiniz" };
            }

            if (room.Players.Count >= room.MaxPlayers)
            {
                return new { success = false, message = $"Otaq doludur (maks. {room.MaxPlayers} bilet)" };
            }

            if (user.Balance < room.EntryFee)
            {
                return new { success = false, message = $"Kifayət qədər balans yoxdur (lazım: {room.EntryFee}₼)" };
            }

            string fullName = $"{user.Name} {user.Surname}".Trim();
            if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

            var ticket = new RoomPlayer
            {
                ConnectionId = Context.ConnectionId,
                UserId = userId,
                Name = fullName,
                Balance = user.Balance,
                Card = LotoCardGenerator.GenerateCard(),
                TicketId = Guid.NewGuid().ToString(),
                IsBot = false
            };

            if (!_roomManager.AddPlayerToRoom(roomId, ticket))
            {
                return new { success = false, message = "Bilet almaq alınmadı" };
            }

            user.Balance -= room.EntryFee;
            await scopedDb.SaveChangesAsync();

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            if (!_userRooms.ContainsKey(Context.ConnectionId))
            {
                _userRooms[Context.ConnectionId] = new HashSet<string>();
            }
            _userRooms[Context.ConnectionId].Add(roomId);

            await Clients.Caller.SendAsync("TicketPurchased", new
            {
                ticketId = ticket.TicketId,
                card = ticket.Card,
                balance = user.Balance,
                ticketNumber = existingTickets + 1,
                maxTickets = room.MaxTicketsPerPlayer
            });

            await BroadcastRoomUpdate(roomId);
            return new { success = true, ticketId = ticket.TicketId, balance = user.Balance };
        }
        public async Task JoinRoomView(string roomId)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("RoomError", "İstifadəçi tapılmadı");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("RoomError", "Room tapılmadı");
                return;
            }
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

            if (!_userRooms.ContainsKey(Context.ConnectionId))
            {
                _userRooms[Context.ConnectionId] = new HashSet<string>();
            }
            _userRooms[Context.ConnectionId].Add(roomId);

            var myTickets = room.Players.Where(p => p.UserId == userId).ToList();
            string winRule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT";

            await Clients.Caller.SendAsync("RoomJoined", new
            {
                roomId,
                roomName = room.RoomName,
                entryFee = room.EntryFee,
                jackpot = room.JackpotPool,
                maxTickets = room.MaxTicketsPerPlayer,
                myTickets = myTickets.Select(t => new { t.TicketId, t.Card }).ToList(),
                isGameStarted = room.IsGameStarted,
                drawnNumbers = room.DrawnNumbers,
                timeRemaining = room.GetTimeRemaining(),
                winRule = winRule,
                playerCount = room.Players.Count
            });

            await BroadcastRoomUpdate(roomId);
        }
        private async Task StartGameInternal(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameStarted) return;
                if (room.Players.Count == 0) return;

                room.IsGameStarted = true;
                room.IsGameFinished = false;
                room.DrawnNumbers.Clear();
                room.NumbersQueue = new Queue<int>(
                    Enumerable.Range(1, 90).OrderBy(x => Guid.NewGuid())
                );
                room.GameStartTime = DateTime.UtcNow;

                // ✅ Timer-i dayandır
                room.TimerCts?.Cancel();
                room.TimerCts?.Dispose();
                room.TimerCts = null;

                Console.WriteLine($"🎮 OYUN BAŞLADI: {room.RoomName} ({room.Players.Count} bilet)");
            }

            await Clients.Group(roomId).SendAsync("GameStarted", new
            {
                jackpot = room.JackpotPool,
                playerCount = room.Players.Count,
                winRule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT"
            });

            _ = Task.Run(() => AutoDrawLoop(roomId));
        }

        private async Task AutoDrawLoop(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            room.AutoDrawCts = new CancellationTokenSource();
            var token = room.AutoDrawCts.Token;

            try
            {
                await Task.Delay(2000, token);

                while (!token.IsCancellationRequested)
                {
                    int? next = null;
                    lock (room.StateLock)
                    {
                        if (!room.IsGameStarted || room.NumbersQueue == null || room.NumbersQueue.Count == 0)
                        {
                            break;
                        }
                        next = room.NumbersQueue.Dequeue();
                        room.DrawnNumbers.Add(next.Value);
                    }

                    if (next.HasValue)
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("NumberDrawn", next.Value);

                        // ✅ Yalnız 5 və altı qaldıqda xəbərdarlıq göndər
                        if (!room.IsGameFinished)
                        {
                            int closest = GetClosestCardDistance(room);

                            if (closest <= 5)
                            {
                                int cardCount = GetCardsAtDistance(room, closest);

                                string message;
                                if (room.RequiresFullCard)
                                {
                                    // TAM KART
                                    message = closest == 1
                                        ? $"🔥 {cardCount} kart 1 nömrəyə qaldı! BINGO GƏLİR!"
                                        : $"⚠️ {cardCount} kart {closest} nömrəyə qaldı!";
                                }
                                else
                                {
                                    // BİR XƏTT - xətt deyirik
                                    message = closest == 1
                                        ? $"🔥 {cardCount} xətt 1 nömrəyə qaldı! BINGO GƏLİR!"
                                        : $"⚠️ {cardCount} xətt {closest} nömrəyə qaldı!";
                                }

                                await _hubContext.Clients.Group(roomId).SendAsync("ClosestCardUpdate", new
                                {
                                    remaining = closest,
                                    cardCount,
                                    message,
                                    isLineMode = !room.RequiresFullCard  // frontend üçün
                                });
                            }
                        }
                        await CheckForAutoBingo(roomId);
                    }

                    await Task.Delay(3000, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ AutoDraw error: {ex.Message}");
            }
        }


        private int GetClosestCardDistance(LotoRoom room)
        {
            var drawnSet = new HashSet<int>(room.DrawnNumbers);
            int minRemaining = int.MaxValue;

            foreach (var player in room.Players)
            {
                if (player.HasWon) continue;

                int remaining;

                if (room.RequiresFullCard)
                {
                    remaining = 0;
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 9; c++)
                            if (player.Card[r][c].HasValue && !drawnSet.Contains(player.Card[r][c].Value))
                                remaining++;
                }
                else
                {
                    remaining = int.MaxValue;
                    for (int r = 0; r < 3; r++)
                    {
                        int lineRemaining = 0;
                        bool hasNumbers = false;
                        for (int c = 0; c < 9; c++)
                        {
                            if (player.Card[r][c].HasValue)
                            {
                                hasNumbers = true;
                                if (!drawnSet.Contains(player.Card[r][c].Value))
                                    lineRemaining++;
                            }
                        }
                        if (hasNumbers)
                            remaining = Math.Min(remaining, lineRemaining);
                    }
                }

                minRemaining = Math.Min(minRemaining, remaining);
            }

            return minRemaining == int.MaxValue ? 0 : minRemaining;
        }

        private int GetCardsAtDistance(LotoRoom room, int distance)
        {
            var drawnSet = new HashSet<int>(room.DrawnNumbers);
            int count = 0;

            foreach (var player in room.Players)
            {
                if (player.HasWon) continue;

                if (room.RequiresFullCard)
                {
                    int remaining = 0;
                    for (int r = 0; r < 3; r++)
                        for (int c = 0; c < 9; c++)
                            if (player.Card[r][c].HasValue && !drawnSet.Contains(player.Card[r][c].Value))
                                remaining++;

                    if (remaining == distance) count++;
                }
                else
                {
                    // ✅ Hər xətti ayrıca say
                    for (int r = 0; r < 3; r++)
                    {
                        int lineRemaining = 0;
                        bool hasNumbers = false;

                        for (int c = 0; c < 9; c++)
                        {
                            if (player.Card[r][c].HasValue)
                            {
                                hasNumbers = true;
                                if (!drawnSet.Contains(player.Card[r][c].Value))
                                    lineRemaining++;
                            }
                        }

                        if (hasNumbers && lineRemaining == distance)
                            count++; // Hər xətt ayrıca sayılır
                    }
                }
            }

            return count;
        }
        private async Task CheckForAutoBingo(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.IsGameFinished) return;
            if (room.DrawnNumbers.Count < 15) return;

            List<RoomPlayer> potentialWinners = new List<RoomPlayer>();
            var drawnSet = new HashSet<int>(room.DrawnNumbers);

            lock (room.StateLock)
            {
                foreach (var player in room.Players)
                {
                    if (player.HasWon) continue;

                    bool isWinner = false;

                    // ✅ 0.20₼ - TAM KART, digərləri - BİR XƏTT
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
                        potentialWinners.Add(player);
                    }
                }
            }

            if (potentialWinners.Count > 0)
            {
                var winner = potentialWinners.First();
                await ProcessBingoWinner(roomId, winner);
            }
        }

        public async Task Bingo(string ticketId)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var roomId = _userRooms[Context.ConnectionId].FirstOrDefault();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            RoomPlayer? ticket = null;
            bool isValid = false;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Clients.Caller.SendAsync("BingoError", "Oyun artıq bitib");
                    return;
                }

                ticket = room.Players.FirstOrDefault(p => p.UserId == userId && p.TicketId == ticketId);
                if (ticket == null || ticket.HasWon) return;

                // ✅ Kartı yoxla (0.20₼ - TAM KART, digərləri - BİR XƏTT)
                var drawnSet = new HashSet<int>(room.DrawnNumbers);

                if (room.RequiresFullCard)
                {
                    isValid = IsFullCardCompleted(ticket.Card, drawnSet);
                }
                else
                {
                    isValid = IsAnyLineCompleted(ticket.Card, drawnSet);
                }
            }

            if (!isValid)
            {
                string requiredRule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT";
                await Clients.Caller.SendAsync("BingoError",
                    $"Yanlış BINGO! Qayda: {requiredRule}");
                return;
            }

            // ✅ Qalibi elan et
            await ProcessBingoWinner(roomId, ticket);
        }
        private async Task ProcessBingoWinner(string roomId, RoomPlayer winner)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameFinished) return;

                room.IsGameFinished = true;
                room.IsGameStarted = false;
                winner.HasWon = true;
                room.WinningTicket = winner; // ✅ Qazanan bileti saxla

                room.Winners.Add(new WinnerInfo
                {
                    Name = winner.Name,
                    UserId = winner.UserId,
                    Prize = room.JackpotPool,
                    WinTime = DateTime.UtcNow
                });
            }

            room.AutoDrawCts?.Cancel();

            decimal totalPot = room.JackpotPool;
            decimal commission = totalPot * 0.20m;
            decimal netPot = totalPot - commission;

            // Bot qazandı
            if (winner.IsBot)
            {
                await _botBudgetService.AddBotWinnings(netPot, $"Bot won in {room.RoomName}");
                Console.WriteLine($"🤖💰 Bot qazandı: {winner.Name} → {netPot}₼");
            }
            // Real oyunçu qazandı
            else
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (user != null)
                {
                    user.Balance += netPot;
                    await _db.SaveChangesAsync();
                    await Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                }

                // Rank yenilə
                try
                {
                    await _rankService.UpdateRankAfterGame(
                        userId: winner.UserId,
                        gameType: GameType.Loto,
                        isWin: true,
                        earnings: netPot
                    );

                    var rankDetails = await _rankService.GetPlayerRankDetails(winner.UserId, GameType.Loto);
                    await Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                    {
                        rank = rankDetails.CurrentRank,
                        level = rankDetails.RankLevel,
                        xp = rankDetails.ExperiencePoints,
                        requiredXP = rankDetails.RequiredXPForNextRank,
                        progress = rankDetails.ProgressPercentage
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Rank update error: {ex.Message}");
                }
            }

            // ✅ HAMIYA qazanan bileti göstər
            await Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winner.Name,
                isBot = winner.IsBot,
                prize = totalPot,
                netPrize = netPot,
                winType = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT",
                winningCard = winner.Card,        // ✅ Qalib kartı
                drawnNumbers = room.DrawnNumbers, // Çəkilmiş nömrələr
                message = $"🎉 {winner.Name} qalib oldu! ({(room.RequiresFullCard ? "TAM KART" : "BİR XƏTT")}) Jackpot: {netPot:F2}₼"
            });

            Console.WriteLine($"🏆 WINNER: {winner.Name} → {netPot}₼ ({(room.RequiresFullCard ? "TAM KART" : "BİR XƏTT")})");

            // Uduzanların rank-ını yenilə
            await UpdateLosersRank(roomId, winner.UserId, room);

            _roomManager.ResetFixedRoom(roomId);
            await Clients.Group(roomId).SendAsync("RoomReset");
            await BroadcastRoomUpdate(roomId);
        }
        private async Task UpdateLosersRank(string roomId, int winnerId, LotoRoom room)
        {
            try
            {
                // Room-dakı bütün real oyunçuları tap (qalib və botlar xaric)
                var loserIds = room.Players
                    .Where(p => p.UserId != winnerId && !p.IsBot && p.UserId > 0)
                    .Select(p => p.UserId)
                    .Distinct()
                    .ToList();

                Console.WriteLine($"📊 Updating rank for {loserIds.Count} losers");

                foreach (var loserId in loserIds)
                {
                    try
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

                        // Oyunçuya yeni rank göndər
                        var loserTickets = room.Players.Where(p => p.UserId == loserId).ToList();

                        if (loserTickets.Any())
                        {
                            var rankDetails = await _rankService.GetPlayerRankDetails(loserId, GameType.Loto);

                            // Bütün connection-lara göndər (user-in birdən çox bileti ola bilər)
                            foreach (var ticket in loserTickets)
                            {
                                try
                                {
                                    await Clients.Client(ticket.ConnectionId).SendAsync("RankUpdated", new
                                    {
                                        rank = rankDetails.CurrentRank,
                                        level = rankDetails.RankLevel,
                                        xp = rankDetails.ExperiencePoints,
                                        requiredXP = rankDetails.RequiredXPForNextRank,
                                        progress = rankDetails.ProgressPercentage
                                    });
                                }
                                catch (Exception connEx)
                                {
                                    Console.WriteLine($"⚠️ Could not send rank to connection {ticket.ConnectionId}: {connEx.Message}");
                                }
                            }
                        }

                        Console.WriteLine($"   ✅ Rank updated for user {loserId}");
                    }
                    catch (Exception userEx)
                    {
                        Console.WriteLine($"   ❌ Error updating rank for user {loserId}: {userEx.Message}");
                    }
                }

                Console.WriteLine($"✅ All losers' ranks updated");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ UpdateLosersRank error: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
        }
        // ✅ BİLET YENİLƏMƏ
        [HubMethodName("RefreshTicket")]
        public async Task RefreshTicket(string ticketId)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var roomId = _userRooms[Context.ConnectionId].FirstOrDefault();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (room.IsGameStarted)
            {
                await Clients.Caller.SendAsync("ShowMessage", "Oyun başladıqdan sonra yeniləyə bilməzsiniz", "error");
                return;
            }

            RoomPlayer? ticket = null;
            lock (room.StateLock)
            {
                ticket = room.Players.FirstOrDefault(p => p.UserId == userId && p.TicketId == ticketId);
                if (ticket == null) return;

                ticket.Card = LotoCardGenerator.GenerateCard();
                ticket.CompletedLines.Clear();
            }

            await Clients.Caller.SendAsync("TicketRefreshed", new
            {
                ticketId = ticket.TicketId,
                card = ticket.Card
            });

            Console.WriteLine($"🔄 Bilet yeniləndi: {ticket.Name}");
        }

        [HubMethodName("DeleteTicket")]
        public async Task DeleteTicket(string ticketId)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var roomId = _userRooms[Context.ConnectionId].FirstOrDefault();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (room.IsGameStarted)
            {
                await Clients.Caller.SendAsync("ShowMessage", "Oyun başladıqdan sonra silə bilməzsiniz", "error");
                return;
            }

            RoomPlayer? ticket = null;
            lock (room.StateLock)
            {
                ticket = room.Players.FirstOrDefault(p => p.UserId == userId && p.TicketId == ticketId);
                if (ticket == null) return;

                room.Players.Remove(ticket);
                room.JackpotPool -= room.EntryFee;

                // ⚡ Son bilet silindisə timer-i sıfırla
                if (room.Players.Count == 0 && room.RoomCreatedTime != null)
                {
                    room.RoomCreatedTime = null;
                    room.TimerCts?.Cancel();
                    room.TimerCts?.Dispose();
                    room.TimerCts = null;
                    Console.WriteLine($"⏰ Timer dayandırıldı: {room.RoomName} (otaq boşaldı)");
                }
            }

            // Balansı geri qaytar
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var scopedDb = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                var user = await scopedDb.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user != null)
                {
                    user.Balance += room.EntryFee;
                    await scopedDb.SaveChangesAsync();

                    await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
                    Console.WriteLine($"🗑️ Bilet silindi və balans qaytarıldı: {ticket.Name} (+{room.EntryFee}₼)");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Delete ticket error: {ex.Message}");
            }

            await Clients.Caller.SendAsync("TicketDeleted", ticketId);
            await BroadcastRoomUpdate(roomId);
        }

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

        private int GetUserId()
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out int userId) ? userId : 0;
        }

        private async Task BroadcastRoomUpdate(string roomId)
        {
            await BroadcastRoomUpdateStatic(roomId, _roomManager, _hubContext);
        }
        public static async Task BroadcastRoomUpdateStatic(
            string roomId,
            LotoRoomManager roomManager,
            IHubContext<LotoHub> hubContext)
        {
            var room = roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"⚠️ BroadcastRoomUpdate: Room not found - {roomId}");
                return;
            }

            var update = new
            {
                roomId,
                playerCount = room.Players.Count,
                jackpot = room.JackpotPool,
                isGameStarted = room.IsGameStarted,
                timeRemaining = room.GetTimeRemaining()
            };

            try
            {
                // Room içindəki oyunçulara
                await hubContext.Clients.Group(roomId).SendAsync("RoomUpdated", update);

                // Bütün lobby-dəki oyunçulara
                await hubContext.Clients.All.SendAsync("RoomListUpdated");

                // ✅ Log-u düzəlt (disposed error əvəzinə)
                // Console.WriteLine($"📢 BroadcastRoomUpdate: {room.RoomName} | Timer: {update.timeRemaining}s | Players: {update.playerCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ BroadcastRoomUpdate error: {ex.Message}");
            }
        }
        internal static class LotoCardGenerator
        {
            public static int?[][] GenerateCard()
            {
                int?[][] card = new int?[3][];
                for (int r = 0; r < 3; r++)
                {
                    card[r] = new int?[9];
                }

                var columnRanges = new[]
                {
                    (1, 9), (10, 19), (20, 29), (30, 39), (40, 49),
                    (50, 59), (60, 69), (70, 79), (80, 90)
                };

                var columnNumbers = new List<int>[9];
                for (int col = 0; col < 9; col++)
                {
                    var (min, max) = columnRanges[col];
                    columnNumbers[col] = Enumerable.Range(min, max - min + 1)
                        .OrderBy(x => Guid.NewGuid())
                        .ToList();
                }

                for (int row = 0; row < 3; row++)
                {
                    var filledColumns = Enumerable.Range(0, 9)
                        .OrderBy(x => Guid.NewGuid())
                        .Take(5)
                        .OrderBy(x => x)
                        .ToList();

                    foreach (var col in filledColumns)
                    {
                        if (columnNumbers[col].Count > 0)
                        {
                            card[row][col] = columnNumbers[col][0];
                            columnNumbers[col].RemoveAt(0);
                        }
                    }
                }

                return card;
            }
        }
        internal static class LotoCardValidator
        {
            public static bool IsFullCardMarked(int?[][] card, IEnumerable<int> drawnNumbers)
            {
                var drawnSet = new HashSet<int>(drawnNumbers);

                // ✅ Kartdakı bütün nömrələri tap
                var cardNumbers = new List<int>();
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 9; c++)
                    {
                        if (card[r][c].HasValue)
                        {
                            cardNumbers.Add(card[r][c].Value);
                        }
                    }
                }

                // ✅ Kartda 15 nömrə yoxdursa - səhv
                if (cardNumbers.Count != 15)
                {
                    Console.WriteLine($"❌ Validator: Kartda {cardNumbers.Count} nömrə (lazım: 15)");
                    return false;
                }

                // ✅ Bütün nömrələr çəkilmiş olmalıdır
                foreach (var num in cardNumbers)
                {
                    if (!drawnSet.Contains(num))
                    {
                        return false;
                    }
                }

                return true;
            }
            public static bool IsLineCompleted(int?[][] card, int lineIndex, IEnumerable<int> drawnNumbers)
            {
                if (lineIndex < 0 || lineIndex >= 3) return false;

                var drawnSet = new HashSet<int>(drawnNumbers);

                for (int c = 0; c < 9; c++)
                {
                    // ✅ Bu sətirdə nömrə varsa və çəkilməyibsə → false
                    if (card[lineIndex][c].HasValue && !drawnSet.Contains(card[lineIndex][c].Value))
                    {
                        return false;
                    }
                }
                return true;
            }
        }
    }
}

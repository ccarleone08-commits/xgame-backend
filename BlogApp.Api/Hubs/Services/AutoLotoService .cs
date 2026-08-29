using BlogApp.Api.Hubs.Services.BlogApp.Api.Hubs.Services;
using BlogApp.Core.Entities;
using Microsoft.AspNetCore.SignalR;

namespace BlogApp.Api.Hubs.Services
{
    public class AutoLotoService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AutoLotoService> _logger;

        public AutoLotoService(
            IServiceProvider serviceProvider,
            ILogger<AutoLotoService> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("🚀 AutoLotoService başladı");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var roomManager = scope.ServiceProvider.GetRequiredService<LotoRoomManager>();
                    var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<LotoHub>>();

                    var rooms = roomManager.GetAvailableRooms();

                    foreach (var roomInfo in rooms.Where(r => !r.IsGameStarted))
                    {
                        var room = roomManager.GetRoom(roomInfo.RoomId);
                        if (room == null) continue;

                        bool shouldStart = false;
                        string startReason = "";

                        lock (room.StateLock)
                        {
                            // ✅ Oyunçu yoxdursa keç
                            if (room.Players.Count == 0)
                                continue;

                            // ✅ Timer başlamayıbsa keç
                            if (room.RoomCreatedTime == null)
                                continue;

                            var elapsed = (DateTime.UtcNow - room.RoomCreatedTime.Value).TotalSeconds;
                            var remaining = room.TimerSeconds - elapsed;

                            // ✅ ŞƏRT 1: Otaq doldu - OYUN BAŞLASIN
                            if (room.Players.Count >= room.MaxPlayers)
                            {
                                shouldStart = true;
                                startReason = $"Otaq doldu ({room.Players.Count}/{room.MaxPlayers} bilet)";
                            }
                            // ✅ ŞƏRT 2: Timer bitdi - OYUN BAŞLASIN
                            else if (remaining <= 0)
                            {
                                shouldStart = true;
                                startReason = $"Timer bitdi ({room.Players.Count} bilet)";
                            }
                            // ✅ ŞƏRT 3: Geri sayım başlayan kimi botları insan kimi aralıqlarla əlavə et
                            else if (remaining > 0 && !room.BotsAdded && room.Players.Count < room.MaxPlayers)
                            {
                                room.BotsAdded = true;

                                _ = Task.Run(async () =>
                                {
                                    try
                                    {
                                        using var botScope = _serviceProvider.CreateScope();
                                        var botManager = botScope.ServiceProvider.GetRequiredService<BotManager>();
                                        var botBudget = botScope.ServiceProvider.GetRequiredService<BotBudgetService>();
                                        var botRoomManager = botScope.ServiceProvider.GetRequiredService<LotoRoomManager>();
                                        var botHubContext = botScope.ServiceProvider.GetRequiredService<IHubContext<LotoHub>>();

                                        _logger.LogInformation("🤖 Bot loop başladı: {RoomName} ({Count}/{Max})",
                                            room.RoomName, room.Players.Count, room.MaxPlayers);

                                        while (!room.IsGameStarted && !room.IsGameFinished && room.Players.Count < room.MaxPlayers)
                                        {
                                            var ticketBatchSize = GetHumanLikeBotTicketBatchSize(room);
                                            var batchCost = room.EntryFee * ticketBatchSize;

                                            if (!await botBudget.CanAffordGame(batchCost))
                                            {
                                                _logger.LogWarning("⚠️ Bot büdcəsi kifayət deyil: {RoomName}", room.RoomName);
                                                break;
                                            }

                                            var added = await botManager.AddBotsGradually(
                                                room,
                                                async (ticket) =>
                                                {
                                                    ticket.Card = LotoHub.LotoCardGenerator.GenerateCard();
                                                    await botHubContext.Clients.Group(room.RoomId).SendAsync("BotTicketAdded", new
                                                    {
                                                        playerName = ticket.Name,
                                                        ticketCount = room.Players.Count
                                                    });
                                                },
                                                async () =>
                                                {
                                                    await LotoHub.BroadcastRoomUpdateStatic(room.RoomId, botRoomManager, botHubContext);
                                                },
                                                customBotCount: ticketBatchSize);

                                            if (added <= 0)
                                            {
                                                break;
                                            }

                                            await botBudget.DeductBotExpense(room.EntryFee * added, $"Bot tickets in {room.RoomName}");

                                            if (room.Players.Count >= room.MaxPlayers || room.IsGameStarted || room.IsGameFinished)
                                            {
                                                break;
                                            }

                                            var delay = GetHumanLikeBotDelay(room, added);
                                            _logger.LogInformation("⏳ Növbəti bot üçün gözlənilir: {DelayMs}ms ({RoomName})",
                                                (int)delay.TotalMilliseconds, room.RoomName);
                                            await Task.Delay(delay, stoppingToken);
                                        }

                                        _logger.LogInformation("✅ Bot loop bitdi: {RoomName} ({Count}/{Max})",
                                            room.RoomName, room.Players.Count, room.MaxPlayers);
                                    }
                                    catch (Exception ex)
                                    {
                                        _logger.LogError(ex, "❌ Bot əlavə etmə xətası");
                                        room.BotsAdded = false;
                                    }
                                });
                            }
                        }

                        if (shouldStart)
                        {
                            _logger.LogInformation("🎮 BAŞLATMA: {RoomName} - {Reason}", room.RoomName, startReason);
                            await StartGameForRoom(room, hubContext, roomManager);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "❌ AutoLotoService xətası");
                }

                await Task.Delay(1000, stoppingToken);
            }
        }

        private static int GetHumanLikeBotTicketBatchSize(LotoRoom room)
        {
            var availableSlots = Math.Max(0, room.MaxPlayers - room.Players.Count);
            if (availableSlots <= 0)
            {
                return 0;
            }

            var maxBatch = Math.Min(Math.Min(room.MaxTicketsPerPlayer, 4), availableSlots);
            if (maxBatch <= 1)
            {
                return 1;
            }

            var roll = Random.Shared.NextDouble();

            var requestedBatch = roll switch
            {
                < 0.18 => 1,
                < 0.47 => 2,
                < 0.72 => 3,
                _ => 4
            };

            return Math.Min(requestedBatch, maxBatch);
        }

        private static TimeSpan GetHumanLikeBotDelay(LotoRoom room, int ticketsBoughtInLastBatch)
        {
            var fillRatio = room.MaxPlayers <= 0
                ? 1.0
                : (double)room.Players.Count / room.MaxPlayers;

            var elapsedSeconds = room.RoomCreatedTime.HasValue
                ? (DateTime.UtcNow - room.RoomCreatedTime.Value).TotalSeconds
                : 0;

            var remainingSeconds = room.RoomCreatedTime.HasValue
                ? Math.Max(0, room.TimerSeconds - elapsedSeconds)
                : room.TimerSeconds;

            int minMs;
            int maxMs;

            if (fillRatio < 0.15)
            {
                minMs = 1800;
                maxMs = 6500;
            }
            else if (fillRatio < 0.4)
            {
                minMs = 1200;
                maxMs = 4500;
            }
            else if (fillRatio < 0.7)
            {
                minMs = 700;
                maxMs = 3000;
            }
            else
            {
                minMs = 500;
                maxMs = 1800;
            }

            if (remainingSeconds < 20)
            {
                maxMs = Math.Min(maxMs, 2400);
            }

            if (remainingSeconds < 10)
            {
                minMs = Math.Min(minMs, 300);
                maxMs = Math.Min(maxMs, 1200);
            }

            if (ticketsBoughtInLastBatch >= 3)
            {
                minMs += 1800;
                maxMs += 5200;
            }
            else if (ticketsBoughtInLastBatch == 2)
            {
                minMs += 800;
                maxMs += 2800;
            }

            if (Random.Shared.NextDouble() < 0.12)
            {
                maxMs += 2500;
            }

            if (maxMs < minMs)
            {
                maxMs = minMs;
            }

            return TimeSpan.FromMilliseconds(Random.Shared.Next(minMs, maxMs + 1));
        }

        private async Task StartGameForRoom(
      LotoRoom room,
      IHubContext<LotoHub> hubContext,
      LotoRoomManager roomManager)
        {
            lock (room.StateLock)
            {
                if (room.IsGameStarted)
                {
                    _logger.LogWarning("⚠️ Oyun artıq başlayıb: {RoomName}", room.RoomName);
                    return;
                }

                if (room.Players.Count == 0)
                {
                    _logger.LogWarning("⚠️ Oyunçu yoxdur: {RoomName}", room.RoomName);
                    return;
                }

                room.IsGameStarted = true;
                room.IsGameFinished = false;
                room.DrawnNumbers.Clear();
                room.NumbersQueue = new Queue<int>(
                    Enumerable.Range(1, 90).OrderBy(x => Guid.NewGuid())
                );

                room.TimerCts?.Cancel();
                room.TimerCts?.Dispose();
                room.TimerCts = null;

                var realTickets = room.Players.Count(p => !p.IsBot);
                var botTickets = room.Players.Count(p => p.IsBot);

                _logger.LogInformation("🎮 OYUN BAŞLADI: {RoomName} (Real: {Real}, Bot: {Bot}, Total: {Total}, Jackpot: {Jackpot}₼)",
                    room.RoomName, realTickets, botTickets, room.Players.Count, room.JackpotPool);
            }

            await hubContext.Clients.Group(room.RoomId).SendAsync("GameStarted", new
            {
                jackpot = room.JackpotPool,
                playerCount = room.Players.Count,
                winRule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT"
            });

            await hubContext.Clients.All.SendAsync("RoomListUpdated");

            _logger.LogInformation("🎱 AutoDraw başlayır: {RoomName}", room.RoomName);

            _ = Task.Run(() => AutoDrawLoop(room, hubContext, roomManager));
        }

        private async Task AutoDrawLoop(
            LotoRoom room,
            IHubContext<LotoHub> hubContext,
            LotoRoomManager roomManager)
        {
            room.AutoDrawCts = new CancellationTokenSource();
            var token = room.AutoDrawCts.Token;

            try
            {
                await Task.Delay(2000, token);

                while (!token.IsCancellationRequested)
                {
                    if (room.IsGameFinished)
                    {
                        _logger.LogInformation("🛑 Oyun bitdi, loop dayandı: {RoomName}", room.RoomName);
                        break;
                    }

                    int? next = null;
                    lock (room.StateLock)
                    {
                        if (!room.IsGameStarted || room.NumbersQueue == null || room.NumbersQueue.Count == 0)
                        {
                            break;
                        }

                        if (room.IsGameFinished)
                        {
                            break;
                        }

                        next = room.NumbersQueue.Dequeue();
                        room.DrawnNumbers.Add(next.Value);
                    }

                    if (next.HasValue)
                    {
                        await hubContext.Clients.Group(room.RoomId).SendAsync("NumberDrawn", next.Value);
                        _logger.LogInformation("🎱 [{RoomName}] Çəkildi: {Number}", room.RoomName, next.Value);

                        // ✅ Ən yaxın kart xəbərdarlığı
                        if (!room.IsGameFinished)
                        {
                            int closest = GetClosestCardDistance(room);
                            _logger.LogInformation("🔍 Closest: {Closest}", closest);

                            if (closest <= 5)
                            {
                                int cardCount = GetCardsAtDistance(room, closest);
                                string message = room.RequiresFullCard
                                    ? (closest == 1 ? $"🔥 {cardCount} kartda 1 nömrə qaldı !" : $"⚠️ {cardCount} kartda {closest} nömrə qaldı !")
                                    : (closest == 1 ? $"🔥 {cardCount} xətt 1 nömrə gözləyir !" : $"⚠️ {cardCount} xətt {closest} nömrə gözləyir !");

                                await hubContext.Clients.Group(room.RoomId).SendAsync("ClosestCardUpdate", new
                                {
                                    remaining = closest,
                                    cardCount,
                                    message,
                                    isLineMode = !room.RequiresFullCard
                                });
                            }
                        }

                        await CheckAndProcessWinner(room, roomManager);

                        if (room.IsGameFinished)
                        {
                            _logger.LogInformation("🏆 Oyun bitdi - Auto-draw dayandırılır");
                            break;
                        }
                    }
                    await Task.Delay(3000, token);
                }

                if (room.NumbersQueue?.Count == 0 && !room.IsGameFinished)
                {
                    await hubContext.Clients.Group(room.RoomId).SendAsync("GameOver", new
                    {
                        message = "Nömrələr qurtardı, qalib yoxdur",
                        winners = new List<object>()
                    });

                    await Task.Delay(10000); // ✅ 10 saniyə gözlə
                    roomManager.ResetFixedRoom(room.RoomId);
                    await hubContext.Clients.All.SendAsync("RoomListUpdated");
                    _logger.LogInformation("⏱️ Vaxt bitdi, qalib yoxdur: {RoomName}", room.RoomName);
                }
            }
            catch (TaskCanceledException)
            {
                _logger.LogInformation("🛑 AutoDraw dayandı: {RoomName}", room.RoomName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ AutoDrawLoop xətası: {RoomName}", room.RoomName);
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
                        {
                            count++;
                            break;
                        }
                    }
                }
            }
            return count;
        }


        private async Task CheckAndProcessWinner(LotoRoom room, LotoRoomManager roomManager)
        {
            try
            {
                if (room.DrawnNumbers.Count < 15 || room.IsGameFinished)
                {
                    return;
                }

                var drawnSet = new HashSet<int>(room.DrawnNumbers);
                RoomPlayer? winner = null;

                lock (room.StateLock)
                {
                    foreach (var player in room.Players)
                    {
                        if (player.HasWon) continue;

                        bool isWinner = false;

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
                            winner = player;
                            break;
                        }
                    }
                }

                if (winner != null)
                {
                    _logger.LogInformation("🎉 AUTO WINNER tapıldı: {Name} (Qayda: {Rule})",
                        winner.Name,
                        room.RequiresFullCard ? "TAM KART" : "BİR XƏTT");

                    using var scope = _serviceProvider.CreateScope();
                    var gameService = scope.ServiceProvider.GetRequiredService<ILotoGameService>();

                    var result = await gameService.ProcessWinner(
                        room.RoomId,
                        winner.TicketId,
                        winner.UserId,
                        isAutoWin: true
                    );

                    if (result != null && result.IsValid)
                    {
                        _logger.LogInformation("✅ Prize uğurla verildi: {NetPrize} coin", result.NetPrize);

                        // ✅ 10 saniyə modal göstər, sonra reset
                        await Task.Delay(10000);
                        roomManager.ResetFixedRoom(room.RoomId);

                        var hubContext = scope.ServiceProvider.GetRequiredService<IHubContext<LotoHub>>();
                        await hubContext.Clients.Group(room.RoomId).SendAsync("RoomReset");
                        await hubContext.Clients.All.SendAsync("RoomListUpdated");

                        _logger.LogInformation("🔄 Otaq reset edildi: {RoomName}", room.RoomName);
                    }
                    else
                    {
                        _logger.LogWarning("⚠️ Winner processing failed: {Error}", result?.ErrorMessage);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ CheckAndProcessWinner xətası");
            }
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
    }
}

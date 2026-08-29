using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace BlogApp.Api.Hubs
{
    public class DominoHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly DominoRoomManager _roomManager;
        private readonly IRankService _rankService;
        private readonly IHubContext<DominoHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;
        private static readonly ConcurrentDictionary<string, string> _userRooms = new();

        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _turnTimers = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _autoPassTimers = new();
        private const int DisconnectReconnectSeconds = 25;

        public DominoHub(
            BlogAppDbContext db,
            DominoRoomManager roomManager,
            IRankService rankService,
            IHubContext<DominoHub> hubContext,
            IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
        }

        public override async Task OnConnectedAsync()
        {
            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Console.WriteLine($"❌ Unauthorized connection");
                Context.Abort();
                return;
            }

            int userId = GetUserId();
            if (userId == 0)
            {
                Console.WriteLine($"❌ Invalid user ID");
                Context.Abort();
                return;
            }

            try
            {
                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Balance })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    Context.Abort();
                    return;
                }

                string displayName = !string.IsNullOrWhiteSpace(user.UserName)
                    ? user.UserName.Trim()
                    : $"User_{userId}";

                Console.WriteLine($"✅ OnConnectedAsync - Username: '{displayName}'");

                var existingRoom = _roomManager.GetRoomByUser(userId);

                if (existingRoom != null)
                {
                    Console.WriteLine($"🔄 {displayName} RECONNECTING");

                    string? oldConnectionId = null;
                    bool isSystemControlled = false;
                    bool reconnectGraceExpired = false;
                    lock (existingRoom.StateLock)
                    {
                        var existingPlayer = existingRoom.Players.FirstOrDefault(p => p.UserId == userId);
                        oldConnectionId = existingPlayer?.ConnectionId;
                        isSystemControlled = existingPlayer?.IsSystemControlled == true;
                        reconnectGraceExpired = existingPlayer?.DisconnectGraceDeadlineUtc.HasValue == true
                            && DateTime.UtcNow >= existingPlayer.DisconnectGraceDeadlineUtc.Value;
                    }

                    if (isSystemControlled || reconnectGraceExpired)
                    {
                        await Clients.Caller.SendAsync("UserData", new
                        {
                            userId = user.Id,
                            username = user.UserName,
                            fullName = displayName,
                            balance = user.Balance
                        });
                        await Clients.Caller.SendAsync("SilentRoomClosed", new
                        {
                            reason = "system_takeover",
                            balance = user.Balance
                        });
                        await base.OnConnectedAsync();
                        return;
                    }

                    existingRoom.MarkReconnected(userId, Context.ConnectionId);
                    existingRoom.CancelDisconnectTimer(userId);
                    _roomManager.UpdateRoomActivity(existingRoom.RoomId);

                    if (!string.IsNullOrWhiteSpace(oldConnectionId) && oldConnectionId != Context.ConnectionId)
                    {
                        _userRooms.TryRemove(oldConnectionId, out _);
                    }

                    await Groups.AddToGroupAsync(Context.ConnectionId, existingRoom.RoomId);
                    _userRooms[Context.ConnectionId] = existingRoom.RoomId;

                    if (existingRoom.IsGameStarted && existingRoom.CurrentTurnUserId == userId)
                    {
                        StartTurnTimer(existingRoom.RoomId);
                    }

                    var fullState = existingRoom.GetFullStateFor(userId);
                    await Clients.Caller.SendAsync("SyncFullGameState", fullState);
                    await Clients.Caller.SendAsync("UpdateBalance", user.Balance);

                    await Clients.Group(existingRoom.RoomId).SendAsync("PlayerReconnected", displayName);
                    await BroadcastPlayers(existingRoom.RoomId);

                    Console.WriteLine($"✅ {displayName} reconnected successfully");
                }
                else
                {
                    await Clients.Caller.SendAsync("UserData", new
                    {
                        userId = user.Id,
                        username = user.UserName,
                        fullName = displayName,
                        balance = user.Balance
                    });

                    Console.WriteLine($"✅ {displayName} connected (new)");
                }
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
            string connId = Context.ConnectionId;
            int userId = GetUserId();

            if (userId == 0)
            {
                await base.OnDisconnectedAsync(exception);
                return;
            }

            try
            {
                var room = _roomManager.GetRoomByUser(userId);
                if (room == null)
                {
                    _userRooms.TryRemove(connId, out _);
                    await base.OnDisconnectedAsync(exception);
                    return;
                }

                Console.WriteLine($"⚠️ {userId} DISCONNECTED");

                // ✅ SADƏCƏ STATUS DƏYİŞ. Köhnə connection gec disconnect olarsa ignore edilir.
                if (!room.MarkDisconnected(userId, connId))
                {
                    _userRooms.TryRemove(connId, out _);
                    await base.OnDisconnectedAsync(exception);
                    return;
                }

                bool isGameActive;
                bool disconnectedPlayerHasTurn;
                string? playerName;

                lock (room.StateLock)
                {
                    isGameActive = room.IsGameStarted && !room.IsRoundFinished && !room.IsGameFinished;
                    disconnectedPlayerHasTurn = room.CurrentTurnUserId == userId;
                    playerName = room.Players.FirstOrDefault(p => p.UserId == userId)?.Name;
                }

                if (isGameActive)
                {
                    room.StartDisconnectTimer(
                        userId,
                        TimeSpan.FromSeconds(DisconnectReconnectSeconds),
                        async timedOutUserId => await HandleActiveDisconnectedPlayerTimeout(room.RoomId, timedOutUserId));

                    DateTime? graceDeadlineUtc;
                    lock (room.StateLock)
                    {
                        graceDeadlineUtc = room.Players.FirstOrDefault(p => p.UserId == userId)?.DisconnectGraceDeadlineUtc;
                    }

                    await Clients.Group(room.RoomId).SendAsync("PlayerTempDisconnected", new
                    {
                        userId,
                        playerName,
                        reconnectTimeoutSeconds = DisconnectReconnectSeconds,
                        disconnectGraceDeadlineUtc = graceDeadlineUtc,
                        isCurrentTurn = disconnectedPlayerHasTurn,
                        message = $"{playerName} bağlantıdan kəsildi. {DisconnectReconnectSeconds} saniyə ərzində qayıda bilər."
                    });

                    if (disconnectedPlayerHasTurn)
                    {
                        StartTurnTimer(room.RoomId);
                    }
                }
                else
                {
                    // Oyun başlamayıbsa boş otaqları təmizləmək üçün timeout saxlanılır.
                    room.StartDisconnectTimer(
                        userId,
                        TimeSpan.FromSeconds(DisconnectReconnectSeconds),
                        async timedOutUserId => await HandleDisconnectedPlayerTimeout(room.RoomId, timedOutUserId));

                    await Clients.OthersInGroup(room.RoomId).SendAsync("PlayerTempDisconnected", new
                    {
                        userId,
                        playerName,
                        reconnectTimeoutSeconds = DisconnectReconnectSeconds,
                        disconnectGraceDeadlineUtc = DateTime.UtcNow.AddSeconds(DisconnectReconnectSeconds),
                        isCurrentTurn = false,
                        message = $"{playerName} bağlantıdan kəsildi..."
                    });
                }

                await BroadcastPlayers(room.RoomId);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnDisconnectedAsync error: {ex.Message}");
            }

            _userRooms.TryRemove(connId, out _);
            await base.OnDisconnectedAsync(exception);
        }
        private async Task HandlePlayerLeftWin(string roomId, int leftPlayerId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            DominoPlayer? winner = null;
            int prizePlayerCount = 0;

            lock (room.StateLock)
            {
                winner = room.Players.FirstOrDefault();
                if (winner == null) return;

                room.IsRoundFinished = true;
                room.RoundWinner = winner;
                prizePlayerCount = room.Players.Count + (leftPlayerId != 0 ? 1 : 0);
            }

            decimal totalPrize = room.EntryFee * prizePlayerCount;
            decimal platformFee = totalPrize * 0.20m;
            bool winnerIsSystemControlled = winner.IsSystemControlled;
            decimal winnerReward = winnerIsSystemControlled ? 0m : totalPrize - platformFee;
            decimal systemAmount = winnerIsSystemControlled ? totalPrize : 0m;
            decimal displayReward = winnerIsSystemControlled ? systemAmount : winnerReward;

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnected", new
            {
                message = winnerIsSystemControlled
                    ? $"🏆 Rəqib oyunu tərk etdi! {winner.Name} qazandı. Mükafat sistemə keçdi."
                    : $"🏆 Rəqib oyunu tərk etdi! {winner.Name} qazandı və {winnerReward:F2} coin aldı!",
                winnerName = winner.Name,
                winnerReward = displayReward,
                reward = displayReward,
                displayReward,
                systemAmount,
                systemWon = winnerIsSystemControlled,
                reason = "opponent_left"
            });

            await Task.Delay(1000);
            await HandleGameEnd(
                roomId,
                forcedLoserIds: leftPlayerId != 0 ? new List<int> { leftPlayerId } : null,
                prizePlayerCountOverride: prizePlayerCount);
        }
        private async Task HandleDisconnectedPlayerTimeout(string roomId, int userId)
        {
            await RemovePlayerFromRoomAsync(
                roomId,
                userId,
                removeFromSignalRGroup: false,
                notifyCallerBalance: false,
                leaveReason: "disconnect_timeout",
                leaveMessage: $"{DisconnectReconnectSeconds} saniyə ərzində geri qayıtmadı və otaqdan çıxarıldı.");
        }

        private async Task HandleActiveDisconnectedPlayerTimeout(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            bool becameSystemControlled = room.MarkSystemControlled(userId);
            if (!becameSystemControlled)
            {
                return;
            }

            DominoPlayer? player;
            bool isCurrentTurn;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                isCurrentTurn = room.CurrentTurnUserId == userId;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerBecameSystemControlled", new
            {
                userId,
                playerName = player?.Name,
                message = $"{player?.Name} {DisconnectReconnectSeconds} saniyə ərzində qayıtmadı. Sistem onun yerinə oynayacaq."
            });

            using (var scope = CreateBackgroundScope())
            {
                var scopedDb = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                await BroadcastPlayers(roomId, scopedDb);
            }

            if (isCurrentTurn)
            {
                StartTurnTimer(roomId);
            }
        }
        private IServiceScope CreateBackgroundScope() => _scopeFactory.CreateScope();
        private async Task RemovePlayerFromRoomAsync(
            string roomId,
            int userId,
            bool removeFromSignalRGroup,
            bool notifyCallerBalance,
            string leaveReason,
            string leaveMessage)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            DominoPlayer? removedPlayer = null;
            bool shouldRefund = false;
            bool turnChanged = false;
            bool shouldRestartTimer = false;
            bool openingStarterChanged = false;
            int remainingPlayers = 0;
            bool gameStarted = false;
            List<(int userId, string connectionId)> remainingPlayerTargets = new();

            lock (room.StateLock)
            {
                var disconnectedPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (disconnectedPlayer == null)
                {
                    Console.WriteLine($"ℹ️ Remove ignored for user {userId} - player not found");
                    return;
                }

                if (leaveReason == "disconnect_timeout" && disconnectedPlayer.IsConnected)
                {
                    Console.WriteLine($"ℹ️ Timeout ignored for user {userId} - player already returned");
                    return;
                }

                gameStarted = room.IsGameStarted;
                shouldRefund = !room.IsGameStarted;

                removedPlayer = room.RemovePlayerAndAdjustTurn(userId, out turnChanged);
                if (removedPlayer == null)
                {
                    return;
                }

                Console.WriteLine($"⏰ Timeout remove: {removedPlayer.Name} removed from room {roomId}");

                if (room.IsGameStarted && !room.IsRoundFinished && room.Players.Count >= 2)
                {
                    shouldRestartTimer = true;

                    if (room.Chain.Tiles.Count == 0 &&
                        (room.GameType == "Classic101" || room.GameType == "AllFives"))
                    {
                        ReassignOpeningTurnAfterLeave(room);
                        openingStarterChanged = true;
                    }
                }

                remainingPlayers = room.Players.Count;
                remainingPlayerTargets = room.Players
                    .Select(p => (p.UserId, p.ConnectionId))
                    .ToList();
            }

            if (removedPlayer == null) return;

            room.CancelDisconnectTimer(userId);
            _userRooms.TryRemove(removedPlayer.ConnectionId, out _);

            if (shouldRefund)
            {
                using var scopeContext = CreateBackgroundScope();
                var scopedDb = scopeContext.ServiceProvider.GetRequiredService<BlogAppDbContext>();

                var user = await scopedDb.Users.FindAsync(removedPlayer.UserId);
                if (user != null)
                {
                    user.Balance += room.EntryFee;
                    await scopedDb.SaveChangesAsync();
                    if (notifyCallerBalance)
                    {
                        await Clients.Caller.SendAsync("UpdateBalance", user.Balance);
                    }
                    Console.WriteLine($"💰 Timeout refund: {user.Name} +{room.EntryFee}");
                }
            }

            if (removeFromSignalRGroup)
            {
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", new
            {
                playerName = removedPlayer.Name,
                playersRemaining = remainingPlayers,
                newStartPlayer = openingStarterChanged ? room.GetCurrentPlayer()?.Name : null,
                reason = leaveReason
            });

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnected", new
            {
                playerName = removedPlayer.Name,
                message = $"⏰ {removedPlayer.Name} {leaveMessage}",
                reason = leaveReason
            });

            if (gameStarted && remainingPlayers < 2)
            {
                StopTurnTimer(roomId);
                await HandlePlayerLeftWin(roomId, removedPlayer.UserId);
                return;
            }

            if (gameStarted && remainingPlayers >= 2)
            {
                using var scopeContext = CreateBackgroundScope();
                var scopedDb = scopeContext.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                await BroadcastPlayers(roomId, scopedDb);
                foreach (var player in remainingPlayerTargets)
                {
                    await SendGameState(roomId, player.connectionId);
                    var fullState = room.GetFullStateFor(player.userId);
                    await _hubContext.Clients.Client(player.connectionId).SendAsync("SyncFullGameState", fullState);
                }

                if (shouldRestartTimer)
                {
                    StopTurnTimer(roomId);
                    StartTurnTimer(roomId);
                    Console.WriteLine($"⏭️ Timeout after remove: {removedPlayer.Name} removed, {remainingPlayers} players left, turn continues with {room.GetCurrentPlayer()?.Name}");
                }

                return;
            }

            if (remainingPlayers == 0)
            {
                _roomManager.DeleteRoom(roomId);
            }
        }
        public async Task<List<object>> GetAvailableLobbies()
        {
            return await Task.FromResult(_roomManager.GetAvailableLobbies());
        }

        public async Task<object> JoinLobby(string gameType, int playerCount, int scoreToWin, decimal entryFee)
        {
            var userId = GetUserId();
            if (userId == 0)
                return new { success = false, message = "İstifadəçi tapılmadı" };

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return new { success = false, message = "İstifadəçi tapılmadı" };

            if (user.Balance < entryFee)
                return new { success = false, message = $"Kifayət qədər balans yoxdur!" };

            string displayName = !string.IsNullOrWhiteSpace(user.UserName)
                ? user.UserName.Trim()
                : $"User_{userId}";

            Console.WriteLine($"✅ Username prepared: '{displayName}'");

            // Qalan kod...
            var oldRoomId = GetCurrentRoom();
            if (!string.IsNullOrEmpty(oldRoomId))
            {
                Console.WriteLine($"⚠️ User {userId} has old room {oldRoomId}, cleaning up...");
                await LeaveRoom();
            }

            var room = _roomManager.FindOrCreateRoom(gameType, playerCount, scoreToWin, entryFee);
            if (room == null)
                return new { success = false, message = "Otaq yaratmak alınmadı" };

            // 🔥 Player yaratma
            var player = new DominoPlayer
            {
                ConnectionId = Context.ConnectionId,
                UserId = user.Id,
                Name = displayName
            };

            lock (room.StateLock)
            {
                if (room.Players.Any(p => p.UserId == userId))
                    return new { success = false, message = "Artıq bu otaqdasınız" };

                if (room.Players.Count >= playerCount)
                    return new { success = false, message = "Otaq doludur" };

                room.Players.Add(player);
            }

            user.Balance -= entryFee;
            await _db.SaveChangesAsync();

            await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);
            _userRooms[Context.ConnectionId] = room.RoomId;

            Console.WriteLine($"✅ {displayName} joined room {room.RoomId} ({room.Players.Count}/{room.PlayerCount})");

            await Clients.Caller.SendAsync("UpdateBalance", user.Balance);

            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId = room.RoomId,
                roomName = room.RoomName,
                gameType = room.GameType,
                playerCount = room.PlayerCount,
                scoreToWin = room.ScoreToWin,
                currentPlayers = room.Players.Count,
                balance = user.Balance,
                isGameStarted = room.IsGameStarted
            });

            await Clients.Group(room.RoomId).SendAsync("PlayerJoined", new
            {
                playerName = displayName,
                currentCount = room.Players.Count,
                maxCount = room.PlayerCount,
                profileImage = user.Image
            });

            await BroadcastPlayers(room.RoomId);

            if (room.Players.Count == room.PlayerCount && !room.IsGameStarted)
            {
                Console.WriteLine($"⏰ Room full! Starting game...");
                await Task.Delay(1500);
                await StartGameAuto(room.RoomId);
            }

            return new { success = true, roomId = room.RoomId, userId = userId, balance = user.Balance };
        }
        private async Task StartGameAuto(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameStarted) return;
                if (room.Players.Count != room.PlayerCount) return;

                room.IsGameStarted = true;
                room.CurrentRound = 1;
                room.ForceOpeningRuleAfterLeave = false;

                int tilesPerPlayer = room.GameType == "Quick5" ? 5 : 7;
                var (stock, hands) = DominoGameGenerator.DealTiles(room.Players.Count, tilesPerPlayer);

                room.Stock = stock;

                for (int i = 0; i < room.Players.Count; i++)
                {
                    room.Players[i].Hand = hands[i];
                    room.Players[i].Status = PlayerStatus.Waiting;
                    room.Players[i].HasPassed = false;
                }

                int startIndex = FindStartingPlayerForRound(room, isFirstRound: true);
                room.CurrentPlayerIndex = startIndex;
                room.Players[startIndex].Status = PlayerStatus.Playing;
                room.CurrentTurnUserId = room.Players[startIndex].UserId;

                var startingPlayer = room.Players[startIndex];

                if (room.GameType == "Classic101")
                {
                    var smallestDouble = FindSmallestDouble(startingPlayer.Hand);
                    Console.WriteLine($"🎮 Classic101 R1: {startingPlayer.Name} starts with [{smallestDouble?.Left}|{smallestDouble?.Right}]");
                }
            }

            await Clients.Group(roomId).SendAsync("GameStarted", new
            {
                message = $"🎮 Oyun başladı! {room.GetCurrentPlayer()?.Name} başlayır",
                gameType = room.GameType,
                playerCount = room.PlayerCount,
                scoreToWin = room.ScoreToWin,
                startPlayer = room.GetCurrentPlayer()?.Name,
                round = room.CurrentRound
            });

            StartTurnTimer(roomId);

            // 🔥 ÖNƏMLİ: HƏR oyunçuya kendi hand-ını göndər
            foreach (var player in room.Players)
            {
                var fullState = room.GetFullStateFor(player.UserId);
                await Clients.Client(player.ConnectionId).SendAsync("GameState", fullState);
            }
        }
        private void StartTurnTimer(string roomId)
        {
            StopTurnTimer(roomId);
            StopAutoPassTimer(roomId);

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            DominoPlayer? currentPlayer;
            bool shouldAutoPass;
            bool shouldAutoPlaySystemControlled;
            bool roundFinishedImmediately = false;
            int disconnectedGraceSeconds = 0;
            int disconnectedGraceMilliseconds = 0;
            const int normalTurnSeconds = 30;
            const int autoPassSeconds = 3;
            const int systemAutoPlaySeconds = 2;
            const int systemVisibleTurnSeconds = normalTurnSeconds;

            lock (room.StateLock)
            {
                currentPlayer = room.GetCurrentPlayer();
                if (currentPlayer == null) return;

                // ✅ Turn state-ni dərhal qeyd et
                room.CurrentTurnUserId = currentPlayer.UserId;
                FinishBlockedRoundIfNeeded(room);
                if (room.IsRoundFinished)
                {
                    roundFinishedImmediately = true;
                    shouldAutoPlaySystemControlled = false;
                    shouldAutoPass = false;
                }
                else
                {
                    shouldAutoPlaySystemControlled = currentPlayer.IsSystemControlled;
                    shouldAutoPass = ShouldAutoPass(room, currentPlayer);

                    if (!currentPlayer.IsConnected &&
                        !currentPlayer.IsSystemControlled &&
                        currentPlayer.DisconnectGraceDeadlineUtc.HasValue)
                    {
                        disconnectedGraceMilliseconds = Math.Max(
                            1,
                            (int)Math.Ceiling((currentPlayer.DisconnectGraceDeadlineUtc.Value - DateTime.UtcNow).TotalMilliseconds));
                        disconnectedGraceSeconds = Math.Max(1, (int)Math.Ceiling(disconnectedGraceMilliseconds / 1000.0));
                    }
                }

                if (!roundFinishedImmediately)
                {
                    room.StartTurnTimerState(
                        shouldAutoPlaySystemControlled
                            ? systemVisibleTurnSeconds
                            : disconnectedGraceSeconds > 0 ? disconnectedGraceSeconds
                            : shouldAutoPass ? autoPassSeconds : normalTurnSeconds,
                        isAutoPass: shouldAutoPass);
                }
            }

            if (roundFinishedImmediately)
            {
                _ = HandleRoundEnd(roomId);
                return;
            }

            var cts = new CancellationTokenSource();
            _turnTimers[roomId] = cts;

            if (shouldAutoPlaySystemControlled)
            {
                Console.WriteLine($"🤖 System auto-play scheduled in {systemAutoPlaySeconds}s: {currentPlayer.Name}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(systemAutoPlaySeconds), cts.Token);
                        await AutoPlaySystemControlledCurrentTurn(roomId, currentPlayer.UserId);
                    }
                    catch (TaskCanceledException)
                    {
                        Console.WriteLine("🤖 System auto-play cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ System auto-play error: {ex.Message}");
                        RestartTurnTimerIfRoundActive(roomId);
                    }
                }, cts.Token);

                return;
            }

            if (disconnectedGraceSeconds > 0)
            {
                Console.WriteLine($"⏳ Waiting reconnect grace for {currentPlayer.Name}: {disconnectedGraceMilliseconds}ms");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(disconnectedGraceMilliseconds), cts.Token);
                        await HandleActiveDisconnectedPlayerTimeout(roomId, currentPlayer.UserId);
                    }
                    catch (TaskCanceledException)
                    {
                        Console.WriteLine("⏳ Reconnect grace timer cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Reconnect grace timer error: {ex.Message}");
                        RestartTurnTimerIfRoundActive(roomId);
                    }
                }, cts.Token);

                return;
            }

            if (shouldAutoPass)
            {
                Console.WriteLine($"⏳ AUTO-PASS scheduled in 3s: {currentPlayer.Name} has no playable tile");

                var autoPassCts = new CancellationTokenSource();
                _autoPassTimers[roomId] = autoPassCts;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(autoPassSeconds), autoPassCts.Token);

                        var roomCheck = _roomManager.GetRoom(roomId);
                        if (roomCheck == null || roomCheck.IsRoundFinished)
                            return;

                        var latestPlayer = roomCheck.GetCurrentPlayer();
                        if (latestPlayer == null || latestPlayer.UserId != currentPlayer.UserId)
                        {
                            RestartTurnTimerIfRoundActive(roomId);
                            return;
                        }

                        if (!ShouldAutoPass(roomCheck, latestPlayer))
                        {
                            Console.WriteLine($"⏳ AUTO-PASS skipped for {latestPlayer.Name}; restarting normal turn timer");
                            RestartTurnTimerIfRoundActive(roomId);
                            return;
                        }

                        bool passApplied = await AutoPassCurrentPlayer(roomId, currentPlayer.UserId, "Əlinizdə qoyula bilən daş yoxdur və bazar bağlıdır.");
                        if (!passApplied)
                        {
                            RestartTurnTimerIfRoundActive(roomId);
                        }
                    }
                    catch (TaskCanceledException)
                    {
                        Console.WriteLine("⏳ AUTO-PASS cancelled");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ AUTO-PASS error: {ex.Message}");
                        RestartTurnTimerIfRoundActive(roomId);
                    }
                    finally
                    {
                        StopAutoPassTimer(roomId, autoPassCts);
                    }
                }, autoPassCts.Token);
                return;
            }

            Console.WriteLine($"⏰ Timer started for userId: {currentPlayer.UserId} ({currentPlayer.Name})");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(normalTurnSeconds), cts.Token);

                    var roomCheck = _roomManager.GetRoom(roomId);
                    if (roomCheck == null || roomCheck.IsRoundFinished)
                    {
                        Console.WriteLine($"⏰ Timer fired but game finished");
                        return;
                    }

                    // ✅ UserId ilə check et
                    if (roomCheck.CurrentTurnUserId != currentPlayer.UserId)
                    {
                        Console.WriteLine($"⏰ Timer fired but turn changed");
                        return;
                    }

                    Console.WriteLine($"⏰ {currentPlayer.Name} timeout!");

                    await _hubContext.Clients.Group(roomId).SendAsync("TimerExpired", new
                    {
                        playerName = currentPlayer.Name,
                        message = $"⏰ {currentPlayer.Name} vaxt bitdi!"
                    });

                    await AutoPlaceTileWithContext(roomId, currentPlayer);
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine($"⏰ Timer cancelled");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Turn timer error: {ex.Message}");
                    RestartTurnTimerIfRoundActive(roomId);
                }
            }, cts.Token);
        }
        private bool ShouldAutoPass(DominoRoom room, DominoPlayer player)
        {
            if (!room.IsGameStarted || room.IsRoundFinished)
                return false;

            if (room.Chain.Tiles.Count == 0)
                return false;

            if (PlayerHasPlayableTile(room, player))
                return false;

            return !CanDrawForCurrentTurn(room);
        }

        private bool PlayerHasPlayableTile(DominoRoom room, DominoPlayer player)
        {
            return player.Hand.Any(tile => CanPlaceForGameType(room, tile));
        }

        private bool CanPlaceForGameType(DominoRoom room, DominoTile tile)
        {
            var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(tile);

            if (room.GameType == "AllFives")
                return canLeft || canRight || canCenterTop || canCenterBottom;

            return canLeft || canRight;
        }

        private bool CanDrawForCurrentTurn(DominoRoom room)
        {
            if (room.GameType == "Quick5")
                return false;

            if (room.GameType == "AllFives" && room.PlayerCount == 4)
                return false;

            return room.CanDrawFromStock();
        }

        private void SyncCurrentTurn(DominoRoom room)
        {
            if (room.Players.Count == 0)
            {
                room.CurrentPlayerIndex = 0;
                room.CurrentTurnUserId = -1;
                return;
            }

            if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                room.CurrentPlayerIndex = 0;
            }

            var currentPlayer = room.GetCurrentPlayer();
            room.CurrentTurnUserId = currentPlayer?.UserId ?? -1;

            foreach (var p in room.Players)
            {
                p.Status = p.UserId == room.CurrentTurnUserId
                    ? PlayerStatus.Playing
                    : PlayerStatus.Waiting;
            }
        }

        private void FinishBlockedRoundIfNeeded(DominoRoom room)
        {
            if (room.IsRoundFinished)
                return;

            var emptyHandWinner = room.Players.FirstOrDefault(p => p.Hand.Count == 0);
            if (emptyHandWinner != null)
            {
                room.IsRoundFinished = true;
                room.RoundWinner = emptyHandWinner;
                Console.WriteLine($"🏁 Round finished: {emptyHandWinner.Name} has no tiles left");
                return;
            }

            bool allPlayersPassed = room.AllPlayersPassed();
            bool noPlayerCanMove = IsRoundBlocked(room);

            if (!allPlayersPassed && !noPlayerCanMove)
                return;

            room.IsRoundFinished = true;

            if (noPlayerCanMove)
            {
                foreach (var p in room.Players)
                {
                    p.HasPassed = true;
                }
            }

            if (room.GameType == "Quick5")
            {
                foreach (var p in room.Players)
                {
                    int handValue = p.GetHandValue();
                    p.Score += handValue;
                    Console.WriteLine($"📉 Quick5 ALL PASSED: {p.Name} +{handValue} xal (Total: {p.Score})");
                }

                room.RoundWinner = room.Players.OrderBy(p => p.Score).First();
                Console.WriteLine($"⚠️ Quick5: Hamı PAS keçdi! Cari ən az xal: {room.RoundWinner.Name} ({room.RoundWinner.Score})");
            }
            else
            {
                room.RoundWinner = room.Players.OrderBy(p => p.GetHandValue()).First();
                Console.WriteLine($"⚠️ Hamı PAS keçdi! Qalib: {room.RoundWinner.Name}");
            }
        }

        private bool IsRoundBlocked(DominoRoom room)
        {
            if (!room.IsGameStarted || room.IsRoundFinished || room.Chain.Tiles.Count == 0)
                return false;

            if (CanDrawForCurrentTurn(room))
                return false;

            return room.Players.All(p => !PlayerHasPlayableTile(room, p));
        }

        private void ResetPassesAfterTilePlaced(DominoRoom room)
        {
            foreach (var p in room.Players)
            {
                p.HasPassed = false;
            }
        }

        private void RestartTurnTimerIfRoundActive(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameStarted || room.IsRoundFinished || room.GetCurrentPlayer() == null)
                return;

            StartTurnTimer(roomId);
        }

        private async Task AutoPlaySystemControlledCurrentTurn(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            DominoPlayer? playerToAutoPlay;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsRoundFinished || room.IsGameFinished)
                    return;

                playerToAutoPlay = room.GetCurrentPlayer();
                if (playerToAutoPlay == null ||
                    playerToAutoPlay.UserId != userId ||
                    !playerToAutoPlay.IsSystemControlled)
                {
                    return;
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("DisconnectedAutoPlay", new
            {
                userId,
                playerName = playerToAutoPlay.Name,
                message = $"{playerToAutoPlay.Name} sistemə keçdiyi üçün sistem oynayır."
            });

            await AutoPlaceTileWithContext(roomId, playerToAutoPlay);
        }

        private async Task<bool> AutoPassCurrentPlayer(string roomId, int userId, string reason)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return false;

            DominoPlayer? passedPlayer = null;
            bool roundFinished = false;

            lock (room.StateLock)
            {
                if (room.IsRoundFinished) return false;

                passedPlayer = room.GetCurrentPlayer();
                if (passedPlayer == null || passedPlayer.UserId != userId) return false;

                if (!ShouldAutoPass(room, passedPlayer)) return false;

                passedPlayer.HasPassed = true;
                passedPlayer.Status = PlayerStatus.Waiting;

                room.NextTurn();
                SyncCurrentTurn(room);

                FinishBlockedRoundIfNeeded(room);
                roundFinished = room.IsRoundFinished;
            }

            if (passedPlayer == null) return false;

            if (!roundFinished)
            {
                StartTurnTimer(roomId);
            }

            try
            {
                await _hubContext.Clients.Group(roomId).SendAsync("TileDrawn", new
                {
                    playerName = passedPlayer.Name,
                    passed = true,
                    stockRemaining = room.Stock.Count,
                    drawnCount = 0,
                    drawnTile = (object?)null,
                    foundPlayable = false,
                    autoPassed = true,
                    message = reason
                });

                using var scopeContext = CreateBackgroundScope();
                var scopedDb = scopeContext.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                await BroadcastPlayers(roomId, scopedDb);

                foreach (var player in room.Players)
                {
                    await SendGameState(roomId, player.ConnectionId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ AUTO-PASS notify error: {ex.Message}");
            }

            if (roundFinished)
            {
                await HandleRoundEnd(roomId);
            }

            return true;
        }
        private void StopAutoPassTimer(string roomId)
        {
            if (_autoPassTimers.TryRemove(roomId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                    Console.WriteLine($"⏳ Auto-pass timer dayandırıldı: {roomId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ StopAutoPassTimer error: {ex.Message}");
                }
            }
        }

        private void StopAutoPassTimer(string roomId, CancellationTokenSource expectedCts)
        {
            var entry = new KeyValuePair<string, CancellationTokenSource>(roomId, expectedCts);
            var removed = ((ICollection<KeyValuePair<string, CancellationTokenSource>>)_autoPassTimers)
                .Remove(entry);

            if (!removed)
                return;

            try
            {
                expectedCts.Dispose();
                Console.WriteLine($"⏳ Auto-pass timer təmizləndi: {roomId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ StopAutoPassTimer cleanup error: {ex.Message}");
            }
        }
        private void StopTurnTimer(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room != null)
            {
                lock (room.StateLock)
                {
                    room.ClearTurnTimerState();
                }
            }

            if (_turnTimers.TryRemove(roomId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    cts.Dispose();
                    Console.WriteLine($"⏰ Timer dayandırıldı: {roomId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ StopTurnTimer error: {ex.Message}");
                }
            }
        }
        private async Task AutoPlaceTileWithContext(string roomId, DominoPlayer player)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.IsRoundFinished) return;

            DominoTile? placedTile = null;
            string side = "right";
            bool moveSuccess = false;
            int earnedPoints = 0;
            bool canPlayAgain = false;

            lock (room.StateLock)
            {
                // İlk daş
                if (room.Chain.Tiles.Count == 0)
                {
                    DominoTile? firstTile = null;
                    bool mustUseOpeningRule = room.CurrentRound == 1 || room.ForceOpeningRuleAfterLeave;

                    if (room.GameType == "Quick5")
                        firstTile = FindBiggestDouble(player.Hand);
                    else if (room.GameType == "Classic101")
                        firstTile = mustUseOpeningRule
                            ? FindSmallestDouble(player.Hand)
                            : player.Hand.FirstOrDefault();
                    else if (room.GameType == "AllFives")
                    {
                        if (mustUseOpeningRule)
                        {
                            firstTile = player.Hand.FirstOrDefault(t =>
                                (t.Left == 2 && t.Right == 3) || (t.Left == 3 && t.Right == 2));
                            firstTile ??= FindSmallestDouble(player.Hand);
                        }
                        else
                            firstTile = player.Hand.FirstOrDefault();
                    }

                    if (firstTile != null)
                    {
                        room.Chain.AddFirst(firstTile);
                        player.RemoveTile(firstTile.Id);
                        room.ForceOpeningRuleAfterLeave = false;
                        placedTile = firstTile;
                        moveSuccess = true;
                    }
                }
                else
                {
                    // Normal daş qoyma
                    var playableTile = player.Hand.FirstOrDefault(t =>
                    {
                        return CanPlaceForGameType(room, t);
                    });

                    if (playableTile != null)
                    {
                        var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(playableTile);
                        bool canUseCenter = room.GameType == "AllFives";
                        side = canLeft ? "left"
                            : canRight ? "right"
                            : canUseCenter && canCenterTop ? "center-top"
                            : "center-bottom";

                        if (side == "left" && canLeft)
                            room.Chain.AddLeft(playableTile);
                        else if (side == "right" && canRight)
                            room.Chain.AddRight(playableTile);
                        else if (canUseCenter && side == "center-top" && canCenterTop)
                            room.Chain.AddCenterTop(playableTile);
                        else if (canUseCenter && side == "center-bottom" && canCenterBottom)
                            room.Chain.AddCenterBottom(playableTile);

                        player.RemoveTile(playableTile.Id);
                        placedTile = playableTile;
                        moveSuccess = true;
                    }
                }

                // ✅ AllFives xal hesablama - PARTIYA SİSTEMİ YOXDUR
                if (moveSuccess && placedTile != null && room.GameType == "AllFives")
                {
                    int chainSum = CalculateChainSum(room.Chain);

                    if (chainSum % 5 == 0 && chainSum > 0)
                    {
                        earnedPoints = chainSum;

                        if (room.PlayerCount == 4)
                        {
                            int team = room.CurrentPlayerIndex % 2;
                            room.TeamScores[team] += earnedPoints;
                        }
                        else
                        {
                            player.Score += earnedPoints;
                        }

                        Console.WriteLine($"🔥 AllFives: {player.Name} earned {earnedPoints} points (sum={chainSum})");
                    }

                    // ❌ PARTIYA SISTEMI YOK - HӘMIŞӘ NOVBӘ KƏÇ
                    canPlayAgain = false;
                    Console.WriteLine($"⏭️ {player.Name}'ın növbəsi bitti");
                }

                // ✅ Həmişə növbə keç (xal yazılsa da, yazılmasa da, double olsa da)
                if (moveSuccess && placedTile != null)
                {
                    ResetPassesAfterTilePlaced(room);
                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);
                    FinishBlockedRoundIfNeeded(room);
                }

                else if (canPlayAgain)
                {
                    Console.WriteLine($"🎯 AUTO: {player.Name} keeps turn (DOUBLE + points)");
                }
            }

            if (moveSuccess && placedTile != null)
            {
                object? centerDoubleData = null;
                List<object>? centerTopTilesData = null;
                List<object>? centerBottomTilesData = null;
                bool canUseCenterDouble = false;
                bool shouldStartNextTurnTimer = !canPlayAgain && !room.IsRoundFinished;

                if (room.GameType == "AllFives" && room.Chain.CenterDouble != null)
                {
                    int centerIndex = room.Chain.Tiles.FindIndex(t => t.Id == room.Chain.CenterDouble.Id);
                    bool centerIsInMiddle = centerIndex > 0 && centerIndex < room.Chain.Tiles.Count - 1;
                    canUseCenterDouble = centerIsInMiddle;

                    if (canUseCenterDouble)
                    {
                        centerDoubleData = new
                        {
                            room.Chain.CenterDouble.Left,
                            room.Chain.CenterDouble.Right,
                            room.Chain.CenterDouble.Id
                        };

                        centerTopTilesData = room.Chain.CenterTop.Select(t => new { t.Left, t.Right, t.Id } as object).ToList();
                        centerBottomTilesData = room.Chain.CenterBottom.Select(t => new { t.Left, t.Right, t.Id } as object).ToList();
                    }
                }

                if (shouldStartNextTurnTimer)
                {
                    StartTurnTimer(roomId);
                }

                await _hubContext.Clients.Group(roomId).SendAsync("TilePlaced", new
                {
                    playerName = player.Name,
                    tile = new { placedTile.Left, placedTile.Right, placedTile.Id },
                    side,
                    leftEnd = room.Chain.LeftEnd,
                    rightEnd = room.Chain.RightEnd,
                    centerDouble = centerDoubleData,
                    centerTopTiles = centerTopTilesData,
                    centerBottomTiles = centerBottomTilesData,
                    canUseCenterDouble,
                    earnedPoints,
                    canPlayAgain,
                    isDouble = placedTile.Left == placedTile.Right,
                    allChainTiles = room.Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                    autoPlaced = true
                });

                await BroadcastPlayersWithContext(roomId);

                foreach (var p in room.Players)
                {
                    await SendGameStateWithContext(roomId, p.ConnectionId);
                }

                if (room.IsRoundFinished)
                {
                    await HandleRoundEnd(roomId);
                }
            }
            else
            {
                // 🔥 DÜZƏLIŞ: Quick5-də bazardan götürmə QADAĞANdır - PAS keç
                if (room.GameType == "Quick5")
                {
                    player.HasPassed = true;
                    Console.WriteLine($"⏭️ AUTO: {player.Name} PAS keçdi (Quick5 - bazar yoxdur)");

                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);

                    await _hubContext.Clients.Group(roomId).SendAsync("TileDrawn", new
                    {
                        playerName = player.Name,
                        passed = true,
                        stockRemaining = room.Stock.Count,
                        drawnCount = 0,
                        drawnTile = (object?)null,
                        foundPlayable = false
                    });

                    FinishBlockedRoundIfNeeded(room);

                    await BroadcastPlayersWithContext(roomId);

                    foreach (var p in room.Players)
                    {
                        await SendGameStateWithContext(roomId, p.ConnectionId);
                    }

                    if (room.IsRoundFinished)
                    {
                        await HandleRoundEnd(roomId);
                    }
                    else
                    {
                        StartTurnTimer(roomId);
                    }
                }
                else
                {
                    // Classic101 və AllFives üçün bazardan götür
                    await TakeFromStockWithContext(roomId, player.UserId, autoPlayAfterDraw: true);
                }
            }
        }
        private async Task TakeFromStockWithContext(string roomId, int userId, bool autoPlayAfterDraw = false)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            DominoPlayer? player;
            DominoTile? drawnTile = null;
            bool passed = false;
            bool foundPlayable = false;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                if (CanDrawForCurrentTurn(room))
                {
                    drawnTile = room.Stock[0];
                    room.Stock.RemoveAt(0);
                    player.Hand.Add(drawnTile);
                    foundPlayable = CanPlaceForGameType(room, drawnTile);
                }
                else
                {
                    player.HasPassed = true;
                    passed = true;
                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);
                    FinishBlockedRoundIfNeeded(room);
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("TileDrawn", new
            {
                playerName = player.Name,
                passed,
                stockRemaining = room.Stock.Count,
                drawnCount = drawnTile != null ? 1 : 0,
                drawnTile = drawnTile != null ? new { drawnTile.Left, drawnTile.Right, drawnTile.Id } : null,
                foundPlayable,
                autoPlayed = autoPlayAfterDraw
            });

            await BroadcastPlayersWithContext(roomId);

            foreach (var p in room.Players)
            {
                await SendGameStateWithContext(roomId, p.ConnectionId);
            }

            if (autoPlayAfterDraw && !room.IsRoundFinished && !passed)
            {
                if (foundPlayable)
                {
                    await AutoPlaceTileWithContext(roomId, player);
                    return;
                }

                if (CanDrawForCurrentTurn(room))
                {
                    await Task.Delay(300);
                    await TakeFromStockWithContext(roomId, player.UserId, autoPlayAfterDraw: true);
                    return;
                }
            }

            if (!room.IsRoundFinished)
            {
                StartTurnTimer(roomId);
            }

            if (room.IsRoundFinished)
            {
                await HandleRoundEnd(roomId);
            }
        }
        private async Task BroadcastPlayersWithContext(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var players = room.Players.Select(p => new
            {
                userId = p.UserId,
                name = p.Name,
                tileCount = p.Hand.Count,
                score = p.Score,
                isCurrentTurn = p.ConnectionId == room.CurrentPlayerId,
                isConnected = p.IsConnected,
                isSystemControlled = p.IsSystemControlled,
                teamIndex = -1
            }).ToList();

            await _hubContext.Clients.Group(roomId).SendAsync("PlayersList", players);
        }
        private async Task SendGameStateWithContext(string roomId, string connectionId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.GetPlayer(connectionId);
            if (player == null) return;

            await _hubContext.Clients.Client(connectionId).SendAsync("GameState", new
            {
                myHand = player.Hand,
                chainTiles = room.Chain.Tiles,
                leftEnd = room.Chain.LeftEnd,
                rightEnd = room.Chain.RightEnd,
                stockCount = room.Stock.Count,
                isMyTurn = player.ConnectionId == room.CurrentPlayerId,
                currentPlayerName = room.GetCurrentPlayer()?.Name,
                currentTurnUserId = room.CurrentTurnUserId,
                turnDeadlineUtc = room.TurnDeadlineUtc,
                turnStartedAtUtc = room.TurnStartedAtUtc,
                turnTimeRemaining = room.GetTurnTimeRemainingSeconds(),
                turnDurationSeconds = room.TurnDurationSeconds,
                isAutoPassTimer = room.IsAutoPassTurnTimer,
                currentRound = room.CurrentRound,
                gameType = room.GameType,
                scoreToWin = room.ScoreToWin,
                players = room.Players.Select(p => new
                {
                    userId = p.UserId,
                    name = p.Name,
                    tileCount = p.Hand.Count,
                    score = p.Score,
                    isCurrentTurn = p.ConnectionId == room.CurrentPlayerId,
                    isConnected = p.IsConnected,
                    isSystemControlled = p.IsSystemControlled
                }).ToList(),
                scores = room.Players.Select(p => p.Score).ToArray()
            });
        }

        private async Task AutoPlaceTile(string roomId, DominoPlayer player)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.IsRoundFinished) return;

            bool shouldTakeFromStock = false;

            lock (room.StateLock)
            {
                // İlk daş
                if (room.Chain.Tiles.Count == 0)
                {
                    DominoTile? firstTile = null;

                    if (room.GameType == "Quick5")
                        firstTile = FindBiggestDouble(player.Hand);
                    else if (room.GameType == "Classic101")
                        firstTile = room.CurrentRound == 1
                            ? FindSmallestDouble(player.Hand)
                            : player.Hand.FirstOrDefault();
                    else if (room.GameType == "AllFives")
                    {
                        if (room.CurrentRound == 1)
                        {
                            firstTile = player.Hand.FirstOrDefault(t =>
                                (t.Left == 2 && t.Right == 3) || (t.Left == 3 && t.Right == 2));
                            firstTile ??= FindSmallestDouble(player.Hand);
                        }
                        else
                            firstTile = player.Hand.FirstOrDefault();
                    }

                    if (firstTile != null)
                    {
                        room.Chain.AddFirst(firstTile);
                        player.RemoveTile(firstTile.Id);
                        ResetPassesAfterTilePlaced(room);

                        _ = Clients.Group(roomId).SendAsync("TilePlaced", new
                        {
                            playerName = player.Name,
                            tile = new { firstTile.Left, firstTile.Right, firstTile.Id },
                            side = "right",
                            leftEnd = room.Chain.LeftEnd,
                            rightEnd = room.Chain.RightEnd,
                            centerDouble = (object?)null,
                            centerTopTiles = (object?)null,
                            centerBottomTiles = (object?)null,
                            canUseCenterDouble = false,
                            earnedPoints = 0,
                            canPlayAgain = false,
                            isDouble = firstTile.Left == firstTile.Right,
                            allChainTiles = room.Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                            autoPlaced = true
                        });

                        player.Status = PlayerStatus.Waiting;
                        room.NextTurn();
                        SyncCurrentTurn(room);
                        FinishBlockedRoundIfNeeded(room);

                        if (!room.IsRoundFinished)
                        {
                            StartTurnTimer(roomId);
                        }

                        _ = BroadcastPlayers(roomId);
                        foreach (var p in room.Players)
                            _ = SendGameState(roomId, p.ConnectionId);
                        if (room.IsRoundFinished)
                        {
                            _ = HandleRoundEnd(roomId);
                        }
                        return;
                    }
                }

                // Normal daş qoyma
                var playableTile = player.Hand.FirstOrDefault(t =>
                {
                    return CanPlaceForGameType(room, t);
                });

                if (playableTile != null)
                {
                    var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(playableTile);
                    bool canUseCenter = room.GameType == "AllFives";
                    string side = canLeft ? "left"
                        : canRight ? "right"
                        : canUseCenter && canCenterTop ? "center-top"
                        : "center-bottom";

                    if (side == "left" && canLeft)
                        room.Chain.AddLeft(playableTile);
                    else if (side == "right" && canRight)
                        room.Chain.AddRight(playableTile);
                    else if (canUseCenter && side == "center-top" && canCenterTop)
                        room.Chain.AddCenterTop(playableTile);
                    else if (canUseCenter && side == "center-bottom" && canCenterBottom)
                        room.Chain.AddCenterBottom(playableTile);

                    player.RemoveTile(playableTile.Id);
                    ResetPassesAfterTilePlaced(room);

                    _ = Clients.Group(roomId).SendAsync("TilePlaced", new
                    {
                        playerName = player.Name,
                        tile = new { playableTile.Left, playableTile.Right, playableTile.Id },
                        side,
                        leftEnd = room.Chain.LeftEnd,
                        rightEnd = room.Chain.RightEnd,
                        centerDouble = (object?)null,
                        centerTopTiles = (object?)null,
                        centerBottomTiles = (object?)null,
                        canUseCenterDouble = false,
                        earnedPoints = 0,
                        canPlayAgain = false,
                        isDouble = playableTile.Left == playableTile.Right,
                        allChainTiles = room.Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                        autoPlaced = true
                    });

                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);
                    FinishBlockedRoundIfNeeded(room);

                    if (!room.IsRoundFinished)
                    {
                        StartTurnTimer(roomId);
                    }

                    _ = BroadcastPlayers(roomId);
                    foreach (var p in room.Players)
                        _ = SendGameState(roomId, p.ConnectionId);
                    if (room.IsRoundFinished)
                    {
                        _ = HandleRoundEnd(roomId);
                    }
                }
                else
                {
                    // Bazardan götür
                    shouldTakeFromStock = true;
                }
            }

            if (shouldTakeFromStock)
            {
                await TakeFromStock();
            }
        }
        public async Task<object> CleanupRooms()
        {
            var userId = GetUserId();
            // 🔥 İstəsəniz burada admin yoxlaması əlavə edə bilərsiniz

            int remainingRooms = _roomManager.CleanupAllInactiveRooms();

            return new
            {
                success = true,
                message = "Təmizləmə tamamlandı",
                remainingRooms
            };
        }

        // 🔥 ƏSAS DÜZƏLIŞ: İlk oyunçunu düzgün seçmək
        private int FindStartingPlayer(DominoRoom room, bool isFirstRound = false, int roundNumber = 1)
        {
            // 🎯 QUICK5: Hər raund [6|6] ilə başlayır
            if (room.GameType == "Quick5")
            {
                // ✅ 2 və 3 nəfərlik: Azalan sıra ilə double axtar (6-6 → 1-1 → 0-0)
                if (room.PlayerCount == 2 || room.PlayerCount == 3)
                {
                    for (int val = 6; val >= 1; val--)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 Quick5 ({room.PlayerCount}P): {room.Players[i].Name} starts with [{val}|{val}]");
                                return i;
                            }
                        }
                    }

                    // [0|0] ən böyükdür
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                        {
                            Console.WriteLine($"🎯 Quick5: {room.Players[i].Name} starts with [0|0]");
                            return i;
                        }
                    }
                }
                // ✅ 4 nəfərlik: HƏR ZAMAN [6|6] ilə başlayır
                else if (room.PlayerCount == 4)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == 6 && t.Right == 6))
                        {
                            Console.WriteLine($"🎯 Quick5 (4P): {room.Players[i].Name} starts with [6|6] (mandatory)");
                            return i;
                        }
                    }

                    // ⚠️ [6|6] heç kimdə yoxdursa ERROR
                    Console.WriteLine("❌ Quick5 (4P): [6|6] heç kimdə yoxdur! Bu mümkün olmamalı!");
                    return 0;
                }
            }

            // 🎯 Classic101: ən KIÇIK double (1-1, 2-2, 3-3... sonra 0-0)
            if (room.GameType == "Classic101")
            {
                for (int val = 1; val <= 6; val++)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                        {
                            Console.WriteLine($"🎯 {room.Players[i].Name} starts with [{val}|{val}] (smallest double)");
                            return i;
                        }
                    }
                }

                for (int i = 0; i < room.Players.Count; i++)
                {
                    if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                    {
                        Console.WriteLine($"🎯 {room.Players[i].Name} starts with [0|0]");
                        return i;
                    }
                }
            }

            // 🔥 AllFives: İlk raundda [2-3], növbəti raundlarda ən böyük double
            if (room.GameType == "AllFives")
            {
                if (isFirstRound)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t =>
                            (t.Left == 2 && t.Right == 3) || (t.Left == 3 && t.Right == 2)))
                        {
                            Console.WriteLine($"🎯 {room.Players[i].Name} starts with [2|3]");
                            return i;
                        }
                    }

                    for (int val = 1; val <= 6; val++)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 {room.Players[i].Name} starts with [{val}|{val}]");
                                return i;
                            }
                        }
                    }

                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                        {
                            Console.WriteLine($"🎯 {room.Players[i].Name} starts with [0|0]");
                            return i;
                        }
                    }
                }
                else
                {
                    for (int val = 6; val >= 1; val--)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 {room.Players[i].Name} starts with [{val}|{val}]");
                                return i;
                            }
                        }
                    }

                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                        {
                            Console.WriteLine($"🎯 {room.Players[i].Name} starts with [0|0]");
                            return i;
                        }
                    }
                }
            }

            return 0;
        }
        public async Task PlaceTile(string tileId, string side)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            _roomManager.UpdateRoomActivity(roomId);

            DominoPlayer? player;
            DominoTile? tile;
            bool moveSuccess = false;
            int earnedPoints = 0;
            bool canPlayAgain = false;

            lock (room.StateLock)
            {
                if (room.IsRoundFinished) return;

                player = room.GetPlayer(Context.ConnectionId);
                if (player == null || room.CurrentPlayerId != Context.ConnectionId)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Sizin növbəniz deyil!");
                    return;
                }

                tile = player.Hand.FirstOrDefault(t => t.Id == tileId);
                if (tile == null)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Bu daş əlinizdə yoxdur");
                    return;
                }

                if (room.Chain.Tiles.Count == 0)
                {
                    // ✅ İLK DAŞ MƏNTIQ
                    bool canPlaceFirstTile = false;
                    string errorMessage = "";
                    bool mustUseOpeningRule = room.CurrentRound == 1 || room.ForceOpeningRuleAfterLeave;

                    if (room.GameType == "Quick5")
                    {
                        int startPlayerIndex = FindStartingPlayerForRound(room, isFirstRound: (room.CurrentRound == 1));

                        if (room.CurrentPlayerIndex != startPlayerIndex)
                        {
                            errorMessage = "İlk daşı yalnız ən böyük double-a sahib oyunçu qoya bilər!";
                        }
                        else
                        {
                            DominoTile? biggestDouble = FindBiggestDouble(player.Hand);

                            if (biggestDouble == null)
                            {
                                errorMessage = "Əlinizdə double yoxdur!";
                            }
                            else if (tile.Id != biggestDouble.Id)
                            {
                                errorMessage = $"İlk daş olaraq [{biggestDouble.Left}|{biggestDouble.Right}] qoymalısınız! (ən böyük double MƏCBURI)";
                            }
                            else
                            {
                                canPlaceFirstTile = true;
                            }
                        }
                    }
                    else if (room.GameType == "Classic101")
                    {
                        if (mustUseOpeningRule)
                        {
                            int startPlayerIndex = FindStartingPlayerForRound(room, isFirstRound: true);

                            if (room.CurrentPlayerIndex != startPlayerIndex)
                            {
                                errorMessage = "İlk daşı yalnız ən kiçik double-a sahib oyunçu qoya bilər!";
                            }
                            else
                            {
                                DominoTile? smallestDouble = FindSmallestDouble(player.Hand);

                                if (smallestDouble == null)
                                {
                                    errorMessage = "Əlinizdə double yoxdur!";
                                }
                                else if (tile.Id != smallestDouble.Id)
                                {
                                    errorMessage = $"İlk daş olaraq [{smallestDouble.Left}|{smallestDouble.Right}] qoymalısınız!";
                                }
                                else
                                {
                                    canPlaceFirstTile = true;
                                }
                            }
                        }
                        else
                        {
                            canPlaceFirstTile = true;
                            Console.WriteLine($"✅ Classic101 R{room.CurrentRound}: {player.Name} can place ANY tile");
                        }
                    }
                    else if (room.GameType == "AllFives")
                    {
                        if (mustUseOpeningRule)
                        {
                            int startPlayerIndex = FindStartingPlayerForRound(room, isFirstRound: true);

                            if (room.CurrentPlayerIndex != startPlayerIndex)
                            {
                                errorMessage = "İlk daşı yalnız [2|3] və ya ən kiçik double-a sahib oyunçu qoya bilər!";
                            }
                            else
                            {
                                bool has23 = player.Hand.Any(t =>
                                    (t.Left == 2 && t.Right == 3) || (t.Left == 3 && t.Right == 2));

                                if (has23)
                                {
                                    if (!((tile.Left == 2 && tile.Right == 3) || (tile.Left == 3 && tile.Right == 2)))
                                    {
                                        errorMessage = "İlk raundda [2|3] daşını qoymalısınız!";
                                    }
                                    else
                                    {
                                        canPlaceFirstTile = true;
                                    }
                                }
                                else
                                {
                                    DominoTile? smallestDouble = FindSmallestDouble(player.Hand);

                                    if (smallestDouble == null)
                                    {
                                        errorMessage = "Əlinizdə nə [2|3], nə də double yoxdur!";
                                    }
                                    else if (tile.Id != smallestDouble.Id)
                                    {
                                        errorMessage = $"İlk daş olaraq [{smallestDouble.Left}|{smallestDouble.Right}] qoymalısınız!";
                                    }
                                    else
                                    {
                                        canPlaceFirstTile = true;
                                    }
                                }
                            }
                        }
                        else
                        {
                            canPlaceFirstTile = true;
                            Console.WriteLine($"✅ AllFives R{room.CurrentRound}: {player.Name} can place ANY tile");
                        }
                    }

                    if (!canPlaceFirstTile)
                    {
                        _ = Clients.Caller.SendAsync("MoveError", errorMessage);
                        return;
                    }

                    room.Chain.AddFirst(tile);
                    player.RemoveTile(tileId);
                    room.ForceOpeningRuleAfterLeave = false;
                    moveSuccess = true;
                }
                else
                {
                    // ✅ NÖVBƏTI DAŞLAR MƏNTIQ
                    var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(tile);
                    bool canUseCenter = room.GameType == "AllFives";

                    Console.WriteLine($"🔍 Can place [{tile.Left}|{tile.Right}]: L={canLeft} R={canRight} CT={canCenterTop} CB={canCenterBottom} | Side={side}");

                    if (!canLeft && !canRight && !(canUseCenter && (canCenterTop || canCenterBottom)))
                    {
                        _ = Clients.Caller.SendAsync("MoveError", "Bu daş qoyula bilməz!");
                        return;
                    }

                    // ✅ ALL FIVES: Center placement
                    if (room.GameType == "AllFives")
                    {
                        if (side == "center-top" && canCenterTop)
                        {
                            room.Chain.AddCenterTop(tile);
                            player.RemoveTile(tileId);
                            moveSuccess = true;
                            Console.WriteLine($"✅ Added to CENTER TOP: [{tile.Left}|{tile.Right}]");
                        }
                        else if (side == "center-bottom" && canCenterBottom)
                        {
                            room.Chain.AddCenterBottom(tile);
                            player.RemoveTile(tileId);
                            moveSuccess = true;
                            Console.WriteLine($"✅ Added to CENTER BOTTOM: [{tile.Left}|{tile.Right}]");
                        }
                    }

                    // ✅ Left placement
                    if (!moveSuccess && side == "left" && canLeft)
                    {
                        room.Chain.AddLeft(tile);
                        player.RemoveTile(tileId);
                        moveSuccess = true;
                        Console.WriteLine($"✅ Added to LEFT: [{tile.Left}|{tile.Right}]");
                    }
                    // ✅ Right placement
                    else if (!moveSuccess && side == "right" && canRight)
                    {
                        room.Chain.AddRight(tile);
                        player.RemoveTile(tileId);
                        moveSuccess = true;
                        Console.WriteLine($"✅ Added to RIGHT: [{tile.Left}|{tile.Right}]");
                    }

                    if (!moveSuccess)
                    {
                        _ = Clients.Caller.SendAsync("MoveError", $"Bu daş '{side}' tərəfə qoyula bilməz!");
                        return;
                    }
                }


                // ✅ ALL FIVES xal hesablama
                if (moveSuccess && room.GameType == "AllFives")
                {
                    int chainSum = CalculateChainSum(room.Chain);

                    if (chainSum % 5 == 0 && chainSum > 0)
                    {
                        earnedPoints = chainSum;

                        if (room.PlayerCount == 4)
                        {
                            int team = room.CurrentPlayerIndex % 2;
                            room.TeamScores[team] += earnedPoints;
                        }
                        else
                        {
                            player.Score += earnedPoints;
                        }

                        Console.WriteLine($"🔥 AllFives: {player.Name} earned {earnedPoints} points (sum={chainSum})");

                        // ✅ 35+ XAL LİMİT
                        if (earnedPoints >= 35)
                        {
                            Console.WriteLine($"🏆 INSTANT WIN! {player.Name} scored {earnedPoints} points (35+ limit)");
                            room.IsRoundFinished = true;
                            room.RoundWinner = player;
                            canPlayAgain = false;
                        }
                        else
                        {
                            canPlayAgain = false;
                        }

                        // Normal qalib yoxlaması (eyni qalır)
                        bool gameWon = false;
                        if (room.PlayerCount == 4)
                        {
                            int team = room.CurrentPlayerIndex % 2;
                            if (room.TeamScores[team] >= room.ScoreToWin)
                            {
                                gameWon = true;
                                room.IsRoundFinished = true;
                                room.RoundWinner = player;
                                canPlayAgain = false;
                            }
                        }
                        else
                        {
                            if (player.Score >= room.ScoreToWin)
                            {
                                gameWon = true;
                                room.IsRoundFinished = true;
                                room.RoundWinner = player;
                                canPlayAgain = false;
                            }
                        }

                        if (gameWon)
                        {
                            Console.WriteLine($"🏆 {player.Name} WON by reaching {room.ScoreToWin} points!");
                        }
                    }
                }
                // ✅ Əl boşaldısa qalib
                if (player.Hand.Count == 0 && !room.IsRoundFinished)
                {
                    room.IsRoundFinished = true;
                    room.RoundWinner = player;
                    canPlayAgain = false;
                }

                // ✅ Növbə keçid
                if (!room.IsRoundFinished && !canPlayAgain)
                {
                    player.Status = PlayerStatus.Waiting;
                    ResetPassesAfterTilePlaced(room);
                    room.NextTurn();
                    SyncCurrentTurn(room);
                    FinishBlockedRoundIfNeeded(room);

                    if (!room.IsRoundFinished)
                    {
                        StartTurnTimer(roomId);
                    }
                }
                else if (canPlayAgain)
                {
                    Console.WriteLine($"🎯 {player.Name} keeps turn (DOUBLE + points)");
                    FinishBlockedRoundIfNeeded(room);

                    if (!room.IsRoundFinished)
                    {
                        StartTurnTimer(roomId);
                    }
                }
            }

            // ✅ Frontend-ə məlumat göndər
            if (moveSuccess)
            {
                object? centerDoubleData = null;
                List<object>? centerTopTilesData = null;
                List<object>? centerBottomTilesData = null;
                bool canUseCenterDouble = false;

                // ✅ DÜZƏLIŞ: Spinner həmişə göndər (bottom sayından asılı olmayaraq)
                if (room.GameType == "AllFives" && room.Chain.CenterDouble != null)
                {
                    int centerIndex = room.Chain.Tiles.FindIndex(t => t.Id == room.Chain.CenterDouble.Id);
                    bool centerIsInMiddle = centerIndex > 0 && centerIndex < room.Chain.Tiles.Count - 1;
                    canUseCenterDouble = centerIsInMiddle;

                    if (canUseCenterDouble)
                    {
                        centerDoubleData = new
                        {
                            room.Chain.CenterDouble.Left,
                            room.Chain.CenterDouble.Right,
                            room.Chain.CenterDouble.Id
                        };

                        centerTopTilesData = room.Chain.CenterTop.Select(t => new { t.Left, t.Right, t.Id } as object).ToList();
                        centerBottomTilesData = room.Chain.CenterBottom.Select(t => new { t.Left, t.Right, t.Id } as object).ToList();

                        Console.WriteLine($"🎯 4-WAY CENTER ACTIVE! (top: {room.Chain.CenterTop.Count}, bottom: {room.Chain.CenterBottom.Count})");
                    }
                }

                var responseData = new
                {
                    playerName = player!.Name,
                    tile = new { tile!.Left, tile.Right, tile.Id },
                    side,
                    leftEnd = room.Chain.LeftEnd,
                    rightEnd = room.Chain.RightEnd,
                    centerDouble = centerDoubleData,
                    centerTopTiles = centerTopTilesData,
                    centerBottomTiles = centerBottomTilesData,
                    canUseCenterDouble,
                    earnedPoints,
                    canPlayAgain,
                    isDouble = tile.Left == tile.Right,
                    allChainTiles = room.Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList()
                };

                await Clients.Group(roomId).SendAsync("TilePlaced", responseData);
                await BroadcastPlayers(roomId);

                foreach (var p in room.Players)
                {
                    await SendGameState(roomId, p.ConnectionId);
                }

                if (room.IsRoundFinished)
                {
                    await HandleRoundEnd(roomId);
                }
            }
        }
        private int CalculateChainSum(DominoChain chain)
        {
            if (chain.Tiles.Count == 0) return 0;

            int centerIndex = chain.CenterDouble != null
                ? chain.Tiles.FindIndex(t => t.Id == chain.CenterDouble.Id)
                : -1;

            bool centerIsInMiddle = centerIndex > 0 && centerIndex < chain.Tiles.Count - 1;

            if (centerIsInMiddle && chain.CenterDouble != null)
            {
                // 🎯 4-WAY SPINNER ACTIVE
                int sum = 0;
                List<string> activeEnds = new();

                // Sol uc
                if (chain.LeftEnd.HasValue)
                {
                    var leftTile = chain.Tiles[0];
                    int leftValue;

                    // ✅ DÜZƏLIŞ: Double = Left + Right (nə 2x, nə də Left*Right)
                    if (leftTile.Left == leftTile.Right)
                    {
                        leftValue = leftTile.Left + leftTile.Right; // [4|4] = 8
                    }
                    else
                    {
                        leftValue = chain.LeftEnd.Value;
                    }

                    sum += leftValue;
                    activeEnds.Add($"L={leftValue}");
                }

                // Sağ uc
                if (chain.RightEnd.HasValue)
                {
                    var rightTile = chain.Tiles[^1];
                    int rightValue;

                    // ✅ DÜZƏLIŞ: Double = Left + Right
                    if (rightTile.Left == rightTile.Right)
                    {
                        rightValue = rightTile.Left + rightTile.Right; // [5|5] = 10
                    }
                    else
                    {
                        rightValue = chain.RightEnd.Value;
                    }

                    sum += rightValue;
                    activeEnds.Add($"R={rightValue}");
                }

                // Top uc
                if (chain.CenterTop.Count > 0)
                {
                    var topTile = chain.CenterTop[^1];
                    int topValue;

                    // ✅ DÜZƏLIŞ: Double = Left + Right
                    if (topTile.Left == topTile.Right)
                    {
                        topValue = topTile.Left + topTile.Right;
                    }
                    else
                    {
                        topValue = chain.GetTopEnd(topTile);
                    }

                    sum += topValue;
                    activeEnds.Add($"T={topValue}");
                }

                // Bottom uc
                if (chain.CenterBottom.Count > 0)
                {
                    var bottomTile = chain.CenterBottom[^1];
                    int bottomValue;

                    // ✅ DÜZƏLIŞ: Double = Left + Right
                    if (bottomTile.Left == bottomTile.Right)
                    {
                        bottomValue = bottomTile.Left + bottomTile.Right;
                    }
                    else
                    {
                        bottomValue = chain.GetBottomEnd(bottomTile);
                    }

                    sum += bottomValue;
                    activeEnds.Add($"B={bottomValue}");
                }

                Console.WriteLine($"📊 4-WAY: {string.Join(" + ", activeEnds)} = {sum}");
                return sum;
            }
            else
            {
                // 🎯 2-WAY
                if (chain.Tiles.Count == 1 && chain.Tiles[0].Left == chain.Tiles[0].Right)
                {
                    var firstDoubleValue = chain.Tiles[0].Left + chain.Tiles[0].Right;
                    Console.WriteLine($"📊 2-way first double: [{chain.Tiles[0].Left}|{chain.Tiles[0].Right}] = {firstDoubleValue}");
                    return firstDoubleValue;
                }

                int sum = 0;

                // Sol uc
                if (chain.LeftEnd.HasValue && chain.Tiles.Count > 0)
                {
                    var leftTile = chain.Tiles[0];
                    // ✅ DÜZƏLIŞ: Double = Left + Right
                    if (leftTile.Left == leftTile.Right)
                    {
                        sum += leftTile.Left + leftTile.Right;
                    }
                    else
                    {
                        sum += chain.LeftEnd.Value;
                    }
                }

                // Sağ uc
                if (chain.RightEnd.HasValue && chain.Tiles.Count > 0)
                {
                    var rightTile = chain.Tiles[^1];
                    // ✅ DÜZƏLIŞ: Double = Left + Right
                    if (rightTile.Left == rightTile.Right)
                    {
                        sum += rightTile.Left + rightTile.Right;
                    }
                    else
                    {
                        sum += chain.RightEnd.Value;
                    }
                }

                Console.WriteLine($"📊 2-way: L={chain.LeftEnd} R={chain.RightEnd} sum = {sum}");
                return sum;
            }
        }

        public async Task TakeFromStock()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            _roomManager.UpdateRoomActivity(roomId);

            // ✅ AllFives üçün ayrı metodla işlə
            if (room.GameType == "AllFives")
            {
                await TakeFromStockAllFives(roomId, room);
                return;
            }

            // ✅ Classic101 və Quick5 üçün BAZAR YOXDURemek sistemi
            await TakeFromStockClassicQuick(roomId, room);
        }

        // ✅ YENİ METOD: Classic101 və Quick5 üçün bazar sistemi
        private async Task TakeFromStockClassicQuick(string roomId, DominoRoom room)
        {
            DominoPlayer? player;
            DominoTile? drawnTile = null;
            bool passed = false;
            bool foundPlayable = false;

            lock (room.StateLock)
            {
                if (room.IsRoundFinished) return;

                player = room.GetPlayer(Context.ConnectionId);
                if (player == null || room.CurrentPlayerId != Context.ConnectionId)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Sizin növbəniz deyil!");
                    return;
                }

                if (room.Chain.Tiles.Count == 0)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "İlk daşı əvvəlcə qoyun!");
                    return;
                }

                // ✅ ƏVVƏLDƏN: Oyunçunun əli boş mu, yoxsa qoya biləcəyi daş var mı?
                bool hasPlayableTile = player.Hand.Any(t =>
                {
                    var (canLeft, canRight, _, _) = room.Chain.CanPlace(t);
                    return canLeft || canRight;
                });

                // ✅ ƏGƏR ƏLİNDE QOYACAQ DAŞ VARSA - ERROR!
                if (hasPlayableTile)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Əlinizdə qoya biləcəyiniz daş var! Əvvəlcə onu qoyun.");
                    return;  // ✅ ÇIKIŞ - PAS KEÇƏ BİLMƏZ!
                }

                // ✅ BU NOQTƏYƏ GƏLƏNSƏ: QOYACAQ DAŞ YOXDUR!
                // → PAS KEÇƏ BİLƏR!

                // ✅ Quick5-də BAZAR YOXDUR → PAS KEÇƏ MƏCBUR!
                if (room.GameType == "Quick5")
                {
                    player.HasPassed = true;
                    passed = true;
                    Console.WriteLine($"⏭️ {player.Name} PAS keçdi (Quick5 - bazar yoxdur)");

                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);

                    FinishBlockedRoundIfNeeded(room);
                }
                // ✅ Classic101-də TƏK-TƏK çək
                else if (room.GameType == "Classic101")
                {
                    if (room.CanDrawFromStock())
                    {
                        drawnTile = room.Stock[0];
                        room.Stock.RemoveAt(0);
                        player.Hand.Add(drawnTile);

                        var (canLeft, canRight, _, _) = room.Chain.CanPlace(drawnTile);
                        foundPlayable = canLeft || canRight;

                        if (foundPlayable)
                        {
                            Console.WriteLine($"✅ {player.Name} çəkdi və oynaya bilər: [{drawnTile.Left}|{drawnTile.Right}]");
                        }
                        else
                        {
                            Console.WriteLine($"⏭️ {player.Name} çəkdi amma oynaya bilməz: [{drawnTile.Left}|{drawnTile.Right}], bazar: {room.Stock.Count}");
                        }
                    }
                    else
                    {
                        player.HasPassed = true;
                        passed = true;
                        Console.WriteLine($"⏭️ {player.Name} PAS keçdi (bazar boşdur və ya son 1 daş saxlanılır)");

                        player.Status = PlayerStatus.Waiting;
                        room.NextTurn();
                        SyncCurrentTurn(room);
                    }
                }

                FinishBlockedRoundIfNeeded(room);
            }

            await Clients.Group(roomId).SendAsync("TileDrawn", new
            {
                playerName = player!.Name,
                passed,
                stockRemaining = room.Stock.Count,
                drawnCount = drawnTile != null ? 1 : 0,
                drawnTile = drawnTile != null ? new { drawnTile.Left, drawnTile.Right, drawnTile.Id } : null,
                foundPlayable
            });

            if (!room.IsRoundFinished)
            {
                StartTurnTimer(roomId);
            }

            await BroadcastPlayers(roomId);

            foreach (var p in room.Players)
            {
                await SendGameState(roomId, p.ConnectionId);
            }

            if (room.IsRoundFinished)
            {
                await HandleRoundEnd(roomId);
            }
        }
        // ✅ YENİ METOD: AllFives üçün bazar sistemi (4 tərəf yoxlanır)
        private async Task TakeFromStockAllFives(string roomId, DominoRoom room)
        {
            DominoPlayer? player;
            DominoTile? drawnTile = null;
            bool passed = false;
            bool foundPlayable = false;

            lock (room.StateLock)
            {
                if (room.IsRoundFinished) return;

                player = room.GetPlayer(Context.ConnectionId);
                if (player == null || room.CurrentPlayerId != Context.ConnectionId)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Sizin növbəniz deyil!");
                    return;
                }

                if (room.Chain.Tiles.Count == 0)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "İlk daşı əvvəlcə qoyun!");
                    return;
                }

                bool hasPlayableTile = player.Hand.Any(t =>
                {
                    var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(t);
                    return canLeft || canRight || canCenterTop || canCenterBottom;
                });

                if (hasPlayableTile)
                {
                    _ = Clients.Caller.SendAsync("MoveError", "Əlinizdə qoya biləcəyiniz daş var! Əvvəlcə onu qoyun.");
                    return;
                }

                // ✅ 4 nəfərlik AllFives-də BAZAR YOXDUR
                if (room.PlayerCount == 4)
                {
                    player.HasPassed = true;
                    passed = true;
                    Console.WriteLine($"⏭️ {player.Name} PAS keçdi (AllFives 4P - bazar yoxdur)");

                    player.Status = PlayerStatus.Waiting;
                    room.NextTurn();
                    SyncCurrentTurn(room);
                }
                // ✅ 2-3 nəfərlik AllFives-də TƏK-TƏK çək
                else
                {
                    if (room.CanDrawFromStock())
                    {
                        drawnTile = room.Stock[0];
                        room.Stock.RemoveAt(0);
                        player.Hand.Add(drawnTile);

                        var (canLeft, canRight, canCenterTop, canCenterBottom) = room.Chain.CanPlace(drawnTile);
                        foundPlayable = canLeft || canRight || canCenterTop || canCenterBottom;

                        if (foundPlayable)
                        {
                            Console.WriteLine($"✅ {player.Name} çəkdi və oynaya bilər: [{drawnTile.Left}|{drawnTile.Right}]");
                        }
                        else
                        {
                            Console.WriteLine($"⏭️ {player.Name} çəkdi amma oynaya bilməz: [{drawnTile.Left}|{drawnTile.Right}], bazar: {room.Stock.Count}");
                        }
                    }
                    else
                    {
                        // Bazar boşdur və ya son 1 daş saxlanılır - PAS keç
                        player.HasPassed = true;
                        passed = true;
                        Console.WriteLine($"⏭️ {player.Name} PAS keçdi (bazar boşdur və ya son 1 daş saxlanılır)");

                        player.Status = PlayerStatus.Waiting;
                        room.NextTurn();
                        SyncCurrentTurn(room);
                    }
                }

                FinishBlockedRoundIfNeeded(room);
            }

            await Clients.Group(roomId).SendAsync("TileDrawn", new
            {
                playerName = player!.Name,
                passed,
                stockRemaining = room.Stock.Count,
                drawnCount = drawnTile != null ? 1 : 0,
                drawnTile = drawnTile != null ? new { drawnTile.Left, drawnTile.Right, drawnTile.Id } : null,
                foundPlayable
            });

            if (!room.IsRoundFinished)
            {
                StartTurnTimer(roomId);
            }

            await BroadcastPlayers(roomId);

            foreach (var p in room.Players)
            {
                await SendGameState(roomId, p.ConnectionId);
            }

            if (room.IsRoundFinished)
            {
                await HandleRoundEnd(roomId);
            }
        }

        private async Task HandleRoundEnd(string roomId)
        {
            StopTurnTimer(roomId);
            StopAutoPassTimer(roomId);

            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.RoundWinner == null) return;

            int earnedPoints = 0;
            bool gameFinished = false;
            int finishedRound;
            string winnerName;

            // 🔥 YENİ: Oyunçuların əl məlumatlarını topla
            List<object> allPlayerHands = new();
            List<object> allScores = new();

            lock (room.StateLock)
            {
                if (room.IsRoundEndProcessing && room.RoundEndProcessingRound == room.CurrentRound)
                {
                    Console.WriteLine($"ℹ️ Round end already processing for {roomId} R{room.CurrentRound}");
                    return;
                }

                room.IsRoundEndProcessing = true;
                room.RoundEndProcessingRound = room.CurrentRound;
                finishedRound = room.CurrentRound;

                // QUICK5 məntiq...
                if (room.GameType == "Quick5")
                {
                    bool allPlayersPassed = room.Players.All(p => p.HasPassed);

                    if (!allPlayersPassed)
                    {
                        foreach (var p in room.Players)
                        {
                            if (p.UserId != room.RoundWinner.UserId)
                            {
                                if (p.IsSystemControlled)
                                {
                                    Console.WriteLine($"🤖 Quick5: {p.Name} system-controlled olduğu üçün məğlub xalı yazılmadı");
                                    continue;
                                }

                                int handValue = p.GetHandValue();
                                p.Score += handValue;
                                Console.WriteLine($"📉 Quick5: {p.Name} +{handValue} xal (məğlub, total: {p.Score})");
                            }
                        }
                    }

                    var playersOver51 = room.Players.Where(p => p.Score >= 51).ToList();

                    if (playersOver51.Any())
                    {
                        var trueWinner = room.Players.OrderBy(p => p.Score).First();
                        room.RoundWinner = trueWinner;
                        gameFinished = true;
                        Console.WriteLine($"🏆 Quick5 GAME OVER: {trueWinner.Name} qazandı (ən az xal: {trueWinner.Score})");
                    }
                }
                // Classic101 və AllFives məntiq...
                else if (room.GameType == "Classic101" || room.GameType == "AllFives")
                {
                    earnedPoints = room.Players
                        .Where(p => p.UserId != room.RoundWinner.UserId)
                        .Sum(p => p.GetHandValue());

                    if (room.GameType == "AllFives")
                    {
                        int remainder = earnedPoints % 5;
                        if (remainder > 0)
                        {
                            earnedPoints += (5 - remainder);
                        }
                    }

                    room.RoundWinner.Score += earnedPoints;
                    _ = BroadcastPlayers(roomId);
                    gameFinished = room.RoundWinner.Score >= room.ScoreToWin;

                    Console.WriteLine($"📈 {room.GameType} R{room.CurrentRound}: {room.RoundWinner.Name} +{earnedPoints} xal (Total: {room.RoundWinner.Score}/{room.ScoreToWin})");
                }

                // 🔥 YENİ: Bütün oyunçuların əl məlumatlarını topla
                allPlayerHands = room.Players.Select(p => new
                {
                    name = p.Name,
                    tiles = p.Hand.Select(t => $"{t.Left}|{t.Right}").ToList(),
                    handValue = p.GetHandValue(),
                    isWinner = p.UserId == room.RoundWinner.UserId
                } as object).ToList();

                winnerName = room.RoundWinner.Name;
                allScores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isWinner = p.UserId == room.RoundWinner.UserId
                } as object).ToList();
            }

            // ✅ Raund nəticəsini bildir (DAŞ MƏLUMATLARI DAXİL)
            await _hubContext.Clients.Group(roomId).SendAsync("RoundFinished", new
            {
                winnerName,
                pointsEarned = earnedPoints,
                round = finishedRound,
                gameFinished = gameFinished,
                gameType = room.GameType,
                scoreToWin = room.ScoreToWin,
                allPlayerHands = allPlayerHands, // 🔥 DAŞ MƏLUMATLARI
                allScores,
                message = $"🏆 {winnerName} raund {finishedRound}-u qazandı! (+{earnedPoints} xal)"
            });

            try
            {
                await Task.Delay(5000);

                room = _roomManager.GetRoom(roomId);
                if (room == null) return;

                lock (room.StateLock)
                {
                    if (!room.IsRoundFinished || room.CurrentRound != finishedRound)
                    {
                        Console.WriteLine($"⏳ Round-end delay ignored for {roomId}: round already advanced");
                        return;
                    }
                }

                if (gameFinished)
                {
                    Console.WriteLine("🏆 Game is FINISHED, proceeding to HandleGameEnd...");
                    await HandleGameEnd(roomId);
                }
                else
                {
                    Console.WriteLine("⏳ 5 second Auto-starting next round...");
                    await StartNewRound(roomId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Round transition error for {roomId}: {ex.Message}");
                var currentRoom = _roomManager.GetRoom(roomId);
                if (currentRoom != null)
                {
                    lock (currentRoom.StateLock)
                    {
                        currentRoom.IsRoundEndProcessing = false;
                        currentRoom.RoundEndProcessingRound = 0;
                    }
                }
            }
        }
        public async Task ReadyForNextRound()
        {
            var userId = GetUserId();
            var roomId = GetCurrentRoom();

            if (string.IsNullOrEmpty(roomId) || userId == 0) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            bool allReady = false;

            lock (room.StateLock)
            {
                if (!room.IsRoundFinished) return;

                room.ReadyPlayers.Add(userId);
                Console.WriteLine($"✅ {userId} ready for next round ({room.ReadyPlayers.Count}/{room.Players.Count})");

                // Hamı hazırdırmı?
                allReady = room.ReadyPlayers.Count >= room.Players.Count;
            }

            // ✅ Hamı hazırdırsa növbəti raunda keç
            if (allReady)
            {
                Console.WriteLine($"🎮 All players ready, starting round {room.CurrentRound + 1}");
                int readyRound = room.CurrentRound;
                await Task.Delay(500);

                room = _roomManager.GetRoom(roomId);
                if (room == null) return;

                lock (room.StateLock)
                {
                    if (!room.IsRoundFinished || room.CurrentRound != readyRound)
                    {
                        Console.WriteLine($"✅ Ready ignored for {roomId}: round already advanced");
                        return;
                    }
                }

                await StartNewRound(roomId);
            }
        }
        // 🔥 Növbəti raunda keçid
        private async Task StartNewRound(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            int previousWinnerIndex = -1;

            lock (room.StateLock)
            {
                // ✅ DÜZƏLIŞ 2: ƏVVƏLKİ QALİBİ NULL ETMƏZDƏN ƏVVƏL SAXLA!
                if (room.RoundWinner != null)
                {
                    previousWinnerIndex = room.Players.FindIndex(p => p.UserId == room.RoundWinner.UserId);
                    Console.WriteLine($"🏆 Previous ROUND winner: {room.RoundWinner.Name} (index: {previousWinnerIndex})");
                }

                room.CurrentRound++;
                room.IsRoundFinished = false;
                room.IsRoundEndProcessing = false;
                room.RoundEndProcessingRound = 0;
                room.ForceOpeningRuleAfterLeave = false;
                room.Chain.Tiles.Clear();
                room.Chain.LeftEnd = null;
                room.Chain.RightEnd = null;
                room.Chain.CenterDouble = null;
                room.Chain.CenterTop.Clear();
                room.Chain.CenterBottom.Clear();

                room.ReadyPlayers.Clear();
                room.RoundWinner = null; // ✅ İNDİ NULL-A ÇEVİRİRİK

                int tilesPerPlayer = room.GameType == "Quick5" ? 5 : 7;
                var (stock, hands) = DominoGameGenerator.DealTiles(room.Players.Count, tilesPerPlayer);

                room.Stock = stock;

                for (int i = 0; i < room.Players.Count; i++)
                {
                    room.Players[i].Hand = hands[i];
                    room.Players[i].Status = PlayerStatus.Waiting;
                    room.Players[i].HasPassed = false;
                }

                // ✅ NÖVBƏ TƏYİNİ: Classic101 Raund 2+ - əvvəlki qalib başlayır
                int startIndex;

                // 🔥 QUICK5: HƏR RAUNDDA ən böyük double
                if (room.GameType == "Quick5")
                {
                    // HƏMIŞƏ ən böyük double başlayır!
                    startIndex = FindStartingPlayerForRound(room, isFirstRound: false);
                    Console.WriteLine($"✅ Quick5 R{room.CurrentRound}: Ən böyük double başlayır!");
                }
                // Classic101 Raund 2+ - əvvəlki qalib başlayır
                else if (room.GameType == "Classic101" && room.CurrentRound > 1 && previousWinnerIndex >= 0)
                {
                    // ✅ Classic101 R2+: ƏVVƏLKİ QALİB başlayır
                    startIndex = previousWinnerIndex;
                    Console.WriteLine($"✅ Classic101 R{room.CurrentRound}: {room.Players[startIndex].Name} (previous WINNER) starts first");
                }
                // AllFives Raund 2+ - əvvəlki qalib başlayır
                else if (room.GameType == "AllFives" && room.CurrentRound > 1 && previousWinnerIndex >= 0)
                {
                    // ✅ AllFives R2+: ƏVVƏLKİ QALİB başlayır
                    startIndex = previousWinnerIndex;
                    Console.WriteLine($"✅ AllFives R{room.CurrentRound}: {room.Players[startIndex].Name} (previous WINNER) starts first");
                }
                else
                {
                    // ✅ Digər hallarda: ən böyük/kiçik double
                    startIndex = FindStartingPlayerForRound(room, isFirstRound: (room.CurrentRound == 1));
                }

                room.CurrentPlayerIndex = startIndex;
                room.Players[startIndex].Status = PlayerStatus.Playing;
                room.CurrentTurnUserId = room.Players[startIndex].UserId;

                var startingPlayer = room.Players[startIndex];

                if (room.GameType == "Classic101" && room.CurrentRound > 1)
                {
                    Console.WriteLine($"✅ Classic101 Round {room.CurrentRound}: {startingPlayer.Name} (previous ROUND winner) can play ANY tile");
                }
                else if (room.GameType == "Quick5")
                {
                    var biggestDouble = FindBiggestDouble(startingPlayer.Hand);
                    Console.WriteLine($"✅ Quick5 Round {room.CurrentRound}: {startingPlayer.Name} MUST play [{biggestDouble?.Left}|{biggestDouble?.Right}]");
                }
                else if (room.GameType == "AllFives" && room.CurrentRound > 1)
                {
                    Console.WriteLine($"✅ AllFives Round {room.CurrentRound}: {startingPlayer.Name} (previous ROUND winner) can place ANY tile");
                }
            }

            string startMessage = room.GameType == "Classic101" && room.CurrentRound > 1
                ? $"🎮 Raund {room.CurrentRound} | {room.GetCurrentPlayer()?.Name} başlayır (əvvəlki qalib - istədiyi daşı qoya bilər)"
                : room.GameType == "Quick5"
                ? $"🎮 Raund {room.CurrentRound} | {room.GetCurrentPlayer()?.Name} başlayır (ən böyük double MƏCBURI)"
                : $"🎮 Raund {room.CurrentRound} | {room.GetCurrentPlayer()?.Name} başlayır";

            await _hubContext.Clients.Group(roomId).SendAsync("NewRoundStarted", new
            {
                round = room.CurrentRound,
                startPlayer = room.GetCurrentPlayer()?.Name,
                message = startMessage,
                gameType = room.GameType
            });

            StartTurnTimer(roomId);

            foreach (var player in room.Players)
            {
                await SendGameState(roomId, player.ConnectionId);
            }
        }
        //Raunda görə başlayanı tap
        private int FindStartingPlayerForRound(DominoRoom room, bool isFirstRound)
        {
            // 🔥 CLASSIC101
            if (room.GameType == "Classic101")
            {
                if (isFirstRound)
                {
                    for (int val = 1; val <= 6; val++)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 Classic101 R1: {room.Players[i].Name} MUST start with [{val}|{val}]");
                                return i;
                            }
                        }
                    }

                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                        {
                            Console.WriteLine($"🎯 Classic101 R1: {room.Players[i].Name} MUST start with [0|0]");
                            return i;
                        }
                    }
                }
                else
                {
                    if (room.RoundWinner != null)
                    {
                        int winnerIndex = room.Players.FindIndex(p => p.UserId == room.RoundWinner.UserId);
                        if (winnerIndex >= 0)
                        {
                            Console.WriteLine($"🎯 Classic101 R{room.CurrentRound}: Winner {room.Players[winnerIndex].Name} can play ANY tile");
                            return winnerIndex;
                        }
                    }
                }
            }

            // 🔥 QUICK5: HƏR RAUND ən böyük double MƏCBURI (yoxdursa RANDOM)
            if (room.GameType == "Quick5")
            {
                for (int val = 6; val >= 1; val--)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                        {
                            Console.WriteLine($"🎯 Quick5 R{room.CurrentRound}: {room.Players[i].Name} MUST start with [{val}|{val}]");
                            return i;
                        }
                    }
                }

                for (int i = 0; i < room.Players.Count; i++)
                {
                    if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                    {
                        Console.WriteLine($"🎯 Quick5: {room.Players[i].Name} MUST start with [0|0]");
                        return i;
                    }
                }

                // 🔥 YENİ: Heç kimdə double yoxdursa RANDOM oyunçu
                var rnd = new Random();
                int randomIndex = rnd.Next(0, room.Players.Count);
                Console.WriteLine($"🎲 Quick5: Heç kimdə double yoxdur! RANDOM: {room.Players[randomIndex].Name} başlayır");
                return randomIndex;
            }

            // 🔥 ALLFIVES
            if (room.GameType == "AllFives")
            {
                if (isFirstRound)
                {
                    for (int i = 0; i < room.Players.Count; i++)
                    {
                        if (room.Players[i].Hand.Any(t =>
                            (t.Left == 2 && t.Right == 3) || (t.Left == 3 && t.Right == 2)))
                        {
                            Console.WriteLine($"🎯 AllFives R1: {room.Players[i].Name} MUST start with [2|3]");
                            return i;
                        }
                    }

                    for (int val = 1; val <= 6; val++)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 AllFives R1: {room.Players[i].Name} starts with [{val}|{val}]");
                                return i;
                            }
                        }
                    }
                }
                else
                {
                    if (room.RoundWinner != null)
                    {
                        int winnerIndex = room.Players.FindIndex(p => p.UserId == room.RoundWinner.UserId);
                        if (winnerIndex >= 0)
                        {
                            Console.WriteLine($"🎯 AllFives R{room.CurrentRound}: Previous ROUND winner {room.Players[winnerIndex].Name} starts");
                            return winnerIndex;
                        }
                    }

                    for (int val = 6; val >= 1; val--)
                    {
                        for (int i = 0; i < room.Players.Count; i++)
                        {
                            if (room.Players[i].Hand.Any(t => t.Left == val && t.Right == val))
                            {
                                Console.WriteLine($"🎯 AllFives R{room.CurrentRound}: {room.Players[i].Name} starts with [{val}|{val}]");
                                return i;
                            }
                        }
                    }
                }

                for (int i = 0; i < room.Players.Count; i++)
                {
                    if (room.Players[i].Hand.Any(t => t.Left == 0 && t.Right == 0))
                    {
                        return i;
                    }
                }
            }

            return 0;
        }

        private void ReassignOpeningTurnAfterLeave(DominoRoom room)
        {
            if (room.Players.Count == 0)
            {
                room.CurrentPlayerIndex = 0;
                room.CurrentTurnUserId = -1;
                room.ForceOpeningRuleAfterLeave = false;
                return;
            }

            int startIndex = room.GameType switch
            {
                "Classic101" => FindStartingPlayerForRound(room, isFirstRound: true),
                "AllFives" => FindStartingPlayerForRound(room, isFirstRound: true),
                _ => room.CurrentPlayerIndex >= room.Players.Count ? 0 : room.CurrentPlayerIndex
            };

            room.CurrentPlayerIndex = startIndex;
            room.CurrentTurnUserId = room.Players[startIndex].UserId;
            room.ForceOpeningRuleAfterLeave = room.GameType == "Classic101" || room.GameType == "AllFives";

            foreach (var existingPlayer in room.Players)
            {
                existingPlayer.Status = existingPlayer.UserId == room.CurrentTurnUserId
                    ? PlayerStatus.Playing
                    : PlayerStatus.Waiting;
            }

            Console.WriteLine($"🎯 Leave recalculated starter: {room.Players[startIndex].Name} ({room.GameType})");
        }
        private async Task HandleGameEnd(string roomId, List<int>? forcedLoserIds = null, int? prizePlayerCountOverride = null)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            decimal totalPrize = room.EntryFee * (prizePlayerCountOverride ?? room.Players.Count);
            List<int> winnerIds = new();
            List<int> loserIds = new();
            string winnerNames = "";
            List<object> allScores = new();
            int winnerCount = 0;
            int systemWinnerCount = 0;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ Domino HandleGameEnd skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                List<DominoPlayer> winners;

                // ✅ QUICK5: EN AZ XALI EYNİ OLANLAR POTU BÖLÜŞÜR
                if (room.GameType == "Quick5")
                {
                    int minimumScore = room.Players.Min(p => p.Score);
                    winners = room.Players
                        .Where(p => p.Score == minimumScore)
                        .ToList();

                    Console.WriteLine($"🏆 Quick5: {string.Join(", ", winners.Select(p => p.Name))} qazandı " +
                                      $"(ən az xal: {minimumScore}, qalib sayı: {winners.Count})");
                }
                // ✅ DİGƏR OYUNLAR: EN ÇOX XALI OLAN QAZANIR
                else
                {
                    var winner = room.Players.OrderByDescending(p => p.Score).First();
                    winners = new List<DominoPlayer> { winner };
                    Console.WriteLine($"🏆 {room.GameType}: {winner.Name} qazandı (ən çox xal: {winner.Score})");
                }

                winnerCount = winners.Count;
                systemWinnerCount = winners.Count(p => p.IsSystemControlled);
                winnerIds = winners
                    .Where(p => !p.IsSystemControlled)
                    .Select(p => p.UserId)
                    .Distinct()
                    .ToList();
                winnerNames = string.Join(", ", winners.Select(p => p.Name));
                var winnerUserIds = winners.Select(p => p.UserId).ToHashSet();

                loserIds = (forcedLoserIds ?? room.Players
                    .Where(p => !p.IsSystemControlled && !winnerUserIds.Contains(p.UserId))
                    .Select(p => p.UserId))
                    .Distinct()
                    .ToList();

                // ✅ QUICK5: EN AZ XAL ƏN YUXARIDA
                if (room.GameType == "Quick5")
                {
                    allScores = room.Players
                        .OrderBy(p => p.Score)
                        .Select(p => new
                        {
                            name = p.Name,
                            score = p.Score,
                            isWinner = winnerUserIds.Contains(p.UserId),
                            isSystemControlled = p.IsSystemControlled
                        } as object)
                        .ToList();
                }
                // ✅ DİGƏR OYUNLAR: EN ÇOX XAL ƏN YUXARIDA
                else
                {
                    allScores = room.Players
                        .OrderByDescending(p => p.Score)
                        .Select(p => new
                        {
                            name = p.Name,
                            score = p.Score,
                            isWinner = winnerUserIds.Contains(p.UserId),
                            isSystemControlled = p.IsSystemControlled
                        } as object)
                        .ToList();
                }
            }

            // 💰 GƏLİR HESABLAMA (20% komissiya, 80% qalana)
            decimal platformFee = totalPrize * 0.20m;
            decimal remainingPrize = totalPrize - platformFee;
            decimal rewardPerWinner = winnerCount > 0 ? remainingPrize / winnerCount : 0m;
            decimal systemAmount = rewardPerWinner * systemWinnerCount;
            decimal displayReward = rewardPerWinner;
            bool systemWon = systemWinnerCount > 0;

            Console.WriteLine($"💰 Prize Pool: {totalPrize} coin");
            Console.WriteLine($"💰 Platform Fee: {platformFee} coin");
            Console.WriteLine($"💰 Reward per winner ({winnerCount} winner): {rewardPerWinner} coin");

            // 🎖️ RANK-A UYĞUN GameType
            GameType gameType = room.GameType switch
            {
                "Classic101" => GameType.Domino,
                "Quick5" => GameType.Domino,
                "AllFives" => GameType.Domino,
                _ => GameType.Domino
            };

            using var scopeContext = CreateBackgroundScope();
            var scopedDb = scopeContext.ServiceProvider.GetRequiredService<BlogAppDbContext>();
            var scopedRankService = scopeContext.ServiceProvider.GetRequiredService<IRankService>();

            // ✅ QALİBİN BALANS VƏ RANKINI YENILƏ
            foreach (var winnerId in winnerIds)
            {
                var user = await scopedDb.Users.FindAsync(winnerId);
                if (user != null)
                {
                    user.Balance += rewardPerWinner;

                    await scopedRankService.UpdateRankAfterGame(
                        userId: winnerId,
                        gameType: gameType,
                        isWin: true,
                        earnings: rewardPerWinner
                    );

                    // 🔥 Frontend-ə balance update
                    var winnerConnId = room.Players.FirstOrDefault(p => p.UserId == winnerId)?.ConnectionId;
                    if (winnerConnId != null)
                    {
                        await _hubContext.Clients.Client(winnerConnId).SendAsync("UpdateBalance", user.Balance);
                    }

                    Console.WriteLine($"💰 {user.Name} won: {rewardPerWinner:F2} coin (after 20% fee) + Rank UP!");
                }
            }

            // ❌ MƏĞLUBLARIN RANKINI YENILƏ
            foreach (var loserId in loserIds)
            {
                Console.WriteLine($"🔍 LOSS - UserId: {loserId}, EntryFee: {room.EntryFee}"); // ← əlavə et
                await scopedRankService.UpdateRankAfterGame(
                    userId: loserId,
                    gameType: gameType,
                    isWin: false,
                    earnings: room.EntryFee
                );

                Console.WriteLine($"📉 User {loserId} rank updated (loss)");
            }

            await scopedDb.SaveChangesAsync();

            // ✅ DÜZƏLIŞ: FİNAL XALLARI YENILƏ (oyun bitəndə)
            await BroadcastPlayers(roomId, scopedDb);

            // 🏆 NƏTİCƏNİ BİLDİR
            await _hubContext.Clients.Group(roomId).SendAsync("GameFinished", new
            {
                message = winnerCount > 1
                    ? $"🏆 {winnerNames} eyni xalla qalib oldu. Komissiyadan sonra hərəsi {rewardPerWinner:F2} coin qazandı!"
                    : systemWon
                        ? $"🏆 {winnerNames} oyunu qazandı. Mükafat sistemə keçdi."
                        : $"🏆 {winnerNames} oyunu qazandı və {rewardPerWinner:F2} coin əldə etdi!",
                winners = winnerNames,
                winnerCount,
                reward = displayReward,
                winnerReward = displayReward,
                displayReward,
                systemAmount,
                platformFee = platformFee,
                totalPrize = totalPrize,
                systemWon,
                allScores
            });

            Console.WriteLine($"🏁 Game finished: {winnerNames} won {rewardPerWinner:F2} coin");

            // 🚪 LOBİYƏ YÖNLƏNDIR
            await _hubContext.Clients.Group(roomId).SendAsync("RedirectToLobby", new
            {
                message = "Oyun bitdi! Lobiyə yönləndirilirsiniz...",
                waitTime = 0
            });

            Console.WriteLine($"📤 Players redirected to lobby from {room.RoomName}");

            // 🗑️ OTAĞI SİL: hamını qruplardan çıxart və internal xəritəni təmizlə
            foreach (var player in room.Players.ToList())
            {
                try { await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId); } catch { }
                _userRooms.TryRemove(player.ConnectionId, out _);
            }

            _roomManager.DeleteRoom(roomId);
            await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
            Console.WriteLine($"🗑️ Room {roomId} deleted");
        }
        public async Task LeaveRoom()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            int userId = GetUserId();
            await RemovePlayerFromRoomAsync(
                roomId,
                userId,
                removeFromSignalRGroup: true,
                notifyCallerBalance: true,
                leaveReason: "manual_leave",
                leaveMessage: "otaqdan ayrıldı.");
        }
        public async Task ReconnectToRoom(string roomId)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                var activeRoom = _roomManager.GetRoomByUser(userId);
                if (activeRoom == null)
                {
                    Console.WriteLine($"❌ ReconnectToRoom: Room {roomId} not found");
                    await Clients.Caller.SendAsync("RoomClosed", "Otaq tapılmadı");
                    return;
                }

                Console.WriteLine($"ℹ️ ReconnectToRoom: stale room {roomId} ignored, using active room {activeRoom.RoomId}");
                room = activeRoom;
                roomId = activeRoom.RoomId;
            }

            DominoPlayer? player = null;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    Console.WriteLine($"❌ Player {userId} not found in room");
                    _ = Clients.Caller.SendAsync("RoomClosed", "Otaqda oyunçu tapılmadı");
                    return;
                }

                bool reconnectGraceExpired = player.DisconnectGraceDeadlineUtc.HasValue
                    && DateTime.UtcNow > player.DisconnectGraceDeadlineUtc.Value;

                if (player.IsSystemControlled || reconnectGraceExpired)
                {
                    Console.WriteLine($"❌ Player {userId} tried to reconnect after system takeover");
                    _ = Clients.Caller.SendAsync("SilentRoomClosed", new
                    {
                        reason = "system_takeover",
                        balance = user.Balance
                    });
                    return;
                }

                string oldConnId = player.ConnectionId;
                if (!string.IsNullOrWhiteSpace(oldConnId) && oldConnId != Context.ConnectionId)
                {
                    _userRooms.TryRemove(oldConnId, out _);
                }

                player.IsConnected = true;
                player.ConnectionId = Context.ConnectionId;
                player.DisconnectedAt = null;
                player.DisconnectGraceDeadlineUtc = null;

                Console.WriteLine($"🔄 {player.Name} reconnected: {oldConnId} → {Context.ConnectionId}");
            }

            room.CancelDisconnectTimer(userId);
            _roomManager.UpdateRoomActivity(roomId);

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            _userRooms[Context.ConnectionId] = roomId;

            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId = room.RoomId,
                roomName = room.RoomName,
                gameType = room.GameType,
                playerCount = room.PlayerCount,
                scoreToWin = room.ScoreToWin,
                currentPlayers = room.Players.Count,
                isGameStarted = room.IsGameStarted,
                profileImage = user.Image,
                balance = user.Balance
            });

            await Clients.Caller.SendAsync("UpdateBalance", user.Balance);

            await BroadcastPlayers(roomId);

            if (room.IsGameStarted)
            {
                if (room.CurrentTurnUserId == userId)
                {
                    StartTurnTimer(roomId);
                }

                await SendGameState(roomId, Context.ConnectionId);
            }

            Console.WriteLine($"✅ {player.Name} successfully reconnected to {room.RoomName}");
        }

        private int GetUserId()
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out int userId) ? userId : 0;
        }

        private string? GetCurrentRoom()
        {
            _userRooms.TryGetValue(Context.ConnectionId, out var roomId);
            return roomId;
        }

        private async Task BroadcastPlayers(string roomId, BlogAppDbContext? dbContext = null)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var db = dbContext ?? _db;

            // 🔥 User məlumatlarını al
            var playerIds = room.Players.Select(p => p.UserId).ToList();
            var users = await db.Users
                .Where(u => playerIds.Contains(u.Id))
                .Select(u => new { u.Id, u.Image })
                .ToListAsync();

            var players = room.Players.Select((p, index) => new
            {
                name = p.Name,
                tileCount = p.Hand.Count,
                score = p.Score,
                isCurrentTurn = p.ConnectionId == room.CurrentPlayerId,
                isConnected = p.IsConnected,
                isSystemControlled = p.IsSystemControlled,
                profileImage = users.FirstOrDefault(u => u.Id == p.UserId)?.Image,  // ✅ ƏLAVƏ
                teamIndex = -1
            }).ToList();

            await _hubContext.Clients.Group(roomId).SendAsync("PlayersList", players);
        }
        private async Task SendGameState(string roomId, string connectionId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.GetPlayer(connectionId);
            if (player == null) return;

            await _hubContext.Clients.Client(connectionId).SendAsync("GameState", new
            {
                myHand = player.Hand.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                chainTiles = room.Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                leftEnd = room.Chain.LeftEnd,
                rightEnd = room.Chain.RightEnd,
                centerDouble = room.Chain.CenterDouble != null
                    ? new { room.Chain.CenterDouble.Left, room.Chain.CenterDouble.Right, room.Chain.CenterDouble.Id }
                    : null,
                centerTopTiles = room.Chain.CenterTop.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                centerBottomTiles = room.Chain.CenterBottom.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                stockCount = room.Stock.Count,
                isMyTurn = player.UserId == room.CurrentTurnUserId,
                hasPlayableTile = room.PlayerHasPlayableTile(player),
                canDrawFromStock = room.CanDrawFromStockForPlayer(player),
                currentPlayerName = room.GetCurrentPlayer()?.Name,
                currentTurnUserId = room.CurrentTurnUserId,
                turnDeadlineUtc = room.TurnDeadlineUtc,
                turnStartedAtUtc = room.TurnStartedAtUtc,
                turnTimeRemaining = room.GetTurnTimeRemainingSeconds(),
                turnDurationSeconds = room.TurnDurationSeconds,
                isAutoPassTimer = room.IsAutoPassTurnTimer,
                currentRound = room.CurrentRound,
                gameType = room.GameType,
                scoreToWin = room.ScoreToWin,
                players = room.Players.Select(p => new
                {
                    userId = p.UserId,
                    name = p.Name,
                    tileCount = p.Hand.Count,
                    score = p.Score,
                    isCurrentTurn = p.ConnectionId == room.CurrentPlayerId,
                    isConnected = p.IsConnected,
                    isSystemControlled = p.IsSystemControlled
                }).ToList(),
                scores = room.Players.Select(p => p.Score).ToArray()
            });
        }
        public async Task SendQuickEmoji(string roomId, string emoji)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.GetPlayer(Context.ConnectionId);
            if (player == null) return;

            Console.WriteLine($"📤 {player.Name} sent emoji: {emoji} to room {roomId}");

            await Clients.Group(roomId).SendAsync("QuickEmoji", new
            {
                senderName = player.Name,
                emoji = emoji
            });
        }

        public async Task SendQuickMessage(string roomId, string message)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.GetPlayer(Context.ConnectionId);
            if (player == null) return;

            Console.WriteLine($"📤 {player.Name} sent message: '{message}' to room {roomId}");

            await Clients.Group(roomId).SendAsync("QuickMessage", new
            {
                senderName = player.Name,
                message = message
            });
        }

        private DominoTile? FindSmallestDouble(List<DominoTile> hand)
        {
            for (int val = 1; val <= 6; val++)
            {
                var tile = hand.FirstOrDefault(t => t.Left == val && t.Right == val);
                if (tile != null) return tile;
            }
            return hand.FirstOrDefault(t => t.Left == 0 && t.Right == 0);
        }

        private DominoTile? FindBiggestDouble(List<DominoTile> hand)
        {
            for (int val = 6; val >= 1; val--)
            {
                var tile = hand.FirstOrDefault(t => t.Left == val && t.Right == val);
                if (tile != null) return tile;
            }
            return hand.FirstOrDefault(t => t.Left == 0 && t.Right == 0);
        }
    }


    public static class DominoGameGenerator
    {
        // ✅ ƏSAS METOD: DealTiles
        public static (List<DominoTile> stock, List<List<DominoTile>> hands) DealTiles(int playerCount, int tilesPerPlayer)
        {
            bool isQuick5 = tilesPerPlayer == 5;

            // ✅ 2-3 nəfərlik TÜM MODLARDA double garantisi
            if (playerCount == 2 || playerCount == 3)
            {
                // Quick5 üçün Quick5 xüsusi paylanması
                if (isQuick5)
                {
                    return DealQuick5Tiles(playerCount, tilesPerPlayer);
                }

                // ✅ YENİ: Classic101 və AllFives (tilesPerPlayer = 7) üçün de double garantisi
                return DealNormalTilesWithDoubleGuarantee(playerCount, tilesPerPlayer);
            }

            // Normal paylanma (4 nəfərlik - double garantisi YOX)
            return DealNormalTiles(playerCount, tilesPerPlayer);
        }
        // ✅ QUICK5 (2-3 nəfərlik): GARANTILƏ BİR OYUNÇUYA DOUBLE
        private static (List<DominoTile> stock, List<List<DominoTile>> hands) DealQuick5Tiles(int playerCount, int tilesPerPlayer)
        {
            var allTiles = GenerateAllTiles();
            var rnd = new Random();

            int attempts = 0;
            const int maxAttempts = 100;

            while (attempts < maxAttempts)
            {
                allTiles = allTiles.OrderBy(_ => rnd.Next()).ToList();

                var hands = new List<List<DominoTile>>();
                int index = 0;

                // Əvvəlcə hər oyunçuya tilesPerPlayer sayda daş ver
                for (int i = 0; i < playerCount; i++)
                {
                    var hand = allTiles.Skip(index).Take(tilesPerPlayer).ToList();
                    hands.Add(hand);
                    index += tilesPerPlayer;
                }

                var stock = allTiles.Skip(index).ToList();

                // ✅ YOXLAMA: ƏN AZ BİR OYUNÇUNUN əlində double varmı?
                bool anyoneHasDouble = hands.Any(hand => hand.Any(t => t.Left == t.Right));

                if (anyoneHasDouble)
                {
                    var playerWithDouble = hands.FindIndex(hand => hand.Any(t => t.Left == t.Right));
                    var doubleInfo = hands[playerWithDouble].First(t => t.Left == t.Right);
                    Console.WriteLine($"✅ Quick5 {playerCount}P: Oyuncu {playerWithDouble + 1} əlində double var [{doubleInfo.Left}|{doubleInfo.Right}] ✓");
                    return (stock, hands);
                }

                attempts++;
            }

            // ❌ Uğursuz olsa (praktiki olaraq mümkün deyil), force etməli
            Console.WriteLine($"⚠️ {maxAttempts} cəhddən sonra da double tapıla bilmədi. FORCE ediliyor...");
            return DealQuick5TilesForced(playerCount, tilesPerPlayer);
        }

        // ✅ FORCE: Quick5 - Bir oyunçuya double veriliir, qalanlar random
        private static (List<DominoTile> stock, List<List<DominoTile>> hands) DealQuick5TilesForced(int playerCount, int tilesPerPlayer)
        {
            var allTiles = GenerateAllTiles();
            var rnd = new Random();
            allTiles = allTiles.OrderBy(_ => rnd.Next()).ToList();

            var hands = new List<List<DominoTile>>();

            // Random oyunçu seç (double alacaq)
            int luckyPlayerIndex = rnd.Next(0, playerCount);

            // Doubles-ları ayır
            var doubles = allTiles.Where(t => t.Left == t.Right).ToList();
            var nonDoubles = allTiles.Where(t => t.Left != t.Right).ToList();

            int index = 0;

            for (int i = 0; i < playerCount; i++)
            {
                var hand = new List<DominoTile>();

                if (i == luckyPlayerIndex)
                {
                    // Bu oyunçu double alır
                    if (doubles.Count > 0)
                    {
                        hand.Add(doubles[0]);
                        doubles.RemoveAt(0);

                        // Qalanını nonDoubles-dan al
                        int needed = tilesPerPlayer - 1;
                        hand.AddRange(nonDoubles.Skip(index).Take(needed));
                        index += needed;
                    }
                }
                else
                {
                    // Normal daş al
                    hand.AddRange(nonDoubles.Skip(index).Take(tilesPerPlayer));
                    index += tilesPerPlayer;
                }

                hands.Add(hand);
            }

            // Stock qalan hər şey
            var stock = new List<DominoTile>();
            stock.AddRange(doubles); // Qalan doubles
            stock.AddRange(nonDoubles.Skip(index)); // Qalan nonDoubles

            Console.WriteLine($"🔧 FORCE (Quick5): Oyuncu {luckyPlayerIndex + 1} əlində double var");
            return (stock, hands);
        }
        private static (List<DominoTile> stock, List<List<DominoTile>> hands) DealNormalTilesWithDoubleGuarantee(int playerCount, int tilesPerPlayer)
        {
            var allTiles = GenerateAllTiles();
            var rnd = new Random();

            int attempts = 0;
            const int maxAttempts = 100;

            while (attempts < maxAttempts)
            {
                allTiles = allTiles.OrderBy(_ => rnd.Next()).ToList();

                var hands = new List<List<DominoTile>>();
                int index = 0;

                // Hər oyunçuya tilesPerPlayer sayda daş ver
                for (int i = 0; i < playerCount; i++)
                {
                    var hand = allTiles.Skip(index).Take(tilesPerPlayer).ToList();
                    hands.Add(hand);
                    index += tilesPerPlayer;
                }

                var stock = allTiles.Skip(index).ToList();

                // ✅ YOXLAMA: ƏN AZ BİR OYUNÇUNUN əlində double varmı?
                bool anyoneHasDouble = hands.Any(hand => hand.Any(t => t.Left == t.Right));

                if (anyoneHasDouble)
                {
                    var playerWithDouble = hands.FindIndex(hand => hand.Any(t => t.Left == t.Right));
                    var doubleInfo = hands[playerWithDouble].First(t => t.Left == t.Right);
                    Console.WriteLine($"✅ {playerCount}P (Classic101/AllFives): Oyuncu {playerWithDouble + 1} əlində double var [{doubleInfo.Left}|{doubleInfo.Right}] ✓");
                    return (stock, hands);
                }

                attempts++;
            }

            // ❌ Uğursuz olsa, force etməli
            Console.WriteLine($"⚠️ {maxAttempts} cəhddən sonra da double tapıla bilmədi. FORCE ediliyor...");
            return DealNormalTilesForcedWithDouble(playerCount, tilesPerPlayer);
        }
        // ✅ YENİ METOD: 2-3 nəfərlik Classic101/AllFives - Double garantisi
        private static (List<DominoTile> stock, List<List<DominoTile>> hands) DealNormalTilesForcedWithDouble(int playerCount, int tilesPerPlayer)
        {
            var allTiles = GenerateAllTiles();
            var rnd = new Random();
            allTiles = allTiles.OrderBy(_ => rnd.Next()).ToList();

            var hands = new List<List<DominoTile>>();

            // Random oyunçu seç (double alacaq)
            int luckyPlayerIndex = rnd.Next(0, playerCount);

            // Doubles-ları ayır
            var doubles = allTiles.Where(t => t.Left == t.Right).ToList();
            var nonDoubles = allTiles.Where(t => t.Left != t.Right).ToList();

            int index = 0;

            for (int i = 0; i < playerCount; i++)
            {
                var hand = new List<DominoTile>();

                if (i == luckyPlayerIndex)
                {
                    // Bu oyunçu double alır
                    if (doubles.Count > 0)
                    {
                        hand.Add(doubles[0]);
                        doubles.RemoveAt(0);

                        // Qalanını nonDoubles-dan al
                        int needed = tilesPerPlayer - 1;
                        hand.AddRange(nonDoubles.Skip(index).Take(needed));
                        index += needed;
                    }
                }
                else
                {
                    // Normal daş al
                    hand.AddRange(nonDoubles.Skip(index).Take(tilesPerPlayer));
                    index += tilesPerPlayer;
                }

                hands.Add(hand);
            }

            // Stock qalan hər şey
            var stock = new List<DominoTile>();
            stock.AddRange(doubles); // Qalan doubles
            stock.AddRange(nonDoubles.Skip(index)); // Qalan nonDoubles

            Console.WriteLine($"🔧 FORCE (Classic101/AllFives): Oyuncu {luckyPlayerIndex + 1} əlində double var");
            return (stock, hands);
        }

        // ✅ Normal daş paylanması (4 nəfərlik - double garantisi YOX)
        private static (List<DominoTile> stock, List<List<DominoTile>> hands) DealNormalTiles(int playerCount, int tilesPerPlayer)
        {
            var allTiles = GenerateAllTiles();
            var rnd = new Random();
            allTiles = allTiles.OrderBy(_ => rnd.Next()).ToList();

            var hands = new List<List<DominoTile>>();
            int index = 0;

            for (int i = 0; i < playerCount; i++)
            {
                var hand = allTiles.Skip(index).Take(tilesPerPlayer).ToList();
                hands.Add(hand);
                index += tilesPerPlayer;
            }

            var stock = allTiles.Skip(index).ToList();

            return (stock, hands);
        }

        // ✅ HƏMİŞƏ: Tüm daşları generasiya et
        private static List<DominoTile> GenerateAllTiles()
        {
            var allTiles = new List<DominoTile>();

            for (int left = 0; left <= 6; left++)
            {
                for (int right = left; right <= 6; right++)
                {
                    allTiles.Add(new DominoTile
                    {
                        Id = Guid.NewGuid().ToString(),
                        Left = left,
                        Right = right
                    });
                }
            }

            return allTiles;
        }
    }
}

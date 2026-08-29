using BlogApp.Api.Hubs.Services;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Threading;

namespace BlogApp.Api.Hubs
{
    public class DurakHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly DurakRoomManager _roomManager;
        private readonly IRankService _rankService;
        private readonly IAuthService _userService;
        private readonly IServiceScopeFactory _scopeFactory;

        private readonly IHubContext<DurakHub> _hubContext;

        public DurakHub(BlogAppDbContext db, DurakRoomManager roomManager, IRankService rankService, IAuthService userService, IHubContext<DurakHub> hubContext, IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
            _userService = userService;
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;

        }

        private static readonly ConcurrentDictionary<string, string> _userRooms = new();
        private static readonly ConcurrentDictionary<int, string> _userActiveRooms = new();
        private const decimal COMMISSION_RATE = 0.03m;
        private const int ATTACK_TURN_SECONDS = 15;
        private const int DEFENSE_TURN_SECONDS = 15;
        private const int EXTRA_TIME_SECONDS = 15;
        private const int DEFAULT_EXTRA_TIMES = 1;
        private const int REMATCH_RESPONSE_SECONDS = 15;
        private const int CONNECTION_RECONNECT_GRACE_SECONDS = 15;

        private sealed class TurnDecision
        {
            public int PlayerId { get; init; }
            public string PlayerName { get; init; } = string.Empty;
            public string ActionKind { get; init; } = string.Empty;
            public int DurationSeconds { get; init; }
            public string StateKey { get; init; } = string.Empty;
            public int ExtraTimesLeft { get; init; }
        }

        private sealed class TurnTimerPlan
        {
            public bool ShouldStartTimer { get; init; }
            public int Sequence { get; init; }
            public CancellationToken Token { get; init; }
            public CancellationTokenSource? PreviousCts { get; init; }
        }

        private sealed class TurnTimerSnapshot
        {
            public bool IsActive { get; init; }
            public int? PlayerId { get; init; }
            public string? PlayerName { get; init; }
            public string? ActionKind { get; init; }
            public string? DeadlineUtc { get; init; }
            public int DurationSeconds { get; init; }
            public int SecondsLeft { get; init; }
            public int ExtraTimesLeft { get; init; }
        }

        private static int FindNextPlayerId(DurakRoom room, int afterUserId, params int[] excludedUserIds)
        {
            if (room.Players.Count == 0) return 0;

            var excluded = excludedUserIds.ToHashSet();
            var startIndex = room.Players.FindIndex(p => p.UserId == afterUserId);
            if (startIndex < 0) startIndex = 0;

            for (var offset = 1; offset <= room.Players.Count; offset++)
            {
                var candidate = room.Players[(startIndex + offset) % room.Players.Count];
                if (!excluded.Contains(candidate.UserId))
                    return candidate.UserId;
            }

            return room.Players[startIndex].UserId;
        }

        private static (int AttackerId, int DefenderId) ApplyRolesAfterDefenderTakes(
            DurakRoom room,
            int oldAttackerId,
            int oldDefenderId)
        {
            if (room.Players.Count <= 2)
            {
                room.AttackerId = oldAttackerId;
                room.DefenderId = oldDefenderId;
                return (room.AttackerId, room.DefenderId);
            }

            var newAttackerId = FindNextPlayerId(room, oldDefenderId, oldDefenderId);
            var newDefenderId = FindNextPlayerId(room, newAttackerId, oldDefenderId, newAttackerId);

            room.AttackerId = newAttackerId;
            room.DefenderId = newDefenderId;
            return (newAttackerId, newDefenderId);
        }

        private static (int AttackerId, int DefenderId) ApplyRolesAfterBeaten(DurakRoom room, int oldDefenderId)
        {
            var newAttackerId = oldDefenderId;
            var newDefenderId = FindNextPlayerId(room, oldDefenderId, newAttackerId);

            room.AttackerId = newAttackerId;
            room.DefenderId = newDefenderId;
            return (newAttackerId, newDefenderId);
        }

        public async Task<object> GetTemplates()
        {
            try
            {
                var templates = _roomManager.GetTemplates();
                return new { success = true, templates };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetTemplates error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }
        public async Task<object> CreateCustomRoom(
       int players,
       int deckSize,
       decimal bet,
       string gameMode,
       string attackMode,
       bool isPassingEnabled)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return new { success = false, message = "User not authenticated" };

                // ✅ TEMPLATE'DEN AVAILABLE BETS'İ AL - players PARAMETRESI İLƏ
                var template = _roomManager.GetTemplate(players);  // ← players LAZIM
                if (template == null)
                {
                    Console.WriteLine($"❌ Template not found for {players}P");
                    return new { success = false, message = $"Game template for {players} players not found" };
                }

                Console.WriteLine($"✅ Template found: {template.Name}");

                // ✅ Enum çevirməsi və template doğrulaması balansdan pul çıxmazdan əvvəl edilməlidir
                if (!Enum.TryParse<GameMode>(gameMode, true, out var gameModeEnum))
                    return new { success = false, message = "Yanlış oyun modu seçildi" };

                if (!Enum.TryParse<AttackMode>(attackMode, true, out var attackModeEnum))
                    return new { success = false, message = "Yanlış hücum modu seçildi" };

                if (!template.AvailableGameModes.Contains(gameModeEnum))
                {
                    var validModes = string.Join(", ", template.AvailableGameModes);
                    return new
                    {
                        success = false,
                        message = $"❌ {players} oyunçu üçün {gameModeEnum} modu aktiv deyil. Seçimlər: {validModes}"
                    };
                }

                if (!template.AvailableAttackModes.Contains(attackModeEnum))
                {
                    var validModes = string.Join(", ", template.AvailableAttackModes);
                    return new
                    {
                        success = false,
                        message = $"❌ {players} oyunçu üçün {attackModeEnum} hücum modu aktiv deyil. Seçimlər: {validModes}"
                    };
                }

                if (isPassingEnabled && !template.IsPassingAvailable)
                {
                    return new { success = false, message = $"❌ {players} oyunçu üçün Passing aktiv deyil" };
                }

                // ✅ BET DOĞRULAMASI
                if (template.AvailableBets == null || template.AvailableBets.Length == 0)
                {
                    return new { success = false, message = "No available bets for this game type" };
                }

                bool betValid = template.AvailableBets.Any(b => Math.Abs(b - bet) < 0.001m);

                if (!betValid)
                {
                    var validBets = string.Join(", ", template.AvailableBets.Select(b => b.ToString("0.##")));
                    return new
                    {
                        success = false,
                        message = $"❌ Geçersiz bet: {bet} AZN\nGeçerli seçenekler: {validBets} AZN"
                    };
                }

                // ✅ USER BALANSINI YOXLA
                var user = await _userService.GetByUserIdAsync(userId);
                if (user == null)
                    return new { success = false, message = "User not found" };

                if (user.Balance < bet)
                {
                    return new
                    {
                        success = false,
                        message = $"❌ Kifayət qədər balansınız yoxdur!\nLazım: {bet} AZN\nMovcud: {user.Balance} AZN"
                    };
                }

                // ✅ BALANSDAN ÇIXAR
                user.Balance -= bet;
                Console.WriteLine($"💰 {user.UserName}: -{bet} AZN (Yeni balans: {user.Balance})");

                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Balance update error: {ex.Message}");
                    user.Balance += bet;
                    return new { success = false, message = "Balance update failed" };
                }

                var settings = new DurakRoomManager.RoomSettings
                {
                    Players = players,
                    DeckSize = deckSize,
                    Bet = bet,
                    GameMode = gameModeEnum,
                    AttackMode = attackModeEnum,
                    IsPassingEnabled = isPassingEnabled,
                };

                var room = _roomManager.CreateRoomFromUserSelection(userId, settings);
                if (room == null)
                {
                    user.Balance += bet;
                    await _db.SaveChangesAsync();
                    return new { success = false, message = "Failed to create room" };
                }

                // ✅ ENTRY FEE SET ET
                room.EntryFee = bet;
                room.TotalPrize = bet;

                var player = new DurakPlayer
                {
                    UserId = userId,
                    Name = user.UserName ?? "Player",
                    ConnectionId = Context.ConnectionId,
                    ProfileImage = user.Image
                };

                if (_roomManager.AddPlayerToRoom(room.RoomId, player))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);
                    _userRooms[Context.ConnectionId] = room.RoomId;
                    _userActiveRooms[userId] = room.RoomId;

                    await Clients.Caller.SendAsync("JoinedRoom", new
                    {
                        roomId = room.RoomId,
                        roomName = room.RoomName,
                        maxPlayers = room.MaxPlayers,
                        currentPlayers = 1,
                        entryFee = room.EntryFee,
                        deckSize = room.DeckSize,
                        balance = user.Balance,  // ✅ SADƏCƏ KENDİ BALANSI
                        gameMode = room.GameMode.ToString(),
                        attackMode = room.GameSettings.AttackMode.ToString(),
                        isPassingEnabled = room.GameSettings.IsPassingEnabled,
                        totalPrize = room.TotalPrize
                    });

                    await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());

                    Console.WriteLine($"✅ Room created: {room.RoomName} (Bet: {bet} AZN)");
                    return new { success = true, roomId = room.RoomId };
                }

                user.Balance += bet;
                await _db.SaveChangesAsync();
                return new { success = false, message = "Failed to join room" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CreateCustomRoom error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }


        public async Task<object> PlayerReady(string roomId)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return new { success = false, message = "User not authenticated" };

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return new { success = false, message = "Room not found" };

                DurakPlayer player = null;

                lock (room.StateLock)
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == userId);
                    if (player == null)
                        return new { success = false, message = "Player not in room" };

                    if (player.IsReady)
                        return new { success = false, message = "Already ready" };

                    // ✅ MINIMUM 2 OYUNÇU LAZIM
                    if (room.Players.Count < 2)
                    {
                        return new
                        {
                            success = false,
                            message = $"Minimum 2 oyunçu lazımdır (Halen: {room.Players.Count})"
                        };
                    }

                    player.IsReady = true;
                    Console.WriteLine($"✅ {player.Name} hazırdır ({room.Players.Count(p => p.IsReady)}/{room.Players.Count})");
                }

                // ✅ HAMIYA BILDIRIŞ GÖNDƏR
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerReady", new
                {
                    userId = userId,
                    playerName = player.Name,
                    readyCount = room.Players.Count(p => p.IsReady),
                    totalPlayers = room.Players.Count,
                    allReady = false,  // ← Hamı ready deyil, çünki minimum 2 lazım
                    readyPlayersList = string.Join(", ", room.Players.Where(p => p.IsReady).Select(p => p.Name))
                });

                // ✅ HAMISI HAZIRSA VƏ MINIMUM 2 OYUNÇU VARSA
                if (room.Players.Count >= 2 && room.Players.All(p => p.IsReady) && room.Players.Count == room.MaxPlayers)
                {
                    Console.WriteLine($"🎮 HAMISI HAZIR! Oyun başlayır...");

                    // ✅ Ready status-u gizlət
                    await _hubContext.Clients.Group(roomId).SendAsync("PlayerReady", new
                    {
                        userId = userId,
                        playerName = player.Name,
                        readyCount = room.Players.Count(p => p.IsReady),
                        totalPlayers = room.Players.Count,
                        allReady = true,  // ← İndi true!
                        readyPlayersList = string.Join(", ", room.Players.Where(p => p.IsReady).Select(p => p.Name))
                    });

                    await Task.Delay(1000);
                    await StartGame(roomId);
                }

                return new { success = true, message = "Ready set" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ PlayerReady error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }
        public async Task<object> PlayerNotReady()
        {
            try
            {
                var userId = GetUserId();
                var roomId = GetCurrentRoom();

                if (string.IsNullOrEmpty(roomId))
                    return new { success = false, message = "Not in a room" };

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return new { success = false, message = "Room not found" };

                DurakPlayer player = null;

                lock (room.StateLock)
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == userId);
                    if (player == null)
                        return new { success = false, message = "Player not found" };

                    if (!player.IsReady)
                        return new { success = false, message = "Not ready" };

                    player.IsReady = false;
                    Console.WriteLine($"⏸️ {player.Name} hazır deyil");
                }

                // ✅ HAMIYA BILDIRIŞ GÖNDƏR
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerNotReady", new
                {
                    userId = userId,
                    playerName = player.Name,
                    readyCount = room.Players.Count(p => p.IsReady),
                    totalPlayers = room.Players.Count,
                    readyPlayersList = string.Join(", ", room.Players.Where(p => p.IsReady).Select(p => p.Name))
                });

                return new { success = true, message = "Not ready" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ PlayerNotReady error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }
        private async Task StartGame(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"❌ Room {roomId} not found");
                return;
            }

            Console.WriteLine($"\n🎮 STARTING GAME: {room.RoomName}");

            lock (room.StateLock)
            {
                room.StartNewGame();
            }

            // ✅ GameStarted event
            await _hubContext.Clients.Group(roomId).SendAsync("GameStarted", new
            {
                message = "Oyun başladı! 🎮",
                trumpCard = new
                {
                    rank = room.TrumpCard.Rank,
                    suit = room.TrumpCard.Suit
                }
            });

            // ✅ Hamıya kartlar göndər
            foreach (var player in room.Players)
            {
                try
                {
                    var cardsData = player.Hand.Select(c => new
                    {
                        rank = c.Rank,
                        suit = c.Suit
                    }).ToList();

                    // ✅ DÜZƏLDILMIŞ
                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("YourCards", cardsData);
                    Console.WriteLine($"📤 {player.Name} → {cardsData.Count} kart");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ {player.Name} xətası: {ex.Message}");
                }
            }

            // ✅ Game state
            await BroadcastGameState(roomId);

            Console.WriteLine($"✅ Game started successfully!\n");
        }
        public async Task<object> GetActiveGames()
        {
            try
            {
                var games = _roomManager.GetActiveGames();
                return new { success = true, games };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetActiveGames error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }

        public async Task<object> GetAvailableRooms()
        {
            try
            {
                var rooms = _roomManager.GetAvailableRooms();
                return new { success = true, rooms };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetAvailableRooms error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }
        public override async Task OnConnectedAsync()
        {
            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Console.WriteLine($"❌ Unauthorized connection attempt");
                Context.Abort();
                return;
            }

            string userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
            {
                Console.WriteLine($"❌ Invalid user ID");
                Context.Abort();
                return;
            }

            try
            {
                var user = _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance })
                    .FirstOrDefault();

                if (user == null)
                {
                    Console.WriteLine($"⚠️ User not found: {userId}");
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.UserName} ".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName,
                    balance = user.Balance
                });

                await ClearPreviousRoomOnFreshConnectionAsync(userId);

                Console.WriteLine($"✅ Durak Connected: {fullName} (ID: {userId})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnConnectedAsync error: {ex.Message}");
                Context.Abort();
            }

            await base.OnConnectedAsync();
        }

        private async Task ClearPreviousRoomOnFreshConnectionAsync(int userId)
        {
            string? roomId = null;

            if (!_userActiveRooms.TryGetValue(userId, out roomId))
                roomId = _roomManager.GetRoomByPlayerUserId(userId)?.RoomId;

            if (string.IsNullOrEmpty(roomId))
                return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                _userActiveRooms.TryRemove(userId, out _);
                return;
            }

            DurakPlayer? stalePlayer;
            string staleConnectionId = string.Empty;
            bool wasGameActive;
            bool isRematchWindowActive;
            bool shouldRestoreConnection;

            lock (room.StateLock)
            {
                stalePlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (stalePlayer == null)
                {
                    _userActiveRooms.TryRemove(userId, out _);
                    return;
                }

                staleConnectionId = stalePlayer.ConnectionId;
                wasGameActive = room.IsGameActive;
                isRematchWindowActive = !wasGameActive && room.RematchDeadlineUtc.HasValue;
                // Aktiv oyun və rematch zamanı eyni user-in yeni SignalR connection-u
                // əvvəlkini əvəz edir; bu, reconnect race səbəbilə otağın silinməsinin
                // qarşısını alır.
                shouldRestoreConnection = wasGameActive || isRematchWindowActive;

                // SignalR reconnect zamanı aktiv/rematch otağındakı oyunçunu silmə.
                // Köhnə connection-u yenisi ilə əvəz et və rematch səsini qoruyub saxla.
                if (shouldRestoreConnection)
                {
                    stalePlayer.ConnectionId = Context.ConnectionId;
                    stalePlayer.IsDisconnected = false;
                    stalePlayer.DisconnectedAt = null;
                }
            }

                if (shouldRestoreConnection)
            {
                if (!string.IsNullOrWhiteSpace(staleConnectionId) && staleConnectionId != Context.ConnectionId)
                {
                    _userRooms.TryRemove(staleConnectionId, out _);
                    try
                    {
                        await _hubContext.Groups.RemoveFromGroupAsync(staleConnectionId, room.RoomId);
                    }
                    catch
                    {
                    }
                }

                _userRooms[Context.ConnectionId] = room.RoomId;
                _userActiveRooms[userId] = room.RoomId;
                await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);
                await Clients.Caller.SendAsync("RematchConnectionRestored", new
                {
                    roomId = room.RoomId,
                    message = wasGameActive
                        ? "Oyuna bağlantı bərpa edildi"
                        : "Rematch otağına bağlantı bərpa edildi"
                });
                await Clients.Caller.SendAsync("RejoinedRoom", new
                {
                    roomId = room.RoomId,
                    roomName = room.RoomName,
                    isGameActive = room.IsGameActive,
                    message = "Durak otağına yenidən qoşuldunuz"
                });
                await Clients.Caller.SendAsync("YourCards", stalePlayer.Hand.Select(c => new
                {
                    rank = c.Rank,
                    suit = c.Suit
                }).ToList());
                await BroadcastGameState(room.RoomId);
                return;
            }

            if (!string.IsNullOrWhiteSpace(staleConnectionId))
            {
                _userRooms.TryRemove(staleConnectionId, out _);
                try
                {
                    await _hubContext.Groups.RemoveFromGroupAsync(staleConnectionId, room.RoomId);
                }
                catch
                {
                }
            }

            _userActiveRooms.TryRemove(userId, out _);
            _userRooms.TryRemove(Context.ConnectionId, out _);

            if (wasGameActive)
            {
                await _hubContext.Clients.Group(room.RoomId).SendAsync("PlayerDisconnected", new
                {
                    userId,
                    playerName = stalePlayer.Name,
                    message = $"{stalePlayer.Name} bağlantını itirdi və oyundan çıxarıldı."
                });

                await HandlePlayerLeftDuringActiveGame(room.RoomId, room, userId);
                await Clients.Caller.SendAsync("RoomStateCleared", new
                {
                    message = "Əvvəlki oyundan çıxarıldınız. Lobby açıldı."
                });
                await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
                return;
            }

            var user = await _userService.GetByUserIdAsync(userId);
            if (user != null && room.EntryFee > 0)
            {
                user.Balance += room.EntryFee;
                room.TotalPrize = Math.Max(0, room.TotalPrize - room.EntryFee);
                await _db.SaveChangesAsync();
                await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
                Console.WriteLine($"💰 Fresh connect refund: {user.UserName} +{room.EntryFee} AZN");
            }

            if (_roomManager.RemovePlayerFromRoom(room.RoomId, userId))
            {
                await _hubContext.Clients.Group(room.RoomId).SendAsync("PlayerLeft", new
                {
                    userId,
                    username = user?.UserName,
                    playerCount = room.Players.Count,
                    totalPrize = room.TotalPrize
                });
            }

            await Clients.Caller.SendAsync("RoomStateCleared", new
            {
                message = "Əvvəlki otaq state-i təmizləndi. Lobby açıldı."
            });
            await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            try
            {
                var userId = GetUserId();

                if (!_userRooms.TryRemove(Context.ConnectionId, out var roomId))
                    return;

                if (userId > 0 && !string.IsNullOrEmpty(roomId))
                {
                    var room = _roomManager.GetRoom(roomId);
                    if (room != null)
                    {
                        bool isActiveGame = false;
                        bool isRematchWindowActive = false;
                        DurakPlayer? disconnectedPlayer = null;

                        lock (room.StateLock)
                        {
                            disconnectedPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                            isActiveGame = room.IsGameActive;
                            isRematchWindowActive = !isActiveGame && room.RematchDeadlineUtc.HasValue;

                            if (isActiveGame && disconnectedPlayer != null)
                            {
                                disconnectedPlayer.IsDisconnected = true;
                                disconnectedPlayer.DisconnectedAt = DateTime.UtcNow;
                                disconnectedPlayer.ConnectionId = string.Empty;
                            }

                            // Rematch pəncərəsində connection qopması oyunçunun dərhal
                            // silinməsi demək deyil. Timeout handler qərar verməyənləri
                            // pəncərə bitəndə siləcək; reconnect isə həmin oyunçunu bərpa
                            // edə biləcək.
                            if (isRematchWindowActive && disconnectedPlayer != null)
                            {
                                disconnectedPlayer.IsDisconnected = true;
                                disconnectedPlayer.DisconnectedAt = DateTime.UtcNow;
                                disconnectedPlayer.ConnectionId = string.Empty;
                            }
                        }

                        if (isActiveGame)
                        {
                            if (disconnectedPlayer != null)
                            {
                                // Connection qopması dərhal oyunçunun məğlubiyyəti
                                // və otağın silinməsi deyil. Reconnect üçün grace period
                                // saxlanılır; oyunçu qayıtsa həmin otaq davam edir.
                                _userActiveRooms[userId] = roomId;

                                await _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnected", new
                                {
                                    userId,
                                    playerName = disconnectedPlayer.Name,
                                    message = room.Players.Count <= 2
                                        ? $"{disconnectedPlayer.Name} bağlantını itirdi. {CONNECTION_RECONNECT_GRACE_SECONDS} saniyə ərzində qayıda bilər."
                                        : $"{disconnectedPlayer.Name} bağlantını itirdi. Oyun qaldığı yerdən davam edir."
                                });

                                _ = Task.Run(() => ResolveDisconnectedPlayerAsync(
                                    roomId,
                                    userId,
                                    disconnectedPlayer.DisconnectedAt!.Value));
                            }

                            return;
                        }

                if (isRematchWindowActive)
                        {
                            await _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnected", new
                            {
                                userId,
                                playerName = disconnectedPlayer?.Name,
                                message = $"{disconnectedPlayer?.Name ?? "Oyunçu"} rematch zamanı bağlantını itirdi."
                            });
                            return;
                        }

                        var user = await _userService.GetByUserIdAsync(userId);

                        bool shouldRefund = false;
                        bool shouldHandleAsActiveGame = false;

                        lock (room.StateLock)
                        {
                            int remainingAfterLeave = room.Players.Count - 1;

                            // ✅ Oyun başlamamışsa HER OYUNCU refund alır
                            if (!room.IsGameActive)
                                shouldRefund = true;

                            // ✅ Oyun başlamışsa game over işlə
                            if (room.IsGameActive || remainingAfterLeave <= 0)
                                shouldHandleAsActiveGame = true;
                        }

                        if (shouldRefund && user != null)
                        {
                            user.Balance += room.EntryFee;
                            room.TotalPrize -= room.EntryFee;
                            await _db.SaveChangesAsync();
                            Console.WriteLine($"💰 Refund: {user.UserName} +{room.EntryFee} AZN");
                        }

                        if (_roomManager.RemovePlayerFromRoom(roomId, userId))
                        {
                            _userActiveRooms.TryRemove(userId, out _);
                            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);

                            if (shouldHandleAsActiveGame)
                            {
                                await HandlePlayerLeftDuringActiveGame(roomId, room, userId);
                            }
                            else
                            {
                                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", new
                                {
                                    userId,
                                    username = user?.UserName,
                                    playerCount = room.Players.Count,
                                    totalPrize = room.TotalPrize
                                });
                            }

                            await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
                        }
                    }
                    else
                    {
                        _userActiveRooms.TryRemove(userId, out _);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnDisconnectedAsync error: {ex.Message}");
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task RequestCurrentState()
        {
            var userId = GetUserId();
            if (userId == 0) return;

            if (!_userActiveRooms.TryGetValue(userId, out var activeRoomId))
            {
                activeRoomId = _roomManager.GetRoomByPlayerUserId(userId)?.RoomId;
            }

            if (string.IsNullOrEmpty(activeRoomId))
            {
                await Clients.Caller.SendAsync("RoomStateCleared", new
                {
                    message = "Aktiv Durak otağınız yoxdur"
                });
                return;
            }

            var room = _roomManager.GetRoom(activeRoomId);
            if (room == null)
            {
                _userActiveRooms.TryRemove(userId, out _);
                await Clients.Caller.SendAsync("RoomStateCleared", new
                {
                    message = "Otaq artıq aktiv deyil"
                });
                return;
            }

            DurakPlayer? player;
            bool shouldClearStaleActiveRoom = false;
            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    _userActiveRooms.TryRemove(userId, out _);
                    _userRooms.TryRemove(Context.ConnectionId, out _);
                    Task.Run(async () => await Clients.Caller.SendAsync("RoomStateCleared", new
                    {
                        message = "Bu otaqdakı əvvəlki oyun state-iniz təmizləndi"
                    }));
                    return;
                }

                if (room.IsGameActive &&
                    (player.IsDisconnected ||
                     (!string.IsNullOrWhiteSpace(player.ConnectionId) &&
                      player.ConnectionId != Context.ConnectionId)))
                {
                    shouldClearStaleActiveRoom = true;
                }
                else
                {
                    player.ConnectionId = Context.ConnectionId;
                    player.IsDisconnected = false;
                    player.DisconnectedAt = null;
                    _userRooms[Context.ConnectionId] = room.RoomId;
                    _userActiveRooms[userId] = room.RoomId;
                }
            }

            if (shouldClearStaleActiveRoom)
            {
                await ClearPreviousRoomOnFreshConnectionAsync(userId);
                return;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);
            await Clients.Caller.SendAsync("RejoinedRoom", new
            {
                roomId = room.RoomId,
                roomName = room.RoomName,
                isGameActive = room.IsGameActive,
                message = "Durak otağına yenidən qoşuldunuz"
            });

            await Clients.Caller.SendAsync("YourCards", player.Hand.Select(c => new
            {
                rank = c.Rank,
                suit = c.Suit
            }).ToList());

            await BroadcastGameState(room.RoomId);
        }
        private async Task ResolveDisconnectedPlayerAsync(string roomId, int userId, DateTime disconnectedAt)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(CONNECTION_RECONNECT_GRACE_SECONDS));

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return;

                bool shouldRemovePlayer;
                lock (room.StateLock)
                {
                    var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                    shouldRemovePlayer = room.IsGameActive &&
                        player != null &&
                        player.IsDisconnected &&
                        player.DisconnectedAt == disconnectedAt;
                }

                // Oyunçu grace period ərzində qayıdıbsa, heç bir game-over işlətmə.
                if (!shouldRemovePlayer)
                    return;

                await HandlePlayerLeftDuringActiveGame(roomId, room, userId);
                _userActiveRooms.TryRemove(userId, out _);
                await _hubContext.Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Disconnected player resolution error: {ex.Message}");
            }
        }

        private async Task HandlePlayerLeftDuringActiveGame(string roomId, DurakRoom room, int leftUserId)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
            var rankService = scope.ServiceProvider.GetRequiredService<IRankService>();

            int remainingCount;
            DurakPlayer? winner = null;
            bool gameFinishedNow = false;

            lock (room.StateLock)
            {
                // ✅ artıq bitibsə heç nə etmə
                if (room.GameStatus == "Finished")
                    return;

                room.Players.RemoveAll(p => p.UserId == leftUserId);
                remainingCount = room.Players.Count;

                if (remainingCount == 1)
                {
                    winner = room.Players.FirstOrDefault();
                    if (winner != null)
                    {
                        room.GameStatus = "Finished";
                        room.IsGameActive = false;
                        room.WinnerId = winner.UserId;
                        room.FinishedAt = DateTime.UtcNow;
                        gameFinishedNow = true;
                    }
                }
            }

            Console.WriteLine($"⚠️ userId={leftUserId}, remaining={remainingCount}");

            // ❗ Əgər qalib tapılıbsa
            if (gameFinishedNow && winner != null)
            {
                Console.WriteLine($"🏆 {winner.Name} qalib!");

                // loser rank
                try
                {
                    await rankService.UpdateRankAfterGame(leftUserId, GameType.Durak, false, room.EntryFee);
                }
                catch { }

                if (room.TotalPrize > 0)
                {
                    decimal commission = room.TotalPrize * COMMISSION_RATE;
                    decimal winnerPrize = room.TotalPrize - commission;

                    var winnerUser = await db.Users.FindAsync(winner.UserId);
                    if (winnerUser != null)
                    {
                        winnerUser.Balance += winnerPrize;
                        await db.SaveChangesAsync();
                    }

                    try
                    {
                        await rankService.UpdateRankAfterGame(winner.UserId, GameType.Durak, true, winnerPrize);
                    }
                    catch { }

                    await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                    {
                        winner = winner.Name,
                        winnerId = winner.UserId,
                        message = $"🏆 {winner.Name} QALIB OLDU!\nRəqib oyunu tərk etdi",
                        winnerPrize,
                        commission,
                        reason = "opponent_left",
                        canRematch = false
                    });
                }

                _roomManager.DeleteRoom(roomId);
                return;
            }

            // ❗ oyun davam edir
            if (remainingCount >= 2)
            {
                try
                {
                    await rankService.UpdateRankAfterGame(leftUserId, GameType.Durak, false, room.EntryFee);
                }
                catch { }

                ReassignRoles(room, leftUserId);

                bool shouldResolveBeatenQueue = false;
                bool shouldResolveTakeCardsVoting = false;

                lock (room.StateLock)
                {
                    if (room.IsBrokenBeatenPhaseActive || room.IsBeatenPhaseActive)
                    {
                        var nextBeatenAttacker = FindNextBeatenAttackerLocked(room);
                        shouldResolveBeatenQueue = nextBeatenAttacker == null || nextBeatenAttacker == 0;
                    }

                    if (room.IsThrowInPhaseActive)
                    {
                        var nextThrowInAttacker = FindNextThrowInAttackerLocked(room);
                        shouldResolveTakeCardsVoting = nextThrowInAttacker == 0;
                    }
                }

                var attacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
                var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);

                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeftGameContinues", new
                {
                    message = "⚠️ Bir oyunçu çıxdı, oyun davam edir",
                    remainingPlayers = remainingCount,
                    currentAttacker = attacker?.Name,
                    currentDefender = defender?.Name,
                    totalPrize = room.TotalPrize
                });

                if (shouldResolveBeatenQueue)
                {
                    await ResolveBeatenQueueFinished(roomId, room);
                    return;
                }

                if (shouldResolveTakeCardsVoting)
                {
                    await CheckTakeCardVotingResult(roomId, room);
                    return;
                }

                await BroadcastGameState(roomId);
            }
        }
        private async Task HandleGameEndDueToDisconnect(string roomId, DurakRoom room, DurakPlayer? disconnectedPlayer)
        {
            await Task.Delay(500);

            // ✅ Disconnect olan oyunçu HARICINDƏ digərləri
            var remainingPlayers = room.Players
                .Where(p => p.UserId != disconnectedPlayer?.UserId)
                .ToList();

            if (remainingPlayers.Count == 0)
            {
                Console.WriteLine($"⚠️ No players left in room - Game cancelled");
                lock (room.StateLock)
                {
                    room.TotalPrize = 0;
                    room.ResetGame();
                }
                // ✅ Disconnect oyunçusuna cərimə (əgər pul varsa)
                if (disconnectedPlayer != null && room.TotalPrize > 0)
                {
                    Console.WriteLine($"⚠️ {disconnectedPlayer.Name} əngəllə oyunu tərk etdi - cərimə tətbiq edildi");
                }
                return;
            }

            // ✅ 2 Oyunçu qaldısa: digəri qalib
            // ✅ 3+ Oyunçu qaldısa: bir sonraki attacker qalib (ranking)
            DurakPlayer winner;

            if (remainingPlayers.Count == 1)
            {
                winner = remainingPlayers[0];
            }
            else
            {
                // 3+ oyunçu - bir sonrakı attacker sırası olanı qalib et
                var nextAttackerIndex = room.CurrentAttackerQueueIndex + 1;
                if (nextAttackerIndex >= remainingPlayers.Count)
                {
                    nextAttackerIndex = 0;
                }
                winner = remainingPlayers[nextAttackerIndex];
            }

            Console.WriteLine($"🏆 Winner due to disconnect: {winner.Name}");
            Console.WriteLine($"   Disconnect: {disconnectedPlayer?.Name ?? "Unknown"}");

            // ✅ PULU DÜZƏLT (Disconnect oyunçusu xarab etməsə)
            if (room.TotalPrize > 0)
            {
                decimal commission = room.TotalPrize * COMMISSION_RATE;
                decimal winnerPrize = room.TotalPrize - commission;

                var winnerUser = _db.Users.FirstOrDefault(u => u.Id == winner.UserId);
                if (winnerUser != null)
                {
                    winnerUser.Balance += winnerPrize;
                    try
                    {
                        await _db.SaveChangesAsync();
                        Console.WriteLine($"💰 Prize distributed: {winnerPrize} AZN to {winner.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ DB Save Error: {ex.Message}");
                        winnerUser.Balance -= winnerPrize;
                    }
                }

                // ✅ Ranking Update
                await _rankService.UpdateRankAfterGame(winner.UserId, GameType.Durak, true, winnerPrize);
                if (disconnectedPlayer != null)
                {
                    await _rankService.UpdateRankAfterGame(disconnectedPlayer.UserId, GameType.Durak, false, room.EntryFee);
                }

                // ✅ BROADCAST - Daha ətraflı məlumat
                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winners = new[] { new { userId = winner.UserId, name = winner.Name } },
                    durak = disconnectedPlayer != null ? new { userId = disconnectedPlayer.UserId, name = disconnectedPlayer.Name } : null,
                    message = $"🏆 {winner.Name} QALIB!\n❌ {disconnectedPlayer?.Name ?? "Rəqib"} oyunu tərk etdi",
                    winnerPrize,
                    commission,
                    reason = "disconnect",
                    canRematch = remainingPlayers.Count >= 1
                });
            }
            else
            {
                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winners = new[] { new { userId = winner.UserId, name = winner.Name } },
                    durak = disconnectedPlayer != null ? new { userId = disconnectedPlayer.UserId, name = disconnectedPlayer.Name } : null,
                    message = $"🏆 {winner.Name} qalib gəldi!\n❌ {disconnectedPlayer?.Name ?? "Rəqib"} oyunu tərk etdi",
                    reason = "disconnect",
                    canRematch = remainingPlayers.Count >= 1
                });
            }

            lock (room.StateLock)
            {
                room.ResetGame();
            }

            await BroadcastGameState(roomId);
            Console.WriteLine($"✅ Game ended due to disconnect in {room.RoomName}");
        }
        public async Task<List<object>> GetQuickRooms()
        {
            var quickRooms = _roomManager.GetQuickRooms();
            return quickRooms.Select(r => new
            {
                roomId = r.RoomId,
                roomName = r.RoomName,
                playerCount = r.PlayerCount,
                maxPlayers = r.MaxPlayers,
                entryFee = r.EntryFee,
                totalPrize = r.TotalPrize,
                isGameActive = r.IsGameActive
            }).ToList<object>();
        }

        public async Task<object> JoinRoom(string roomId)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return new { success = false, message = "User not authenticated" };

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return new { success = false, message = "Room not found" };

                var user = await _userService.GetByUserIdAsync(userId);
                if (user == null)
                    return new { success = false, message = "User not found" };

                DurakPlayer? existingPlayer = null;
                lock (room.StateLock)
                {
                    existingPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                    if (existingPlayer != null)
                    {
                        existingPlayer.ConnectionId = Context.ConnectionId;
                        existingPlayer.IsDisconnected = false;
                        existingPlayer.DisconnectedAt = null;
                    }
                }

                if (existingPlayer != null)
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
                    _userRooms[Context.ConnectionId] = roomId;
                    _userActiveRooms[userId] = roomId;

                    await Clients.Caller.SendAsync("JoinedRoom", new
                    {
                        roomId = room.RoomId,
                        roomName = room.RoomName,
                        maxPlayers = room.MaxPlayers,
                        currentPlayers = room.PlayerCount,
                        entryFee = room.EntryFee,
                        deckSize = room.DeckSize,
                        balance = user.Balance,
                        gameMode = room.GameMode.ToString(),
                        attackMode = room.GameSettings.AttackMode.ToString(),
                        isPassingEnabled = room.GameSettings.IsPassingEnabled,
                        totalPrize = room.TotalPrize
                    });

                    await BroadcastGameState(roomId);
                    return new { success = true, room, balance = user.Balance, alreadyJoined = true };
                }

                if (room.IsGameActive)
                    return new { success = false, message = "Oyun artıq başlayıb" };

                var otherRoom = _roomManager.GetRoomByPlayerUserId(userId);
                if (otherRoom != null && otherRoom.RoomId != roomId)
                    return new { success = false, message = "Əvvəlcə mövcud otaqdan çıxın" };

                // ✅ USER BALANSINI YOXLA
                var entryFee = room.EntryFee;

                if (user.Balance < entryFee)
                {
                    return new
                    {
                        success = false,
                        message = $"❌ Kifayət qədər balansınız yoxdur!\nLazım: {entryFee} AZN\nMovcud: {user.Balance} AZN"
                    };
                }

                // ✅ BALANSDAN ÇIXAR
                user.Balance -= entryFee;
                Console.WriteLine($"💰 {user.UserName}: -{entryFee} AZN (Yeni balans: {user.Balance})");

                try
                {
                    await _db.SaveChangesAsync();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Balance update error: {ex.Message}");
                    user.Balance += entryFee;
                    return new { success = false, message = "Balance update failed" };
                }

                // ✅ OYUNÇU ƏLAVƏ ET
                var player = new DurakPlayer
                {
                    UserId = userId,
                    Name = user.UserName ?? "Player",
                    ConnectionId = Context.ConnectionId,
                    ProfileImage = user.Image
                };

                if (_roomManager.AddPlayerToRoom(roomId, player))
                {
                    await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
                    _userRooms[Context.ConnectionId] = roomId;
                    _userActiveRooms[userId] = roomId;

                    room.TotalPrize += entryFee;
                    Console.WriteLine($"✅ Room {room.RoomName}: TotalPrize = {room.TotalPrize} AZN");

                    // ✅ HAMIYA JoinedRoom GÖNDƏRİ - balance İLƏ
                    await Clients.Caller.SendAsync("JoinedRoom", new
                    {
                        roomId = room.RoomId,
                        roomName = room.RoomName,
                        maxPlayers = room.MaxPlayers,
                        currentPlayers = room.PlayerCount,
                        entryFee = room.EntryFee,
                        deckSize = room.DeckSize,
                        balance = user.Balance,  // ✅ BALANCE İLƏ
                        gameMode = room.GameMode.ToString(),
                        attackMode = room.GameSettings.AttackMode.ToString(),
                        isPassingEnabled = room.GameSettings.IsPassingEnabled,
                        totalPrize = room.TotalPrize
                    });

                    // ✅ SADƏCƏ CALLER'A BALANS GÜNCƏLLƏ
                    await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);

                    // ✅ PlayerJoined event
                    await _hubContext.Clients.Group(roomId).SendAsync("PlayerJoined", new
                    {
                        userId,
                        username = player.Name,
                        playerCount = room.PlayerCount,
                        maxPlayers = room.MaxPlayers,
                        totalPrize = room.TotalPrize
                    });

                    // ✅ RoomList güncəllə
                    await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());

                    Console.WriteLine($"✅ {player.Name} joined room. New balance: {user.Balance}");
                    return new { success = true, room, balance = user.Balance };
                }

                user.Balance += entryFee;  // Rollback
                await _db.SaveChangesAsync();
                return new { success = false, message = "Failed to join room" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ JoinRoom error: {ex.Message}\n{ex.StackTrace}");
                return new { success = false, message = ex.Message };
            }
        }
        public async Task<object> LeaveRoom(string roomId)
        {
            try
            {
                var userId = GetUserId();
                if (userId == 0)
                    return new { success = false, message = "User not authenticated" };

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return new { success = false, message = "Room not found" };

                var user = await _userService.GetByUserIdAsync(userId);

                decimal refundAmount = 0;
                bool shouldRefund = false;
                bool shouldHandleAsActiveGame = false;

                lock (room.StateLock)
                {
                    int remainingAfterLeave = room.Players.Count - 1;

                    // ✅ Oyun başlamamışsa HER OYUNCU refund alır
                    if (!room.IsGameActive)
                        shouldRefund = true;

                    // ✅ Oyun başlamışsa game over işlə
                    if (room.IsGameActive || remainingAfterLeave <= 0)
                        shouldHandleAsActiveGame = true;
                }

                // ✅ REFUND
                if (shouldRefund && user != null)
                {
                    user.Balance += room.EntryFee;
                    refundAmount = room.EntryFee;
                    room.TotalPrize -= room.EntryFee;

                    try { await _db.SaveChangesAsync(); }
                    catch
                    {
                        user.Balance -= room.EntryFee;
                        room.TotalPrize += room.EntryFee;
                        refundAmount = 0;
                    }
                    
                    Console.WriteLine($"💰 Refund: {user?.UserName} +{refundAmount} AZN");
                }

                if (user != null)
                    await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);

                _userRooms.TryRemove(Context.ConnectionId, out _);
                _userActiveRooms.TryRemove(userId, out _);

                if (_roomManager.RemovePlayerFromRoom(roomId, userId))
                {
                    await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId);

                    if (shouldHandleAsActiveGame)
                    {
                        await HandlePlayerLeftDuringActiveGame(roomId, room, userId);
                    }
                    else
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", new
                        {
                            userId,
                            username = user?.UserName,
                            playerCount = room.Players.Count,
                            totalPrize = room.TotalPrize
                        });
                    }

                    await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());

                    return new
                    {
                        success = true,
                        refund = refundAmount,
                        gameWasActive = room.IsGameActive,
                        newBalance = user?.Balance
                    };
                }

                return new { success = false, message = "Failed to leave room" };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ LeaveRoom error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }
        private void ReassignRoles(DurakRoom room, int disconnectedUserId = 0)
        {
            lock (room.StateLock)
            {
                // ✅ Qalan aktiv oyunçular (disconnect olanı çıxart)
                var activePlayers = room.Players
                    .Where(p => p.UserId != disconnectedUserId)
                    .ToList();

                if (activePlayers.Count == 0) return;

                // ✅ Köhnə attacker və defender-i tap
                var oldAttacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
                var oldDefender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);

                bool attackerLeft = disconnectedUserId == room.AttackerId;
                bool defenderLeft = disconnectedUserId == room.DefenderId;

                // ✅ Hamının rolunu sıfırla
                foreach (var p in activePlayers)
                {
                    p.IsAttacker = false;
                    p.IsDefender = false;
                }

                if (attackerLeft)
                {
                    // ✅ Attacker getdi - defender yeni attacker olur
                    var newAttacker = activePlayers.FirstOrDefault(p => p.UserId == room.DefenderId);
                    if (newAttacker == null) newAttacker = activePlayers[0];

                    newAttacker.IsAttacker = true;
                    room.AttackerId = newAttacker.UserId;

                    // ✅ Yeni defender - növbəti oyunçu
                    var newDefender = activePlayers
                        .FirstOrDefault(p => p.UserId != newAttacker.UserId);
                    if (newDefender != null)
                    {
                        newDefender.IsDefender = true;
                        room.DefenderId = newDefender.UserId;
                    }

                    Console.WriteLine($"   ✅ Attacker getdi → Yeni Attacker: {newAttacker.Name}");
                    Console.WriteLine($"   ✅ Yeni Defender: {newDefender?.Name}");
                }
                else if (defenderLeft)
                {
                    // ✅ Defender getdi - attacker qalır, növbəti oyunçu defender olur
                    var newAttacker = activePlayers.FirstOrDefault(p => p.UserId == room.AttackerId);
                    if (newAttacker == null) newAttacker = activePlayers[0];

                    newAttacker.IsAttacker = true;
                    room.AttackerId = newAttacker.UserId;

                    // ✅ Disconnect olanın növbəti indeksini tap
                    int disconnectedIndex = room.Players.FindIndex(p => p.UserId == disconnectedUserId);
                    int newDefenderIndex = (disconnectedIndex + 1) % room.Players.Count;

                    // ✅ Yeni defender disconnect olan deyil
                    while (room.Players[newDefenderIndex].UserId == disconnectedUserId ||
                           room.Players[newDefenderIndex].UserId == room.AttackerId)
                    {
                        newDefenderIndex = (newDefenderIndex + 1) % room.Players.Count;
                    }

                    var newDefender = room.Players[newDefenderIndex];
                    newDefender.IsDefender = true;
                    room.DefenderId = newDefender.UserId;

                    Console.WriteLine($"   ✅ Defender getdi → Attacker qalır: {newAttacker.Name}");
                    Console.WriteLine($"   ✅ Yeni Defender: {newDefender.Name}");
                }
                else
                {
                    // ✅ Nə attacker nə defender - sadəcə mövcud rolları saxla
                    var attacker = activePlayers.FirstOrDefault(p => p.UserId == room.AttackerId);
                    var defender = activePlayers.FirstOrDefault(p => p.UserId == room.DefenderId);

                    if (attacker != null)
                    {
                        attacker.IsAttacker = true;
                    }
                    else
                    {
                        // Attacker yoxdursa - birinci aktiv oyunçu
                        activePlayers[0].IsAttacker = true;
                        room.AttackerId = activePlayers[0].UserId;
                    }

                    if (defender != null)
                    {
                        defender.IsDefender = true;
                    }
                    else
                    {
                        // Defender yoxdursa - ikinci aktiv oyunçu
                        var newDef = activePlayers.FirstOrDefault(p => p.UserId != room.AttackerId);
                        if (newDef != null)
                        {
                            newDef.IsDefender = true;
                            room.DefenderId = newDef.UserId;
                        }
                    }

                    Console.WriteLine($"   ✅ Normal oyunçu getdi - roller dəyişmədi");
                }

                room.AttackerQueue?.RemoveAll(id => id == disconnectedUserId);
                room.PlayersWhoPassedThisRound?.Remove(disconnectedUserId);
                room.TakeCardsVotes?.Remove(disconnectedUserId);
                room.IsThrowInPhaseActive = false;

                Console.WriteLine($"   📊 Yeni AttackerId: {room.AttackerId}, DefenderId: {room.DefenderId}");
            }
        }
        public async Task<object> GetStatistics()
        {
            try
            {
                var stats = _roomManager.GetStatistics();
                return new { success = true, stats };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetStatistics error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }

        private async Task HandlePlayerLeftDuringGame(string roomId, DurakRoom room, DurakPlayer winner, DurakPlayer loser)
        {
            Console.WriteLine($"🏆 Winner due to disconnect: {winner.Name}");

            if (room.TotalPrize > 0)
            {
                decimal commission = room.TotalPrize * COMMISSION_RATE;
                decimal winnerPrize = room.TotalPrize - commission;

                var winnerUser = _db.Users.FirstOrDefault(u => u.Id == winner.UserId);
                if (winnerUser != null)
                {
                    winnerUser.Balance += winnerPrize;

                    try
                    {
                        await _db.SaveChangesAsync();
                        Console.WriteLine($"💰 Prize distributed: {winnerPrize} AZN to {winner.Name} (New balance: {winnerUser.Balance})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ DB Save Error: {ex.Message}");
                        winnerUser.Balance -= winnerPrize;
                    }
                }

                try
                {
                    await _rankService.UpdateRankAfterGame(winner.UserId, GameType.Durak, true, winnerPrize);
                    await _rankService.UpdateRankAfterGame(loser.UserId, GameType.Durak, false, 0);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Rank Update Error: {ex.Message}");
                }

                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winner = winner.Name,
                    durak = loser.Name,
                    message = $"🏆 {winner.Name} QALIB!\n{loser.Name} oyunu tərk etdi",
                    winnerPrize,
                    commission
                });
            }
            else
            {
                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winner = winner.Name,
                    durak = loser.Name,
                    message = $"🏆 {winner.Name} qalib gəldi!\n{loser.Name} oyunu tərk etdi"
                });
            }

            lock (room.StateLock)
            {
                room.ResetGame();
            }

            await BroadcastGameState(roomId);

            Console.WriteLine($"✅ Game ended due to disconnect in {room.RoomName}");
        }

        public async Task Attack(List<Card> cards)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive) return;

            var userId = GetUserId();
            DurakPlayer? player = null;
            bool isThrowInAttack = false;

            lock (room.StateLock)
            {
                bool isMainAttacker = (userId == room.AttackerId);
                if (room.IsThrowInPhaseActive)
                {
                    if (room.DefenderId == userId)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "Müdafiəçi throw-in edə bilməz"));
                        return;
                    }

                    var currentThrowInPlayerId = FindNextThrowInAttackerLocked(room);

                    if (currentThrowInPlayerId == 0)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "Throw-in növbəsi bitib"));
                        return;
                    }

                    if (currentThrowInPlayerId != userId)
                    {
                        var currentThrowInPlayer = room.Players.FirstOrDefault(p => p.UserId == currentThrowInPlayerId);
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            $"⏳ Sizin sıranız deyil — {currentThrowInPlayer?.Name ?? "digər oyunçu"} throw-in etməlidir"));
                        return;
                    }

                    if (room.PlayersWhoPassedThisRound.Contains(userId))
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "❌ Pas etdiniz — artıq throw-in edə bilməzsiniz"));
                        return;
                    }

                    isThrowInAttack = true;
                }
                else if (isMainAttacker)
                {
                    // ✅ BrokenBeaten fazasında Main Attacker hücum EDƏMƏZ
                    if (room.IsBrokenBeatenPhaseActive)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "❌ Beaten etdiniz — indi queue oyunçularının sırası"));
                        return;
                    }

                    // ✅ TakeCard fazasında Main Attacker hücum EDƏ BİLƏR — queue sıfırlama
                    // Normal hücum — queue sıfırla
                    room.IsBeatenPhaseActive = false;
                    room.IsTakeCardPhaseActive = false;  // ← Main Attacker hücum etdi, faza bitdi
                    room.AttackerQueue.Clear();
                    room.PlayersWhoPassedThisRound.Clear();
                    room.CurrentAttackerQueueIndex = 0;

                    Console.WriteLine($"✅ Main Attacker ({room.Players.FirstOrDefault(p => p.UserId == userId)?.Name}) hücum etti - Queue sıfırlandı");


                    // ✅ Normal hücum — queue sıfırla
                    room.IsBeatenPhaseActive = false;
                    room.IsTakeCardPhaseActive = false;
                    room.AttackerQueue.Clear();
                    room.PlayersWhoPassedThisRound.Clear();
                    room.CurrentAttackerQueueIndex = 0;

                    Console.WriteLine($"✅ Main Attacker ({room.Players.FirstOrDefault(p => p.UserId == userId)?.Name}) hücum etti - Queue sıfırlandı");
                }
                else
                {
                    if (room.Players.Count > 2)
                    {
                        if (room.IsBrokenBeatenPhaseActive)
                        {
                            // ✅ BrokenBeaten fazasında queue yoxlaması
                            if (room.AttackerQueue.Count == 0)
                            {
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    "⏳ Queue aktiv deyil"));
                                return;
                            }

                            var currentInQueueBB = room.GetCurrentAttackerInQueue();
                            if (currentInQueueBB == null || currentInQueueBB != userId)
                            {
                                var currentPlayerBB = room.Players.FirstOrDefault(p => p.UserId == currentInQueueBB);
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    $"⏳ Sizin sıranız deyil — {currentPlayerBB?.Name ?? "digər oyunçu"} hücum etməlidir"));
                                return;
                            }

                            if (room.PlayersWhoPassedThisRound.Contains(userId))
                            {
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    "❌ Pas etdiniz — artıq hücum edə bilməzsiniz"));
                                return;
                            }

                            // ✅ BrokenBeaten-də sırada qal, keçmə — davam et
                        }
                        else
                        {
                            // ✅ Normal queue yoxlaması
                            if (!room.IsBeatenPhaseActive && !room.IsTakeCardPhaseActive)
                            {
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    "⏳ Gözləyin — əvvəlcə müdafiəçi cavab verməlidir"));
                                return;
                            }

                            if (room.AttackerQueue.Count == 0)
                            {
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    "⏳ Gözləyin — əvvəlcə müdafiəçi cavab verməlidir"));
                                return;
                            }

                            var currentInQueue = room.GetCurrentAttackerInQueue();
                            if (currentInQueue == null || currentInQueue != userId)
                            {
                                var currentPlayer = room.Players.FirstOrDefault(p => p.UserId == currentInQueue);
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    $"⏳ Sizin sıranız deyil — {currentPlayer?.Name ?? "digər oyunçu"} hücum etməlidir"));
                                return;
                            }

                            if (room.PlayersWhoPassedThisRound.Contains(userId))
                            {
                                Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                                    "❌ Bu raundda pas etdiniz"));
                                return;
                            }
                        }
                    }
                }

                // ═══════════════════════════════════════════════════════════════════════════════
                // ✅ VALIDATION — BrokenBeaten fazasında ayrı validation
                // ═══════════════════════════════════════════════════════════════════════════════
                AttackValidationResult validation;
                if (!isMainAttacker && room.IsBrokenBeatenPhaseActive)
                {
                    validation = room.GameEngine.ValidateBrokenBeatenAttack(userId, cards);
                }
                else
                {
                    validation = room.GameEngine.ValidateAttack(userId, cards);
                }

                if (!validation.IsValid)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                // ═══════════════════════════════════════════════════════════════════════════════
                // ✅ EXECUTE — BrokenBeaten fazasında ayrı execute
                // ═══════════════════════════════════════════════════════════════════════════════
                if (!isMainAttacker && room.IsBrokenBeatenPhaseActive)
                {
                    room.GameEngine.ExecuteBrokenBeatenAttack(userId, cards);
                    Console.WriteLine($"✅ BrokenBeaten - {player.Name} {cards.Count} kart atdı - sırada qalır");
                }
                else
                {
                    room.GameEngine.ExecuteAttack(userId, cards);
                }

                // ✅ Queue oyunçusu hücum etdikdə sırada QALIN — yalnız PassAttack() ilə növbə keçir
                // MoveToNextAttackerInQueue() burda ÇAĞIRILMIR
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ BROADCAST
            // ═══════════════════════════════════════════════════════════════════════════════
            await _hubContext.Clients.Group(roomId).SendAsync("CardsAttacked", new
            {
                playerName = player!.Name,
                cards = cards.Select(c => new { rank = c.Rank, suit = c.Suit }).ToList(),
                isMainAttacker = (userId == room.AttackerId)
            });

            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("YourCards", player.Hand);
            await BroadcastGameState(roomId);

            if (isThrowInAttack)
            {
                await ShowTakeCardModalToNextPlayer(roomId, room);
            }
        }
        public async Task AcceptTakeCards()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            bool shouldCheckVotingResult = false;
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            lock (room.StateLock)
            {
                if (!room.IsThrowInPhaseActive)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Throw-in fase aktiv deyil"));
                    return;
                }

                if (room.DefenderId == userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Müdafiəçi səs verə bilməz"));
                    return;
                }

                var currentThrowInPlayerId = room.AttackerQueue
                    .Where(playerId => !room.TakeCardsVotes.Contains(playerId) &&
                                       !room.PlayersWhoPassedThisRound.Contains(playerId))
                    .FirstOrDefault();

                if (currentThrowInPlayerId == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Throw-in növbəsi bitib"));
                    return;
                }

                if (currentThrowInPlayerId != userId)
                {
                    var currentThrowInPlayer = room.Players.FirstOrDefault(p => p.UserId == currentThrowInPlayerId);
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        $"⏳ Sizin sıranız deyil — {currentThrowInPlayer?.Name ?? "digər oyunçu"} qərar verməlidir"));
                    return;
                }

                if (room.TakeCardsVotes.Contains(userId) || room.PlayersWhoPassedThisRound.Contains(userId))
                {
                    return;
                }

                // Razılıq kartların dərhal götürülməsi demək deyil. Bu oyunçu artıq
                // throw-in etməyəcəyini bildirir və növbə növbəti queue oyunçusuna keçir.
                // Əvvəlki kod burada birbaşa ExecuteDefenderTakesCards çağırırdı; buna
                // görə bir kart atıldıqdan sonra açılan modalda Agree seçiləndə bütün
                // masa dərhal bağlanırdı.
                room.TakeCardsVotes.Add(userId);
                shouldCheckVotingResult = room.TakeCardsVotes.Count +
                    room.PlayersWhoPassedThisRound.Count >= room.Players.Count - 1;
                Console.WriteLine($"✅ {player.Name} Agree etdi - vote qeyd edildi " +
                                  $"({room.TakeCardsVotes.Count}/{room.Players.Count - 1})");
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerVoted", new
            {
                playerName = player.Name,
                action = "Accept",
                message = $"{player.Name} razılaşdı",
                acceptCount = room.TakeCardsVotes.Count,
                rejectCount = room.PlayersWhoPassedThisRound.Count,
                totalNeeded = room.Players.Count - 1
            });

            if (shouldCheckVotingResult)
                await ExecuteDefenderTakesCards(roomId, room);
            else
                await ShowTakeCardModalToNextPlayer(roomId, room);

            await BroadcastGameState(roomId);
        }
        public async Task RejectTakeCards()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            lock (room.StateLock)
            {
                if (!room.IsThrowInPhaseActive)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Throw-in fase aktiv deyil"));
                    return;
                }

                if (room.DefenderId == userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Müdafiəçi rədd edə bilməz"));
                    return;
                }

                var currentThrowInPlayerId = room.AttackerQueue
                    .Where(playerId => !room.TakeCardsVotes.Contains(playerId) &&
                                       !room.PlayersWhoPassedThisRound.Contains(playerId))
                    .FirstOrDefault();

                if (currentThrowInPlayerId == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Throw-in növbəsi bitib"));
                    return;
                }

                if (currentThrowInPlayerId != userId)
                {
                    var currentThrowInPlayer = room.Players.FirstOrDefault(p => p.UserId == currentThrowInPlayerId);
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        $"⏳ Sizin sıranız deyil — {currentThrowInPlayer?.Name ?? "digər oyunçu"} qərar verməlidir"));
                    return;
                }

                if (room.PlayersWhoPassedThisRound.Contains(userId) || room.TakeCardsVotes.Contains(userId))
                {
                    return;
                }

                // ✅ Rədd qeyd et
                room.PlayersWhoPassedThisRound.Add(userId);
                Console.WriteLine($"❌ {player.Name} rədd etdi (REJECT) - {room.PlayersWhoPassedThisRound.Count} oyunçu");
            }

            // ✅ Bildiriş göndər
            await _hubContext.Clients.Group(roomId).SendAsync("PlayerVoted", new
            {
                playerName = player.Name,
                action = "Reject",
                message = $"{player.Name} rədd etdi",
                acceptCount = room.TakeCardsVotes.Count,
                rejectCount = room.PlayersWhoPassedThisRound.Count,
                totalNeeded = room.Players.Count - 1
            });

            // ✅ YENİ - Növbəti oyunçuya modal göstər
            await ShowTakeCardModalToNextPlayer(roomId, room);

            await BroadcastGameState(roomId);
        }
        public async Task VoteTakeCards(bool accept)
        {
            // Backward compatibility for older clients:
            // accept confirms the defender should take the table cards.
            if (accept)
            {
                await AcceptTakeCards();
            }
            else
            {
                await RejectTakeCards();
            }
        }
        public async Task GetThrowInStatus()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ThrowInStatus", new
                {
                    isActive = false,
                    error = "Otaqda deyilsiniz"
                });
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ThrowInStatus", new
                {
                    isActive = false,
                    error = "Otaq tapılmadı"
                });
                return;
            }

            lock (room.StateLock)
            {
                var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                int totalNonDefenders = room.Players.Count - 1;

                Clients.Caller.SendAsync("ThrowInStatus", new
                {
                    isActive = room.IsThrowInPhaseActive,
                    defenderName = defender?.Name,
                    acceptCount = room.TakeCardsVotes.Count,
                    rejectCount = room.PlayersWhoPassedThisRound.Count,
                    totalNeeded = totalNonDefenders,
                    message = room.IsThrowInPhaseActive
                      ? $"Throw-in: {room.TakeCardsVotes.Count} razı, {room.PlayersWhoPassedThisRound.Count} rədd, {totalNonDefenders} lazım"
                      : "Throw-in aktiv deyil",
                    canAccept = room.IsThrowInPhaseActive && room.DefenderId != GetUserId(),
                    canReject = room.IsThrowInPhaseActive && room.DefenderId != GetUserId()
                });
            }
        }
        public async Task RequestTakeCardsConfirmation()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktif deyil");
                return;
            }

            var userId = GetUserId();

            lock (room.StateLock)
            {
                if (room.DefenderId != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Yalnız müdafiəçi başlada bilər"));
                    return;
                }

                if (!room.IsThrowInPhaseActive)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Əvvəlcə 'Take Cards' düyməsini basın"));
                    return;
                }

                room.TakeCardsVotes.Clear();
                room.PlayersWhoPassedThisRound.Clear();
            }

            foreach (var p in room.Players)
            {
                if (p.UserId != userId)
                {
                    await _hubContext.Clients.Client(p.ConnectionId).SendAsync("ConfirmTakeCards", new
                    {
                        defenderName = room.Players.FirstOrDefault(pl => pl.UserId == userId)?.Name,
                        message = "Müdafiəçi kartları götürmək istəyir. Razı mısınız?",
                        timeoutSeconds = 10
                    });
                }
            }

            Console.WriteLine($"📋 Take cards confirmation requested by defender");
        }
        public async Task Defend(List<DefendPair> defenses)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Otaq tapılmadı");
                return;
            }

            if (!room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun başlamayıb");
                return;
            }

            var userId = GetUserId();
            DurakPlayer? defender = null;

            lock (room.StateLock)
            {
                var validation = room.GameEngine.ValidateDefend(userId, defenses);
                if (!validation.IsValid)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
                    return;
                }

                defender = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (defender == null) return;

                room.GameEngine.ExecuteDefend(userId, defenses);

                // ═══════════════════════════════════════════════════════════════════════════════
                // ✅ 3P+ QUEUE BAŞLATMA - DEFENDER İLK KART VURDUKTAN SONRA
                // ═══════════════════════════════════════════════════════════════════════════════
                if (room.Players.Count > 2 && room.DefendedPairs.Count == 1 && room.AttackerQueue.Count == 0)
                {
                    // ✅ Queue-ni açmadan əvvəl Beaten/TakeCard fazalarını sıfırla
                    room.IsBeatenPhaseActive = false;
                    room.IsTakeCardPhaseActive = false;

                    // ✅ Queue başlat - İndi Queue oyunçuları hücum edə biləcəklər
                    room.InitializeAttackerQueue();

                    // ✅ DÜZƏLLŞ: Main Attacker sırada ilk olduğundan, finish olmadı
                    room.MainAttackerFinished = false;

                    Console.WriteLine($"✅ Defender ilk kartı vurdu - Queue aktivləşdi");
                    Console.WriteLine($"   MainAttackerFinished: {room.MainAttackerFinished}");
                    Console.WriteLine($"   Queue: {string.Join(" → ", room.AttackerQueue.Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name ?? id.ToString()))}");
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("CardsDefended", new
            {
                playerName = defender.Name,
                defenses = defenses.Select(d => new
                {
                    attackCard = new { rank = d.AttackCard.Rank, suit = d.AttackCard.Suit },
                    defendCard = new { rank = d.DefendCard.Rank, suit = d.DefendCard.Suit }
                }).ToList()
            });

            await _hubContext.Clients.Client(defender.ConnectionId).SendAsync("YourCards", defender.Hand);
            await BroadcastGameState(roomId);

            bool shouldCheckGameOver = false;
            lock (room.StateLock)
            {
                if (defender.Hand.Count == 0 && room.Deck.Count == 0)
                {
                    shouldCheckGameOver = true;
                }
            }

            if (shouldCheckGameOver)
            {
                await Task.Delay(500);

                // ✅ Trump kartını deck-dən çıxart
                if (room.TrumpCard != null && room.Deck.Contains(room.TrumpCard))
                {
                    room.Deck.Remove(room.TrumpCard);
                    Console.WriteLine($"✅ Trump kartı deskin dışında - game over check");
                }

                var result = room.CheckGameOver();
                if (result != null)
                {
                    await EndGame(roomId, result);
                }
            }
        }
        public async Task TakeCards()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            DurakPlayer? defender = null;
            List<string> allowedRanks = new();

            lock (room.StateLock)
            {
                if (room.DefenderId != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Yalnız müdafiəçi götürmək istəyə bilər"));
                    return;
                }

                defender = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (defender == null) return;

                if (room.IsThrowInPhaseActive)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Throw-in fase artıq aktivdir"));
                    return;
                }

                room.IsThrowInPhaseActive = true;
                room.IsBeatenPhaseActive = false;
                room.IsTakeCardPhaseActive = false;
                room.IsBrokenBeatenPhaseActive = false;

                room.AttackerQueue.Clear();
                room.PlayersWhoPassedThisRound.Clear();
                room.CurrentAttackerQueueIndex = 0;
                room.TakeCardsVotes.Clear();

                int defenderIndex = room.Players.FindIndex(p => p.UserId == userId);
                for (int i = 1; i < room.Players.Count; i++)
                {
                    int index = (defenderIndex + i) % room.Players.Count;
                    room.AttackerQueue.Add(room.Players[index].UserId);
                }

                allowedRanks = GetThrowInAllowedRanks(room);

                Console.WriteLine($"📥 {defender.Name} götürmək istəyir - THROW-IN FASE BAŞLADI");
                Console.WriteLine($"   Throw-in Queue: {string.Join(" → ", room.AttackerQueue.Select(id => room.Players.First(p => p.UserId == id).Name))}");
                Console.WriteLine($"   ✅ Bütün modlarda Take Card üçün Agree/Pass modalı açılır");
            }

            await _hubContext.Clients.Group(roomId).SendAsync("ThrowInPhaseStarted", new
            {
                defenderName = defender.Name,
                message = $"{defender.Name} kartları götürmək istəyir! THROW-IN başladı",
                maxCards = Math.Min(6, defender.Hand.Count),
                allowedRanks
            });

            await ShowTakeCardModalToNextPlayer(roomId, room);
            await BroadcastGameState(roomId);
        }
        private async Task ShowTakeCardModalToNextPlayer(string roomId, DurakRoom room)
        {
            bool shouldCheckVotingResult = false;
            DurakPlayer? nextPlayer = null;
            string? defenderName = null;
            int acceptCount = 0;
            int rejectCount = 0;
            int totalNeeded = 0;

            lock (room.StateLock)
            {
                var nextPlayerId = FindNextThrowInAttackerLocked(room);

                if (nextPlayerId == 0)
                {
                    shouldCheckVotingResult = true;
                }
                else
                {
                    nextPlayer = room.Players.FirstOrDefault(p => p.UserId == nextPlayerId);
                    defenderName = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name;
                    acceptCount = room.TakeCardsVotes.Count;
                    rejectCount = room.PlayersWhoPassedThisRound.Count;
                    totalNeeded = room.Players.Count - 1;
                }
            }

            if (shouldCheckVotingResult)
            {
                await CheckTakeCardVotingResult(roomId, room);
                return;
            }

            if (nextPlayer == null || string.IsNullOrEmpty(nextPlayer.ConnectionId))
            {
                return;
            }

            await _hubContext.Clients.Client(nextPlayer.ConnectionId).SendAsync("ShowTakeCardModal", new
            {
                defenderName,
                message = "Müdafiəçi kartları götürmək istəyir. Razısınız?",
                acceptCount,
                rejectCount,
                totalNeeded
            });
        }

        private static List<string> GetThrowInAllowedRanks(DurakRoom room)
        {
            return room.TableCards.Select(c => c.Rank)
                .Concat(room.DefendedPairs.SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank }))
                .Distinct()
                .ToList();
        }

        private static List<string> GetBeatenAllowedRanks(DurakRoom room)
        {
            return room.DefendedPairs
                .SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank })
                .Concat(room.TableCards.Select(c => c.Rank))
                .Distinct()
                .ToList();
        }

        private static bool HasPlayableAttackCard(DurakRoom room, DurakPlayer player, List<string> allowedRanks)
        {
            return GetMaxNewAttackCardsStatic(room) > 0 &&
                   allowedRanks.Count > 0 &&
                   player.Hand.Any(c => allowedRanks.Contains(c.Rank));
        }

        private static bool IsPlayerAvailableForQueue(DurakPlayer? player)
        {
            return player != null &&
                   !player.IsDisconnected &&
                   !string.IsNullOrWhiteSpace(player.ConnectionId);
        }

        private static void NormalizeQueueIndexLocked(DurakRoom room)
        {
            if (room.AttackerQueue.Count == 0)
            {
                room.CurrentAttackerQueueIndex = 0;
                return;
            }

            if (room.CurrentAttackerQueueIndex < 0)
                return;

            if (room.CurrentAttackerQueueIndex >= room.AttackerQueue.Count)
                room.CurrentAttackerQueueIndex = room.AttackerQueue.Count - 1;
        }

        private static int FindNextThrowInAttackerLocked(DurakRoom room)
        {
            var allowedRanks = GetThrowInAllowedRanks(room);

            foreach (var playerId in room.AttackerQueue)
            {
                if (room.TakeCardsVotes.Contains(playerId) ||
                    room.PlayersWhoPassedThisRound.Contains(playerId))
                    continue;

                var player = room.Players.FirstOrDefault(p => p.UserId == playerId);
                if (IsPlayerAvailableForQueue(player) && HasPlayableAttackCard(room, player!, allowedRanks))
                    return playerId;

                room.PlayersWhoPassedThisRound.Add(playerId);
                Console.WriteLine($"⏭️ Throw-in auto-pass: {player?.Name ?? playerId.ToString()} uyğun kartı yoxdur");
            }

            return 0;
        }

        private static int? FindNextBeatenAttackerLocked(DurakRoom room)
        {
            var allowedRanks = GetBeatenAllowedRanks(room);
            NormalizeQueueIndexLocked(room);

            while (true)
            {
                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker == null || currentAttacker == 0)
                    return null;

                if (currentAttacker == room.AttackerId || currentAttacker == room.DefenderId)
                {
                    room.PlayersWhoPassedThisRound.Add(currentAttacker.Value);
                    room.MoveToNextAttackerInQueue();
                    continue;
                }

                var player = room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                if (IsPlayerAvailableForQueue(player) && HasPlayableAttackCard(room, player!, allowedRanks))
                    return currentAttacker;

                room.PlayersWhoPassedThisRound.Add(currentAttacker.Value);
                Console.WriteLine($"⏭️ Beaten auto-pass: {player?.Name ?? currentAttacker.Value.ToString()} uyğun kartı yoxdur");
                room.MoveToNextAttackerInQueue();
            }
        }

        private static int GetMaxNewAttackCardsStatic(DurakRoom room)
        {
            int currentAttackCardCount = room.DefendedPairs.Count + room.TableCards.Count;
            int maxByTableLimit = 6 - currentAttackCardCount;

            var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
            if (defender == null)
                return Math.Max(0, maxByTableLimit);

            int maxByDefenderCards = defender.Hand.Count - room.TableCards.Count;
            int maxNew = Math.Min(maxByTableLimit, maxByDefenderCards);
            return Math.Max(0, maxNew);
        }

        // ✅ YENI METOD
        private async Task AutoExecuteBeaten(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔥 AUTO BEATEN - 6 kart müdafiə olundu");



            bool gameEnded = false;
            int oldAttackerId = room.AttackerId;
            int oldDefenderId = room.DefenderId;

            GameEndResult? gameResult = null;


            lock (room.StateLock)
            {
                // Kartları yandır
                room.GameEngine.ExecuteBeat();

                ApplyRolesAfterBeaten(room, oldDefenderId);

                room.ResetAttackRound();
                room.RefillHands();

                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            foreach (var p in room.Players)
            {
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("CardsDiscarded", new
            {
                message = $"🔥 6 kart müdafiə olundu - AVTOMATIK BEATEN!\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}\n🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name}"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
            {
                await EndGame(roomId, gameResult);
            }
        }
        private async Task CheckTakeCardVotingResult(string roomId, DurakRoom room)
        {
            bool shouldExecuteTakeCards = false;

            lock (room.StateLock)
            {
                int totalNonDefenders = room.Players.Count - 1;
                int totalVoted = room.TakeCardsVotes.Count + room.PlayersWhoPassedThisRound.Count;

                if (totalVoted >= totalNonDefenders)
                {
                    shouldExecuteTakeCards = true;
                }
            }

            if (shouldExecuteTakeCards)
            {
                await ExecuteDefenderTakesCards(roomId, room);
            }
        }
        public async Task PassAttack()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            int? nextAttackerId = null;
            bool shouldExecuteTakeCards = false;
            bool shouldExecuteBeaten = false;

            lock (room.StateLock)
            {
                if (room.Players.Count == 2)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "2 oyunçuda pas mümkün deyil"));
                    return;
                }

                if (room.TableCards.Count == 0 && room.DefendedPairs.Count == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "İlk hücumdan əvvəl pas edilə bilməz"));
                    return;
                }

                if (room.IsBrokenBeatenPhaseActive)
                {
                    var currentAttackerBB = room.GetCurrentAttackerInQueue();
                    if (currentAttackerBB == null || currentAttackerBB.Value != userId)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "⏳ Sizin sıranız deyil"));
                        return;
                    }


                    if (room.PlayersWhoPassedThisRound.Contains(userId))
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "❌ Artıq pas etdiniz"));
                        return;
                    }

                    room.PlayersWhoPassedThisRound.Add(userId);
                    Console.WriteLine($"⏭️ {player.Name} BrokenBeaten-də pas etdi");

                    room.MoveToNextAttackerInQueue();
                    var nextBB = room.GetCurrentAttackerInQueue();

                    if (nextBB == null || nextBB == 0)
                    {
                        shouldExecuteBeaten = true;
                    }
                    else
                    {
                        nextAttackerId = nextBB;
                    }
                }
                else
                {
                    if (room.AttackerQueue.Count == 0)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "Attack queue aktiv deyil"));
                        return;
                    }

                    var currentAttacker = room.GetCurrentAttackerInQueue();
                    if (currentAttacker == null || currentAttacker.Value != userId)
                    {
                        Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                            "Sizin sıranız deyil"));
                        return;
                    }

                    if (room.PlayersWhoPassedThisRound.Contains(userId))
                    {
                        return;
                    }

                    room.PlayerPassThisRound(userId);
                    Console.WriteLine($"⏭️ {player.Name} pas etdi");

                    room.MoveToNextAttackerInQueue();
                    var nextAttacker = room.GetCurrentAttackerInQueue();

                    if (nextAttacker == null)
                    {
                        Console.WriteLine($"🛑 Hamı pas etti - yoxlanılır");

                        if (room.TableCards.Count == 0 && room.DefendedPairs.Count > 0)
                        {
                            Console.WriteLine($"🔥 Hamı pas etti və bütün kartlar müdafiə olunub - BEATEN!");
                            shouldExecuteBeaten = true;
                        }
                        else if (room.TableCards.Count > 0)
                        {
                            Console.WriteLine($"📥 Hamı pas etti və müdafiə olunmamış kartlar var - TAKE CARDS!");
                            shouldExecuteTakeCards = true;
                        }
                    }
                    else
                    {
                        nextAttackerId = nextAttacker;
                    }
                }
            }

            // ✅ HAMISI PAS ETDİ - BEATEN
            if (shouldExecuteBeaten)
            {
                // BrokenBeaten fazasında - defender yoxla
                if (room.IsBrokenBeatenPhaseActive)
                {
                    DurakPlayer? defender = null;
                    bool canDefendAll = true;
                    bool noNewCardsThrown = false;

                    lock (room.StateLock)
                    {
                        defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                        if (defender == null) return;

                        if (room.TableCards.Count == 0)
                        {
                            noNewCardsThrown = true;
                        }
                        else
                        {
                            foreach (var attackCard in room.TableCards)
                            {
                                bool canDefendThis = defender.Hand.Any(defCard =>
                                    room.GameEngine.CanDefend(attackCard, defCard));

                                if (!canDefendThis)
                                {
                                    canDefendAll = false;
                                    break;
                                }
                            }
                        }
                    }

                    if (noNewCardsThrown)
                    {
                        await CompleteBeatenDirectly(roomId, room);
                        return;
                    }

                    if (canDefendAll)
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                        {
                            message = $"Hamı pas etdi!\n{defender?.Name} müdafiə etməlidir!",
                            defenderName = defender?.Name,
                            canDefend = true
                        });
                        await BroadcastGameState(roomId);
                        return;
                    }
                    else
                    {
                        await ExecuteDefenderTakesCards(roomId, room);
                        return;
                    }
                }

                // Normal Beaten - 2P vs 3P+ ayrılır
                if (room.Players.Count == 2)
                {
                    lock (room.StateLock)
                    {
                        room.TableCards.Clear();
                        room.DefendedPairs.Clear();
                        int oldDefenderId2P = room.DefenderId;
                        int oldAttackerId2P = room.AttackerId;
                        room.AttackerId = oldDefenderId2P;
                        room.DefenderId = oldAttackerId2P;
                        room.ResetAttackRound();
                        room.RefillHands();
                    }

                    foreach (var p in room.Players)
                        await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

                    await _hubContext.Clients.Group(roomId).SendAsync("BeatenComplete", new
                    {
                        message = $"✅ Beaten tamamlandı!\n⚔️ Yeni Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}"
                    });

                    await BroadcastGameState(roomId);

                    var result2P = room.CheckGameOver();
                    if (result2P != null) await EndGame(roomId, result2P);
                    return;
                }
                else
                {
                    // 3P+ → BrokenBeaten fazasını aç
                    lock (room.StateLock)
                    {
                        room.IsBeatenPhaseActive = true;
                        room.IsTakeCardPhaseActive = false;
                        room.IsBrokenBeatenPhaseActive = true;

                        room.AttackerQueue.Clear();
                        room.PlayersWhoPassedThisRound.Clear();
                        room.CurrentAttackerQueueIndex = 0;

                        foreach (var p in room.Players)
                        {
                            if (p.UserId != room.AttackerId && p.UserId != room.DefenderId)
                                room.AttackerQueue.Add(p.UserId);
                        }

                        Console.WriteLine($"✅ PassAttack → BEATEN PHASE (3P+):");
                        Console.WriteLine($"   Main Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}");
                        Console.WriteLine($"   Defender: {room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name}");
                        Console.WriteLine($"   Queue: {string.Join(" → ", room.AttackerQueue.Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name ?? id.ToString()))}");
                    }

                    if (room.AttackerQueue.Count == 0)
                    {
                        await CompleteBeatenDirectly(roomId, room);
                        return;
                    }

                    await _hubContext.Clients.Group(roomId).SendAsync("BeatenPhaseStarted", new
                    {
                        message = "✅ Beaten! Queue oyunçuları hücum edə bilər VƏ YA pas edə bilərlər",
                        attackerId = room.AttackerId,
                        defenderId = room.DefenderId,
                        queueOrder = room.AttackerQueue
                            .Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name)
                            .Where(n => n != null)
                            .ToList()
                    });

                    await BroadcastGameState(roomId);
                    await ShowBeatenQueueChoiceModal(roomId, room);
                    return;
                }
            }

            // ✅ HAMISI PAS ETDİ - TAKE CARDS
            if (shouldExecuteTakeCards)
            {
                await ExecuteDefenderTakesCards(roomId, room);
                return;
            }

            // ✅ NÖVBƏTI OYUNCUYA KEÇ
            if (nextAttackerId.HasValue)
            {
                var nextPlayer = room.Players.FirstOrDefault(p => p.UserId == nextAttackerId.Value);
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerPassed", new
                {
                    playerName = player.Name,
                    nextPlayer = nextPlayer?.Name,
                    message = $"⏭️ {player.Name} pas etdi → {nextPlayer?.Name} hücum edəcək"
                });

                if (room.IsBrokenBeatenPhaseActive)
                {
                    await ShowBeatenQueueChoiceModal(roomId, room);
                }
            }

            await BroadcastGameState(roomId);
        }
        private async Task ExecuteBeatenAfterQueue(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔥 BEATEN (Queue bitdi)");

            bool gameEnded = false;
            int oldAttackerId = room.AttackerId;
            int oldDefenderId = room.DefenderId;
            GameEndResult? gameResult = null;

            lock (room.StateLock)
            {
                // Kartları yandır
                room.GameEngine.ExecuteBeat();

                ApplyRolesAfterBeaten(room, oldDefenderId);

                // Queue sıfırla
                room.ResetAttackRound();

                // Kartları doldur
                room.RefillHands();

                // Oyun bitdi mi?
                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            // Hamıya yeni kartlar göndər
            foreach (var p in room.Players)
            {
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("CardsDiscarded", new
            {
                message = $"🔥 Kartlar yandırıldı!\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}\n🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name}"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
            {
                await EndGame(roomId, gameResult);
            }

            Console.WriteLine($"✅ Beaten (Queue sonrası) tamamlandı");
        }

        // ✅ YENİ METOD - Müdafiəçi avtomatik götürür
        private async Task ExecuteDefenderTakesCards(string roomId, DurakRoom room)
        {
            DurakPlayer? defender = null;
            int oldAttackerId = 0;
            int oldDefenderId = 0;
            int newAttackerId = 0;
            int newDefenderId = 0;
            int totalCards = 0;
            bool gameEnded = false;
            GameEndResult? gameResult = null;

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null)
                {
                    Console.WriteLine("❌ Defender tapılmadı!");
                    return;
                }

                oldAttackerId = room.AttackerId;
                oldDefenderId = room.DefenderId;

                defender.Hand.AddRange(room.TableCards);
                foreach (var pair in room.DefendedPairs)
                {
                    defender.Hand.Add(pair.AttackCard);
                    defender.Hand.Add(pair.DefendCard);
                }

                totalCards = room.TableCards.Count + (room.DefendedPairs.Count * 2);

                room.TableCards.Clear();
                room.DefendedPairs.Clear();
                room.TakeCardsVotes.Clear();

                // ✅ BÜTÜN FAZA FLAG-LARINI SIFIRLA
                room.IsThrowInPhaseActive = false;
                room.IsBrokenBeatenPhaseActive = false;
                room.IsBeatenPhaseActive = false;
                room.IsTakeCardPhaseActive = false;

                Console.WriteLine($"📥 {defender.Name} {totalCards} kart götürdü");

                (newAttackerId, newDefenderId) = ApplyRolesAfterDefenderTakes(room, oldAttackerId, oldDefenderId);

                room.AttackerQueue.Clear();
                room.PlayersWhoPassedThisRound.Clear();
                room.CurrentAttackerQueueIndex = 0;
                room.MainAttackerFinished = false;

                Console.WriteLine($"📥 TakeCards ({room.Players.Count}P):");
                Console.WriteLine($"   YENİ ATTACKER: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}");
                Console.WriteLine($"   Old Defender ({defender.Name}) kart aldı");
                Console.WriteLine($"   YENİ DEFENDER: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}");

                room.RefillHands();

                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            var newAttacker = room.Players.FirstOrDefault(p => p.UserId == newAttackerId);
            var newDefender = room.Players.FirstOrDefault(p => p.UserId == newDefenderId);

            foreach (var p in room.Players)
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

            await _hubContext.Clients.Group(roomId).SendAsync("CardsTaken", new
            {
                oldAttacker = room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name,
                oldDefender = defender!.Name,
                newAttacker = newAttacker?.Name,
                newDefender = newDefender?.Name,
                playerName = defender!.Name,
                totalCards,
                message = $"📥 {defender!.Name} {totalCards} kart götürdü!\n" +
                          $"⚔️ YENİ Attacker: {newAttacker?.Name}\n" +
                          $"🛡️ YENİ Defender: {newDefender?.Name}"
            });

            await _hubContext.Clients.Group(roomId).SendAsync("CloseBrokenBeatenModal", new
            {
                message = "Broken Beaten fazesinə son verildi"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
            {
                await Task.Delay(500);
                await EndGame(roomId, gameResult);
            }
        }
        public async Task BrokenBeatenPassFromModal()
        {
            var roomId = GetCurrentRoom();
            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            bool shouldCompleteBeaten = false;
            bool shouldExecuteTakeCards = false;

            lock (room.StateLock)
            {
                // ✅ VALIDATION: Main Attacker pas YAPMAZ
                if (userId == room.AttackerId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Main Attacker Broken Beaten-də pas YAPMAZ!"));
                    return;
                }

                // ✅ VALIDATION: Queue aktif
                if (room.AttackerQueue.Count == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Queue aktiv deyil"));
                    return;
                }

                // ✅ VALIDATION: Sırada olan oyunçu
                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Sizin sıranız deyil"));
                    return;
                }

                // ✅ Pas et
                room.PlayersWhoPassedThisRound.Add(userId);
                Console.WriteLine($"⏭️ {player.Name} pas etti");

                // ✅ Sonrakı sıraya keç
                room.MoveToNextAttackerInQueue();
                var nextAttacker = room.GetCurrentAttackerInQueue();

                // ═══════════════════════════════════════════════════════════════════════════════
                // 🎯 HAMISI PAS ETDİMİ?
                // ═══════════════════════════════════════════════════════════════════════════════

                if (nextAttacker == null || nextAttacker == 0)
                {
                    Console.WriteLine($"🛑 Hamı pas etti");

                    // ✅ Defender müdafiə edə biləcəkmi?
                    var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                    if (defender == null)
                    {
                        Console.WriteLine($"❌ Defender tapılmadı");
                        return;
                    }

                    bool canDefendAll = true;

                    // TableCards-a baxırıq (açıq kartlar)
                    if (room.TableCards.Count == 0)
                    {
                        // Bütün kartlar müdafiə edilib
                        canDefendAll = true;
                        Console.WriteLine($"✅ Bütün kartlar müdafiə edilib");
                    }
                    else
                    {
                        // Açıq kartlar var - defend edə biləcəkmi?
                        foreach (var attackCard in room.TableCards)
                        {
                            bool canDefendThis = false;

                            foreach (var defenseCard in defender.Hand)
                            {
                                if (room.GameEngine.CanDefend(attackCard, defenseCard))
                                {
                                    canDefendThis = true;
                                    break;
                                }
                            }

                            if (!canDefendThis)
                            {
                                canDefendAll = false;
                                break;
                            }
                        }
                    }

                    if (canDefendAll)
                    {
                        Console.WriteLine($"✅ Defender müdafiə edə bilər → BEATEN!");
                        shouldCompleteBeaten = true;
                    }
                    else
                    {
                        Console.WriteLine($"❌ Defender müdafiə edə bilmir → TAKE CARDS!");
                        shouldExecuteTakeCards = true;
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // 🎯 SONRAKINA MODAL GÖSTƏR
            // ═══════════════════════════════════════════════════════════════════════════════

            if (!shouldCompleteBeaten && !shouldExecuteTakeCards)
            {
                // ✅ Bu oyunçunun modalını bağla
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CloseBrokenBeatenQueueModal", new
                {
                    message = "⏭️ Pas etdiniz"
                });

                // ✅ Sonrakı oyunçuya modal göstər
                await ShowBrokenBeatenModalToNextPlayer(roomId, room);

                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenPlayerPassed", new
                {
                    playerName = player.Name,
                    message = $"⏭️ {player.Name} pas etti"
                });

                await BroadcastGameState(roomId);
                return;
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // 🎯 BEATEN TAMAMLAN
            // ═══════════════════════════════════════════════════════════════════════════════

            if (shouldCompleteBeaten)
            {
                Console.WriteLine($"🔥 BEATEN TAMAMLANDI");

                int oldAttackerId = 0;
                int oldDefenderId = 0;
                int newAttackerId = 0;
                int newDefenderId = 0;
                bool gameEnded = false;

                GameEndResult? gameResult = null;


                lock (room.StateLock)
                {
                    oldAttackerId = room.AttackerId;
                    oldDefenderId = room.DefenderId;

                    // ✅ Kartları yandır
                    room.TableCards.Clear();
                    room.DefendedPairs.Clear();

                    (newAttackerId, newDefenderId) = ApplyRolesAfterBeaten(room, oldDefenderId);

                        room.ResetAttackRound();
                        room.RefillHands();

                        var result = room.CheckGameOver();
                        if (result != null)
                        {
                            gameEnded = true;
                            gameResult = result;
                        }
                    }

                // ✅ Hamıya kartlar göndər
                foreach (var p in room.Players)
                {
                    await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);
                }

                await _hubContext.Clients.Group(roomId).SendAsync("CardsDiscarded", new
                {
                    message = $"🔥 BEATEN TAMAMLANDI - Kartlar yandırıldı!\n" +
                              $"⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}\n" +
                              $"🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}"
                });

                await BroadcastGameState(roomId);

                if (gameEnded)
                {
                    await Task.Delay(500);
                    await EndGame(roomId, gameResult);
                }

                Console.WriteLine($"✅ Beaten - roller dəyişdi");
                return;
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // 🎯 TAKE CARDS
            // ═══════════════════════════════════════════════════════════════════════════════

            if (shouldExecuteTakeCards)
            {
                await ExecuteDefenderTakesCards(roomId, room);
            }
        }
        private async Task ShowBrokenBeatenModalToNextPlayer(string roomId, DurakRoom room)
        {
            DurakPlayer? player;
            List<string> allowedRanks;
            int currentAttackCardCount;
            int maxNewCards;

            lock (room.StateLock)
            {
                var currentAttacker = FindNextBeatenAttackerLocked(room);
                if (currentAttacker == null || currentAttacker == 0)
                {
                    Console.WriteLine($"🛑 Queue bitib");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                if (player == null) return;

                allowedRanks = GetBeatenAllowedRanks(room);
                currentAttackCardCount = GetCurrentAttackCardCount(room);
                maxNewCards = GetMaxNewAttackCards(room);
            }

            Console.WriteLine($"📤 BB Modal → {player.Name}");

            // ✅ await ilə göndər
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("ShowBrokenBeatenQueueModal", new
            {
                currentAttackerName = player.Name,
                defenderName = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name,
                allowedRanks = allowedRanks,
                tableCardCount = currentAttackCardCount,
                maxNewCards = Math.Max(0, maxNewCards),
                canAttack = true,
                message = $"🔥 Sizin sıranız!"
            });
        }
        /// <summary>
        /// ✅ Maksimum yeni hücum kartı sayı
        /// </summary>
        private int GetMaxNewAttackCardsForRoom(DurakRoom room)
        {
            return GetMaxNewAttackCards(room);
        }
        private int GetMaxNewAttackCards(DurakRoom room)
        {
            return GetMaxNewAttackCardsStatic(room);
        }

        private int GetCurrentAttackCardCount(DurakRoom room)
        {
            return room.DefendedPairs.Count + room.TableCards.Count;
        }

        private async Task StartBrokenBeatenPhase(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔥 BROKEN BEATEN PHASE BAŞLADI");

            DurakPlayer defender = null;
            List<string> allowedRanks = new();

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null) return;

                // ✅ İcazə verilən rank-lar DefendedPairs-dən gəlir
                allowedRanks = room.DefendedPairs
                    .SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank })
                    .Distinct()
                    .ToList();

                // ✅ ƏGƏR TableCards varsa, onlardan da al
                if (room.TableCards.Count > 0)
                {
                    allowedRanks = allowedRanks
                        .Union(room.TableCards.Select(c => c.Rank))
                        .ToList();
                }

                Console.WriteLine($"📋 Broken Beaten başladı:");
                Console.WriteLine($"   Müdafiəçi: {defender.Name}");
                Console.WriteLine($"   İcazə verilən rank-lar: {string.Join(", ", allowedRanks)}");
                Console.WriteLine($"   DefendedPairs.Count: {room.DefendedPairs.Count}");
                Console.WriteLine($"   TableCards.Count: {room.TableCards.Count}");
            }

            // ✅ Hamıya bildiriş göndər
            await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenPhaseStarted", new
            {
                defenderName = defender?.Name,
                allowedRanks = allowedRanks,
                message = $"🔥 BROKEN BEATEN başladı! {string.Join(", ", allowedRanks)} rank-larına uyğun kartlar atıla bilərlər",
                queueOrder = string.Join(" → ", room.AttackerQueue.Select(id => room.Players.First(p => p.UserId == id).Name))
            });

            // ✅ İlk queue oyunçusuna modal göstər
            await ShowBrokenBeatenNotificationToNextPlayer(roomId, room);

            Console.WriteLine($"✅ Broken Beaten phase başladı");
        }

        // ✅ YENİ METOD - Növbəti oyunçuya BB notification göstər
        private async Task ShowBrokenBeatenNotificationToNextPlayer(string roomId, DurakRoom room)
        {
            DurakPlayer? player;
            List<string> allowedRanks;
            int currentAttackCardCount;
            int maxNewCards;

            lock (room.StateLock)
            {
                var currentAttacker = FindNextBeatenAttackerLocked(room);
                if (currentAttacker == null || currentAttacker == 0)
                {
                    Console.WriteLine($"🛑 Queue bitib");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                if (player == null)
                {
                    Console.WriteLine($"❌ Player not found for queue");
                    return;
                }

                allowedRanks = GetBeatenAllowedRanks(room);
                currentAttackCardCount = GetCurrentAttackCardCount(room);
                maxNewCards = GetMaxNewAttackCards(room);
            }

            Console.WriteLine($"📤 BB Notification → {player.Name}");
            Console.WriteLine($"   AllowedRanks: {string.Join(", ", allowedRanks)}");
            Console.WriteLine($"   AttackCards: {currentAttackCardCount}, MaxNew: {maxNewCards}");

            // ✅ await ilə göndər - fire-and-forget deyil
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("ShowBrokenBeatenQueueModal", new
            {
                currentAttackerName = player.Name,
                defenderName = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name,
                allowedRanks = allowedRanks,
                tableCardCount = currentAttackCardCount,
                maxNewCards = maxNewCards,
                canAttack = true,
                message = $"🔥 Sizin sıranız! {string.Join(", ", allowedRanks)} rank-larına uyğun kartlar əlavə edə bilərsiz"
            });
        }
        //public async Task BrokenBeatenAttack(List<Card> cards)
        //{
        //    var roomId = GetCurrentRoom();
        //    if (string.IsNullOrEmpty(roomId))
        //    {
        //        await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
        //        return;
        //    }

        //    var room = _roomManager.GetRoom(roomId);
        //    if (room == null || !room.IsGameActive)
        //    {
        //        await Clients.Caller.SendAsync("ActionError", "Oyun aktif deyil");
        //        return;
        //    }

        //    var userId = GetUserId();
        //    DurakPlayer player = null;
        //    bool queueEmpty = false;
        //    int? nextAttackerId = null;

        //    lock (room.StateLock)
        //    {
        //        if (room.AttackerQueue.Count == 0)
        //        {
        //            Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Queue aktiv deyil"));
        //            return;
        //        }

        //        if (userId == room.AttackerId)
        //        {
        //            Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Main Attacker bu fazada hücum edə bilməz"));
        //            return;
        //        }

        //        var currentAttacker = room.GetCurrentAttackerInQueue();
        //        if (currentAttacker == null || currentAttacker != userId)
        //        {
        //            Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Sizin sıranız deyil"));
        //            return;
        //        }

        //        if (room.PlayersWhoPassedThisRound.Contains(userId))
        //        {
        //            Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Pas etdiniz"));
        //            return;
        //        }

        //        var validation = room.GameEngine.ValidateBrokenBeatenAttack(userId, cards);
        //        if (!validation.IsValid)
        //        {
        //            Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
        //            return;
        //        }

        //        player = room.Players.FirstOrDefault(p => p.UserId == userId);
        //        if (player == null) return;

        //        // ✅ Kartları masaya əlavə et - DefendedPairs-ə TOXUNMA
        //        room.GameEngine.ExecuteBrokenBeatenAttack(userId, cards);

        //        // ✅ Hücum etdi = bu raundda bitdi, növbəni keçir
        //        room.PlayersWhoPassedThisRound.Add(userId);
        //        room.MoveToNextAttackerInQueue();

        //        var next = room.GetCurrentAttackerInQueue();
        //        if (next == null || next == 0)
        //        {
        //            queueEmpty = true;
        //            Console.WriteLine($"✅ {player.Name} hücum etdi - Queue bitdi");
        //        }
        //        else
        //        {
        //            nextAttackerId = next;
        //            Console.WriteLine($"✅ {player.Name} hücum etdi → {room.Players.FirstOrDefault(p => p.UserId == next)?.Name}");
        //        }
        //    }

        //    // ✅ Bu oyunçunun modalını bağla
        //    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CloseBrokenBeatenQueueModal", new
        //    {
        //        message = $"✅ {cards.Count} kart atıldı"
        //    });

        //    await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenCardAttacked", new
        //    {
        //        playerName = player.Name,
        //        cardsAttacked = cards.Count
        //    });

        //    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("YourCards", player.Hand);

        //    if (queueEmpty)
        //    {
        //        // ✅ Hamı bitdi - Beaten tamamla
        //        // DefendedPairs + yeni TableCards hamısını sil, roller dəyiş
        //        await CompleteBeatenDirectly(roomId, room);
        //        return;
        //    }

        //    // ✅ GameState göndər - DefendedPairs masada qalır, görünür
        //    await BroadcastGameState(roomId);

        //    // ✅ Növbəti oyunçuya modal göstər
        //    await ShowBeatenQueueModalToNextPlayer(roomId, room);
        //}

        public async Task BrokenBeatenAttack(List<Card> cards)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktif deyil");
                return;
            }

            var userId = GetUserId();
            DurakPlayer player = null;

            lock (room.StateLock)
            {
                if (room.AttackerQueue.Count == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Queue aktiv deyil"));
                    return;
                }

                if (userId == room.AttackerId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Main Attacker bu fazada hücum edə bilməz"));
                    return;
                }

                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker == null || currentAttacker != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Sizin sıranız deyil"));
                    return;
                }

                if (room.PlayersWhoPassedThisRound.Contains(userId))
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Siz artıq hücum etdiniz"));
                    return;
                }

                // ✅ VALIDATION
                var validation = room.GameEngine.ValidateBrokenBeatenAttack(userId, cards);
                if (!validation.IsValid)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                // ✅ Kartları masaya əlavə et
                room.GameEngine.ExecuteBrokenBeatenAttack(userId, cards);
                Console.WriteLine($"✅ {player.Name} {cards.Count} kart atdı - sırada qalır, attack/pass seçə bilər");
            }

            // ✅ Bu oyunçunun modal-ını bağla
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CloseBeatenAttackMode", new
            {
                message = $"✅ {cards.Count} kart atıldı"
            });

            // ✅ Hamıya bildiriş
            await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenCardAttacked", new
            {
                playerName = player.Name,
                cardsAttacked = cards.Count,
                cards = cards.Select(c => new { rank = c.Rank, suit = c.Suit }).ToList()
            });

            // ✅ Oyunçuya güncel kartları göndər
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("YourCards", player.Hand);
            await BroadcastGameState(roomId);

            // ✅ Eyni queue oyunçusu attack/pass seçimini davam etdirir
            await ShowBeatenQueueChoiceModal(roomId, room);
        }
        public async Task BrokenBeatenPass()
        {
            var roomId = GetCurrentRoom();
            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktif deyil");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            bool shouldCheckDefender = false;
            int? nextAttackerId = null;

            lock (room.StateLock)
            {
                if (userId == room.AttackerId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Main Attacker pas edə bilməz!"));
                    return;
                }

                if (room.AttackerQueue.Count == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Queue aktiv deyil"));
                    return;
                }

                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker == null || currentAttacker != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Şu an sizin sıranız deyil"));
                    return;
                }

                if (room.PlayersWhoPassedThisRound.Contains(userId))
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "❌ Artıq hücum etdiniz"));
                    return;
                }

                // ✅ Pas et - artıq hücum edə BILMƏZ
                room.PlayersWhoPassedThisRound.Add(userId);
                Console.WriteLine($"⏭️ {player.Name} pas etti");

                // ✅ Sonrakı sıraya keç
                room.MoveToNextAttackerInQueue();
                var next = room.GetCurrentAttackerInQueue();

                if (next == null || next == 0)
                {
                    shouldCheckDefender = true;
                    Console.WriteLine($"🛑 QUEUE BOŞDU - Hamı pas etti");
                }
                else
                {
                    nextAttackerId = next;
                    Console.WriteLine($"➡️ Sonrakı: {room.Players.FirstOrDefault(p => p.UserId == next)?.Name}");
                }
            }

            // ✅ Pas edənin modalını bağla
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CloseBrokenBeatenQueueModal", new
            {
                message = "⏭️ Pas etdiniz - novbəniz bitdi"
            });

            await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenPlayerPassed", new
            {
                playerName = player.Name,
                message = $"⏭️ {player.Name} pas etti"
            });

            // ═══════════════════════════════════════════════════════════════════════════════
            // HAMISI PAS ETDİ - DEFENDER YOXLANMASI
            // ═══════════════════════════════════════════════════════════════════════════════
            if (shouldCheckDefender)
            {
                DurakPlayer defender = null;
                bool canDefendAll = true;
                bool noNewCardsThrown = false;

                lock (room.StateLock)
                {
                    defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                    if (defender == null) return;

                    // ✅ Queue oyunçuları heç kart atmadısa → birbaşa beaten
                    if (room.TableCards.Count == 0)
                    {
                        noNewCardsThrown = true;
                    }
                    else
                    {
                        // ✅ DƏYİŞİKLİK: DefendedPairs yox, TableCards yoxlanır
                        foreach (var attackCard in room.TableCards)
                        {
                            bool canDefendThis = defender.Hand.Any(defCard =>
                                room.GameEngine.CanDefend(attackCard, defCard));

                            if (!canDefendThis)
                            {
                                canDefendAll = false;
                                Console.WriteLine($"   ❌ {attackCard.Rank} müdafiə edə bilmir");
                                break;
                            }
                        }
                    }
                }

                // Yeni kart yoxdur → beaten birbaşa tamamla
                if (noNewCardsThrown)
                {
                    Console.WriteLine($"✅ Queue oyunçuları kart atmadı → birbaşa BEATEN");
                    await CompleteBeatenDirectly(roomId, room);
                    return;
                }

                if (canDefendAll)
                {
                    Console.WriteLine($"🛡️ {defender?.Name} müdafiə etməli!");
                    await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                    {
                        message = $"Hamı pas etti!\n{defender?.Name} MƏCBUR müdafiə etməlidir!",
                        defenderName = defender?.Name,
                        canDefend = true
                    });
                    await BroadcastGameState(roomId);
                    return;
                }

                Console.WriteLine($"📥 {defender?.Name} müdafiə edə bilmir!");
                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustTake", new
                {
                    message = $"Hamı pas etti!\n{defender?.Name} müdafiə edə bilmir - Kartları götürməlidir!",
                    defenderName = defender?.Name,
                    canDefend = false
                });
                await ExecuteDefenderTakesCards(roomId, room);
                return;
            }
            // ✅ Davam - daha sırada oyuncu var
            await BroadcastGameState(roomId);

            // ✅ Sonrakı oyunçuya SEÇIM modal-ı göstər
            await ShowBeatenQueueChoiceModal(roomId, room);
        }
        private async Task CheckBrokenBeatenDefenderStatus(string roomId, DurakRoom room)
        {
            DurakPlayer? defender = null;
            bool defenderCanDefend = true;
            bool allCardsDefended = false;

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null)
                {
                    Console.WriteLine("❌ Defender tapılmadı!");
                    return;
                }

                // ✅ 1. Masada açıq kart varmı?
                var undefendedCards = room.TableCards.Where(c =>
                    !room.DefendedPairs.Any(p =>
                        p.AttackCard.Rank == c.Rank && p.AttackCard.Suit == c.Suit ||
                        p.DefendCard.Rank == c.Rank && p.DefendCard.Suit == c.Suit
                    )).ToList();

                if (undefendedCards.Count == 0)
                {
                    // Bütün kartlar müdafiə edilib
                    allCardsDefended = true;
                    Console.WriteLine($"✅ Bütün kartlar müdafiə edilib - BEATEN!");
                }
                else
                {
                    // ✅ 2. Defender müdafiə edə bilərmi?
                    Console.WriteLine($"🔍 {undefendedCards.Count} açıq kart var");

                    foreach (var attackCard in undefendedCards)
                    {
                        bool canDefendThis = false;

                        foreach (var defenseCard in defender.Hand)
                        {
                            if (room.GameEngine.CanDefend(attackCard, defenseCard))
                            {
                                canDefendThis = true;
                                break;
                            }
                        }

                        if (!canDefendThis)
                        {
                            defenderCanDefend = false;
                            break;
                        }
                    }
                }
            }

            // ✅ SENARYO A: Bütün kartlar müdafiə edilib → BEATEN
            if (allCardsDefended)
            {
                Console.WriteLine($"🔥 AUTO BEATEN - Bütün kartlar müdafiə edilib");
                await AutoExecuteBrokenBeaten(roomId, room);
                return;
            }

            // ✅ SENARYO B: Defender müdafiə edə bilir → MƏCBUR MÜDAFİƏ
            if (defenderCanDefend)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                {
                    message = $"Queue oyunçuları pas etdi!\n{defender?.Name} müdafiə etməlidir!",
                    defenderName = defender?.Name,
                    canDefend = true
                });

                Console.WriteLine($"🛡️ {defender?.Name} müdafiə edə bilir - MƏCBUR MÜDAFIƏ");
            }
            // ✅ SENARYO C: Defender müdafiə edə bilmir → TAKE CARDS
            else
            {
                Console.WriteLine($"📥 {defender?.Name} müdafiə edə bilmir - KARTLARI GÖTÜRÜR");
                await ExecuteDefenderTakesCards(roomId, room);
            }

            await BroadcastGameState(roomId);
        }
        private async Task HandleBrokenBeatenComplete(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🏁 Broken Beaten tamamlandı - queue oyunçuları bitirdi");

            DurakPlayer? defender = null;
            bool defenderCanDefend = true;
            bool allCardsDefended = false; // ✅ ƏLAVƏ

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null)
                {
                    Console.WriteLine("❌ Defender tapılmadı!");
                    return;
                }

                // ✅ 1. YOXLA: Masada açıq (müdafiə olunmamış) kart varsa?
                if (room.TableCards.Count == 0)
                {
                    // Bütün kartlar artıq müdafiə edilib
                    allCardsDefended = true;
                    defenderCanDefend = true; // Artıq müdafiə lazım deyil

                    Console.WriteLine($"✅ Bütün kartlar artıq müdafiə edilib - BEATEN!");
                }
                else
                {
                    // ✅ 2. Masada açıq kartlar var - Defender müdafiə edə bilərmi?
                    Console.WriteLine($"🔍 Masada {room.TableCards.Count} açıq kart var - yoxlanılır");

                    foreach (var attackCard in room.TableCards)
                    {
                        bool canDefendThis = false;

                        foreach (var defenseCard in defender.Hand)
                        {
                            if (room.GameEngine.CanDefend(attackCard, defenseCard))
                            {
                                canDefendThis = true;
                                break;
                            }
                        }

                        if (!canDefendThis)
                        {
                            defenderCanDefend = false;
                            break;
                        }
                    }
                }
            }

            // ═══════════════════════════════════════════════════════════
            // 🎯 3 SENARYO
            // ═══════════════════════════════════════════════════════════

            // ✅ SENARYO A: Bütün kartlar artıq müdafiə edilib
            if (allCardsDefended)
            {
                Console.WriteLine($"🔥 SENARYO A: Bütün kartlar müdafiə edilib - AVTOMATIK BEATEN!");

                // Kartları yandır və rolları dəyiş
                await AutoExecuteBrokenBeaten(roomId, room);
                return;
            }

            // ✅ SENARYO B: Açıq kartlar var və Defender müdafiə edə bilir
            if (defenderCanDefend)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                {
                    message = $"Queue oyunçuları hücumu bitirdi!\n{defender?.Name} müdafiə etməlidir!",
                    defenderName = defender?.Name,
                    canDefend = true
                });

                Console.WriteLine($"🛡️ SENARYO B: {defender?.Name} müdafiə edə bilir - MƏCBUR MÜDAFIƏ");
            }
            // ✅ SENARYO C: Açıq kartlar var və Defender müdafiə edə bilmir
            else
            {
                await Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustTake", new
                {
                    message = $"Queue oyunçuları hücumu bitirdi!\n{defender?.Name} müdafiə edə bilmir - Kartları götürməlidir!",
                    defenderName = defender?.Name,
                    canDefend = false
                });

                Console.WriteLine($"📥 SENARYO C: {defender?.Name} müdafiə edə bilmir - KARTLARI GÖTÜRMƏLI");
            }

            await BroadcastGameState(roomId);
        }
        public async Task Beaten()
        {
            var roomId = GetCurrentRoom();
            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive) return;

            var userId = GetUserId();
            bool is2P = false;
            bool queueEmpty = false;

            lock (room.StateLock)
            {
                if (room.AttackerId != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Yalnız Main Attacker beaten edə bilər"));
                    return;
                }

                if (room.TableCards.Count > 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Masada açıq kart var"));
                    return;
                }

                if (room.DefendedPairs.Count == 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "❌ Müdafiə edilmiş kart yoxdur"));
                    return;
                }

                if (room.Players.Count == 2)
                {
                    int oldDefenderId2P = room.DefenderId;
                    int oldAttackerId2P = room.AttackerId;
                    room.TableCards.Clear();
                    room.DefendedPairs.Clear();
                    room.AttackerId = oldDefenderId2P;
                    room.DefenderId = oldAttackerId2P;
                    room.ResetAttackRound();
                    room.RefillHands();
                    is2P = true;
                }
                else
                {
                    // ✅ 3P+ — əvvəlcə bütün fazaları sıfırla
                    room.IsBeatenPhaseActive = true;
                    room.IsTakeCardPhaseActive = false;
                    room.IsBrokenBeatenPhaseActive = true;
                    room.IsThrowInPhaseActive = false;

                    room.AttackerQueue.Clear();
                    room.PlayersWhoPassedThisRound.Clear();
                    room.CurrentAttackerQueueIndex = 0;
                    room.TakeCardsVotes.Clear();

                    // ✅ Queue: attacker və defender XARİC bütün oyunçular
                    foreach (var p in room.Players)
                    {
                        if (p.UserId != room.AttackerId && p.UserId != room.DefenderId)
                            room.AttackerQueue.Add(p.UserId);
                    }

                    // ✅ Lock içində queueEmpty yoxla
                    queueEmpty = room.AttackerQueue.Count == 0;

                    Console.WriteLine($"✅ BEATEN PHASE (3P+):");
                    Console.WriteLine($"   Main Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}");
                    Console.WriteLine($"   Defender: {room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name}");
                    Console.WriteLine($"   Queue: {(queueEmpty ? "BOŞ" : string.Join(" → ", room.AttackerQueue.Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name ?? id.ToString())))}");
                }
            }

            // 2P broadcast
            if (is2P)
            {
                foreach (var p in room.Players)
                    await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

                await _hubContext.Clients.Group(roomId).SendAsync("BeatenComplete", new
                {
                    message = $"✅ Beaten! Yeni raund başlayır\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}"
                });

                var result2P = room.CheckGameOver();
                if (result2P != null)
                    await EndGame(roomId, result2P);
                else
                    await BroadcastGameState(roomId);
                return;
            }

            // 3P+ queue boşdursa birbaşa tamamla
            if (queueEmpty)
            {
                await CompleteBeatenDirectly(roomId, room);
                return;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("BeatenPhaseStarted", new
            {
                message = "✅ Beaten! Queue oyunçuları hücum edə bilər VƏ YA pas edə bilərlər",
                attackerId = room.AttackerId,
                defenderId = room.DefenderId,
                queueOrder = room.AttackerQueue
                    .Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name)
                    .Where(n => n != null)
                    .ToList()
            });

            await BroadcastGameState(roomId);
            await ShowBeatenQueueChoiceModal(roomId, room);
        }
        // ✅ YENİ METOD - Beaten fazasında queue oyunçusuna modal göstər
        private async Task ShowBeatenQueueModalToNextPlayer(string roomId, DurakRoom room)
        {
            DurakPlayer? player;
            List<string> allowedRanks;
            int currentAttackCardCount;
            int maxNewCards;

            lock (room.StateLock)
            {
                var currentAttacker = FindNextBeatenAttackerLocked(room);
                if (currentAttacker == null || currentAttacker == 0)
                {
                    Console.WriteLine($"🛑 Beaten queue bitib - defender status yoxlanır");
                    player = null;
                    allowedRanks = new List<string>();
                    currentAttackCardCount = 0;
                    maxNewCards = 0;
                }
                else
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                    allowedRanks = GetBeatenAllowedRanks(room);
                    currentAttackCardCount = GetCurrentAttackCardCount(room);
                    maxNewCards = GetMaxNewAttackCards(room);
                }
            }

            if (player == null)
            {
                await ResolveBeatenQueueFinished(roomId, room);
                return;
            }

            Console.WriteLine($"📤 Beaten Queue Modal → {player.Name}");
            Console.WriteLine($"   AllowedRanks: {string.Join(", ", allowedRanks)}");
            Console.WriteLine($"   MaxNewCards: {maxNewCards}");

            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("ShowBrokenBeatenQueueModal", new
            {
                currentAttackerName = player.Name,
                defenderName = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name,
                allowedRanks = allowedRanks,
                tableCardCount = currentAttackCardCount,
                maxNewCards = Math.Max(0, maxNewCards),
                canAttack = true,
                message = $"✅ BEATEN - Sizin sıranız! Uyğun kart ata və ya PAS edə bilərsiniz"
            });
        }

        private async Task ShowBeatenQueueChoiceModal(string roomId, DurakRoom room)
        {
            DurakPlayer? player;
            List<string> allowedRanks;
            int currentAttackCardCount;
            int maxNewCards;

            lock (room.StateLock)
            {
                var currentAttacker = FindNextBeatenAttackerLocked(room);
                if (currentAttacker == null || currentAttacker == 0)
                {
                    Console.WriteLine($"🛑 Queue boş");
                    player = null;
                    allowedRanks = new List<string>();
                    currentAttackCardCount = 0;
                    maxNewCards = 0;
                }
                else
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                    allowedRanks = GetBeatenAllowedRanks(room);
                    currentAttackCardCount = GetCurrentAttackCardCount(room);
                    maxNewCards = GetMaxNewAttackCards(room);
                }
            }

            if (player == null)
            {
                await ResolveBeatenQueueFinished(roomId, room);
                return;
            }

            Console.WriteLine($"📤 Beaten Choice Modal → {player.Name}");
            Console.WriteLine($"   AllowedRanks: {string.Join(", ", allowedRanks)}");
            Console.WriteLine($"   MaxNewCards: {maxNewCards}");

            // ✅ DÜZƏLIŞ: kartlar AKTIV qalır, modal sadəcə "seçim" göstərir
            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("ShowBeatenChoiceModal", new
            {
                currentAttackerName = player.Name,
                defenderName = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId)?.Name,
                allowedRanks = allowedRanks,
                tableCardCount = currentAttackCardCount,
                maxNewCards = Math.Max(0, maxNewCards),

                // ✅ KRİTİK: Kartlar AKTIV qalmalı
                canAttack = true,  // Kartlar seçilə bilər
                canPass = true,    // Pass düymesi aktiv

                message = $"✅ BEATEN - İki seçim:",
                instruction = "Uyğun kart(lar) seçərək 'Hücum Et' basın VƏ YA 'Pas Et' basın",

                options = new
                {
                    attack = "Uyğun kart(lar) seçib Hücum Et",
                    pass = "Pas Et (novbə bitir)"
                }
            });
        }

        private async Task ResolveBeatenQueueFinished(string roomId, DurakRoom room)
        {
            DurakPlayer? defender = null;
            bool noNewCardsThrown = false;
            bool canDefendAll = true;

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null)
                    return;

                noNewCardsThrown = room.TableCards.Count == 0;
                if (!noNewCardsThrown)
                {
                    foreach (var attackCard in room.TableCards)
                    {
                        if (!defender.Hand.Any(defCard => room.GameEngine.CanDefend(attackCard, defCard)))
                        {
                            canDefendAll = false;
                            break;
                        }
                    }
                }
            }

            if (noNewCardsThrown)
            {
                await CompleteBeatenDirectly(roomId, room);
                return;
            }

            if (canDefendAll)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                {
                    message = $"Hamı pas etdi!\n{defender?.Name} müdafiə etməlidir!",
                    defenderName = defender?.Name,
                    canDefend = true
                });
                await BroadcastGameState(roomId);
                return;
            }

            await ExecuteDefenderTakesCards(roomId, room);
        }
        // ✅ YENİ METOD - Beaten birbaşa tamamla (queue yoxdursa və ya hamı pas etdisə)
        private async Task CompleteBeatenDirectly(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔥 Beaten birbaşa tamamlanır (hamı pas etti)");

            bool gameEnded = false;
            int newAttackerId = 0;
            int newDefenderId = 0;
            GameEndResult? gameResult = null;

            lock (room.StateLock)
            {
                // ✅ SADƏCƏ MASA TEMIZLƏ
                // DefendedPairs aşağıdakı karta yapışır:

                int oldDefenderId = room.DefenderId;
                (newAttackerId, newDefenderId) = ApplyRolesAfterBeaten(room, oldDefenderId);

                // ✅ HƏR İKİSİNİ SİL (onsuz da hamı pas etti, daha hücum yoxdur)
                room.TableCards.Clear();
                room.DefendedPairs.Clear();

                room.ResetAttackRound();
                room.RefillHands();

                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            // ✅ Hamıya kartlar göndər
            foreach (var p in room.Players)
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

            await _hubContext.Clients.Group(roomId).SendAsync("CardsDiscarded", new
            {
                message = $"🔥 BEATEN TAMAMLANDI (hamı pas etti)!\n" +
                          $"⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}\n" +
                          $"🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
                await EndGame(roomId, gameResult);
        }
        private async Task AutoExecuteBrokenBeaten(string roomId, DurakRoom room)
        {
            bool gameEnded = false;
            int oldAttackerId = 0;
            int oldDefenderId = 0;
            int newAttackerId = 0;
            int newDefenderId = 0;
            GameEndResult? gameResult = null;

            lock (room.StateLock)
            {
                oldAttackerId = room.AttackerId;
                oldDefenderId = room.DefenderId;

                // ✅ KARTLARI YANDIR
                room.GameEngine.ExecuteBeat();

                Console.WriteLine($"🔥 AUTO BEATEN - {room.DefendedPairs.Count} cüt kart yandırıldı");

                (newAttackerId, newDefenderId) = ApplyRolesAfterBeaten(room, oldDefenderId);

                Console.WriteLine($"🔥 AUTO BEATEN - Roller dəyişdi:");
                Console.WriteLine($"   Old Attacker ({room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name}) → Normal");
                Console.WriteLine($"   Old Defender → NEW ATTACKER: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}");
                Console.WriteLine($"   NEW DEFENDER: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}");

                // ✅ Queue sıfırla
                room.ResetAttackRound();

                // ✅ Kartları doldur
                room.RefillHands();

                // ✅ Oyun bitdi mi?
                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            // ✅ Hamıya yeni kartlar göndər
            foreach (var p in room.Players)
            {
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);
            }

            // ✅ BİLDİRİŞ GÖNDƏR
            await _hubContext.Clients.Group(roomId).SendAsync("CardsDiscarded", new
            {
                message = $"🔥 Kartlar yandırıldı!\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}\n🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}"
            });

            // ✅ Broken Beaten notification-ı bağla
            await _hubContext.Clients.Group(roomId).SendAsync("CloseBrokenBeatenModal", new
            {
                message = "Broken Beaten avtomatik tamamlandı"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
            {
                await Task.Delay(500);
                await EndGame(roomId, gameResult);
            }

            Console.WriteLine($"✅ AUTO BEATEN tamamlandı");
        }
        private async Task CheckBrokenBeatenDefense(string roomId, DurakRoom room)
        {
            DurakPlayer? defender = null;
            bool canDefendAll = true;

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null) return;

                // ✅ Masadakı hər kartı müdafiə edə biləcəkmi?
                foreach (var attackCard in room.TableCards)
                {
                    bool canDefendThis = false;

                    // Müdafiəçinin əlində bu kartı müdafiə edə biləcəyi kart varsa?
                    foreach (var defenseCard in defender.Hand)
                    {
                        if (room.GameEngine.CanDefend(attackCard, defenseCard))
                        {
                            canDefendThis = true;
                            break;
                        }
                    }

                    if (!canDefendThis)
                    {
                        canDefendAll = false;
                        break;
                    }
                }
            }

            // ✅ Müdafiə edə bilmiyibsə - kartları götürmə düyməsi göstər
            if (!canDefendAll)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("DefenderCantDefendBrokenBeaten", new
                {
                    message = "Müdafiəçi müdafiə edə bilmir - Kartları götürməlidir!",
                    defenderName = defender?.Name
                });

                Console.WriteLine($"❌ {defender?.Name} Broken Beaten-də müdafiə edə bilmir");
            }
        }
        private async Task AutoCompleteBeaten(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔥 AUTO BEATEN - Hamı hücum etti");

            DurakPlayer? defender = null;
            bool defenderCanDefend = true;

            lock (room.StateLock)
            {
                defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                if (defender == null)
                {
                    Console.WriteLine("❌ Defender tapılmadı!");
                    return;
                }

                // ✅ Müdafiəçi bütün kartları müdafiə edə biləcəkmi?
                foreach (var attackCard in room.TableCards)
                {
                    bool canDefendThis = false;

                    foreach (var defenseCard in defender.Hand)
                    {
                        if (room.GameEngine.CanDefend(attackCard, defenseCard))
                        {
                            canDefendThis = true;
                            break;
                        }
                    }

                    if (!canDefendThis)
                    {
                        defenderCanDefend = false;
                        break;
                    }
                }
            }

            // ✅ ƏGƏR MÜDAFIƏÇI MÜDAFIƏ EDƏBILIYIBSƏ - Müdafiə etməli
            if (defenderCanDefend)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                {
                    message = "Hamı hücum etti - Müdafiəçi müdafiə etməlidir!",
                    defenderName = defender?.Name
                });

                Console.WriteLine($"🛡️ {defender?.Name} bütün kartları müdafiə etə bilir - MƏCBUR MÜDAFIƏ");
            }
            // ✅ ƏGƏR MÜDAFIƏÇI MÜDAFIƏ EDƏBILMIYIBSƏ - KARTLARI GÖTÜRMƏ
            else
            {
                Console.WriteLine($"📥 {defender?.Name} müdafiə edə bilmir - KARTLARI GÖTÜRÜR");

                // ExecuteDefenderTakesCards() çağırıyoruz
                await ExecuteDefenderTakesCards(roomId, room);
            }
        }
        public async Task ExecuteBrokenBeatenTakeCards()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();

            lock (room.StateLock)
            {
                if (room.DefenderId != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Yalnız müdafiəçi götürə bilər"));
                    return;
                }
            }

            // ✅ Defender kartları götürür və ROLLER DEYİŞİR
            await ExecuteDefenderTakesCards(roomId, room);
        }
        public async Task CompleteBrokenBeatenSuccess()
        {
            GameEndResult? gameResult = null;


            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            bool gameEnded = false;
            int oldAttackerId = 0;
            int oldDefenderId = 0;
            int newAttackerId = 0;
            int newDefenderId = 0;

            lock (room.StateLock)
            {
                // ✅ Yalnız Defender çağıra bilər
                if (room.DefenderId != userId)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Yalnız müdafiəçi tamamlaya bilər"));
                    return;
                }

                // ✅ Bütün kartlar müdafiə edilmişmi?
                if (room.TableCards.Count > 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError",
                        "Bütün kartlar müdafiə edilməyib"));
                    return;
                }

                oldAttackerId = room.AttackerId;
                oldDefenderId = room.DefenderId;

                // ✅ KARTLARI YANDIR
                room.GameEngine.ExecuteBeat();

                (newAttackerId, newDefenderId) = ApplyRolesAfterBeaten(room, oldDefenderId);

                Console.WriteLine($"🔥 BROKEN BEATEN TAMAMLANDI - Kartlar yandı");
                Console.WriteLine($"   Old Attacker ({room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name}) → Normal");
                Console.WriteLine($"   Old Defender → NEW ATTACKER: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}");
                Console.WriteLine($"   NEW DEFENDER: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}");

                // ✅ Queue sıfırla
                room.ResetAttackRound();

                // ✅ Kartları doldur
                room.RefillHands();

                // ✅ Oyun bitdi mi?
                var result = room.CheckGameOver();
                if (result != null)
                {
                    gameEnded = true;
                    gameResult = result;
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenCompleted", new
            {
                success = true,
                message = $"🔥 Kartlar yandırıldı!\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == newAttackerId)?.Name}\n🛡️ YENİ Defender: {room.Players.FirstOrDefault(p => p.UserId == newDefenderId)?.Name}"
            });

            await BroadcastGameState(roomId);

            if (gameEnded)
            {
                await EndGame(roomId, gameResult);
            }

            Console.WriteLine($"✅ Broken Beaten SUCCESS tamamlandı");
        }
        public async Task SendQuickMessage(string emoji, string message = "")
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var userId = GetUserId();
            var room = _roomManager.GetRoom(roomId);
            var player = room?.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null) return;

            await _hubContext.Clients.Group(roomId).SendAsync("QuickMessage", new
            {
                playerName = player.Name,
                emoji,
                message,
                timestamp = DateTime.Now
            });

            Console.WriteLine($"💬 {player.Name}: {emoji} {message}");
        }

        public async Task TransferAttack(Card card)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            DurakPlayer? transferer = null;
            DurakPlayer? newAttacker = null;
            DurakPlayer? newDefender = null;
            int oldDefenderId = 0;

            lock (room.StateLock)
            {
                var validation = room.GameEngine.ValidateTransfer(userId, card);
                if (!validation.IsValid)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
                    return;
                }

                transferer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (transferer == null) return;

                // ✅ Köhnə defender-i saxla
                oldDefenderId = room.DefenderId;

                // ✅ Transfer icra et
                int newDefenderId = room.GameEngine.ExecuteTransfer(userId, card);

                // ✅ Yeni rolları tap
                newAttacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
                newDefender = room.Players.FirstOrDefault(p => p.UserId == newDefenderId);
            }

            // ✅ YENİLƏNMİŞ BİLDİRİŞ
            await _hubContext.Clients.Group(roomId).SendAsync("AttackTransferred", new
            {
                transfererName = transferer.Name,
                card = new { rank = card.Rank, suit = card.Suit },
                newAttacker = newAttacker?.Name,
                newDefender = newDefender?.Name,
                message = $"🔄 {transferer.Name} transfer etdi!\n" +
                          $"⚔️ YENİ Attacker: {newAttacker?.Name}\n" +
                          $"🛡️ YENİ Defender: {newDefender?.Name}"
            });

            await _hubContext.Clients.Client(transferer.ConnectionId).SendAsync("YourCards", transferer.Hand);
            if (newDefender != null)
            {
                await _hubContext.Clients.Client(newDefender.ConnectionId).SendAsync("YourCards", newDefender.Hand);
            }

            await BroadcastGameState(roomId);
        }
        public async Task PassDefense(Card card)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktif deyil");
                return;
            }

            var userId = GetUserId();
            DurakPlayer? passPlayer = null;
            DurakPlayer? newAttacker = null;
            DurakPlayer? newDefender = null;
            int oldAttackerId = 0;
            int oldDefenderId = 0;
            int newAttackerId = 0;
            int newDefenderId = 0;

            lock (room.StateLock)
            {
                // ✅ VALIDATION
                var validation = room.GameEngine.ValidatePass(userId, card);
                if (!validation.IsValid)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", validation.ErrorMessage));
                    return;
                }

                passPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (passPlayer == null)
                {
                    return;
                }

                // ✅ KÖHNƏ ROLLER SAXLA
                oldAttackerId = room.AttackerId;
                oldDefenderId = room.DefenderId;

                // ✅ PASSING ICRA ET
                newDefenderId = room.GameEngine.ExecutePassing(userId, card);

                // ✅ YENİ ROLLER TAP
                newAttacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
                newDefender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                newAttackerId = room.AttackerId;
            }

            Console.WriteLine($"✅ PassDefense Hub:");
            Console.WriteLine($"   Old Attacker: {room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name}");
            Console.WriteLine($"   Pass edən (Old Defender): {passPlayer.Name} → YENİ ATTACKER");
            Console.WriteLine($"   YENİ DEFENDER: {newDefender?.Name}");

            // ✅ BİLDİRİŞ GÖNDƏR
            await _hubContext.Clients.Group(roomId).SendAsync("DefensePassed", new
            {
                playerName = passPlayer.Name,
                card = new { rank = card.Rank, suit = card.Suit },
                oldAttacker = room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name,
                oldDefender = passPlayer.Name,
                newAttacker = newAttacker?.Name,
                newDefender = newDefender?.Name,
                message = $"🔄 {passPlayer.Name} pass etdi!\n" +
                          $"⚔️ YENİ Attacker: {newAttacker?.Name}\n" +
                          $"🛡️ YENİ Defender: {newDefender?.Name}"
            });

            // ✅ KARTLARI YENİLƏ
            if (newAttacker != null)
            {
                await _hubContext.Clients.Client(newAttacker.ConnectionId).SendAsync("YourCards", newAttacker.Hand);
            }
            if (newDefender != null)
            {
                await _hubContext.Clients.Client(newDefender.ConnectionId).SendAsync("YourCards", newDefender.Hand);
            }

            // ✅ OYUN VƏZIYYƏTINI YAYINLA
            await BroadcastGameState(roomId);

            Console.WriteLine($"✅ PassDefense tamamlandı - GameState yayınlandı");
        }

        private async Task EndGame(string roomId, GameEndResult? knownResult = null)
        {
            try
            {
                var existingRoom = _roomManager.GetRoom(roomId);
                if (existingRoom != null)
                {
                    lock (existingRoom.StateLock)
                    {
                        existingRoom.TurnTimerCts?.Cancel();
                    }
                }

                // ✅ Yeni scope aç - disposed DbContext problemini həll edir
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                var rankService = scope.ServiceProvider.GetRequiredService<IRankService>();

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                {
                    Console.WriteLine($"❌ EndGame: Room {roomId} tapılmadı");
                    return;
                }

                Console.WriteLine($"🏁🏁🏁 ENDING GAME in {room.RoomName} (Mode: {room.GameMode}) 🏁🏁🏁");

                var result = knownResult ?? room.CheckGameOver();
                if (result == null)
                {
                    Console.WriteLine("⚠️ CheckGameOver returned null - game not over yet");
                    return;
                }

                lock (room.StateLock)
                {
                    if (room.GameStatus == "Finished")
                    {
                        Console.WriteLine($"⚠️ EndGame skipped: {room.RoomName} already finished");
                        return;
                    }

                    room.GameStatus = "Finished";
                    room.IsGameActive = false;
                    room.FinishedAt = DateTime.UtcNow;
                }

                bool gameOverSent = false;

                if (result.IsDraw && result.Winners.Count > 1)
                {
                    Console.WriteLine($"🤝 DRAW GAME - pot {result.Winners.Count} oyunçu arasında bölünür");

                    lock (room.StateLock)
                    {
                        room.LastWinner = string.Join(" & ", result.Winners.Select(w => w.Name));
                        room.LastDurak = "Bərabərə";
                        room.GameEndTime = DateTime.Now;
                    }

                    if (room.TotalPrize > 0)
                    {
                        decimal commission = room.TotalPrize * COMMISSION_RATE;
                        decimal remainingPrize = room.TotalPrize - commission;
                        decimal prizePerWinner = remainingPrize / result.Winners.Count;

                        foreach (var winner in result.Winners)
                        {
                            var winnerUser = await db.Users.FindAsync(winner.UserId);
                            if (winnerUser != null)
                            {
                                winnerUser.Balance += prizePerWinner;
                                Console.WriteLine($"💰 DRAW: {winner.Name} balance +{prizePerWinner} AZN");
                            }
                        }

                        try
                        {
                            await db.SaveChangesAsync();
                            Console.WriteLine("✅ Balance SaveChanges OK (DRAW split)");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Balance SaveChanges FAILED (DRAW split): {ex.Message}");
                            foreach (var winner in result.Winners)
                            {
                                var winnerUser = await db.Users.FindAsync(winner.UserId);
                                if (winnerUser != null) winnerUser.Balance -= prizePerWinner;
                            }
                        }

                        foreach (var winner in result.Winners)
                        {
                            try
                            {
                                await rankService.UpdateRankAfterGame(
                                    winner.UserId, GameType.Durak, true, prizePerWinner);
                                Console.WriteLine($"✅ Rank updated: {winner.Name} (DRAW WIN)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ Rank Update Error (draw winner) {winner.Name}: {ex.Message}");
                            }
                        }

                        await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                        {
                            winners = result.Winners.Select(w => w.Name).ToList(),
                            isDraw = true,
                            message = $"🤝 BƏRABƏRƏ!\n{string.Join(" və ", result.Winners.Select(w => w.Name))} son kartlarını eyni əldə bitirdi.",
                            prizePerWinner,
                            commission,
                            canRematch = true,
                            rematchSeconds = REMATCH_RESPONSE_SECONDS,
                            entryFee = room.EntryFee,
                            gameMode = room.GameMode.ToString()
                        });

                        Console.WriteLine($"📤 GameOver sent (DRAW split) - {prizePerWinner} AZN each");
                        gameOverSent = true;
                    }
                    else
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                        {
                            winners = result.Winners.Select(w => w.Name).ToList(),
                            isDraw = true,
                            message = $"🤝 BƏRABƏRƏ!\n{string.Join(" və ", result.Winners.Select(w => w.Name))} son kartlarını eyni əldə bitirdi.",
                            canRematch = true,
                            rematchSeconds = REMATCH_RESPONSE_SECONDS,
                            entryFee = room.EntryFee,
                            gameMode = room.GameMode.ToString()
                        });
                        gameOverSent = true;
                    }
                }
                else if (result.Winners.Count > 1)
                {
                    Console.WriteLine($"⚠️ Multiple winners received; forcing single winner model.");

                    var forcedWinner = result.Winners
                        .OrderBy(p => p.Hand.Count)
                        .ThenBy(p => p.UserId == room.DefenderId ? 0 : 1)
                        .ThenBy(p => p.UserId)
                        .First();

                    var forcedDurak = room.Players
                        .Where(p => p.UserId != forcedWinner.UserId)
                        .OrderByDescending(p => p.Hand.Count)
                        .ThenByDescending(p => p.UserId == room.AttackerId)
                        .FirstOrDefault();

                    result = new GameEndResult
                    {
                        Winners = new List<DurakPlayer> { forcedWinner },
                        Durak = forcedDurak,
                        IsDraw = false
                    };
                }

                // ═══════════════════════════════════════════════════════════════════════
                // 🏆 TEK QALIB
                // ═══════════════════════════════════════════════════════════════════════
                if (!gameOverSent && result.Winners.Count == 1)
                {
                    var winner = result.Winners[0];
                    var durak = result.Durak;

                    Console.WriteLine($"🏆 Winner: {winner.Name} (0 cards)");
                    if (durak != null)
                        Console.WriteLine($"🎯 Durak: {durak.Name} ({durak.Hand.Count} cards)");

                    lock (room.StateLock)
                    {
                        room.LastWinner = winner.Name;
                        room.LastDurak = durak?.Name;
                        room.GameEndTime = DateTime.Now;
                    }

                    if (room.TotalPrize > 0)
                    {
                        decimal commission = room.TotalPrize * COMMISSION_RATE;
                        decimal winnerPrize = room.TotalPrize - commission;

                        Console.WriteLine($"💰 TotalPrize={room.TotalPrize} | Commission={commission} | WinnerPrize={winnerPrize}");

                        // --- 1. Balance yenilə ---
                        var winnerUser = await db.Users.FindAsync(winner.UserId);
                        if (winnerUser != null)
                        {
                            winnerUser.Balance += winnerPrize;
                            Console.WriteLine($"💰 {winner.Name} balance +{winnerPrize} AZN (pending save)");
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ Winner user tapılmadı: {winner.UserId}");
                        }

                        // --- 2. SaveChanges (Balance) ---
                        try
                        {
                            await db.SaveChangesAsync();
                            Console.WriteLine($"✅ Balance SaveChanges OK: {winner.Name} +{winnerPrize} AZN");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Balance SaveChanges FAILED: {ex.Message}");
                            if (winnerUser != null) winnerUser.Balance -= winnerPrize;
                        }

                        // --- 3. Rank yenilə (winner) ---
                        try
                        {
                            await rankService.UpdateRankAfterGame(
                                winner.UserId, GameType.Durak, true, winnerPrize);
                            Console.WriteLine($"✅ Rank updated: {winner.Name} (QALIB)");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Rank Update Error (winner) {winner.Name}: {ex.Message}");
                        }

                        // --- 4. Rank yenilə (durak) ---
                        if (durak != null)
                        {
                            try
                            {
                                await rankService.UpdateRankAfterGame(
                                    durak.UserId, GameType.Durak, false, room.EntryFee);
                                Console.WriteLine($"✅ Rank updated: {durak.Name} (DURAK)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ Rank Update Error (durak) {durak.Name}: {ex.Message}");
                            }
                        }

                        // --- 5. GameOver göndər ---
                        await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                        {
                            winner = winner.Name,
                            durak = durak?.Name,
                            message = durak != null
                                ? $"🎯 {durak.Name} DURAK oldu! 🏆 Qalib: {winner.Name}"
                                : $"🏆 {winner.Name} QALIB!",
                            winnerPrize,
                            commission,
                            canRematch = true,
                            rematchSeconds = REMATCH_RESPONSE_SECONDS,
                            entryFee = room.EntryFee,
                            gameMode = room.GameMode.ToString()
                        });

                        Console.WriteLine($"📤 GameOver sent - Winner: {winner.Name} +{winnerPrize} AZN | Commission: {commission} AZN");
                        gameOverSent = true;
                    }
                    else
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                        {
                            winner = winner.Name,
                            durak = durak?.Name ?? "Heç kim",
                            message = durak != null
                                ? $"🎯 {durak.Name} DURAK oldu! 🏆 Qalib: {winner.Name}"
                                : $"🏆 {winner.Name} QALIB!",
                            canRematch = true,
                            rematchSeconds = REMATCH_RESPONSE_SECONDS,
                            entryFee = room.EntryFee,
                            gameMode = room.GameMode.ToString()
                        });

                        // --- Rank yenilə (prize olmadan) ---
                        try
                        {
                            await rankService.UpdateRankAfterGame(
                                winner.UserId, GameType.Durak, true, 0);
                            Console.WriteLine($"✅ Rank updated (no prize): {winner.Name} (QALIB)");
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"❌ Rank Update Error (no prize) {winner.Name}: {ex.Message}");
                        }

                        if (durak != null)
                        {
                            try
                            {
                                await rankService.UpdateRankAfterGame(
                                    durak.UserId, GameType.Durak, false, 0);
                                Console.WriteLine($"✅ Rank updated (no prize): {durak.Name} (DURAK)");
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"❌ Rank Update Error (no prize) {durak.Name}: {ex.Message}");
                            }
                        }

                        Console.WriteLine($"📤 GameOver sent (no prize) - Winner: {winner.Name}");
                        gameOverSent = true;
                    }
                }
                else if (!gameOverSent)
                {
                    Console.WriteLine($"⚠️ Qeyri-adi vəziyyət: Winners={result.Winners.Count}, IsDraw={result.IsDraw}");
                }

                // ✅ Frontend GameOver-i alsın deyə gözlə
                await Task.Delay(1000);

                lock (room.StateLock)
                {
                    if (gameOverSent)
                    {
                        room.TotalPrize = 0;
                    }

                    room.ResetGame();
                }

                if (gameOverSent)
                {
                    await StartRematchWindowAsync(roomId, room);
                }
                Console.WriteLine($"✅🏁 Game ended successfully in {room.RoomName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌❌❌ EndGame CRITICAL ERROR: {ex.Message}");
                Console.WriteLine($"   StackTrace: {ex.StackTrace}");
            }
        }
        public async Task VoteRematch()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Otaq tapılmadı");
                return;
            }

            if (room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun hələ davam edir");
                return;
            }

            var userId = GetUserId();
            var user = _db.Users.FirstOrDefault(u => u.Id == userId);

            if (user == null)
            {
                await Clients.Caller.SendAsync("ActionError", "İstifadəçi tapılmadı");
                return;
            }

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            if (room.EntryFee > 0 && user.Balance < room.EntryFee)
            {
                await Clients.Caller.SendAsync("ActionError", $"Kifayət qədər balansınız yoxdur. Lazım: {room.EntryFee} AZN");
                return;
            }

            bool shouldStartGame = false;
            int votes = 0;
            int required = 0;
            int secondsLeft = 0;
            string? duplicateError = null;
            string? windowError = null;

            lock (room.StateLock)
            {
                if (room.RematchDeadlineUtc == null || room.RematchDeadlineUtc <= DateTime.UtcNow)
                {
                    windowError = "Yenidən oynama vaxtı bitib";
                }
                else if (room.RematchVotes.Contains(userId))
                {
                    duplicateError = "Siz artıq səs vermişsiniz";
                }
                else
                {
                    room.RematchVotes.Add(userId);
                    Console.WriteLine($"🔄 {player.Name} rematch üçün səs verdi ({room.RematchVotes.Count}/{room.Players.Count})");

                    votes = room.RematchVotes.Count;
                    required = room.Players.Count;
                    secondsLeft = Math.Max(0, (int)Math.Ceiling((room.RematchDeadlineUtc!.Value - DateTime.UtcNow).TotalSeconds));

                    if (room.Players.Count == room.MaxPlayers && room.RematchVotes.Count >= room.Players.Count)
                    {
                        room.RematchTimerCts?.Cancel();
                        room.RematchTimerCts?.Dispose();
                        room.RematchTimerCts = null;
                        room.RematchDeadlineUtc = null;
                        shouldStartGame = true;
                    }
                }
            }

            if (windowError != null)
            {
                await Clients.Caller.SendAsync("ActionError", windowError);
                return;
            }

            if (duplicateError != null)
            {
                await Clients.Caller.SendAsync("ActionError", duplicateError);
                return;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("RematchVote", new
            {
                playerName = player.Name,
                votes,
                required,
                secondsLeft
            });

            if (shouldStartGame)
            {
                await StartRematchGame(roomId, room);
            }
        }

        public async Task DeclineRematch()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Otaq tapılmadı");
                return;
            }

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            List<string> allowedRanks = new();
            int maxCards = 0;

            lock (room.StateLock)
            {
                room.RematchDeclines.Add(userId);
                room.RematchTimerCts?.Cancel();
                room.RematchTimerCts?.Dispose();
                room.RematchTimerCts = null;
                room.RematchDeadlineUtc = null;
            }

            await RemovePlayersAfterRematchExitAsync(roomId, room, new List<DurakPlayer> { player }, $"{player.Name} yenidən oynamaq istəmədi");
        }

        private async Task StartRematchWindowAsync(string roomId, DurakRoom room)
        {
            DateTime deadlineUtc;
            CancellationToken token;

            lock (room.StateLock)
            {
                if (room.IsGameActive || room.Players.Count == 0)
                    return;

                room.RematchTimerCts?.Cancel();
                room.RematchTimerCts?.Dispose();
                room.RematchVotes.Clear();
                room.RematchDeclines.Clear();
                room.RematchDeadlineUtc = DateTime.UtcNow.AddSeconds(REMATCH_RESPONSE_SECONDS);
                room.RematchTimerCts = new CancellationTokenSource();
                deadlineUtc = room.RematchDeadlineUtc.Value;
                token = room.RematchTimerCts.Token;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("RematchWindowStarted", new
            {
                timeoutSeconds = REMATCH_RESPONSE_SECONDS,
                deadlineUtc = deadlineUtc.ToString("O"),
                entryFee = room.EntryFee,
                message = "Yenidən oynamaq üçün qərar verin"
            });

            _ = Task.Run(async () => await HandleRematchTimeoutAsync(roomId, deadlineUtc, token));
        }

        private async Task HandleRematchTimeoutAsync(string roomId, DateTime deadlineUtc, CancellationToken token)
        {
            try
            {
                var delay = deadlineUtc - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, token);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested)
                return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
                return;

            List<DurakPlayer> timedOutPlayers;
            lock (room.StateLock)
            {
                if (room.IsGameActive || room.RematchDeadlineUtc != deadlineUtc)
                    return;

                timedOutPlayers = room.Players
                    .Where(p => !room.RematchVotes.Contains(p.UserId) && !room.RematchDeclines.Contains(p.UserId))
                    .ToList();

                room.RematchTimerCts?.Cancel();
                room.RematchTimerCts?.Dispose();
                room.RematchTimerCts = null;
                room.RematchDeadlineUtc = null;
            }

            if (timedOutPlayers.Count == 0)
                return;

            await RemovePlayersAfterRematchExitAsync(roomId, room, timedOutPlayers, "Yenidən oynamaq vaxtı bitdi");
        }

        private async Task RemovePlayersAfterRematchExitAsync(string roomId, DurakRoom room, List<DurakPlayer> playersToRemove, string reason)
        {
            foreach (var player in playersToRemove)
            {
                _userRooms.TryRemove(player.ConnectionId, out _);
                _userActiveRooms.TryRemove(player.UserId, out _);
                _roomManager.RemovePlayerFromRoom(roomId, player.UserId);
                if (!string.IsNullOrEmpty(player.ConnectionId))
                {
                    await _hubContext.Groups.RemoveFromGroupAsync(player.ConnectionId, roomId);
                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("RematchExited", new
                    {
                        reason,
                        message = $"{reason}. Otaqdan çıxarıldınız."
                    });
                }
            }

            var updatedRoom = _roomManager.GetRoom(roomId);
            if (updatedRoom != null)
            {
                lock (updatedRoom.StateLock)
                {
                    updatedRoom.RematchVotes.Clear();
                    updatedRoom.RematchDeclines.Clear();
                    updatedRoom.RematchDeadlineUtc = null;
                    updatedRoom.RematchTimerCts?.Cancel();
                    updatedRoom.RematchTimerCts?.Dispose();
                    updatedRoom.RematchTimerCts = null;

                    foreach (var player in updatedRoom.Players)
                        player.IsReady = false;
                }

                await _hubContext.Clients.Group(roomId).SendAsync("RematchCancelled", new
                {
                    reason,
                    message = $"{reason}. Qalan oyunçular yeni oyunçu gözləyir.",
                    playerCount = updatedRoom.PlayerCount,
                    maxPlayers = updatedRoom.MaxPlayers,
                    totalPrize = updatedRoom.TotalPrize
                });

                await BroadcastGameState(roomId);
            }

            await _hubContext.Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
        }

        private async Task StartRematchGame(string roomId, DurakRoom room)
        {
            Console.WriteLine($"🔄 All players voted for rematch - starting new game!");

            bool allPaid = true;

            if (room.EntryFee > 0)
            {
                using var scope = _scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                var rematchUsers = new List<BlogApp.Core.Entities.User>();

                foreach (var p in room.Players)
                {
                    var pUser = await db.Users.FindAsync(p.UserId);
                    if (pUser == null || pUser.Balance < room.EntryFee)
                    {
                        allPaid = false;
                        Console.WriteLine($"❌ {p.Name} does not have enough balance for rematch");
                        break;
                    }

                    rematchUsers.Add(pUser);
                }

                if (allPaid)
                {
                    foreach (var pUser in rematchUsers)
                    {
                        pUser.Balance -= room.EntryFee;
                    }

                    room.TotalPrize += room.EntryFee * rematchUsers.Count;

                    try
                    {
                        await db.SaveChangesAsync();
                        Console.WriteLine("✅ Rematch payments saved");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Rematch DB Save Error: {ex.Message}");
                        foreach (var pUser in rematchUsers)
                        {
                            pUser.Balance += room.EntryFee;
                        }

                        room.TotalPrize -= room.EntryFee * rematchUsers.Count;
                        allPaid = false;
                    }
                }
            }

            if (!allPaid && room.EntryFee > 0)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("RematchFailed", new
                {
                    message = "Bəzi oyunçuların balansı kifayət deyil"
                });
                return;
            }

            lock (room.StateLock)
            {
                room.RematchVotes.Clear();
                room.RematchDeclines.Clear();
                room.RematchDeadlineUtc = null;
                room.StartNewGame();
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameStarted", new
            {
                trumpCard = room.TrumpCard != null ? new
                {
                    rank = room.TrumpCard.Rank,
                    suit = room.TrumpCard.Suit
                } : null,
                trumpSuit = room.TrumpCard?.Suit,
                totalPrize = room.TotalPrize,
                isRematch = true
            });

            foreach (var p in room.Players)
            {
                await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);
                Console.WriteLine($"📤 {p.Name} - {p.Hand.Count} cards dealt (rematch)");
            }

            await BroadcastGameState(roomId);

            var attacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
            var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);

            Console.WriteLine($"🎮 REMATCH STARTED in {room.RoomName}");
            Console.WriteLine($"   🗡️ Attacker: {attacker?.Name}");
            Console.WriteLine($"   🛡️ Defender: {defender?.Name}");
            Console.WriteLine($"   🃏 Trump: {room.TrumpCard?.Rank} of {room.TrumpCard?.Suit}");
            Console.WriteLine($"   💰 Prize Pool: {room.TotalPrize} AZN");
        }
        public static class CardHelper
        {
            public static string ToKey(Card card)
            {
                return $"{card.Rank}_{card.Suit}";
            }

            public static Card FromKey(string key)
            {
                var parts = key.Split('_');
                if (parts.Length != 2) return null;

                return new Card
                {
                    Rank = parts[0],
                    Suit = parts[1]
                };
            }
        }

        private int GetUserId()
        {
            var userIdClaim = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdClaim, out var userId) ? userId : 0;
        }

        private int GetRemainingExtraTimesLocked(DurakRoom room, int playerId)
        {
            if (!room.ExtraTimeRemaining.TryGetValue(playerId, out var remaining))
            {
                remaining = DEFAULT_EXTRA_TIMES;
                room.ExtraTimeRemaining[playerId] = remaining;
            }

            return remaining;
        }

        private TurnDecision? DetermineTurnDecisionLocked(DurakRoom room)
        {
            if (!room.IsGameActive || room.Players.Count < 2)
                return null;

            var attacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
            var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
            bool hasOpenCards = room.TableCards.Count > 0;
            bool hasDefendedCards = room.DefendedPairs.Count > 0;

            TurnDecision CreateDecision(int playerId, string actionKind, int durationSeconds, string phase)
            {
                var playerName = room.Players.FirstOrDefault(p => p.UserId == playerId)?.Name ?? "Unknown";
                var extraTimesLeft = GetRemainingExtraTimesLocked(room, playerId);

                return new TurnDecision
                {
                    PlayerId = playerId,
                    PlayerName = playerName,
                    ActionKind = actionKind,
                    DurationSeconds = durationSeconds,
                    StateKey = string.Join(":",
                        phase,
                        playerId,
                        room.TableCards.Count,
                        room.DefendedPairs.Count,
                        room.AttackerQueue.Count,
                        room.CurrentAttackerQueueIndex,
                        room.TakeCardsVotes.Count,
                        room.PlayersWhoPassedThisRound.Count,
                        room.IsThrowInPhaseActive,
                        room.IsBeatenPhaseActive,
                        room.IsTakeCardPhaseActive,
                        room.IsBrokenBeatenPhaseActive),
                    ExtraTimesLeft = extraTimesLeft
                };
            }

            if (room.IsThrowInPhaseActive)
            {
                var currentThrowInPlayerId = FindNextThrowInAttackerLocked(room);

                if (currentThrowInPlayerId != 0)
                    return CreateDecision(currentThrowInPlayerId, "throw_in", ATTACK_TURN_SECONDS, "throw-in");

                return null;
            }

            if (room.IsBrokenBeatenPhaseActive)
            {
                var brokenBeatenAttacker = FindNextBeatenAttackerLocked(room);
                if (brokenBeatenAttacker.HasValue && brokenBeatenAttacker.Value != 0)
                    return CreateDecision(brokenBeatenAttacker.Value, "broken_beaten_attack", ATTACK_TURN_SECONDS, "broken-beaten-attack");

                if (hasOpenCards && defender != null)
                    return CreateDecision(defender.UserId, "defense", DEFENSE_TURN_SECONDS, "broken-beaten-defense");

                return null;
            }

            if (hasOpenCards && defender != null)
            {
                return CreateDecision(defender.UserId, "defense", DEFENSE_TURN_SECONDS, "defense");
            }

            if (room.IsBeatenPhaseActive || room.IsTakeCardPhaseActive)
            {
                var queueAttacker = FindNextBeatenAttackerLocked(room);
                if (queueAttacker.HasValue && queueAttacker.Value != 0)
                {
                    return CreateDecision(queueAttacker.Value, "queue_attack", ATTACK_TURN_SECONDS,
                        room.IsBeatenPhaseActive ? "beaten-queue" : "take-queue");
                }
            }

            if (hasDefendedCards && attacker != null)
            {
                return CreateDecision(attacker.UserId, "main_attack", ATTACK_TURN_SECONDS, "main-attack-after-defense");
            }

            if (!hasOpenCards && !hasDefendedCards && attacker != null && attacker.Hand.Count > 0)
            {
                return CreateDecision(attacker.UserId, "opening_attack", ATTACK_TURN_SECONDS, "opening-attack");
            }

            return null;
        }

        private TurnTimerSnapshot BuildTurnTimerSnapshotLocked(DurakRoom room)
        {
            if (room.TurnPlayerId == null || room.TurnDeadlineUtc == null || string.IsNullOrEmpty(room.TurnActionKind))
            {
                return new TurnTimerSnapshot { IsActive = false };
            }

            var secondsLeft = (int)Math.Max(0, Math.Ceiling((room.TurnDeadlineUtc.Value - DateTime.UtcNow).TotalSeconds));
            var playerName = room.Players.FirstOrDefault(p => p.UserId == room.TurnPlayerId)?.Name;
            var extraTimesLeft = room.TurnPlayerId.HasValue
                ? GetRemainingExtraTimesLocked(room, room.TurnPlayerId.Value)
                : 0;

            return new TurnTimerSnapshot
            {
                IsActive = true,
                PlayerId = room.TurnPlayerId,
                PlayerName = playerName,
                ActionKind = room.TurnActionKind,
                DeadlineUtc = room.TurnDeadlineUtc.Value.ToString("O"),
                DurationSeconds = room.TurnDurationSeconds,
                SecondsLeft = secondsLeft,
                ExtraTimesLeft = extraTimesLeft
            };
        }

        private TurnTimerPlan PrepareTurnTimerLocked(DurakRoom room)
        {
            var decision = DetermineTurnDecisionLocked(room);

            if (decision == null)
            {
                var previousCts = room.TurnTimerCts;
                room.TurnTimerCts = null;
                room.TurnDeadlineUtc = null;
                room.TurnPlayerId = null;
                room.TurnActionKind = null;
                room.TurnStateKey = null;
                room.TurnDurationSeconds = 0;

                return new TurnTimerPlan
                {
                    PreviousCts = previousCts
                };
            }

            var timerStillValid =
                room.TurnPlayerId == decision.PlayerId &&
                room.TurnActionKind == decision.ActionKind &&
                room.TurnStateKey == decision.StateKey &&
                room.TurnDeadlineUtc.HasValue &&
                room.TurnDeadlineUtc.Value > DateTime.UtcNow &&
                room.TurnTimerCts != null;

            if (timerStillValid)
            {
                return new TurnTimerPlan();
            }

            var oldCts = room.TurnTimerCts;
            var newCts = new CancellationTokenSource();

            room.TurnTimerCts = newCts;
            room.TurnTimerSequence++;
            room.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(decision.DurationSeconds);
            room.TurnPlayerId = decision.PlayerId;
            room.TurnActionKind = decision.ActionKind;
            room.TurnStateKey = decision.StateKey;
            room.TurnDurationSeconds = decision.DurationSeconds;

            return new TurnTimerPlan
            {
                ShouldStartTimer = true,
                Sequence = room.TurnTimerSequence,
                Token = newCts.Token,
                PreviousCts = oldCts
            };
        }

        private void ActivatePreparedTurnTimer(string roomId, TurnTimerPlan plan)
        {
            if (plan.PreviousCts != null)
            {
                try
                {
                    plan.PreviousCts.Cancel();
                }
                catch
                {
                }

                plan.PreviousCts.Dispose();
            }

            if (!plan.ShouldStartTimer)
                return;

            _ = RunTurnTimerAsync(roomId, plan.Sequence, plan.Token);
        }

        private async Task RunTurnTimerAsync(string roomId, int sequence, CancellationToken cancellationToken)
        {
            try
            {
                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                    return;

                DateTime deadlineUtc;
                lock (room.StateLock)
                {
                    if (room.TurnTimerSequence != sequence || room.TurnDeadlineUtc == null)
                        return;

                    deadlineUtc = room.TurnDeadlineUtc.Value;
                }

                var delay = deadlineUtc - DateTime.UtcNow;
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                await HandleTurnTimeoutAsync(roomId, sequence);
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Turn timer error ({roomId}): {ex.Message}");
            }
        }

        private async Task HandleTurnTimeoutAsync(string roomId, int sequence)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
                return;

            int? playerId;
            string? actionKind;
            string? playerName;

            lock (room.StateLock)
            {
                if (!room.IsGameActive || room.TurnTimerSequence != sequence || room.TurnDeadlineUtc == null || room.TurnDeadlineUtc > DateTime.UtcNow)
                    return;

                playerId = room.TurnPlayerId;
                actionKind = room.TurnActionKind;
                playerName = playerId != null
                    ? room.Players.FirstOrDefault(p => p.UserId == playerId)?.Name
                    : null;
            }

            if (playerId == null || string.IsNullOrEmpty(actionKind))
                return;

            if (await TryUseAutomaticExtraTimeAsync(roomId, room, sequence, playerId.Value))
                return;

            await _hubContext.Clients.Group(roomId).SendAsync("TurnTimedOut", new
            {
                playerId,
                playerName,
                actionKind
            });

            if (actionKind == "defense")
            {
                await AutoStartTakeCardsAsync(roomId, room, playerId.Value);
                return;
            }

            if (actionKind == "throw_in")
            {
                await AutoRejectTakeCardsAsync(roomId, room, playerId.Value);
                return;
            }

            if (actionKind == "broken_beaten_attack")
            {
                await AutoBrokenBeatenPassAsync(roomId, room, playerId.Value);
                return;
            }

            if (actionKind == "queue_attack")
            {
                await AutoPassAttackAsync(roomId, room, playerId.Value);
                return;
            }

            if (actionKind == "main_attack")
            {
                await AutoBeatenAsync(roomId, room, playerId.Value);
                return;
            }

            if (actionKind == "opening_attack")
            {
                lock (room.StateLock)
                {
                    if (room.AttackerId == playerId.Value && room.TableCards.Count == 0 && room.DefendedPairs.Count > 0)
                    {
                        actionKind = "main_attack";
                    }
                }

                if (actionKind == "main_attack")
                {
                    await AutoBeatenAsync(roomId, room, playerId.Value);
                    return;
                }
            }

            if (actionKind == "opening_attack")
            {
                await RemoveAfkOpeningAttackerAsync(roomId, room, playerId.Value);
            }
        }

        private async Task<bool> TryUseAutomaticExtraTimeAsync(string roomId, DurakRoom room, int sequence, int playerId)
        {
            CancellationTokenSource? previousCts = null;
            CancellationToken newToken = CancellationToken.None;
            int newSequence = 0;
            int extraTimesLeft = 0;
            string? playerName = null;

            lock (room.StateLock)
            {
                if (room.TurnTimerSequence != sequence || room.TurnPlayerId != playerId || room.TurnDeadlineUtc == null)
                    return false;

                extraTimesLeft = GetRemainingExtraTimesLocked(room, playerId);
                if (extraTimesLeft <= 0)
                    return false;

                room.ExtraTimeRemaining[playerId] = extraTimesLeft - 1;
                room.TurnDeadlineUtc = DateTime.UtcNow.AddSeconds(EXTRA_TIME_SECONDS);
                room.TurnDurationSeconds = EXTRA_TIME_SECONDS;

                previousCts = room.TurnTimerCts;
                room.TurnTimerCts = new CancellationTokenSource();
                room.TurnTimerSequence++;
                newSequence = room.TurnTimerSequence;
                newToken = room.TurnTimerCts.Token;
                extraTimesLeft = room.ExtraTimeRemaining[playerId];
                playerName = room.Players.FirstOrDefault(p => p.UserId == playerId)?.Name;
            }

            previousCts?.Cancel();
            previousCts?.Dispose();
            _ = RunTurnTimerAsync(roomId, newSequence, newToken);

            await _hubContext.Clients.Group(roomId).SendAsync("ExtraTimeUsed", new
            {
                playerId,
                playerName,
                extraTimesLeft,
                automatic = true
            });

            await BroadcastGameState(roomId);
            return true;
        }

        private async Task HandlePlayerRemovedForTimeoutAsync(string roomId, DurakRoom room, DurakPlayer removedPlayer)
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
            var rankService = scope.ServiceProvider.GetRequiredService<IRankService>();

            int remainingCount;
            DurakPlayer? winner = null;
            bool gameFinishedNow = false;

            lock (room.StateLock)
            {
                if (room.GameStatus == "Finished")
                    return;

                room.Players.RemoveAll(p => p.UserId == removedPlayer.UserId);
                remainingCount = room.Players.Count;

                if (remainingCount == 1)
                {
                    winner = room.Players.FirstOrDefault();
                    if (winner != null)
                    {
                        room.GameStatus = "Finished";
                        room.IsGameActive = false;
                        room.WinnerId = winner.UserId;
                        room.FinishedAt = DateTime.UtcNow;
                        gameFinishedNow = true;
                    }
                }
            }

            if (gameFinishedNow && winner != null)
            {
                try
                {
                    await rankService.UpdateRankAfterGame(removedPlayer.UserId, GameType.Durak, false, room.EntryFee);
                }
                catch
                {
                }

                if (room.TotalPrize > 0)
                {
                    decimal commission = room.TotalPrize * COMMISSION_RATE;
                    decimal winnerPrize = room.TotalPrize - commission;

                    var winnerUser = await db.Users.FindAsync(winner.UserId);
                    if (winnerUser != null)
                    {
                        winnerUser.Balance += winnerPrize;
                        try
                        {
                            await db.SaveChangesAsync();
                        }
                        catch
                        {
                            winnerUser.Balance -= winnerPrize;
                        }
                    }

                    try
                    {
                        await rankService.UpdateRankAfterGame(winner.UserId, GameType.Durak, true, winnerPrize);
                    }
                    catch
                    {
                    }

                    await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                    {
                        winner = winner.Name,
                        winnerId = winner.UserId,
                        message = $"🏆 {winner.Name} QALIB OLDU!\n{removedPlayer.Name} vaxtında ilk hücumu etmədiyi üçün oyundan çıxarıldı",
                        winnerPrize,
                        commission,
                        reason = "timeout_kick",
                        canRematch = false
                    });
                }
                else
                {
                    await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                    {
                        winner = winner.Name,
                        winnerId = winner.UserId,
                        message = $"🏆 {winner.Name} QALIB OLDU!\n{removedPlayer.Name} vaxtında ilk hücumu etmədiyi üçün oyundan çıxarıldı",
                        reason = "timeout_kick",
                        canRematch = false
                    });
                }

                _roomManager.DeleteRoom(roomId);
                return;
            }

            if (remainingCount >= 2)
            {
                try
                {
                    await rankService.UpdateRankAfterGame(removedPlayer.UserId, GameType.Durak, false, room.EntryFee);
                }
                catch
                {
                }

                ReassignRoles(room, removedPlayer.UserId);

                var attacker = room.Players.FirstOrDefault(p => p.UserId == room.AttackerId);
                var defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);

                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeftGameContinues", new
                {
                    message = $"⏱️ {removedPlayer.Name} ilk hücumu etmədiyi üçün oyundan çıxarıldı, oyun davam edir",
                    remainingPlayers = remainingCount,
                    currentAttacker = attacker?.Name,
                    currentDefender = defender?.Name,
                    totalPrize = room.TotalPrize
                });

                await BroadcastGameState(roomId);
            }
        }

        private async Task RemoveAfkOpeningAttackerAsync(string roomId, DurakRoom room, int playerId)
        {
            DurakPlayer? attacker;

            lock (room.StateLock)
            {
                attacker = room.Players.FirstOrDefault(p => p.UserId == playerId);
                if (attacker == null || room.AttackerId != playerId || room.TableCards.Count > 0 || room.DefendedPairs.Count > 0)
                    return;
            }

            _userRooms.TryRemove(attacker.ConnectionId, out _);
            _userActiveRooms.TryRemove(attacker.UserId, out _);

            try
            {
                if (!string.IsNullOrEmpty(attacker.ConnectionId))
                    await Groups.RemoveFromGroupAsync(attacker.ConnectionId, roomId);
            }
            catch
            {
            }

            if (!string.IsNullOrEmpty(attacker.ConnectionId))
            {
                await _hubContext.Clients.Client(attacker.ConnectionId).SendAsync("KickedForTimeout", new
                {
                    userId = playerId,
                    playerName = attacker.Name,
                    message = "Vaxtında ilk hücumu etmədiyiniz üçün oyundan çıxarıldınız"
                });
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerRemovedForTimeout", new
            {
                userId = playerId,
                playerName = attacker.Name,
                message = $"⏱️ {attacker.Name} ilk hücumu etmədiyi üçün oyundan çıxarıldı"
            });

            await HandlePlayerRemovedForTimeoutAsync(roomId, room, attacker);
            await Clients.All.SendAsync("RoomListUpdated", _roomManager.GetAvailableRooms());
        }

        private async Task AutoStartTakeCardsAsync(string roomId, DurakRoom room, int defenderUserId)
        {
            DurakPlayer? defender = null;
            List<string> allowedRanks = new();

            lock (room.StateLock)
            {
                if (!room.IsGameActive || room.DefenderId != defenderUserId || room.IsThrowInPhaseActive)
                    return;

                defender = room.Players.FirstOrDefault(p => p.UserId == defenderUserId);
                if (defender == null)
                    return;

                room.IsThrowInPhaseActive = true;
                room.IsBeatenPhaseActive = false;
                room.IsTakeCardPhaseActive = false;
                room.IsBrokenBeatenPhaseActive = false;

                room.AttackerQueue.Clear();
                room.PlayersWhoPassedThisRound.Clear();
                room.CurrentAttackerQueueIndex = 0;
                room.TakeCardsVotes.Clear();

                int defenderIndex = room.Players.FindIndex(p => p.UserId == defenderUserId);
                for (int i = 1; i < room.Players.Count; i++)
                {
                    int index = (defenderIndex + i) % room.Players.Count;
                    room.AttackerQueue.Add(room.Players[index].UserId);
                }

                allowedRanks = GetThrowInAllowedRanks(room);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("ThrowInPhaseStarted", new
            {
                defenderName = defender!.Name,
                message = $"{defender.Name} vaxt bitdiyi üçün kartları götürür! THROW-IN başladı",
                maxCards = Math.Min(6, defender.Hand.Count),
                allowedRanks
            });

            await ShowTakeCardModalToNextPlayer(roomId, room);
            await BroadcastGameState(roomId);
        }

        private async Task AutoRejectTakeCardsAsync(string roomId, DurakRoom room, int userId)
        {
            DurakPlayer? player;

            lock (room.StateLock)
            {
                if (!room.IsThrowInPhaseActive || room.DefenderId == userId)
                    return;

                var currentThrowInPlayerId = room.AttackerQueue
                    .Where(playerId => !room.TakeCardsVotes.Contains(playerId) &&
                                       !room.PlayersWhoPassedThisRound.Contains(playerId))
                    .FirstOrDefault();

                if (currentThrowInPlayerId != userId)
                    return;

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                    return;

                room.PlayersWhoPassedThisRound.Add(userId);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerVoted", new
            {
                playerName = player!.Name,
                action = "Reject",
                message = $"{player.Name} vaxt bitdiyi üçün rədd etdi",
                acceptCount = room.TakeCardsVotes.Count,
                rejectCount = room.PlayersWhoPassedThisRound.Count,
                totalNeeded = room.Players.Count - 1
            });

            await ShowTakeCardModalToNextPlayer(roomId, room);
            await BroadcastGameState(roomId);
        }

        private async Task AutoPassAttackAsync(string roomId, DurakRoom room, int userId)
        {
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
                return;

            int? nextAttackerId = null;
            bool shouldExecuteTakeCards = false;
            bool shouldExecuteBeaten = false;

            lock (room.StateLock)
            {
                if (room.AttackerQueue.Count == 0)
                    return;

                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker == null || currentAttacker.Value != userId)
                    return;

                room.PlayerPassThisRound(userId);
                room.MoveToNextAttackerInQueue();
                var nextAttacker = room.GetCurrentAttackerInQueue();

                if (nextAttacker == null)
                {
                    if (room.TableCards.Count == 0 && room.DefendedPairs.Count > 0)
                        shouldExecuteBeaten = true;
                    else if (room.TableCards.Count > 0)
                        shouldExecuteTakeCards = true;
                }
                else
                {
                    nextAttackerId = nextAttacker;
                }
            }

            if (shouldExecuteBeaten)
            {
                if (room.Players.Count == 2)
                {
                    lock (room.StateLock)
                    {
                        room.TableCards.Clear();
                        room.DefendedPairs.Clear();
                        int oldDefenderId = room.DefenderId;
                        int oldAttackerId = room.AttackerId;
                        room.AttackerId = oldDefenderId;
                        room.DefenderId = oldAttackerId;
                        room.ResetAttackRound();
                        room.RefillHands();
                    }

                    foreach (var p in room.Players)
                        await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

                    await _hubContext.Clients.Group(roomId).SendAsync("BeatenComplete", new
                    {
                        message = $"⏱️ {player.Name} vaxtı bitdiyi üçün pas etdi. Yeni attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}"
                    });

                    await BroadcastGameState(roomId);
                    var result2P = room.CheckGameOver();
                    if (result2P != null)
                        await EndGame(roomId, result2P);
                    return;
                }

                await AutoBeatenAsync(roomId, room, room.AttackerId);
                return;
            }

            if (shouldExecuteTakeCards)
            {
                await ExecuteDefenderTakesCards(roomId, room);
                return;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerPassed", new
            {
                playerName = player.Name,
                nextAttackerName = room.Players.FirstOrDefault(p => p.UserId == nextAttackerId)?.Name,
                isAutoAction = true
            });

            await BroadcastGameState(roomId);
        }

        private async Task AutoBrokenBeatenPassAsync(string roomId, DurakRoom room, int userId)
        {
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
                return;

            bool shouldCheckDefender = false;

            lock (room.StateLock)
            {
                if (room.AttackerQueue.Count == 0)
                    return;

                var currentAttacker = room.GetCurrentAttackerInQueue();
                if (currentAttacker == null || currentAttacker != userId)
                    return;

                room.PlayersWhoPassedThisRound.Add(userId);
                room.MoveToNextAttackerInQueue();
                var next = room.GetCurrentAttackerInQueue();
                shouldCheckDefender = next == null || next == 0;
            }

            await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CloseBrokenBeatenQueueModal", new
            {
                message = "⏱️ Vaxt bitdi - pas edildi"
            });

            await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenPlayerPassed", new
            {
                playerName = player.Name,
                message = $"⏱️ {player.Name} vaxtı bitdiyi üçün pas etdi"
            });

            if (shouldCheckDefender)
            {
                DurakPlayer? defender = null;
                bool canDefendAll = true;
                bool noNewCardsThrown = false;

                lock (room.StateLock)
                {
                    defender = room.Players.FirstOrDefault(p => p.UserId == room.DefenderId);
                    if (defender == null)
                        return;

                    if (room.TableCards.Count == 0)
                    {
                        noNewCardsThrown = true;
                    }
                    else
                    {
                        foreach (var attackCard in room.TableCards)
                        {
                            bool canDefendThis = defender.Hand.Any(defCard => room.GameEngine.CanDefend(attackCard, defCard));
                            if (!canDefendThis)
                            {
                                canDefendAll = false;
                                break;
                            }
                        }
                    }
                }

                if (noNewCardsThrown)
                {
                    await CompleteBeatenDirectly(roomId, room);
                    return;
                }

                if (canDefendAll)
                {
                    await _hubContext.Clients.Group(roomId).SendAsync("BrokenBeatenDefenderMustDefend", new
                    {
                        message = $"Hamı pas etdi!\n{defender?.Name} müdafiə etməlidir!",
                        defenderName = defender?.Name,
                        canDefend = true
                    });
                    await BroadcastGameState(roomId);
                    return;
                }

                await ExecuteDefenderTakesCards(roomId, room);
                return;
            }

            await BroadcastGameState(roomId);
            await ShowBeatenQueueChoiceModal(roomId, room);
        }

        private async Task AutoBeatenAsync(string roomId, DurakRoom room, int userId)
        {
            bool is2P = false;
            bool queueEmpty = false;

            lock (room.StateLock)
            {
                if (room.AttackerId != userId || room.TableCards.Count > 0 || room.DefendedPairs.Count == 0)
                    return;

                if (room.Players.Count == 2)
                {
                    int oldDefenderId2P = room.DefenderId;
                    int oldAttackerId2P = room.AttackerId;
                    room.TableCards.Clear();
                    room.DefendedPairs.Clear();
                    room.AttackerId = oldDefenderId2P;
                    room.DefenderId = oldAttackerId2P;
                    room.ResetAttackRound();
                    room.RefillHands();
                    is2P = true;
                }
                else
                {
                    room.IsBeatenPhaseActive = true;
                    room.IsTakeCardPhaseActive = false;
                    room.IsBrokenBeatenPhaseActive = true;
                    room.IsThrowInPhaseActive = false;

                    room.AttackerQueue.Clear();
                    room.PlayersWhoPassedThisRound.Clear();
                    room.CurrentAttackerQueueIndex = 0;
                    room.TakeCardsVotes.Clear();

                    foreach (var p in room.Players)
                    {
                        if (p.UserId != room.AttackerId && p.UserId != room.DefenderId)
                            room.AttackerQueue.Add(p.UserId);
                    }

                    queueEmpty = room.AttackerQueue.Count == 0;
                }
            }

            if (is2P)
            {
                foreach (var p in room.Players)
                    await _hubContext.Clients.Client(p.ConnectionId).SendAsync("YourCards", p.Hand);

                await _hubContext.Clients.Group(roomId).SendAsync("BeatenComplete", new
                {
                    message = $"⏱️ Vaxt bitdi - beaten avtomatik tamamlandı\n⚔️ YENİ Attacker: {room.Players.FirstOrDefault(p => p.UserId == room.AttackerId)?.Name}"
                });

                var result2P = room.CheckGameOver();
                if (result2P != null)
                    await EndGame(roomId, result2P);
                else
                    await BroadcastGameState(roomId);
                return;
            }

            if (queueEmpty)
            {
                await CompleteBeatenDirectly(roomId, room);
                return;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("BeatenPhaseStarted", new
            {
                message = "⏱️ Main attacker vaxtı bitdiyi üçün beaten avtomatik başladı",
                attackerId = room.AttackerId,
                defenderId = room.DefenderId,
                queueOrder = room.AttackerQueue
                    .Select(id => room.Players.FirstOrDefault(p => p.UserId == id)?.Name)
                    .Where(n => n != null)
                    .ToList()
            });

            await BroadcastGameState(roomId);
            await ShowBeatenQueueChoiceModal(roomId, room);
        }

        public async Task UseExtraTime()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaqda deyilsiniz");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyun aktiv deyil");
                return;
            }

            var userId = GetUserId();
            CancellationTokenSource? previousCts = null;
            CancellationToken newToken = CancellationToken.None;
            int newSequence = 0;
            int extraTimesLeft = 0;

            lock (room.StateLock)
            {
                if (room.TurnPlayerId != userId || room.TurnDeadlineUtc == null || room.TurnTimerCts == null)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "Hazırda sıra sizdə deyil"));
                    return;
                }

                extraTimesLeft = GetRemainingExtraTimesLocked(room, userId);
                if (extraTimesLeft <= 0)
                {
                    Task.Run(async () => await Clients.Caller.SendAsync("ActionError", "Əlavə vaxtınız qalmayıb"));
                    return;
                }

                room.ExtraTimeRemaining[userId] = extraTimesLeft - 1;
                room.TurnDeadlineUtc = room.TurnDeadlineUtc.Value.AddSeconds(EXTRA_TIME_SECONDS);
                room.TurnDurationSeconds += EXTRA_TIME_SECONDS;

                previousCts = room.TurnTimerCts;
                room.TurnTimerCts = new CancellationTokenSource();
                room.TurnTimerSequence++;
                newSequence = room.TurnTimerSequence;
                newToken = room.TurnTimerCts.Token;
                extraTimesLeft = room.ExtraTimeRemaining[userId];
            }

            previousCts?.Cancel();
            previousCts?.Dispose();
            _ = RunTurnTimerAsync(roomId, newSequence, newToken);

            await _hubContext.Clients.Group(roomId).SendAsync("ExtraTimeUsed", new
            {
                playerId = userId,
                playerName = room.Players.FirstOrDefault(p => p.UserId == userId)?.Name,
                extraTimesLeft
            });

            await BroadcastGameState(roomId);
        }

        public async Task ResolveTurnTimeout()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
                return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null || !room.IsGameActive)
                return;

            int sequence;

            lock (room.StateLock)
            {
                if (room.TurnDeadlineUtc == null || room.TurnDeadlineUtc > DateTime.UtcNow)
                    return;

                sequence = room.TurnTimerSequence;
            }

            await HandleTurnTimeoutAsync(roomId, sequence);
        }

        private string? GetCurrentRoom()
        {
            if (_userRooms.TryGetValue(Context.ConnectionId, out var roomId))
            {
                if (_roomManager.GetRoom(roomId) != null)
                    return roomId;

                // Otaq timeout zamanı silinibsə stale connection mapping-i saxlamamaq
                // və aşağıdakı user/room fallback-ə keçmək lazımdır.
                _userRooms.TryRemove(Context.ConnectionId, out _);
            }

            var userId = GetUserId();
            if (userId > 0)
            {
                // Mapping connection reconnect zamanı itə bilər. Oyunçunun hələ
                // otaqda olduğunu manager-dən də tap ki, rematch sorğusu köhnə
                // connection mapping-ə görə itirilməsin.
                if (!_userActiveRooms.TryGetValue(userId, out roomId))
                    roomId = _roomManager.GetRoomByPlayerUserId(userId)?.RoomId;

                if (string.IsNullOrEmpty(roomId))
                    return null;

                var room = _roomManager.GetRoom(roomId);
                if (room != null)
                {
                    lock (room.StateLock)
                    {
                        var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                        if (player != null &&
                            (!player.IsDisconnected && player.ConnectionId == Context.ConnectionId ||
                             room.RematchDeadlineUtc.HasValue && player.IsDisconnected))
                        {
                            player.ConnectionId = Context.ConnectionId;
                            player.IsDisconnected = false;
                            player.DisconnectedAt = null;
                            _userRooms[Context.ConnectionId] = roomId;
                            _userActiveRooms[userId] = roomId;
                            return roomId;
                        }
                    }
                }

                _userActiveRooms.TryRemove(userId, out _);
            }

            return null;
        }

        private async Task BroadcastGameState(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<(int UserId, string Name, int CardCount, bool IsAttacker, bool IsDefender, bool IsQueuePlayer, string ProfileImage, bool IsDisconnected)> playerSnapshots;
            List<(string Rank, string Suit)> tableCardsSnapshot;
            List<((string Rank, string Suit) AttackCard, (string Rank, string Suit) DefendCard)> defendedCardsSnapshot;
            List<(string ConnectionId, string Name, List<(string Rank, string Suit)> Cards)> handSnapshots;
            (string Rank, string Suit)? trumpCardSnapshot = null;
            string attackMode;
            bool isThrowInEnabled;
            bool isTransferEnabled;
            string gameMode;
            bool isPassingEnabled;
            int deckCount;
            int deckSize;
            bool isGameActive;
            decimal totalPrize;
            bool isThrowInPhaseActive;
            bool isBeatenPhaseActive;
            bool isTakeCardPhaseActive;
            bool isBrokenBeatenPhaseActive;
            bool showBeatenChoiceModal;
            int? currentAttackerId;
            string? currentAttackerName;
            TurnTimerSnapshot turnTimerSnapshot;
            TurnTimerPlan turnTimerPlan;

            lock (room.StateLock)
            {
                turnTimerPlan = PrepareTurnTimerLocked(room);
                turnTimerSnapshot = BuildTurnTimerSnapshotLocked(room);

                if (room.IsThrowInPhaseActive)
                {
                    var throwInAttacker = FindNextThrowInAttackerLocked(room);
                    currentAttackerId = throwInAttacker == 0 ? null : throwInAttacker;
                }
                else if (room.IsBrokenBeatenPhaseActive || room.IsBeatenPhaseActive || room.IsTakeCardPhaseActive)
                {
                    currentAttackerId = FindNextBeatenAttackerLocked(room);
                }
                else
                {
                    currentAttackerId = room.GetCurrentAttackerInQueue();
                }

                currentAttackerName = currentAttackerId != null
                    ? room.Players.FirstOrDefault(p => p.UserId == currentAttackerId)?.Name
                    : null;

                playerSnapshots = room.Players
                    .Select(p => (
                        p.UserId,
                        p.Name,
                        p.Hand.Count,
                        room.AttackerId == p.UserId,
                        room.DefenderId == p.UserId,
                        room.AttackerQueue.Contains(p.UserId),
                        p.ProfileImage ?? "/assets/characters/default.png",
                        p.IsDisconnected))
                    .ToList();

                tableCardsSnapshot = room.TableCards
                    .Select(c => (c.Rank, c.Suit))
                    .ToList();

                defendedCardsSnapshot = room.DefendedPairs
                    .Select(pair => (
                        (pair.AttackCard.Rank, pair.AttackCard.Suit),
                        (pair.DefendCard.Rank, pair.DefendCard.Suit)))
                    .ToList();

                handSnapshots = room.Players
                    .Select(player => (
                        player.ConnectionId,
                        player.Name,
                        player.Hand.Select(c => (c.Rank, c.Suit)).ToList()))
                    .ToList();

                if (room.TrumpCard != null)
                {
                    trumpCardSnapshot = (room.TrumpCard.Rank, room.TrumpCard.Suit);
                }

                attackMode = room.GameSettings.AttackMode.ToString();
                isThrowInEnabled = room.GameSettings.IsThrowInEnabled;
                isTransferEnabled = room.GameSettings.IsTransferEnabled;
                gameMode = room.GameSettings.GameMode.ToString();
                isPassingEnabled = room.GameSettings.IsPassingEnabled;
                deckCount = room.Deck.Count;
                deckSize = room.DeckSize;
                isGameActive = room.IsGameActive;
                totalPrize = room.TotalPrize;
                isThrowInPhaseActive = room.IsThrowInPhaseActive;
                isBeatenPhaseActive = room.IsBeatenPhaseActive;
                isTakeCardPhaseActive = room.IsTakeCardPhaseActive;
                isBrokenBeatenPhaseActive = room.IsBrokenBeatenPhaseActive;
                showBeatenChoiceModal = room.IsBrokenBeatenPhaseActive && currentAttackerId != null;
            }

            var playersData = playerSnapshots.Select(player => new
            {
                userId = player.UserId,
                name = player.Name,
                cardCount = player.CardCount,
                isAttacker = player.IsAttacker,
                isDefender = player.IsDefender,
                isQueuePlayer = player.IsQueuePlayer,
                profileImage = player.ProfileImage,
                isDisconnected = player.IsDisconnected
            }).ToList();

            Console.WriteLine($"🎮 BroadcastGameState - Oyunçu Vəziyyəti:");
            foreach (var player in playerSnapshots)
            {
                Console.WriteLine($"   {player.Name}: Attacker={player.IsAttacker}, Defender={player.IsDefender}, QueuePlayer={player.IsQueuePlayer}");
            }

            ActivatePreparedTurnTimer(roomId, turnTimerPlan);

            await _hubContext.Clients.Group(roomId).SendAsync("GameState", new
            {
                players = playersData,
                deckCount,
                deckSize,
                gameSettings = new
                {
                    attackMode,
                    isThrowInEnabled,
                    isTransferEnabled,
                    gameMode,
                    isPassingEnabled
                },
                trumpCard = trumpCardSnapshot != null ? new
                {
                    rank = trumpCardSnapshot.Value.Rank,
                    suit = trumpCardSnapshot.Value.Suit
                } : null,
                tableCards = tableCardsSnapshot.Select(card => new
                {
                    rank = card.Rank,
                    suit = card.Suit
                }).ToList(),
                defendedCards = defendedCardsSnapshot.Select(pair => new
                {
                    attackCard = new { rank = pair.AttackCard.Rank, suit = pair.AttackCard.Suit },
                    defendCard = new { rank = pair.DefendCard.Rank, suit = pair.DefendCard.Suit }
                }).ToList(),
                isGameActive,
                totalPrize,
                isThrowInPhaseActive,
                isBeatenPhaseActive,
                isTakeCardPhaseActive,
                isBrokenBeatenPhaseActive,
                showBeatenChoiceModal,
                currentAttacker = currentAttackerId,
                currentAttackerName,
                turnTimer = new
                {
                    isActive = turnTimerSnapshot.IsActive,
                    playerId = turnTimerSnapshot.PlayerId,
                    playerName = turnTimerSnapshot.PlayerName,
                    actionKind = turnTimerSnapshot.ActionKind,
                    deadlineUtc = turnTimerSnapshot.DeadlineUtc,
                    durationSeconds = turnTimerSnapshot.DurationSeconds,
                    secondsLeft = turnTimerSnapshot.SecondsLeft,
                    extraTimesLeft = turnTimerSnapshot.ExtraTimesLeft
                }
            });

            foreach (var player in handSnapshots)
            {
                try
                {
                    if (string.IsNullOrEmpty(player.ConnectionId))
                    {
                        Console.WriteLine($"⚠️ {player.Name} - ConnectionId yoxdur!");
                        continue;
                    }

                    var cardsData = player.Cards.Select(card => new
                    {
                        rank = card.Rank,
                        suit = card.Suit
                    }).ToList();

                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("YourCards", cardsData);
                    Console.WriteLine($"📤 {player.Name} → {cardsData.Count} kart");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ {player.Name} xətası: {ex.Message}");
                }
            }
        }
        private async Task BroadcastGameOver(string roomId, GameEndResult gameEndResult)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            Console.WriteLine($"🏁 GameOver broadcast başlanır - {roomId}");

            // ✅ Hamıya GameOver mesajı göndər
            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                isDraw = gameEndResult.IsDraw,
                winners = gameEndResult.Winners.Select(w => new
                {
                    userId = w.UserId,
                    name = w.Name,
                    cardCount = w.Hand.Count
                }).ToList(),
                durak = gameEndResult.Durak != null ? new
                {
                    userId = gameEndResult.Durak.UserId,
                    name = gameEndResult.Durak.Name,
                    cardCount = gameEndResult.Durak.Hand.Count
                } : null,
                totalPrize = room.TotalPrize,
                gameMode = room.GameSettings.GameMode.ToString(),
                timestamp = DateTime.UtcNow
            });

            Console.WriteLine($"🏁 GameOver - {string.Join(", ", gameEndResult.Winners.Select(w => w.Name))} qalib!");
            if (gameEndResult.Durak != null)
            {
                Console.WriteLine($"   Durak: {gameEndResult.Durak.Name}");
            }
        }
    }
}

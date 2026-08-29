using BlogApp.Api.Hubs;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Security.Claims;
using static OkeyRoomManager;

namespace BlogApp.Api.Hubs
{
    public class OkeyHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly OkeyRoomManager _roomManager;
        private readonly IRankService _rankService;
        private readonly IHubContext<OkeyHub> _hubContext;
        private readonly IServiceScopeFactory _scopeFactory;

        public OkeyHub(
            BlogAppDbContext db,
            OkeyRoomManager roomManager,
            IRankService rankService,
            IHubContext<OkeyHub> hubContext,
            IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
            _hubContext = hubContext;
            _scopeFactory = scopeFactory;
        }

        private static readonly ConcurrentDictionary<int, DisconnectRecord> _disconnectedPlayers = new();
        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _roomCleanupTimers = new();
        private static readonly ConcurrentDictionary<string, string> _userRooms = new();
        private const decimal COMMISSION_RATE = 0.20m;

        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _turnTimers = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _systemAutoPlayTasks = new();
        private const int TURN_TIMEOUT_SECONDS = 30;
        private const int RECONNECT_TIMEOUT_SECONDS = 25;
        private const int SYSTEM_AUTO_PLAY_SECONDS = 2;
        private const int ROOM_CLEANUP_CHECK_INTERVAL = 10; // 
        private static readonly (string Name, decimal EntryFee, int MaxPlayers, OkeyGameMode Mode)[] PresetRooms =
        {
            ("Orta 2x", 0.50m, 2, OkeyGameMode.Okey101),
            ("Orta 3x", 0.50m, 3, OkeyGameMode.Okey101),
            ("Orta 4x", 0.50m, 4, OkeyGameMode.Okey101),
            ("Peşəkar 2x", 1m, 2, OkeyGameMode.Okey101),
            ("Peşəkar 3x", 1m, 3, OkeyGameMode.Okey101),
            ("Peşəkar 4x", 1m, 4, OkeyGameMode.Okey101),
            ("VIP 2x", 2m, 2, OkeyGameMode.Okey51),
            ("VIP 3x", 2m, 3, OkeyGameMode.Okey51),
            ("VIP 4x", 2m, 4, OkeyGameMode.Okey51),
            ("Master 2x", 5m, 2, OkeyGameMode.Okey51),
            ("Master 3x", 5m, 3, OkeyGameMode.Okey51),
            ("Master 4x", 5m, 4, OkeyGameMode.Okey51),
            ("Elite 2x", 10m, 2, OkeyGameMode.Okey51),
            ("Elite 3x", 10m, 3, OkeyGameMode.Okey51),
            ("Elite 4x", 10m, 4, OkeyGameMode.Okey51),
            ("Pro 2x", 20m, 2, OkeyGameMode.Okey51),
            ("Pro 3x", 20m, 3, OkeyGameMode.Okey51),
            ("Pro 4x", 20m, 4, OkeyGameMode.Okey51),
            ("Champion 2x", 50m, 2, OkeyGameMode.Okey51),
            ("Champion 3x", 50m, 3, OkeyGameMode.Okey51),
            ("Champion 4x", 50m, 4, OkeyGameMode.Okey51),
            ("Legend 2x", 100m, 2, OkeyGameMode.Okey51),
            ("Legend 3x", 100m, 3, OkeyGameMode.Okey51),
            ("Legend 4x", 100m, 4, OkeyGameMode.Okey51)
        };


        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"🔵 OkeyHub connection from: {Context.ConnectionId}");

            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Console.WriteLine($"❌ Unauthorized");
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
                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance, u.Image })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    Console.WriteLine($"❌ User not found: {userId}");
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.UserName}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                await RemoveExpiredSeatsForUserFromWaitingRooms(userId);

                PlayerRankDetails? rankDetails = null;
                try
                {
                    rankDetails = await _rankService.GetPlayerRankDetails(userId, GameType.Okey);
                }
                catch { }

                // ✅ RECONNECT SIYAHISINI YOXLA
                if (_disconnectedPlayers.TryGetValue(userId, out var record))
                {
                    if ((DateTime.UtcNow - record.DisconnectTime).TotalSeconds <= RECONNECT_TIMEOUT_SECONDS)
                    {
                        var room = _roomManager.GetRoom(record.RoomId);
                        if (room != null)
                        {
                            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                            if (player != null)
                            {
                                var restore = await RestoreExistingPlayerConnectionAsync(record.RoomId, room, player);

                                await Clients.Caller.SendAsync("ReconnectSuccess", new
                                {
                                    roomId = record.RoomId,
                                    roomName = room.RoomName,
                                    hand = ToClientHand(player.Hand),
                                    gameState = room.GetPublicState(),
                                    message = "🔗 Qoşulma bərpa edildi! Oyuna daxil oldun.",
                                    title = "✅ Reconnect Uğurlu",
                                    reconnectTime = DateTime.UtcNow
                                });

                                await Clients.Group(record.RoomId).SendAsync("PlayerReconnected", new
                                {
                                    playerName = fullName,
                                    message = $"🔗 {fullName} qayıtdı - oyunda yenidən aktiv",
                                    timestamp = DateTime.UtcNow
                                });

                                await Clients.Caller.SendAsync("UserData", new
                                {
                                    userId = user.Id,
                                    username = user.UserName,
                                    fullName,
                                    balance = user.Balance,
                                    rank = rankDetails?.CurrentRank ?? "Yeni Başlayan",
                                    profileImage = user.Image
                                });

                                if (restore.IsCurrentTurn)
                                {
                                    await NotifyCurrentTurnPlayer(record.RoomId, room);
                                }

                                Console.WriteLine($"✅ {fullName} reconnected to {room.RoomName}");

                                await base.OnConnectedAsync();
                                return;
                            }
                        }
                    }
                    else
                    {
                        _disconnectedPlayers.TryRemove(userId, out _);
                        await MarkPlayerSystemControlledAfterTimeout(record.RoomId, userId);
                        var expiredRoom = _roomManager.GetRoom(record.RoomId);
                        if (expiredRoom != null && !expiredRoom.IsGameStarted)
                        {
                            await RemoveExpiredSystemSeatsFromWaitingRoom(record.RoomId, expiredRoom);
                        }

                        await Clients.Caller.SendAsync("UserData", new
                        {
                            userId = user.Id,
                            username = user.UserName,
                            fullName,
                            balance = user.Balance,
                            rank = rankDetails?.CurrentRank ?? "Yeni Başlayan",
                            profileImage = user.Image
                        });

                        Console.WriteLine($"⏰ {fullName} reconnect timeout-a uğradı ({RECONNECT_TIMEOUT_SECONDS} saniyə keçdi)");

                        await Clients.Caller.SendAsync("ReconnectFailed", new
                        {
                            title = "❌ Reconnect Mümkün Deyil",
                            message = $"{RECONNECT_TIMEOUT_SECONDS} saniyə keçib. Köhnə otaqla əlaqəniz kəsildi, yeni oyun axtara bilərsiniz.",
                            reason = "TIMEOUT",
                            suggestion = "Otaqlar siyahısından yeni oyun seçin.",
                            redirectToHome = false,
                            canBrowseRooms = true
                        });

                        await base.OnConnectedAsync();
                        return;
                    }
                }

                var existingRoom = _roomManager.GetRoomByUser(userId);
                var existingRoomPlayer = existingRoom?.Players.FirstOrDefault(p => p.UserId == userId);
                if (existingRoom != null && existingRoomPlayer != null && IsReconnectWindowClosed(existingRoomPlayer))
                {
                    if (!existingRoom.IsGameStarted)
                    {
                        await RemoveExpiredSystemSeatsFromWaitingRoom(existingRoom.RoomId, existingRoom);
                    }

                    await Clients.Caller.SendAsync("ReconnectFailed", new
                    {
                        title = "❌ Reconnect Mümkün Deyil",
                        message = $"{RECONNECT_TIMEOUT_SECONDS} saniyə keçib. Yeni oyun axtara bilərsiniz.",
                        reason = "TIMEOUT",
                        suggestion = "Otaqlar siyahısından yeni oyun seçin.",
                        redirectToHome = false,
                        canBrowseRooms = true
                    });
                }

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName,
                    balance = user.Balance,
                    rank = rankDetails?.CurrentRank ?? "Yeni Başlayan",
                    profileImage = user.Image
                });

                Console.WriteLine($"✅ Okey Connected: {fullName} (ID: {userId})");

                await base.OnConnectedAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnConnectedAsync error: {ex.Message}");
                Context.Abort();
                return;
            }
        }
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string connId = Context.ConnectionId;

            if (_userRooms.TryRemove(connId, out var roomId))
            {
                var room = _roomManager.GetRoom(roomId);
                if (room == null) return;

                OkeyPlayer? player = null;
                bool disconnectedCurrentPlayer = false;
                lock (room.StateLock)
                {
                    player = room.Players.FirstOrDefault(p => p.ConnectionId == connId);
                    if (player != null)
                    {
                        if (!room.IsGameStarted)
                        {
                            // ✅ OYUN BAŞLAMAYIBSA - NORMAL REFUND
                            room.Players.Remove(player);

                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    using var scope = _scopeFactory.CreateScope();
                                    var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                                    var user = await db.Users.FirstOrDefaultAsync(u => u.Id == player.UserId);
                                    if (user != null)
                                    {
                                        user.Balance += room.EntryFee;
                                        room.PotAmount -= room.EntryFee;
                                        await db.SaveChangesAsync();
                                        Console.WriteLine($"💰 Refund: {room.EntryFee}₼ → {player.Name}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"❌ Refund error: {ex.Message}");
                                }
                            });

                            _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", new
                            {
                                playerName = player.Name,
                                message = $"{player.Name} otaqdan çıxdı"
                            });
                        }
                        else
                        {
                            // ✅ OYUN DAVAM EDIRSƏ - RECONNECT MODUNA GEÇ
                            disconnectedCurrentPlayer = IsCurrentPlayer(room, player.UserId);
                            player.IsDisconnected = true;
                            player.DisconnectedAt = DateTime.UtcNow;
                            player.DisconnectGraceDeadlineUtc = player.DisconnectedAt.Value.AddSeconds(RECONNECT_TIMEOUT_SECONDS);

                            // ✅ DISCONNECT RECORD YARAD
                            _disconnectedPlayers[player.UserId] = new DisconnectRecord
                            {
                                UserId = player.UserId,
                                RoomId = roomId,
                                PlayerPosition = player.Position,
                                DisconnectTime = DateTime.UtcNow,
                                ReconnectTimeoutSeconds = RECONNECT_TIMEOUT_SECONDS
                            };

                            // ✅ DİĞƏR OYUNÇULARA BİLDİRİŞ (RECONNECT SAYAC)
                            _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnected", new
                            {
                                playerName = player.Name,
                                userId = player.UserId,
                                message = $"⏳ {player.Name} əlaqəni itirdi ({RECONNECT_TIMEOUT_SECONDS} saniyə gözlənilir...)",
                                reconnectTimeoutSeconds = RECONNECT_TIMEOUT_SECONDS,
                                disconnectTime = DateTime.UtcNow,
                                disconnectGraceDeadlineUtc = player.DisconnectGraceDeadlineUtc,
                                isCurrentTurn = disconnectedCurrentPlayer
                            });

                            _hubContext.Clients.Group(roomId).SendAsync("PlayerTempDisconnected", new
                            {
                                playerName = player.Name,
                                userId = player.UserId,
                                message = $"⏳ {player.Name} əlaqəni itirdi ({RECONNECT_TIMEOUT_SECONDS} saniyə gözlənilir...)",
                                reconnectTimeoutSeconds = RECONNECT_TIMEOUT_SECONDS,
                                disconnectTime = DateTime.UtcNow,
                                disconnectGraceDeadlineUtc = player.DisconnectGraceDeadlineUtc,
                                isCurrentTurn = disconnectedCurrentPlayer
                            });

                            Console.WriteLine($"⚠️ {player.Name} disconnect oldu - {RECONNECT_TIMEOUT_SECONDS} saniyə gözləyiliyor...");

                            if (disconnectedCurrentPlayer)
                            {
                                StopTurnTimer(roomId);
                                _hubContext.Clients.Group(roomId).SendAsync("TurnTimerStopped", new
                                {
                                    playerName = player.Name,
                                    userId = player.UserId,
                                    reason = "disconnect_reconnect_grace",
                                    message = $"{player.Name} disconnect oldu, reconnect gözlənilir."
                                });
                            }

                            // ✅ RECONNECT MÜDDƏTİNDƏN SONRA CHECK ET
                            _ = Task.Delay(RECONNECT_TIMEOUT_SECONDS * 1000).ContinueWith(async _ =>
                            {
                                // ✅ ƏGƏR HƏLƏ DƏ DISCONNECT SİYAHISINDADIRSA - SİSTEM İDARƏSİNƏ KEÇİR
                                if (_disconnectedPlayers.TryRemove(player.UserId, out var expiredRecord))
                                {
                                    Console.WriteLine($"⏰ {RECONNECT_TIMEOUT_SECONDS} saniyə keçdi: {player.Name} sistem idarəsinə keçirilir");
                                    await MarkPlayerSystemControlledAfterTimeout(expiredRecord.RoomId, player.UserId);

                                    var expiredRoom = _roomManager.GetRoom(expiredRecord.RoomId);
                                    if (expiredRoom != null && !expiredRoom.IsGameStarted)
                                    {
                                        await RemoveExpiredSystemSeatsFromWaitingRoom(expiredRecord.RoomId, expiredRoom);
                                    }
                                }
                            });
                        }
                    }
                }

                // ✅ OYUN BAŞLAMAYIBSA - BOŞOTAĞI SİL
                if (room.Players.Count == 0 && !room.IsGameStarted && room.CreatorId != 0)
                {
                    _roomManager.DeleteRoom(roomId);
                    await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
                    Console.WriteLine($"🗑️ Boş otaq silindi: {room.RoomName}");
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
        private int GetNextActivePlayerIndex(OkeyRoom room)
        {
            if (room.Players.Count == 0)
            {
                return -1;
            }

            if (room.CurrentPlayerIndex < -1 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                room.CurrentPlayerIndex = -1;
            }

            int attempts = 0;

            do
            {
                room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % room.Players.Count;
                attempts++;

                var currentPlayer = room.Players[room.CurrentPlayerIndex];
                if (!currentPlayer.IsEliminated && (!currentPlayer.IsDisconnected || currentPlayer.IsSystemControlled))
                {
                    return room.CurrentPlayerIndex;
                }

                if (attempts >= room.Players.Count)
                {
                    Console.WriteLine("❌ Heç aktiv oyunçu tapılmadı!");
                    return -1;
                }

            } while (true);
        }

        private bool IsCurrentPlayer(OkeyRoom room, int userId)
        {
            return room.CurrentPlayerIndex >= 0 &&
                   room.CurrentPlayerIndex < room.Players.Count &&
                   room.Players[room.CurrentPlayerIndex].UserId == userId;
        }

        private object CreateYourTurnPayload(OkeyRoom room, OkeyTile? lastDiscardedTile = null)
        {
            var currentPlayer = room.CurrentPlayerIndex >= 0 && room.CurrentPlayerIndex < room.Players.Count
                ? room.Players[room.CurrentPlayerIndex]
                : null;

            bool mustDiscard = currentPlayer != null && currentPlayer.HasDrawn;
            bool canDrawFromDiscard = currentPlayer != null && !currentPlayer.HasDrawn && room.DiscardPile.Count > 0;
            bool canDrawFromStock = currentPlayer != null && !currentPlayer.HasDrawn && !room.IsFinalDiscardRound && room.Stock.Count > 0;

            return new
            {
                canDrawFromDiscard,
                canDrawFromStock,
                mustDrawFromStock = canDrawFromStock && !canDrawFromDiscard,
                mustDiscard,
                isFinalDiscardRound = room.IsFinalDiscardRound,
                finalDiscardRemainingPlayers = room.IsFinalDiscardRound
                    ? GetFinalDiscardPlayers(room).Count(p => !room.FinalDiscardedUserIds.Contains(p.UserId))
                    : 0,
                lastDiscardedTile = lastDiscardedTile ?? room.DiscardPile.LastOrDefault()
            };
        }

        private static List<OkeyPlayer> GetFinalDiscardPlayers(OkeyRoom room)
        {
            return room.Players
                .Where(p => !p.IsEliminated && (!p.IsDisconnected || p.IsSystemControlled))
                .ToList();
        }

        private static void StartFinalDiscardRoundIfNeeded(OkeyRoom room)
        {
            if (room.IsFinalDiscardRound || room.Stock.Count > 0)
            {
                return;
            }

            room.IsFinalDiscardRound = true;
            room.FinalDiscardedUserIds.Clear();
            Console.WriteLine($"⚠️ FINAL DISCARD ROUND STARTED: {room.RoomName}");
        }

        private static bool MarkFinalDiscardAndCheckComplete(OkeyRoom room, OkeyPlayer player)
        {
            if (!room.IsFinalDiscardRound)
            {
                return false;
            }

            room.FinalDiscardedUserIds.Add(player.UserId);

            // Dəstənin son daşını çəkən oyunçunun cari atışı son şansdır.
            // O, adi qaydada discard edirsə, oyun dərhal heç-heçə bitir.
            return true;
        }

        private static int GetSystemAutoPlayDelaySeconds()
        {
            return SYSTEM_AUTO_PLAY_SECONDS;
        }

        private static object CreateTurnTimerPayload(OkeyRoom room, OkeyPlayer currentPlayer, int seconds, bool isSystemControlled = false)
        {
            return new
            {
                userId = currentPlayer.UserId,
                username = currentPlayer.Name,
                playerName = currentPlayer.Name,
                playerPosition = currentPlayer.Position,
                currentPlayerIndex = room.CurrentPlayerIndex,
                seconds,
                isSystemControlled,
                players = room.Players.Select(p => new
                {
                    userId = p.UserId,
                    username = p.Name,
                    playerName = p.Name,
                    playerPosition = p.Position,
                    isYourTurn = p.UserId == currentPlayer.UserId,
                    isOpponentTurn = p.UserId != currentPlayer.UserId,
                    timerSeconds = p.UserId == currentPlayer.UserId ? seconds : 0,
                    isSystemControlled = p.IsSystemControlled
                }).ToArray()
            };
        }

        private async Task NotifyCurrentTurnPlayer(string roomId, OkeyRoom room, OkeyTile? lastDiscardedTile = null)
        {
            if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                return;
            }

            var nextPlayer = room.Players[room.CurrentPlayerIndex];

            if (nextPlayer.IsSystemControlled)
            {
                int delaySeconds = GetSystemAutoPlayDelaySeconds();

                await _hubContext.Clients.Group(roomId).SendAsync("SystemTurnStarted", new
                {
                    userId = nextPlayer.UserId,
                    playerName = nextPlayer.Name,
                    playerPosition = nextPlayer.Position,
                    seconds = TURN_TIMEOUT_SECONDS,
                    message = $"🤖 {nextPlayer.Name} əvəzinə sistem oynayır"
                });

                await _hubContext.Clients.Group(roomId).SendAsync(
                    "TurnTimerStarted",
                    CreateTurnTimerPayload(room, nextPlayer, TURN_TIMEOUT_SECONDS, isSystemControlled: true));

                ScheduleSystemAutoPlay(roomId, nextPlayer.UserId, delaySeconds);
                return;
            }

            await _hubContext.Clients.Client(nextPlayer.ConnectionId).SendAsync(
                "YourTurn",
                CreateYourTurnPayload(room, lastDiscardedTile));

            StartTurnTimer(roomId, nextPlayer.UserId);
        }

        private async Task MarkPlayerSystemControlledAfterTimeout(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            OkeyPlayer? player = null;
            bool changed = false;
            bool shouldAutoPlayCurrentTurn = false;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.IsEliminated || !player.IsDisconnected)
                {
                    return;
                }

                if (!player.IsSystemControlled)
                {
                    player.IsSystemControlled = true;
                    player.SystemControlledAtUtc = DateTime.UtcNow;
                    changed = true;
                }

                shouldAutoPlayCurrentTurn = IsCurrentPlayer(room, userId);
            }

            if (!changed || player == null)
            {
                return;
            }

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerBecameSystemControlled", new
            {
                playerName = player.Name,
                userId = player.UserId,
                message = $"🤖 {player.Name} qayıtmadı ({RECONNECT_TIMEOUT_SECONDS} saniyə keçdi) - yerinə sistem oynayacaq",
                timestamp = DateTime.UtcNow
            });

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerDisconnectExpired", new
            {
                playerName = player.Name,
                userId = player.UserId,
                message = $"🤖 {player.Name} {RECONNECT_TIMEOUT_SECONDS} saniyə ərzində qayıtmadı. Sistem onun yerinə oynayacaq.",
                timestamp = DateTime.UtcNow
            });

            if (shouldAutoPlayCurrentTurn)
            {
                await NotifyCurrentTurnPlayer(roomId, room);
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());
        }

        private async Task<(bool WasSystemControlled, bool IsCurrentTurn)> RestoreExistingPlayerConnectionAsync(
            string roomId,
            OkeyRoom room,
            OkeyPlayer player)
        {
            bool wasSystemControlled;
            bool isCurrentTurn;

            lock (room.StateLock)
            {
                wasSystemControlled = player.IsSystemControlled;
                player.ConnectionId = Context.ConnectionId;
                player.IsDisconnected = false;
                player.IsSystemControlled = false;
                player.DisconnectedAt = null;
                player.DisconnectGraceDeadlineUtc = null;
                player.SystemControlledAtUtc = null;
                isCurrentTurn = IsCurrentPlayer(room, player.UserId);
            }

            _disconnectedPlayers.TryRemove(player.UserId, out _);
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            _userRooms[Context.ConnectionId] = roomId;

            if (wasSystemControlled && isCurrentTurn)
            {
                StopSystemAutoPlay(roomId);
            }

            return (wasSystemControlled, isCurrentTurn);
        }

        private static bool IsReconnectWindowClosed(OkeyPlayer player)
        {
            return player.IsSystemControlled ||
                   (player.DisconnectGraceDeadlineUtc.HasValue &&
                    DateTime.UtcNow > player.DisconnectGraceDeadlineUtc.Value);
        }

        private static bool HasExpiredSeatInStartedRoom(OkeyRoom room, int userId)
        {
            if (!room.IsGameStarted)
            {
                return false;
            }

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            return player != null && IsReconnectWindowClosed(player);
        }

        private static bool IsExpiredSystemSeat(OkeyPlayer player)
        {
            return player.IsDisconnected && IsReconnectWindowClosed(player);
        }

        private async Task<List<OkeyPlayer>> RemoveExpiredSystemSeatsFromWaitingRoom(string roomId, OkeyRoom room)
        {
            var removedPlayers = new List<OkeyPlayer>();

            lock (room.StateLock)
            {
                if (room.IsGameStarted)
                {
                    return removedPlayers;
                }

                removedPlayers = room.Players
                    .Where(IsExpiredSystemSeat)
                    .ToList();

                foreach (var player in removedPlayers)
                {
                    room.Players.Remove(player);
                    _disconnectedPlayers.TryRemove(player.UserId, out _);
                    _userRooms.TryRemove(player.ConnectionId, out _);
                    Console.WriteLine($"🧹 Expired system seat removed from waiting room: {player.Name}");
                }

                for (int i = 0; i < room.Players.Count; i++)
                {
                    room.Players[i].Position = i;
                }
            }

            foreach (var player in removedPlayers)
            {
                if (!string.IsNullOrWhiteSpace(player.ConnectionId))
                {
                    try { await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId); } catch { }
                }

                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", new
                {
                    playerName = player.Name,
                    message = $"{player.Name} reconnect vaxtı bitdiyi üçün otaqdan çıxarıldı"
                });
            }

            if (removedPlayers.Count > 0)
            {
                await BroadcastRoomPlayers(roomId);
                await _hubContext.Clients.All.SendAsync("RoomUpdated", new
                {
                    roomId,
                    playerCount = room.Players.Count,
                    maxPlayers = room.MaxPlayers,
                    isGameStarted = room.IsGameStarted,
                    canJoin = !room.IsGameStarted && room.Players.Count < room.MaxPlayers
                });
            }

            return removedPlayers;
        }

        private async Task RemoveExpiredSeatsForUserFromWaitingRooms(int userId)
        {
            foreach (var room in _roomManager.GetAllRooms())
            {
                bool hasExpiredSeat;
                lock (room.StateLock)
                {
                    hasExpiredSeat = !room.IsGameStarted &&
                        room.Players.Any(p => p.UserId == userId && IsExpiredSystemSeat(p));
                }

                if (hasExpiredSeat)
                {
                    await RemoveExpiredSystemSeatsFromWaitingRoom(room.RoomId, room);
                }
            }
        }

        private async Task SendReconnectWindowClosedAsync(string roomId, int userId)
        {
            _disconnectedPlayers.TryRemove(userId, out _);
            await MarkPlayerSystemControlledAfterTimeout(roomId, userId);
            var room = _roomManager.GetRoom(roomId);
            if (room != null && !room.IsGameStarted)
            {
                await RemoveExpiredSystemSeatsFromWaitingRoom(roomId, room);
            }

            await Clients.Caller.SendAsync("ReconnectFailed", new
            {
                title = "❌ Reconnect Vaxtı Bitdi",
                message = $"{RECONNECT_TIMEOUT_SECONDS} saniyə keçib. Bu otağa geri qayıtmaq mümkün deyil.",
                reason = "TIMEOUT",
                suggestion = "Otaqlar siyahısından yeni oyun seçin.",
                redirectToHome = false,
                canBrowseRooms = true
            });
        }

        public async Task RequestManualReconnect(string roomId)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ReconnectFailed", new
                {
                    title = "❌ Otaq Tapılmadı",
                    message = "Oyunun olduğu otaq artıq silinib.",
                    reason = "ROOM_NOT_FOUND",
                    suggestion = "Yeni oyun axtarın"
                });
                return;
            }

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                await Clients.Caller.SendAsync("ReconnectFailed", new
                {
                    title = "❌ Oyunçu Tapılmadı",
                    message = "Sizin yeriniz əlində başqa oyunçu tərəfindən tutulub.",
                    reason = "PLAYER_NOT_FOUND",
                    suggestion = "Yeni oyun axtarın"
                });
                return;
            }

            if (IsReconnectWindowClosed(player))
            {
                await SendReconnectWindowClosedAsync(roomId, userId);
                return;
            }

            if (_disconnectedPlayers.TryGetValue(userId, out var record))
            {
                var timeElapsed = (DateTime.UtcNow - record.DisconnectTime).TotalSeconds;
                if (timeElapsed > RECONNECT_TIMEOUT_SECONDS)
                {
                    await SendReconnectWindowClosedAsync(roomId, userId);
                    return;
                }
            }
            else if (room.IsGameStarted && IsReconnectWindowClosed(player))
            {
                await SendReconnectWindowClosedAsync(roomId, userId);
                return;
            }

            var restore = await RestoreExistingPlayerConnectionAsync(roomId, room, player);

            await Clients.Caller.SendAsync("ReconnectSuccess", new
            {
                roomId = roomId,
                roomName = room.RoomName,
                hand = ToClientHand(player.Hand),
                gameState = room.GetPublicState(),
                message = restore.WasSystemControlled
                    ? "🔗 Yeriniz sistemdən geri alındı. Oyuna davam edə bilərsiniz."
                    : "🔗 Qoşulma bərpa edildi! Oyuna daxil oldun.",
                title = "✅ Reconnect Uğurlu",
                reconnectTime = DateTime.UtcNow,
                wasSystemControlled = restore.WasSystemControlled
            });

            await Clients.Group(roomId).SendAsync("PlayerReconnected", new
            {
                playerName = player.Name,
                message = restore.WasSystemControlled
                    ? $"🔗 {player.Name} qayıtdı - sistem idarəsi dayandırıldı"
                    : $"🔗 {player.Name} qayıtdı - oyunda yenidən aktiv",
                timestamp = DateTime.UtcNow
            });

            await BroadcastRoomPlayers(roomId);
            await Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());

            if (restore.IsCurrentTurn)
            {
                await NotifyCurrentTurnPlayer(roomId, room);
            }

            Console.WriteLine($"✅ Manual reconnect: {player.Name} (system was: {restore.WasSystemControlled})");
        }

        private async Task<object> SendExistingPlayerJoinedAsync(
            string roomId,
            OkeyRoom room,
            OkeyPlayer existingPlayer,
            decimal balance)
        {
            if (IsReconnectWindowClosed(existingPlayer))
            {
                await SendReconnectWindowClosedAsync(roomId, existingPlayer.UserId);
                return new { success = false, reason = "TIMEOUT", canBrowseRooms = true };
            }

            if (room.IsGameStarted)
            {
                if (_disconnectedPlayers.TryGetValue(existingPlayer.UserId, out var record) &&
                    (DateTime.UtcNow - record.DisconnectTime).TotalSeconds > RECONNECT_TIMEOUT_SECONDS)
                {
                    await SendReconnectWindowClosedAsync(roomId, existingPlayer.UserId);
                    return new { success = false, reason = "TIMEOUT", canBrowseRooms = true };
                }
            }

            var restore = await RestoreExistingPlayerConnectionAsync(roomId, room, existingPlayer);

            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId,
                roomName = room.RoomName,
                hand = ToClientHand(existingPlayer.Hand),
                balance,
                position = existingPlayer.Position,
                gameState = room.GetPublicState(),
                isGameStarted = room.IsGameStarted,
                maxPlayers = room.MaxPlayers,
                wasSystemControlled = restore.WasSystemControlled
            });

            await Clients.Group(roomId).SendAsync("PlayerReconnected", new
            {
                playerName = existingPlayer.Name,
                message = restore.WasSystemControlled
                    ? $"🔗 {existingPlayer.Name} qayıtdı - sistem idarəsi dayandırıldı"
                    : $"🔗 {existingPlayer.Name} qayıtdı - oyunda yenidən aktiv",
                timestamp = DateTime.UtcNow
            });

            await BroadcastRoomPlayers(roomId);
            await Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());

            if (restore.IsCurrentTurn)
            {
                await NotifyCurrentTurnPlayer(roomId, room);
            }

            return new { success = true, roomId };
        }

        private async Task EndGameNoWinner(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ EndGameNoWinner skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                room.IsGameStarted = false;
            }

            StopTurnTimer(roomId);

            Console.WriteLine($"⚠️ NO WINNER: Hamı oyunu tərk etdi - {room.RoomName}");

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                reason = "Hamı oyunu tərk etdi. Qalib yoxdur.",
                noWinner = true,
                finalScores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isEliminated = true
                }).ToArray()
            });

            // ✅ Otağı təmizlə
            foreach (var player in room.Players.ToList())
            {
                await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId);
                _userRooms.TryRemove(player.ConnectionId, out _);
            }

            _roomManager.DeleteRoom(roomId);
            await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
            Console.WriteLine($"🗑️ Otaq silindi (qalib yoxdur): {room.RoomName}");
        }
        private async Task AnnounceGameOver(string roomId, string reason = null)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            // Final qalibi tap
            var finalWinner = room.Players
                .OrderByDescending(p => p.Score)
                .FirstOrDefault();

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                finalWinner = finalWinner?.Name ?? "Naməlum",
                reason = reason ?? "Oyun tamamlandı",
                finalScores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score
                }).ToList()
            });

            // ✅ 5 saniyə sonra hamını otaqdan çıxart
            // ✅ Hamını çıxart
            var allPlayers = room.Players.ToList();
            foreach (var player in allPlayers)
            {
                try
                {
                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("LeftRoom");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error removing player: {ex.Message}");
                }
            }

            // Hamını SignalR qruplarından çıxart və internal xəritəni təmizlə
            foreach (var p in room.Players.ToList())
            {
                try { await Groups.RemoveFromGroupAsync(p.ConnectionId, roomId); } catch { }
                _userRooms.TryRemove(p.ConnectionId, out _);
            }

            _roomManager.DeleteRoom(roomId);
            await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
            Console.WriteLine($"🗑️ Room deleted: {roomId}");
        }
        private async Task AnnounceRoundWinner(string roomId, OkeyPlayer winner, string winType, List<List<OkeyTile>> melds)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            // Qalibi elan et
            await _hubContext.Clients.Group(roomId).SendAsync("RoundOver", new
            {
                winner = winner.Name,
                winType = winType,
                melds = melds.Select(m => m.Select(ToClientTile).ToList()).ToList()
            });

            // ✅ 5 saniyə gözlə
            await Task.Delay(5000);

            // ✅ OTAĞI TAM TEMİZLƏ
            await ResetRoomAfterRound(roomId);
        }

        private async Task ResetRoomAfterRound(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            Console.WriteLine($"🔄 Raund sıfırlanıyor: {roomId}");

            StopTurnTimer(roomId);

            lock (room.StateLock)
            {
                // ✅ OYUN VƏZİYYƏTİNİ TAMAMILƏ SİFİRLA
                room.IsGameStarted = false;
                room.IsGameFinished = false;
                room.CurrentPlayerIndex = 0;
                room.RoundNumber++;

                // ✅ DƏSTƏ VƏ DAŞLARI TAMAMILƏ TEMİZLƏ
                room.Stock.Clear();
                room.DiscardPile.Clear(); // ✅ TAM TEMİZLİK
                room.Indicator = null;
                room.JokerTile = null;
                room.IsFinalDiscardRound = false;
                room.FinalDiscardedUserIds.Clear();

                // ✅ OYUNÇULARIN ƏLLƏRİNİ TAMAMILƏ TEMİZLƏ
                foreach (var player in room.Players)
                {
                    player.Hand.Clear();
                    player.HasDrawn = false;
                    player.HasRankResultApplied = false;
                }

                // ✅ Xalı 0 və ya mənfi olan oyunçuları çıxart
                var playersToRemove = room.Players.Where(p => p.Score <= 0 || p.IsEliminated).ToList();
                foreach (var player in playersToRemove)
                {
                    room.Players.Remove(player);
                    Console.WriteLine($"🚪 Oyunçu silinir (eliminated): {player.Name}");
                }
            }

            // ✅ Frontend-ə tamamilə yeni raund başladığını bildir
            await _hubContext.Clients.Group(roomId).SendAsync("GameReset", new
            {
                message = "Yeni raund başlayır...",
                roundNumber = room.RoundNumber,
                clearedState = true // ✅ Köhnə daşlar silinib
            });

            await BroadcastRoomPlayers(roomId);
            Console.WriteLine($"✅ Raund reset tamamlandı: {roomId} | Raund: {room.RoundNumber}");

            await RemoveExpiredSystemSeatsFromWaitingRoom(roomId, room);

            // ✅ 2+ oyunçu varsa oyun başlat
            if (room.Players.Count >= 2)
            {
                await Task.Delay(2000);
                await StartGame(roomId);
            }
            else
            {
                await _hubContext.Clients.Group(roomId).SendAsync("WaitingForPlayers", new
                {
                    message = "Yeni oyun üçün ən azı 2 oyunçu gözlənilir...",
                    currentPlayers = room.Players.Count
                });
            }
        }
        public async Task<object> CreateRoom(
            string roomName,
            decimal entryFee = 50,
            int maxPlayers = 4,
            string gameMode = "Okey101",
            bool isPrivate = false,
            string? password = null)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                return new { success = false, message = "İstifadəçi tapılmadı" };
            }

            try
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null || user.Balance < entryFee)
                {
                    return new { success = false, message = "Kifayət qədər balans yoxdur" };
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                OkeyGameMode mode = gameMode == "Okey51" ? OkeyGameMode.Okey51 : OkeyGameMode.Okey101;

                var room = _roomManager.CreateRoom(
                    roomName, fullName, userId, entryFee, maxPlayers, mode, isPrivate, password);

                if (room == null)
                {
                    return new { success = false, message = "Otaq yaradıla bilmədi" };
                }

                await Clients.All.SendAsync("RoomCreated", new
                {
                    roomId = room.RoomId,
                    roomName = room.RoomName,
                    creatorName = room.CreatorName,
                    playerCount = 0,
                    maxPlayers = room.MaxPlayers,
                    entryFee = room.EntryFee,
                    gameMode = room.Mode.ToString(),
                    isPrivate = room.IsPrivate
                });

                return new { success = true, roomId = room.RoomId };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CreateRoom error: {ex.Message}");
                return new { success = false, message = ex.Message };
            }
        }

        private async Task EnsureJoinablePresetRooms()
        {
            foreach (var preset in PresetRooms)
            {
                if (_roomManager.JoinableRoomExistsByName(preset.Name))
                {
                    continue;
                }

                var room = _roomManager.CreateRoom(
                    roomName: preset.Name,
                    creatorName: "System",
                    creatorId: 0,
                    entryFee: preset.EntryFee,
                    maxPlayers: preset.MaxPlayers,
                    mode: preset.Mode,
                    isPrivate: false,
                    password: null);

                if (room == null)
                {
                    continue;
                }

                await Clients.All.SendAsync("RoomCreated", new
                {
                    roomId = room.RoomId,
                    roomName = room.RoomName,
                    creatorName = room.CreatorName,
                    playerCount = 0,
                    maxPlayers = room.MaxPlayers,
                    entryFee = room.EntryFee,
                    gameMode = room.Mode.ToString(),
                    isPrivate = room.IsPrivate
                });

                Console.WriteLine($"🔄 Ensured joinable preset room: {room.RoomName} ({room.MaxPlayers}P)");
            }
        }

        public async Task<List<object>> GetRoomList()
        {
            var userId = GetUserId();
            await EnsureJoinablePresetRooms();
            var rooms = _roomManager.GetAvailableRooms();
            return rooms.Where(r =>
            {
                var room = _roomManager.GetRoom(r.RoomId);
                var currentPlayer = room?.Players.FirstOrDefault(p => p.UserId == userId);
                var canReconnect = room?.IsGameStarted == true &&
                    currentPlayer != null &&
                    !IsReconnectWindowClosed(currentPlayer);

                return !r.IsGameStarted || canReconnect;
            }).Select(r =>
            {
                var room = _roomManager.GetRoom(r.RoomId);
                var hasExpiredSeat = room != null && userId != 0 && HasExpiredSeatInStartedRoom(room, userId);
                var staleSystemSeats = room?.Players.Count(p => p.UserId != userId && IsExpiredSystemSeat(p)) ?? 0;
                var displayedPlayerCount = Math.Max(0, Math.Min(r.PlayerCount, r.MaxPlayers) - staleSystemSeats);
                if (hasExpiredSeat)
                {
                    displayedPlayerCount = Math.Max(0, displayedPlayerCount - 1);
                }

                return (object)new
                {
                    roomId = r.RoomId,
                    roomName = r.RoomName,
                    creatorName = r.CreatorName,
                    playerCount = displayedPlayerCount,
                    maxPlayers = r.MaxPlayers,
                    entryFee = r.EntryFee,
                    gameMode = r.GameMode,
                    isPrivate = r.IsPrivate,
                    isGameStarted = hasExpiredSeat ? false : r.IsGameStarted,
                    canJoin = hasExpiredSeat || (!r.IsGameStarted && displayedPlayerCount < r.MaxPlayers),
                    willCreateNewRoom = hasExpiredSeat || displayedPlayerCount >= r.MaxPlayers
                };
            }).ToList();
        }


        private async void StartTurnTimer(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;
            // Köhnə timer-i ləğv et
            StopTurnTimer(roomId);

            System.Threading.Timer? timer = null;
            timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await HandleTurnTimeout(roomId, userId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Timer error: {ex.Message}");
                }
                finally
                {
                    if (_turnTimers.TryGetValue(roomId, out var currentTimer) &&
                        ReferenceEquals(currentTimer, timer) &&
                        _turnTimers.TryRemove(roomId, out var existingTimer))
                    {
                        existingTimer?.Dispose();
                        Console.WriteLine($"⏹️ Expired timer disposed: {roomId}");
                    }
                }
            }, null, TimeSpan.FromSeconds(TURN_TIMEOUT_SECONDS), Timeout.InfiniteTimeSpan);

            _turnTimers[roomId] = timer;

            // ✅ Frontend-ə timer başladığını bildir
            await _hubContext.Clients.Group(roomId).SendAsync(
                "TurnTimerStarted",
                CreateTurnTimerPayload(room, player, TURN_TIMEOUT_SECONDS));

            Console.WriteLine($"⏱️ Timer started: {userId} in {roomId} ({TURN_TIMEOUT_SECONDS}s)");
        }

        // ✅ TIMER DURDUR
        private void StopTurnTimer(string roomId)
        {
            StopSystemAutoPlay(roomId);

            if (_turnTimers.TryRemove(roomId, out var timer))
            {
                timer?.Dispose();
                Console.WriteLine($"⏹️ Timer stopped: {roomId}");
            }
        }

        private void StopSystemAutoPlay(string roomId)
        {
            if (_systemAutoPlayTasks.TryRemove(roomId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        private void ScheduleSystemAutoPlay(string roomId, int userId, int delaySeconds)
        {
            StopSystemAutoPlay(roomId);

            var cts = new CancellationTokenSource();
            _systemAutoPlayTasks[roomId] = cts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token);
                    await HandleTurnTimeout(roomId, userId);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ System auto-play error: {ex.Message}");
                }
                finally
                {
                    if (_systemAutoPlayTasks.TryGetValue(roomId, out var current) &&
                        ReferenceEquals(current, cts) &&
                        _systemAutoPlayTasks.TryRemove(roomId, out var existing))
                    {
                        existing.Dispose();
                    }
                }
            });
        }

        // ✅ VAXT BİTDİKDƏ AVTOMATIK DAŞ ATMA
        private async Task HandleTurnTimeout(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (room.IsGameFinished || !room.IsGameStarted)
            {
                Console.WriteLine($"⏹️ Timer ignored for finished/inactive Okey room: {roomId}");
                return;
            }

            OkeyPlayer? player = null;
            OkeyTile? randomTile = null;
            OkeyTile? systemWinningDiscardTile = null;
            List<OkeyTile>? updatedPlayerHand = null;
            bool isSystemControlled = false;
            bool systemDeclaredWin = false;
            bool finalDiscardRoundStarted = false;
            bool finalDiscardRoundCompleted = false;
            List<List<OkeyTile>> systemWinMelds = new();
            string systemWinType = "";

            lock (room.StateLock)
            {
                if (room.IsGameFinished || !room.IsGameStarted)
                {
                    Console.WriteLine($"⏹️ Auto-play skipped for finished/inactive Okey room: {roomId}");
                    return;
                }

                // Hələ də bu oyunçunun növbəsidirsə
                if (room.CurrentPlayerIndex < 0 ||
                    room.CurrentPlayerIndex >= room.Players.Count ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId)
                    return;

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.IsEliminated) return;
                if (player.IsDisconnected && !player.IsSystemControlled) return;

                isSystemControlled = player.IsSystemControlled;

                if (!player.HasDrawn && player.Hand.Count == 15)
                {
                    player.HasDrawn = true;
                    Console.WriteLine($"⚠️ TURN STATE REPAIRED: {player.Name} already has 15 tiles, switching to discard phase.");
                }
                else if (player.HasDrawn && player.Hand.Count == 14)
                {
                    player.HasDrawn = false;
                    Console.WriteLine($"⚠️ TURN STATE REPAIRED: {player.Name} has 14 tiles, switching to draw phase.");
                }
                else if (player.Hand.Count != 14 && player.Hand.Count != 15)
                {
                    Console.WriteLine($"❌ TURN STATE INVALID: {player.Name} has {player.Hand.Count} tiles. Auto-play stopped.");
                    return;
                }

                // ✅ ƏGƏR HƏLƏ DAŞ ÇƏKMƏYIBSƏ - DƏSTƏDƏN AVTOMATIK ÇƏK
                if (!player.HasDrawn && !room.IsFinalDiscardRound)
                {
                    if (room.Stock.Count > 0)
                    {
                        var drawnTile = room.Stock[0];
                        room.Stock.RemoveAt(0);
                        player.Hand.Add(drawnTile);
                        player.HasDrawn = true;
                        if (room.Stock.Count == 0)
                        {
                            StartFinalDiscardRoundIfNeeded(room);
                            finalDiscardRoundStarted = true;
                        }
                        Console.WriteLine($"⏰ AUTO-DRAW: {player.Name} ({drawnTile.Color} {drawnTile.Number})");
                    }
                    else
                    {
                        Console.WriteLine($"⏰ Dəstə boşdur, DiscardPile-dən çəkiləcək");
                        if (room.DiscardPile.Count > 0)
                        {
                            var drawnTile = room.DiscardPile.Last();
                            room.DiscardPile.RemoveAt(room.DiscardPile.Count - 1);
                            player.Hand.Add(drawnTile);
                            player.HasDrawn = true;
                        }
                    }
                }
                else if (room.IsFinalDiscardRound && !player.HasDrawn)
                {
                    if (room.DiscardPile.Count > 0)
                    {
                        var drawnTile = room.DiscardPile.Last();
                        room.DiscardPile.RemoveAt(room.DiscardPile.Count - 1);
                        player.Hand.Add(drawnTile);
                        player.HasDrawn = true;
                        Console.WriteLine($"⏰ FINAL AUTO-DRAW: {player.Name} ({drawnTile.Color} {drawnTile.Number})");
                    }
                }

                if (!player.HasDrawn)
                {
                    Console.WriteLine($"❌ AUTO-PLAY STOPPED: {player.Name} could not draw a tile.");
                    return;
                }

                if (player.Hand.Count != 15)
                {
                    Console.WriteLine($"❌ AUTO-PLAY STOPPED: {player.Name} has {player.Hand.Count} tiles before discard.");
                    return;
                }

                if (isSystemControlled && player.Hand.Count == 15 && room.JokerTile != null)
                {
                    for (int i = 0; i < player.Hand.Count; i++)
                    {
                        var candidateDiscard = player.Hand[i];
                        var candidateHand = player.Hand
                            .Where((_, index) => index != i)
                            .ToList();

                        var winResult = OkeyGameEngine.ValidateWin(candidateHand, room.JokerTile);
                        if (!winResult.IsValid)
                        {
                            continue;
                        }

                        systemWinningDiscardTile = candidateDiscard;
                        player.Hand.RemoveAt(i);
                        room.DiscardPile.Add(candidateDiscard);
                        player.HasDrawn = false;
                        systemDeclaredWin = true;
                        systemWinMelds = winResult.Melds;
                        systemWinType = winResult.WinType;
                        Console.WriteLine($"🤖 SYSTEM DECLARED WIN: {player.Name} → {candidateDiscard.Color} {candidateDiscard.Number}");
                        break;
                    }
                }

                // ✅ ƏLDƏN RANDOM DAŞ AT (SİSTEM DAŞINDAN FƏRQLI)
                if (!systemDeclaredWin && player.Hand.Count == 15 && player.HasDrawn)
                {
                    // ✅ Random indeks seç
                    int randomIndex = new Random().Next(player.Hand.Count);
                    randomTile = player.Hand[randomIndex];
                    player.Hand.RemoveAt(randomIndex);
                    room.DiscardPile.Add(randomTile);
                    player.HasDrawn = false;
                    finalDiscardRoundCompleted = MarkFinalDiscardAndCheckComplete(room, player);
                    updatedPlayerHand = player.Hand.ToList();

                    Console.WriteLine($"⏰ AUTO-DISCARD: {player.Name} → {randomTile.Color} {randomTile.Number}");
                }

                if (!systemDeclaredWin)
                {
                    // Növbəni keç
                    room.CurrentPlayerIndex = GetNextActivePlayerIndex(room);
                }
            }

            if (player != null)
            {
                if (systemDeclaredWin)
                {
                    await CompleteSystemDeclaredWin(roomId, room, player, systemWinningDiscardTile, systemWinMelds, systemWinType);
                    return;
                }

                // ✅ Bildiriş göndər
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerTimeout", new
                {
                    playerName = player.Name,
                    discardedTile = ToClientTile(randomTile),
                    isSystemControlled,
                    isFinalDiscardRound = room.IsFinalDiscardRound,
                    message = isSystemControlled
                        ? $"🤖 Sistem {player.Name} əvəzinə oynadı"
                        : $"{player.Name} vaxtı bitdi - avtomatik daş atıldı"
                });

                if (finalDiscardRoundStarted)
                {
                    await _hubContext.Clients.Group(roomId).SendAsync("FinalDiscardRoundStarted", new
                    {
                        playerName = player.Name,
                        message = $"Dəstənin son daşını {player.Name} çəkdi. Bu onun oyunu bitirmək üçün son şansıdır.",
                        remainingPlayers = 1
                    });
                }

                if (randomTile != null)
                {
                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("TileDiscarded", new
                    {
                        tileId = randomTile.Id,
                        hand = ToClientHand(updatedPlayerHand ?? player.Hand),
                        discardedTile = ToClientTile(randomTile),
                        wasLastTile = finalDiscardRoundCompleted,
                        isFinalDiscardRound = room.IsFinalDiscardRound,
                        isAutoDiscard = true
                    });

                    await _hubContext.Clients.Group(roomId).SendAsync("PlayerDiscardedTile", new
                    {
                        playerName = player.Name,
                        playerPosition = player.Position,
                        tile = ToClientTile(randomTile),
                        isSystemControlled
                    });
                }

                if (finalDiscardRoundCompleted)
                {
                    await _hubContext.Clients.Group(roomId).SendAsync("LastTileDiscarded", new
                    {
                        playerName = player.Name,
                        message = $"{player.Name} son daşı çəkdikdən sonra oyunu bitirə bilmədi. Oyun heç-heçə bitdi."
                    });

                    await HandleGameOver(roomId, room);
                    return;
                }

                // ✅ Növbəti oyunçuya bildiriş
                if (room.CurrentPlayerIndex != -1)
                {
                    await NotifyCurrentTurnPlayer(roomId, room, randomTile);
                }

                await _hubContext.Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());
            }
        }


        private async Task CompleteSystemDeclaredWin(
            string roomId,
            OkeyRoom room,
            OkeyPlayer winner,
            OkeyTile? discardedTile,
            List<List<OkeyTile>> melds,
            string winType)
        {
            var losers = room.Players
                .Where(p => p.UserId != winner.UserId && !p.IsEliminated)
                .ToList();
            var loserScores = new Dictionary<int, (int oldScore, int newScore, int penalty)>();

            foreach (var loser in losers)
            {
                int penalty = room.JokerTile != null
                    ? OkeyGameEngine.CalculatePenalty(loser.Hand, room.JokerTile)
                    : 0;
                int oldScore = loser.Score;
                loser.Score -= penalty;

                if (loser.Score <= 0)
                {
                    loser.Score = 0;
                    loser.IsEliminated = true;
                    Console.WriteLine($"❌ {loser.Name} oyundan çıxdı!");
                }

                loserScores[loser.UserId] = (oldScore, loser.Score, penalty);

                if (loser.IsEliminated && !loser.HasRankResultApplied)
                {
                    try
                    {
                        decimal loserLossAmount = room.EntryFee;

                        await _rankService.UpdateRankAfterGame(loser.UserId, GameType.Okey, false, loserLossAmount);
                        loser.HasRankResultApplied = true;
                        var loserRankDetails = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Okey);

                        if (!string.IsNullOrEmpty(loser.ConnectionId))
                        {
                            await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                            {
                                rank = loserRankDetails.CurrentRank,
                                level = loserRankDetails.RankLevel,
                                xp = loserRankDetails.ExperiencePoints,
                                requiredXP = loserRankDetails.RequiredXPForNextRank,
                                progress = loserRankDetails.ProgressPercentage,
                                totalEarnings = loserRankDetails.TotalEarnings,
                                totalLossAmount = loserRankDetails.TotalLossAmount,
                                winRate = loserRankDetails.WinRate
                            });
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ System win eliminated loser rank update error for {loser.Name}: {ex.Message}");
                    }
                }
            }

            List<OkeyPlayer> remainingPlayers;
            lock (room.StateLock)
            {
                room.IsGameFinished = true;
                room.IsGameStarted = false;
                remainingPlayers = room.Players.Where(p => !p.IsEliminated).ToList();
            }

            StopTurnTimer(roomId);

            decimal systemAmount = room.PotAmount;

            await _hubContext.Clients.Group(roomId).SendAsync("PlayerTimeout", new
            {
                playerName = winner.Name,
                discardedTile,
                isSystemControlled = true,
                isFinalDiscardRound = room.IsFinalDiscardRound,
                message = $"🤖 Sistem {winner.Name} əvəzinə kombinasiya edib bitirdi"
            });

            if (discardedTile != null)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerDiscardedTile", new
                {
                    playerName = winner.Name,
                    playerPosition = winner.Position,
                    tile = ToClientTile(discardedTile),
                    isSystemControlled = true
                });
            }

            await _hubContext.Clients.Group(roomId).SendAsync("RoundOver", new
            {
                winner = winner.Name,
                winnerHand = winner.Hand,
                melds,
                winType,
                winnerPosition = winner.Position,
                winAmount = 0,
                winnerReward = 0,
                displayReward = systemAmount,
                systemAmount,
                systemWon = true,
                scores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isEliminated = p.IsEliminated,
                    isSystemControlled = p.IsSystemControlled,
                    penalty = loserScores.ContainsKey(p.UserId) ? loserScores[p.UserId].penalty : 0
                }).ToArray()
            });

            await Task.Delay(5000);

            if (remainingPlayers.Count == 1)
            {
                var finalWinner = remainingPlayers[0];

                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    finalWinner = finalWinner.Name,
                    winAmount = 0,
                    winnerReward = 0,
                    displayReward = finalWinner.IsSystemControlled ? room.PotAmount : Math.Max(0, room.PotAmount - (room.PotAmount * COMMISSION_RATE)),
                    systemAmount = finalWinner.IsSystemControlled ? room.PotAmount : 0,
                    systemWon = finalWinner.IsSystemControlled,
                    finalScores = room.Players.Select(p => new
                    {
                        name = p.Name,
                        score = p.Score,
                        isEliminated = p.IsEliminated
                    }).OrderByDescending(p => p.score).ToArray()
                });

                await Task.Delay(5000);

                room.PotAmount = 0;

                foreach (var roomPlayer in room.Players.ToList())
                {
                    if (!string.IsNullOrEmpty(roomPlayer.ConnectionId))
                    {
                        await Groups.RemoveFromGroupAsync(roomPlayer.ConnectionId, roomId);
                    }

                    _userRooms.TryRemove(roomPlayer.ConnectionId, out _);
                }

                _roomManager.DeleteRoom(roomId);
                await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
                Console.WriteLine($"🗑️ Sistem bitirdi, otaq silindi: {room.RoomName}");
            }
            else
            {
                lock (room.StateLock)
                {
                    room.DiscardPile.Clear();
                }

                room.RoundNumber++;
                await StartGame(roomId);
            }
        }


        public async Task<object> JoinRoom(string roomId, string? password = null)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return new { success = false };
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return new { success = false };
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinError", "Otaq tapılmadı");
                return new { success = false };
            }

            string fullName = user.UserName;

            await RemoveExpiredSeatsForUserFromWaitingRooms(userId);

            if (!room.IsGameStarted)
            {
                await RemoveExpiredSystemSeatsFromWaitingRoom(roomId, room);
            }

            OkeyPlayer? existingPlayer = null;
            lock (room.StateLock)
            {
                existingPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
            }

            if (existingPlayer != null)
            {
                if (HasExpiredSeatInStartedRoom(room, userId))
                {
                    _disconnectedPlayers.TryRemove(userId, out _);
                    await MarkPlayerSystemControlledAfterTimeout(roomId, userId);

                    var replacementRoom = _roomManager.FindJoinableRoom(
                        room.RoomName,
                        room.EntryFee,
                        room.Mode,
                        room.MaxPlayers);

                    var replacementCreated = false;
                    if (replacementRoom == null)
                    {
                        replacementRoom = _roomManager.CreateRoom(
                            roomName: room.RoomName,
                            creatorName: "System",
                            creatorId: 0,
                            entryFee: room.EntryFee,
                            maxPlayers: room.MaxPlayers,
                            mode: room.Mode,
                            isPrivate: false,
                            password: null);
                        replacementCreated = replacementRoom != null;
                    }

                    if (replacementRoom == null)
                    {
                        await Clients.Caller.SendAsync("JoinError", "Yeni otaq yaradıla bilmədi");
                        return new { success = false };
                    }

                    if (replacementCreated)
                    {
                        await Clients.All.SendAsync("RoomCreated", new
                        {
                            roomId = replacementRoom.RoomId,
                            roomName = replacementRoom.RoomName,
                            creatorName = replacementRoom.CreatorName,
                            playerCount = 0,
                            maxPlayers = replacementRoom.MaxPlayers,
                            entryFee = replacementRoom.EntryFee,
                            gameMode = replacementRoom.Mode.ToString(),
                            isPrivate = replacementRoom.IsPrivate
                        });
                    }

                    roomId = replacementRoom.RoomId;
                    room = replacementRoom;
                    existingPlayer = null;
                }
                else
                {
                    return await SendExistingPlayerJoinedAsync(roomId, room, existingPlayer, user.Balance);
                }
            }

            if (existingPlayer != null)
            {
                return await SendExistingPlayerJoinedAsync(roomId, room, existingPlayer, user.Balance);
            }

            if (!room.IsGameStarted && room.Players.Count >= room.MaxPlayers)
            {
                // ✅ DOLU OTAQSA - YENİ OTAQ YARAD (eyni qiymət)
                Console.WriteLine($"⚠️ Otaq doludur: {room.RoomName}, yeni otaq yaradılır...");

                var newRoom = _roomManager.CreateRoomWithSameFee(room.EntryFee, room.Mode, room.MaxPlayers);
                if (newRoom == null)
                {
                    await Clients.Caller.SendAsync("JoinError", "Yeni otaq yaradıla bilmədi");
                    return new { success = false };
                }

                // ✅ Hamıya yeni otağı bildir
                await Clients.All.SendAsync("RoomCreated", new
                {
                    roomId = newRoom.RoomId,
                    roomName = newRoom.RoomName,
                    creatorName = newRoom.CreatorName,
                    playerCount = 0,
                    maxPlayers = newRoom.MaxPlayers,
                    entryFee = newRoom.EntryFee,
                    gameMode = newRoom.Mode.ToString(),
                    isPrivate = newRoom.IsPrivate
                });

                // ✅ Yeni otağa keç
                roomId = newRoom.RoomId;
                room = newRoom;

                Console.WriteLine($"✅ Yeni otaq yaradıldı: {newRoom.RoomName} ({newRoom.MaxPlayers}P)");
            }

            if (room.IsGameStarted)
            {
                var joinableReplacement = _roomManager.FindJoinableRoom(
                    room.RoomName,
                    room.EntryFee,
                    room.Mode,
                    room.MaxPlayers);

                if (joinableReplacement == null)
                {
                    joinableReplacement = _roomManager.CreateRoom(
                        roomName: room.RoomName,
                        creatorName: "System",
                        creatorId: 0,
                        entryFee: room.EntryFee,
                        maxPlayers: room.MaxPlayers,
                        mode: room.Mode,
                        isPrivate: false,
                        password: null);

                    if (joinableReplacement != null)
                    {
                        await Clients.All.SendAsync("RoomCreated", new
                        {
                            roomId = joinableReplacement.RoomId,
                            roomName = joinableReplacement.RoomName,
                            creatorName = joinableReplacement.CreatorName,
                            playerCount = 0,
                            maxPlayers = joinableReplacement.MaxPlayers,
                            entryFee = joinableReplacement.EntryFee,
                            gameMode = joinableReplacement.Mode.ToString(),
                            isPrivate = joinableReplacement.IsPrivate
                        });
                    }
                }

                if (joinableReplacement == null)
                {
                    await Clients.Caller.SendAsync("JoinError", "Yeni otaq yaradıla bilmədi");
                    return new { success = false };
                }

                Console.WriteLine($"🔄 Started room join redirected: {room.RoomName} → {joinableReplacement.RoomId}");
                roomId = joinableReplacement.RoomId;
                room = joinableReplacement;
            }

            if (user.Balance < room.EntryFee)
            {
                await Clients.Caller.SendAsync("JoinError", $"Kifayət qədər balans yoxdur (lazım: {room.EntryFee}₼)");
                return new { success = false };
            }

            user.Balance -= room.EntryFee;
            room.PotAmount += room.EntryFee;
            await _db.SaveChangesAsync();

            var player = new OkeyPlayer
            {
                ConnectionId = Context.ConnectionId,
                UserId = userId,
                Name = fullName,
                Balance = user.Balance,
                Hand = new List<OkeyTile>(),
                Position = room.Players.Count,
                Score = room.GetInitialScore(),
                ProfileImage = user.Image
            };

            if (!_roomManager.AddPlayerToRoom(roomId, player, password))
            {
                user.Balance += room.EntryFee;
                room.PotAmount -= room.EntryFee;
                await _db.SaveChangesAsync();
                await Clients.Caller.SendAsync("JoinError", "Otağa qoşulmaq alınmadı");
                return new { success = false };
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            _userRooms[Context.ConnectionId] = roomId;

            await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId,
                roomName = room.RoomName,
                balance = user.Balance,
                position = player.Position,
                isGameStarted = false,
                maxPlayers = room.MaxPlayers
            });

            await Clients.Group(roomId).SendAsync("PlayerJoined", new
            {
                playerName = fullName,
                playerCount = room.Players.Count,
                maxPlayers = room.MaxPlayers
            });

            await BroadcastRoomPlayers(roomId);

            Console.WriteLine($"✅ {fullName} → {room.RoomName} | Pot: {room.PotAmount}₼ ({room.Players.Count}/{room.MaxPlayers})");

            if (room.Players.Count == room.MaxPlayers)
            {
                await Task.Delay(2000);
                await StartGame(roomId);
            }

            return new { success = true, roomId };
        }

        private async Task CleanupInactiveRooms()
        {
            var allRooms = _roomManager.GetAllRooms();

            foreach (var room in allRooms)
            {
                lock (room.StateLock)
                {
                    // ✅ OYUN BİTMİŞSƏ VƏ 5 DƏQİQƏDƏN ÇOX KEÇMIŞSƏ - SİL
                    if (room.IsGameFinished && (DateTime.UtcNow - room.GameFinishedTime).TotalMinutes > 5)
                    {
                        _roomManager.DeleteRoom(room.RoomId);
                        Console.WriteLine($"🗑️ Bitirilmiş otaq silindi: {room.RoomName}");
                        continue;
                    }

                    // ✅ OYUN BAŞLAMAYIBSA VƏ 10 DƏQİQƏDƏN ÇOX KEÇMIŞSƏ - SİL
                    if (!room.IsGameStarted && (DateTime.UtcNow - room.CreatedTime).TotalMinutes > 10)
                    {
                        if (room.Players.Count == 0 && room.CreatorId != 0)
                        {
                            _roomManager.DeleteRoom(room.RoomId);
                            Console.WriteLine($"🗑️ Inactive otaq silindi: {room.RoomName}");
                        }
                    }
                }
            }
        }

        // ✅ YENİ METOD: ROOM CLEANUP SERVICE
        public async Task StartRoomCleanupService()
        {
            var cleanupTimer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    await CleanupInactiveRooms();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Cleanup error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(ROOM_CLEANUP_CHECK_INTERVAL), TimeSpan.FromSeconds(ROOM_CLEANUP_CHECK_INTERVAL));
        }
        public async Task LeaveRoom()
        {
            var connId = Context.ConnectionId;
            if (!_userRooms.TryGetValue(connId, out var roomId)) return;

            var userId = GetUserId();
            if (userId == 0) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return;

            // ✅ Oyun davam edirsə - avtomatik eliminate
            if (room.IsGameStarted && !room.IsGameFinished)
            {
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player != null)
                {
                    player.IsEliminated = true;
                    player.IsDisconnected = true;

                    await Clients.Group(roomId).SendAsync("PlayerLeft", new
                    {
                        playerName = player.Name,
                        message = $"{player.Name} oyunu tərk etdi"
                    });

                    Console.WriteLine($"🚪 {player.Name} oyunu tərk etdi - eliminated");

                    // ✅ OYUNÇUNUN RANK-INI GÜNCƏLLƏ (KAYBEDEN)
                    try
                    {
                        await _rankService.UpdateRankAfterGame(
                            userId: userId,
                            gameType: GameType.Okey,
                            isWin: false,
                            earnings: room.EntryFee);
                        player.HasRankResultApplied = true;
                        var playerRankDetails = await _rankService.GetPlayerRankDetails(userId, GameType.Okey);

                        await Clients.Caller.SendAsync("RankUpdated", new
                        {
                            rank = playerRankDetails.CurrentRank,
                            level = playerRankDetails.RankLevel,
                            xp = playerRankDetails.ExperiencePoints,
                            requiredXP = playerRankDetails.RequiredXPForNextRank,
                            progress = playerRankDetails.ProgressPercentage,
                            totalEarnings = playerRankDetails.TotalEarnings,
                            totalLossAmount = playerRankDetails.TotalLossAmount,  // ✅ LOSS GÖSTƏR
                            winRate = playerRankDetails.WinRate
                        });

                        Console.WriteLine($"❌ {player.Name} rank updated (LOSS): {room.EntryFee}₼");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Rank update error for {player.Name}: {ex.Message}");
                    }

                    // ✅ QALAN AKTİV OYUNÇULARI YOXLA
                    var remainingActivePlayers = room.Players
                        .Where(p => !p.IsEliminated && (!p.IsDisconnected || p.IsSystemControlled))
                        .ToList();

                    // ✅ 2-nəfərlik oyunda 1 oyunçu qalsa - avtomatik qalib
                    if (room.MaxPlayers == 2 && remainingActivePlayers.Count == 1)
                    {
                        await AwardLastPlayerWin(roomId, remainingActivePlayers[0]);
                    }
                    else if (remainingActivePlayers.Count == 1)
                    {
                        await AwardLastPlayerWin(roomId, remainingActivePlayers[0]);
                    }
                    else if (remainingActivePlayers.Count == 0)
                    {
                        await EndGameNoWinner(roomId);
                    }
                    else
                    {
                        // ✅ 2+ OYUNÇU QALIBSA - OYUN DAVAM EDİR
                        Console.WriteLine($"✅ {remainingActivePlayers.Count} oyunçu qaldı - oyun davam edir");

                        // Növbəni keç
                        if (IsCurrentPlayer(room, userId))
                        {
                            room.CurrentPlayerIndex = GetNextActivePlayerIndex(room);

                            if (room.CurrentPlayerIndex != -1)
                            {
                                await NotifyCurrentTurnPlayer(roomId, room);
                            }
                        }
                    }

                    await Groups.RemoveFromGroupAsync(connId, roomId);
                    _userRooms.TryRemove(connId, out _);
                    await Clients.Caller.SendAsync("LeftRoom");
                    await Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());
                    return;
                }
            }

            // ✅ Oyun başlamayıbsa - normal refund
            user.Balance += room.EntryFee;
            room.PotAmount -= room.EntryFee;
            await _db.SaveChangesAsync();

            await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
            Console.WriteLine($"💰 Refund: {room.EntryFee}₼ → {user.Name}");

            _roomManager.RemovePlayerFromRoom(roomId, userId);
            await Groups.RemoveFromGroupAsync(connId, roomId);
            _userRooms.TryRemove(connId, out _);

            await Clients.Caller.SendAsync("LeftRoom");
            await Clients.Group(roomId).SendAsync("PlayerLeft", new
            {
                playerName = user.Name,
                playerCount = room.Players.Count
            });

            await BroadcastRoomPlayers(roomId);

            if (room.Players.Count == 0 && !room.IsGameStarted)
            {
                _roomManager.DeleteRoom(roomId);
                await Clients.All.SendAsync("RoomDeleted", roomId);
                Console.WriteLine($"🗑️ Boş otaq silindi: {room.RoomName}");
            }
        }
        private async Task StartGame(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.Players.Count < 2) return;

            await RemoveExpiredSystemSeatsFromWaitingRoom(roomId, room);
            if (room.Players.Count < 2) return;

            lock (room.StateLock)
            {
                if (room.IsGameStarted) return;

                int initialScore = room.GetInitialScore();
                foreach (var player in room.Players)
                {
                    if (player.Score == 0)
                    {
                        player.Score = initialScore;
                    }
                }

                var (stock, hands, indicator, startIndex, dealerIndex) = OkeyGameEngine.DealTiles(room.Players.Count);
                OkeyGameEngine.ValidateDealIntegrity(stock, hands, indicator, "start game");

                room.Stock = stock;
                room.DiscardPile = new List<OkeyTile>();
                room.Indicator = indicator;
                room.JokerTile = OkeyGameEngine.CalculateJoker(indicator);
                foreach (var tile in stock)
                {
                    tile.IsJoker = OkeyGameEngine.IsJokerTile(tile, room.JokerTile);
                }

                foreach (var hand in hands)
                {
                    foreach (var tile in hand)
                    {
                        tile.IsJoker = OkeyGameEngine.IsJokerTile(tile, room.JokerTile);
                    }
                }

                LogTileIntegrity(roomId, stock, hands, indicator);

                room.CurrentPlayerIndex = startIndex;
                room.IsGameStarted = true;
                room.IsGameFinished = false;
                room.IsFinalDiscardRound = false;
                room.FinalDiscardedUserIds.Clear();

                for (int i = 0; i < room.Players.Count; i++)
                {
                    room.Players[i].Hand = hands[i];
                    room.Players[i].HasDrawn = (i == dealerIndex);
                }

                Console.WriteLine($"🎮 OKEY STARTED: Dealer: {room.Players[dealerIndex].Name} (15 daş)");
            }

            if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                room.CurrentPlayerIndex = GetNextActivePlayerIndex(room);
            }

            if (room.CurrentPlayerIndex == -1)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("WaitingForPlayers", new
                {
                    message = "Yeni oyun üçün aktiv oyunçu gözlənilir...",
                    currentPlayers = room.Players.Count
                });
                return;
            }

            var currentPlayer = room.Players[room.CurrentPlayerIndex];

            foreach (var player in room.Players)
            {
                bool isDealer = currentPlayer.UserId == player.UserId;
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("GameStarted", new
                {
                    hand = ToClientHand(player.Hand),
                    indicator = room.Indicator,
                    joker = room.JokerTile,
                    currentPlayer = currentPlayer.Name,
                    isYourTurn = isDealer,
                    playerPosition = player.Position,
                    gameMode = room.Mode.ToString(),
                    roundNumber = room.RoundNumber,
                    initialScore = player.Score,
                    mustDiscard = isDealer,
                    handCount = player.Hand.Count,
                    maxPlayers = room.MaxPlayers
                });
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());

            if (room.CurrentPlayerIndex >= 0 && room.CurrentPlayerIndex < room.Players.Count)
            {
                await NotifyCurrentTurnPlayer(roomId, room);
            }
        }

        private static List<object?> ToClientHand(IEnumerable<OkeyTile> hand)
        {
            return hand.Select(ToClientTile).ToList();
        }

        private static object? ToClientTile(OkeyTile? tile)
        {
            if (tile == null) return null;

            var isRealOkey = tile.IsJoker && !tile.IsFakeJoker;

            return new
            {
                id = tile.Id,
                number = tile.Number,
                color = tile.Color,
                isFakeJoker = tile.IsFakeJoker,
                isJoker = false,
                isRealOkey,
                tileKind = tile.IsFakeJoker ? "FakeJoker" : isRealOkey ? "RealOkey" : "Normal"
            };
        }

        private static void LogTileIntegrity(string roomId, List<OkeyTile> stock, List<OkeyTile>[] hands, OkeyTile indicator)
        {
            var allTiles = hands.SelectMany(h => h)
                .Concat(stock)
                .Append(indicator)
                .ToList();

            var duplicateIds = allTiles
                .GroupBy(t => t.Id)
                .Where(g => g.Count() > 1)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToList();

            var totalFakeJokers = allTiles.Count(t => t.IsFakeJoker);

            Console.WriteLine($"🧪 OKEY TILE CHECK room={roomId}: total={allTiles.Count}, fakeJokers={totalFakeJokers}, duplicateIds={duplicateIds.Count}");

            for (int i = 0; i < hands.Length; i++)
            {
                var hand = hands[i];
                var fakeCount = hand.Count(t => t.IsFakeJoker);
                var duplicateHandIds = hand.GroupBy(t => t.Id)
                    .Where(g => g.Count() > 1)
                    .Select(g => $"{g.Key}x{g.Count()}")
                    .ToList();

                if (fakeCount > 2 || duplicateHandIds.Count > 0)
                {
                    Console.WriteLine(
                        $"🚨 OKEY HAND CHECK room={roomId}, playerIndex={i}: hand={hand.Count}, fakeJokers={fakeCount}, duplicateIds=[{string.Join(", ", duplicateHandIds)}]");
                }
            }

            if (totalFakeJokers != 2 || duplicateIds.Count > 0 || allTiles.Count != 106)
            {
                Console.WriteLine(
                    $"🚨 OKEY DECK INTEGRITY FAILED room={roomId}: total={allTiles.Count}, fakeJokers={totalFakeJokers}, duplicateIds=[{string.Join(", ", duplicateIds.Select(d => $"{d.Id}x{d.Count}"))}]");
            }
        }
        private async Task AwardLastPlayerWin(string roomId, OkeyPlayer winner)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ AwardLastPlayerWin skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                room.IsGameStarted = false;
            }

            StopTurnTimer(roomId);

            decimal totalPot = room.PotAmount;
            decimal commission = totalPot * COMMISSION_RATE;
            decimal systemAmount = winner.IsSystemControlled ? totalPot : 0;
            decimal winAmount = winner.IsSystemControlled ? 0 : totalPot - commission;
            decimal displayReward = winner.IsSystemControlled ? systemAmount : winAmount;
            bool systemWon = winner.IsSystemControlled;

            if (systemWon)
            {
                winner.HasRankResultApplied = true;
                Console.WriteLine($"🤖 SYSTEM WIN: {winner.Name} | Pot saxlanıldı: {totalPot}₼");
            }
            else
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (user != null)
                {

                    user.Balance += winAmount;
                    await _db.SaveChangesAsync();

                    await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

                    try
                    {
                        // ✅ QALIBƏ WIN EKLE
                        await _rankService.UpdateRankAfterGame(
                            userId: winner.UserId,
                            gameType: GameType.Okey,
                            isWin: true,
                            earnings: winAmount);
                        winner.HasRankResultApplied = true;
                        var rankDetails = await _rankService.GetPlayerRankDetails(winner.UserId, GameType.Okey);

                        await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = rankDetails.CurrentRank,
                            level = rankDetails.RankLevel,
                            xp = rankDetails.ExperiencePoints,
                            requiredXP = rankDetails.RequiredXPForNextRank,
                            progress = rankDetails.ProgressPercentage,
                            totalEarnings = rankDetails.TotalEarnings,      // ✅ ƏLAVƏ ET
                            totalLossAmount = rankDetails.TotalLossAmount,  // ✅ ƏLAVƏ ET
                            winRate = rankDetails.WinRate                   // ✅ ƏLAVƏ ET
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Winner rank update error: {ex.Message}");
                    }

                    Console.WriteLine($"🏆 AUTO WIN: {winner.Name} | Pot: {totalPot}₼ | Won: {winAmount}₼");
                }
            }

            // ✅ KAYBEDƏNLƏRƏ LOSS EKLE
            var losers = room.Players
                .Where(p => p.UserId != winner.UserId && !p.HasRankResultApplied)
                .ToList();
            foreach (var loser in losers)
            {
                try
                {
                    // ❌ Kaybedənin entry fee-sini LOSS olarak sayın
                    await _rankService.UpdateRankAfterGame(
                        userId: loser.UserId,
                        gameType: GameType.Okey,
                        isWin: false,
                        earnings: room.EntryFee);
                    loser.HasRankResultApplied = true;
                    var loserRankDetails = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Okey);

                    // ✅ Kaybedene bildirim gönder
                    if (!string.IsNullOrEmpty(loser.ConnectionId))
                    {
                        await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = loserRankDetails.CurrentRank,
                            level = loserRankDetails.RankLevel,
                            xp = loserRankDetails.ExperiencePoints,
                            requiredXP = loserRankDetails.RequiredXPForNextRank,
                            progress = loserRankDetails.ProgressPercentage,
                            totalEarnings = loserRankDetails.TotalEarnings,
                            totalLossAmount = loserRankDetails.TotalLossAmount,  // ✅ BU ARTIQ DOLU OLACAQ
                            winRate = loserRankDetails.WinRate
                        });
                    }

                    Console.WriteLine($"❌ LOSER: {loser.Name} | Loss: {room.EntryFee}₼");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Loser rank update error for {loser.Name}: {ex.Message}");
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winner.Name,
                winAmount,
                winnerReward = winAmount,
                displayReward,
                systemAmount,
                systemWon,
                reason = systemWon
                    ? "Oyunçu qayıtmadığı üçün sistem oynadı. Udüş sistemə keçdi."
                    : "Bütün rəqiblər oyunu tərk etdi",
                finalScores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isEliminated = p.IsEliminated,
                    isWinner = p.UserId == winner.UserId,
                    isSystemControlled = p.IsSystemControlled
                }).OrderByDescending(p => p.score).ToArray()
            });

            await Task.Delay(5000);

            room.PotAmount = 0;

            // ✅ Otağı təmizlə
            foreach (var player in room.Players.ToList())
            {
                await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId);
                _userRooms.TryRemove(player.ConnectionId, out _);
            }

            _roomManager.DeleteRoom(roomId);
            await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
            Console.WriteLine($"🗑️ Oyun bitdi, otaq silindi: {room.RoomName}");
        }
        public async Task DrawTile(string source)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            OkeyPlayer? player = null;
            OkeyTile? drawnTile = null;
            bool isLastTile = false; // ✅ SON DAŞ ÇƏKIDI MI?
            bool finalDiscardRoundStarted = false;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyun artıq bitib");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                if (room.CurrentPlayerIndex < 0 ||
                    room.CurrentPlayerIndex >= room.Players.Count ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                if (player.HasDrawn)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Artıq daş çəkmisiniz, indi DAŞ ATIN");
                    return;
                }

                if (player.Hand.Count != 14)
                {
                    _ = Clients.Caller.SendAsync("ActionError", $"Əldə {player.Hand.Count} daş var. Daş çəkmək üçün əldə 14 daş olmalıdır.");
                    return;
                }

                if (source == "stock")
                {
                    if (room.IsFinalDiscardRound)
                    {
                        _ = Clients.Caller.SendAsync("ActionError", "Dəstə bitib. Son dövrdə əvvəlki atılan daşı götürün.");
                        return;
                    }

                    if (room.Stock.Count == 0)
                    {
                        _ = Clients.Caller.SendAsync("ActionError", "Dəstədə daş yoxdur");
                        return;
                    }

                    drawnTile = room.Stock[0];
                    room.Stock.RemoveAt(0);

                    // ✅ SON DAŞ ÇƏKIDI MI?
                    if (room.Stock.Count == 0)
                    {
                        isLastTile = true;
                        StartFinalDiscardRoundIfNeeded(room);
                        finalDiscardRoundStarted = true;
                        Console.WriteLine($"⚠️ SON DAŞ ÇƏKƏLDƏ: {player.Name}");
                    }
                }
                else if (source == "discard")
                {
                    if (room.DiscardPile.Count == 0)
                    {
                        _ = Clients.Caller.SendAsync("ActionError", "Atılmış daş yoxdur");
                        return;
                    }
                    drawnTile = room.DiscardPile.Last();
                    room.DiscardPile.RemoveAt(room.DiscardPile.Count - 1);
                }
                else
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Daş çəkmə mənbəyi düzgün deyil");
                    return;
                }

                if (drawnTile != null)
                {
                    player.Hand.Add(drawnTile);
                    player.HasDrawn = true;
                }
            }

            // ✅ OYUNÇUYA DAŞ ÇƏKƏLDIYINI BILDIR
            await Clients.Caller.SendAsync("TileDrawn", new
            {
                tile = ToClientTile(drawnTile),
                hand = ToClientHand(player.Hand),
                source,
                mustDiscard = true,
                isLastTile = isLastTile, // ✅ Frontend-ə son daş olduğunu bildir
                isFinalDiscardRound = room.IsFinalDiscardRound,
                message = isLastTile ? "⚠️ BU SON DAŞDIR! Bitirmə ən sonuncu şanstır!" : null
            });

            if (finalDiscardRoundStarted)
            {
                await Clients.Group(roomId).SendAsync("FinalDiscardRoundStarted", new
                {
                    playerName = player.Name,
                    message = $"Dəstənin son daşını {player.Name} çəkdi. Bu onun oyunu bitirmək üçün son şansıdır.",
                    remainingPlayers = 1
                });
            }

            await Clients.OthersInGroup(roomId).SendAsync("PlayerDrew", new
            {
                playerName = player.Name,
                playerPosition = player.Position,
                source,
                isLastTile = isLastTile,
                message = isLastTile ? $"⚠️ {player.Name} son daşı çəkdi!" : null
            });

            await Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());
        }
        private async Task HandleGameOver(string roomId, OkeyRoom room)
        {
            Console.WriteLine($"⚠️ GAME OVER: Son daşı çəkən oyunçu bitirə bilmədi - heç-heçə.");

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"ℹ️ Draw/refund already processed: {room.RoomName}");
                    return;
                }

                room.IsGameFinished = true;
                room.IsGameStarted = false;
            }

            StopTurnTimer(roomId);

            var players = room.Players.ToList();
            var refundablePlayers = players.Where(p => !p.IsSystemControlled).ToList();
            var playerIds = refundablePlayers.Select(p => p.UserId).Distinct().ToList();
            var refundedBalances = new Dictionary<int, decimal>();

            using (var scope = _scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
                var users = await db.Users
                    .Where(u => playerIds.Contains(u.Id))
                    .ToListAsync();

                foreach (var user in users)
                {
                    user.Balance += room.EntryFee;
                    refundedBalances[user.Id] = user.Balance;
                }

                await db.SaveChangesAsync();
            }

            lock (room.StateLock)
            {
                room.PotAmount = Math.Max(0, room.PotAmount - (room.EntryFee * refundablePlayers.Count));
            }

            foreach (var player in players)
            {
                player.HasRankResultApplied = true;

                bool wasRefunded = !player.IsSystemControlled && refundedBalances.ContainsKey(player.UserId);

                if (wasRefunded &&
                    !string.IsNullOrEmpty(player.ConnectionId) &&
                    refundedBalances.TryGetValue(player.UserId, out var balance))
                {
                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", balance);
                }

                Console.WriteLine(wasRefunded
                    ? $"💰 DRAW REFUND: {player.Name} | Refund: {room.EntryFee}₼"
                    : $"🤖 DRAW NO REFUND: {player.Name} system-controlled idi | Refund: 0₼");
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                reason = "Dəstənin son daşını çəkən oyunçu bir daş atdı və oyunu bitirə bilmədi. Oyun heç-heçə bitdi; yalnız sistemin idarə etmədiyi oyunçulara refund olundu.",
                resultType = "STOCK_EXHAUSTED_DRAW",
                noWinner = true,
                noLossAssigned = true,
                noRankChange = true,
                lossAssigned = false,
                lossAmount = 0,
                refunded = true,
                refundAmount = room.EntryFee,
                winAmount = 0,
                systemAmount = 0,
                finalScores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isEliminated = p.IsEliminated,
                    isSystemControlled = p.IsSystemControlled,
                    handCount = p.Hand.Count,
                    lossAmount = 0,
                    refundAmount = p.IsSystemControlled ? 0 : room.EntryFee,
                    wasRefunded = !p.IsSystemControlled
                }).OrderBy(p => p.handCount).ToArray()
            });

            Console.WriteLine($"🎬 {room.RoomName} - Qalib yoxdur (dəstə bitdi, pullar qaytarıldı)");

            await Task.Delay(5000);

            // ✅ Otağı təmizlə
            foreach (var player in room.Players.ToList())
            {
                await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId);
                _userRooms.TryRemove(player.ConnectionId, out _);
            }

            _roomManager.DeleteRoom(roomId);
            await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
            Console.WriteLine($"🗑️ Otaq silindi (dəstə bitdi - qalib yoxdur): {room.RoomName}");
        }

        public async Task DiscardTile(int tileId)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            OkeyPlayer? player = null;
            OkeyTile? discardedTile = null;
            int playerPosition = 0;
            bool isFinalDiscardRound = false;
            bool finalDiscardRoundCompleted = false;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyun artıq bitib");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                if (room.CurrentPlayerIndex < 0 ||
                    room.CurrentPlayerIndex >= room.Players.Count ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                isFinalDiscardRound = room.IsFinalDiscardRound;

                if (!player.HasDrawn && !isFinalDiscardRound)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Əvvəlcə daş çəkin");
                    return;
                }

                if (!player.HasDrawn && isFinalDiscardRound)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Son dövrdə əvvəlcə atılmış daşı götürün");
                    return;
                }

                if (player.Hand.Count != 15)
                {
                    _ = Clients.Caller.SendAsync("ActionError", $"Daş atmaq üçün əldə 15 daş olmalıdır. Hazırda {player.Hand.Count} daş var.");
                    return;
                }

                var tile = player.Hand.FirstOrDefault(t => t.Id == tileId);
                if (tile == null)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Daş tapılmadı");
                    return;
                }

                player.Hand.Remove(tile);
                room.DiscardPile.Add(tile);
                discardedTile = tile;
                playerPosition = player.Position;
                player.HasDrawn = false;
                finalDiscardRoundCompleted = MarkFinalDiscardAndCheckComplete(room, player);

                room.CurrentPlayerIndex = GetNextActivePlayerIndex(room);
            }

            await Clients.Caller.SendAsync("TileDiscarded", new
            {
                tileId,
                hand = ToClientHand(player.Hand),
                discardedTile = ToClientTile(discardedTile),
                wasLastTile = finalDiscardRoundCompleted,
                isFinalDiscardRound
            });

            if (finalDiscardRoundCompleted)
            {
                Console.WriteLine($"🎬 LAST STOCK DRAWER DISCARDED - OYUN BİTİR (QALIB YOX)");

                await Clients.Group(roomId).SendAsync("PlayerDiscardedTile", new
                {
                    playerName = player.Name,
                    playerPosition,
                    tile = ToClientTile(discardedTile)
                });

                await Clients.Group(roomId).SendAsync("LastTileDiscarded", new
                {
                    playerName = player.Name,
                    message = $"{player.Name} son daşı çəkdikdən sonra oyunu bitirə bilmədi. Oyun heç-heçə bitdi və pullar qaytarılacaq."
                });

                await HandleGameOver(roomId, room);
                return;
            }

            // ✅ Normal atış devam edir
            await NotifyCurrentTurnPlayer(roomId, room, discardedTile);

            await Clients.Group(roomId).SendAsync("PlayerDiscardedTile", new
            {
                playerName = player.Name,
                playerPosition = playerPosition,
                tile = ToClientTile(discardedTile)
            });

            await Clients.Group(roomId).SendAsync("GameStateUpdated", room.GetPublicState());
        }

        // ✅ YENİ METOD: Göstərici üzərinə daş ataraq oyunu bitirmək
        public async Task DiscardOnIndicator(int tileId, List<int>? arrangedTileIds = null)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            OkeyPlayer? winner;
            bool isValid;
            List<List<OkeyTile>> melds;
            string winType;
            bool isFinalDiscardRound = false;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyun artıq bitib");
                    return;
                }

                winner = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (winner == null) return;

                if (room.CurrentPlayerIndex < 0 ||
                    room.CurrentPlayerIndex >= room.Players.Count ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                if (room.JokerTile == null)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Okey daşı tapılmadı");
                    return;
                }

                isFinalDiscardRound = room.IsFinalDiscardRound;

                if (!winner.HasDrawn)
                {
                    _ = Clients.Caller.SendAsync("ActionError", isFinalDiscardRound
                        ? "Son dövrdə əvvəlcə atılmış daşı götürün"
                        : "Əvvəlcə daş çəkin");
                    return;
                }

                if (winner.Hand.Count != 15)
                {
                    var message = winner.Hand.Count == 14
                        ? "Bitirmək üçün əvvəlcə daş çəkməlisiniz. Hazır kombinasiyadan bir daşı atsanız, serverdə 13 daş qalır."
                        : $"Bitirmək üçün əldə 15 daş olmalıdır. Hazırda {winner.Hand.Count} daş var.";

                    _ = Clients.Caller.SendAsync("ActionError", message);
                    return;
                }

                var tileToDiscard = winner.Hand.FirstOrDefault(t => t.Id == tileId);
                if (tileToDiscard == null)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Daş tapılmadı");
                    return;
                }

                winner.Hand.Remove(tileToDiscard);
                var handToValidate = winner.Hand;

                var result = OkeyGameEngine.ValidateWin(handToValidate, room.JokerTile);
                isValid = result.IsValid;
                melds = result.Melds;
                winType = result.WinType;

                if (!isValid)
                {
                    var discardedTileDescription = OkeyGameEngine.DescribeTile(tileToDiscard, room.JokerTile);
                    var validatedHandSnapshot = handToValidate.ToList();
                    var remainingTileDescriptions = validatedHandSnapshot
                        .Select(tile => OkeyGameEngine.DescribeTile(tile, room.JokerTile))
                        .ToList();

                    Console.WriteLine(
                        $"❌ Okey finish rejected | Player={winner.Name} | Discarded={discardedTileDescription} | " +
                        $"RemainingCount={handToValidate.Count} | Remaining=[{string.Join(", ", remainingTileDescriptions)}]");

                    if (!winner.Hand.Contains(tileToDiscard))
                    {
                        winner.Hand.Add(tileToDiscard);
                    }

                    _ = Clients.Caller.SendAsync("WinDeclared", new
                    {
                        isValid = false,
                        message = isFinalDiscardRound
                            ? "❌ Kombinasiya tamamlanmadı. Bir daş atdıqda oyun heç-heçə bitəcək."
                            : "❌ Yanlış bitirmə! Server atdığınız daşdan sonra qalan 14 daşı düzgün kombinasiya kimi görmədi.",
                        details = $"Atılan daş: {discardedTileDescription}. Yoxlanılan daş sayı: {handToValidate.Count}. Qalan daşlar: {string.Join(", ", remainingTileDescriptions)}",
                        suggestion = "Ekrandakı daşlarla bu siyahını müqayisə edin; fərq varsa, client/server əl vəziyyəti sinxron deyil.",
                        hand = ToClientHand(winner.Hand),
                        validatedHand = validatedHandSnapshot
                    });

                    return;
                }

                room.DiscardPile.Add(tileToDiscard);
                winner.HasDrawn = false;

                if (arrangedTileIds != null && arrangedTileIds.Count > 0)
                {
                    var order = arrangedTileIds
                        .Where(id => id != tileId)
                        .Select((id, index) => new { id, index })
                        .ToDictionary(x => x.id, x => x.index);

                    winner.Hand = winner.Hand
                        .OrderBy(tile => order.TryGetValue(tile.Id, out var index) ? index : int.MaxValue)
                        .ThenBy(tile => tile.Id)
                        .ToList();
                }
            }

            var losers = room.Players.Where(p => p.UserId != userId && !p.IsEliminated).ToList();
            var loserScores = new Dictionary<int, (int oldScore, int newScore, int penalty)>();

            // ✅ KAYBEDƏNLƏRƏ LOSS EKLE (RANK GÜNCƏLLƏ)
            foreach (var loser in losers)
            {
                int penalty = OkeyGameEngine.CalculatePenalty(loser.Hand, room.JokerTile);
                int oldScore = loser.Score;
                loser.Score -= penalty;

                if (loser.Score <= 0)
                {
                    loser.Score = 0;
                    loser.IsEliminated = true;
                    Console.WriteLine($"❌ {loser.Name} oyundan çıxdı!");
                }

                loserScores[loser.UserId] = (oldScore, loser.Score, penalty);

                // Rank loss yalnız oyunçu həqiqətən oyundan çıxanda yazılmalıdır.
                if (loser.IsEliminated && !loser.HasRankResultApplied)
                {
                    try
                    {
                        decimal loserLossAmount = room.EntryFee;

                        await _rankService.UpdateRankAfterGame(loser.UserId, GameType.Okey, false, loserLossAmount);
                        loser.HasRankResultApplied = true;
                        var loserRankDetails = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Okey);

                        if (!string.IsNullOrEmpty(loser.ConnectionId))
                        {
                            await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                            {
                                rank = loserRankDetails.CurrentRank,
                                level = loserRankDetails.RankLevel,
                                xp = loserRankDetails.ExperiencePoints,
                                requiredXP = loserRankDetails.RequiredXPForNextRank,
                                progress = loserRankDetails.ProgressPercentage,
                                totalEarnings = loserRankDetails.TotalEarnings,
                                totalLossAmount = loserRankDetails.TotalLossAmount,
                                winRate = loserRankDetails.WinRate
                            });
                        }

                        Console.WriteLine($"❌ ELIMINATED: {loser.Name} | Loss: {loserLossAmount}₼ | Penalty Points: {penalty}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Eliminated loser rank update error for {loser.Name}: {ex.Message}");
                    }
                }
            }

            var remainingPlayers = room.Players.Where(p => !p.IsEliminated).ToList();

            lock (room.StateLock)
            {
                room.IsGameFinished = true;
                room.IsGameStarted = false;
            }

            StopTurnTimer(roomId);

            decimal roundWinAmount = 0;
            bool systemRoundWin = winner.IsSystemControlled;
            bool isFinalGameWin = remainingPlayers.Count == 1;
            decimal roundSystemAmount = systemRoundWin && isFinalGameWin ? room.PotAmount : 0;
            decimal roundDisplayReward = 0;

            if (!isFinalGameWin)
            {
                Console.WriteLine($"🏁 ROUND WIN: {winner.Name} | Pot final qalibə qədər saxlanılır");
            }
            else if (systemRoundWin)
            {
                roundDisplayReward = roundSystemAmount;
                Console.WriteLine($"🤖 SYSTEM ROUND WIN: {winner.Name} | Udüş balansına verilmədi");
            }
            else
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                decimal totalPot = room.PotAmount;
                decimal commission = totalPot * COMMISSION_RATE;
                decimal winAmount = totalPot - commission;
                roundWinAmount = winAmount;
                roundDisplayReward = winAmount;

                if (user != null)
                {
                    user.Balance += winAmount;
                    await _db.SaveChangesAsync();

                    await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

                    try
                    {
                        await _rankService.UpdateRankAfterGame(userId, GameType.Okey, true, winAmount);
                        winner.HasRankResultApplied = true;
                        var rankDetails = await _rankService.GetPlayerRankDetails(userId, GameType.Okey);

                        await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = rankDetails.CurrentRank,
                            level = rankDetails.RankLevel,
                            xp = rankDetails.ExperiencePoints,
                            requiredXP = rankDetails.RequiredXPForNextRank,
                            progress = rankDetails.ProgressPercentage,
                            totalEarnings = rankDetails.TotalEarnings,
                            totalLossAmount = rankDetails.TotalLossAmount,  // ✅ QALIBIN LOSS-U (ƏGƏR VARSA)
                            winRate = rankDetails.WinRate
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Winner rank update error: {ex.Message}");
                    }
                }
            }

            // ✅ Qalibin kombinasiyalarını hamıya göstər
            await _hubContext.Clients.Group(roomId).SendAsync("RoundOver", new
            {
                winner = winner.Name,
                winnerHand = winner.Hand,
                melds,
                winType,
                winnerPosition = winner.Position,
                winAmount = roundWinAmount,
                winnerReward = roundWinAmount,
                displayReward = roundDisplayReward,
                systemAmount = roundSystemAmount,
                systemWon = systemRoundWin,
                scores = room.Players.Select(p => new
                {
                    name = p.Name,
                    score = p.Score,
                    isEliminated = p.IsEliminated,
                    isSystemControlled = p.IsSystemControlled,
                    penalty = loserScores.ContainsKey(p.UserId) ? loserScores[p.UserId].penalty : 0
                }).ToArray()
            });

            await Task.Delay(5000);

            if (remainingPlayers.Count == 1)
            {
                var finalWinner = remainingPlayers[0];
                decimal finalWinAmount = finalWinner.IsSystemControlled ? 0 : roundWinAmount;
                decimal finalDisplayReward = finalWinner.IsSystemControlled ? room.PotAmount : finalWinAmount;
                decimal finalSystemAmount = finalWinner.IsSystemControlled ? room.PotAmount : 0;

                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    finalWinner = finalWinner.Name,
                    winAmount = finalWinAmount,
                    winnerReward = finalWinAmount,
                    displayReward = finalDisplayReward,
                    systemAmount = finalSystemAmount,
                    systemWon = finalWinner.IsSystemControlled,
                    finalScores = room.Players.Select(p => new
                    {
                        name = p.Name,
                        score = p.Score,
                        isEliminated = p.IsEliminated
                    }).OrderByDescending(p => p.score).ToArray()
                });

                room.PotAmount = 0;

                await Task.Delay(5000);

                // ✅ Otağı təmizlə
                foreach (var player in room.Players.ToList())
                {
                    await Groups.RemoveFromGroupAsync(player.ConnectionId, roomId);
                    _userRooms.TryRemove(player.ConnectionId, out _);
                }

                _roomManager.DeleteRoom(roomId);
                await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
                Console.WriteLine($"🗑️ Oyun bitdi, otaq silindi: {room.RoomName}");
            }
            else
            {
                // ✅ Növbəti raund başlamadan əvvəl atılmış daşları təmizlə
                lock (room.StateLock)
                {
                    room.DiscardPile.Clear();
                }

                room.RoundNumber++;
                await StartGame(roomId);
            }
        }
        public async Task GetGameState()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            await Clients.Caller.SendAsync("GameStateUpdated", room.GetPublicState());
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

        private async Task BroadcastRoomPlayers(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var playersData = room.Players.Select(p => new
            {
                name = p.Name,
                position = p.Position,
                handCount = p.Hand.Count,
                score = p.Score,
                isEliminated = p.IsEliminated,
                isDisconnected = p.IsDisconnected,
                isSystemControlled = p.IsSystemControlled,
                disconnectGraceRemainingSeconds = p.DisconnectGraceDeadlineUtc.HasValue
                    ? Math.Max(0, (int)Math.Ceiling((p.DisconnectGraceDeadlineUtc.Value - DateTime.UtcNow).TotalSeconds))
                    : 0,
                profileImage = p.ProfileImage ?? "/assets/characters/default.png"
            }).ToArray();

            await _hubContext.Clients.Group(roomId).SendAsync("PlayersList", playersData);
        }

        public async Task SendMessage(string message)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var userId = GetUserId();
            if (userId == 0) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            await _hubContext.Clients.Group(roomId).SendAsync("ChatMessage", new
            {
                username = player.Name,
                message,
                timestamp = DateTime.UtcNow
            });
        }

        public async Task RequestHint()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            var hint = OkeyGameEngine.GetHint(player.Hand, room.JokerTile!);

            await Clients.Caller.SendAsync("HintProvided", hint);
        }
    }
}

// ========== DATA MODELS ==========
public class OkeyTile
{
    public int Id { get; set; }
    public string Color { get; set; } = "";
    public int Number { get; set; }
    public bool IsFakeJoker { get; set; }
    public bool IsJoker { get; set; }
}

public enum OkeyGameMode
{
    Okey101,
    Okey51
}

public class OkeyPlayer
{
    public string ConnectionId { get; set; } = "";
    public int UserId { get; set; }
    public string Name { get; set; } = "";
    public decimal Balance { get; set; }
    public List<OkeyTile> Hand { get; set; } = new();
    public int Position { get; set; }
    public bool HasDrawn { get; set; }
    public bool IsDisconnected { get; set; }
    public bool IsSystemControlled { get; set; }
    public DateTime? DisconnectedAt { get; set; }
    public DateTime? DisconnectGraceDeadlineUtc { get; set; }
    public DateTime? SystemControlledAtUtc { get; set; }
    public int Score { get; set; }
    public bool IsEliminated { get; set; }
    public bool HasRankResultApplied { get; set; }
    public string? ProfileImage { get; set; }
}

public class OkeyRoom
{
    public string RoomId { get; set; } = Guid.NewGuid().ToString();
    public string RoomName { get; set; } = "";
    public string CreatorName { get; set; } = "";
    public int CreatorId { get; set; }
    public decimal EntryFee { get; set; }
    public int MaxPlayers { get; set; }
    public string GameMode { get; set; } = "casual";
    public bool IsPrivate { get; set; }
    public string? Password { get; set; }
    public List<OkeyPlayer> Players { get; set; } = new();
    public object StateLock { get; } = new();

    public OkeyGameMode Mode { get; set; } = OkeyGameMode.Okey101;
    public int RoundNumber { get; set; } = 1;
    public decimal PotAmount { get; set; } = 0;

    public bool IsGameStarted { get; set; }
    public bool IsGameFinished { get; set; }
    public DateTime CreatedTime { get; set; } = DateTime.UtcNow;
    public DateTime GameFinishedTime { get; set; } = DateTime.UtcNow;

    public List<OkeyTile> Stock { get; set; } = new();
    public List<OkeyTile> DiscardPile { get; set; } = new();
    public OkeyTile? Indicator { get; set; }
    public OkeyTile? JokerTile { get; set; }
    public int CurrentPlayerIndex { get; set; }
    public bool IsFinalDiscardRound { get; set; }
    public HashSet<int> FinalDiscardedUserIds { get; set; } = new();

    public int GetInitialScore() => Mode == OkeyGameMode.Okey101 ? 101 : 51;

    public object GetPublicState()
    {
        return new
        {
            stockCount = Stock.Count,
            discardPile = DiscardPile.LastOrDefault(),
            indicator = Indicator,
            joker = JokerTile,
            currentPlayer = CurrentPlayerIndex >= 0 && CurrentPlayerIndex < Players.Count
                ? Players[CurrentPlayerIndex].Name
                : "",
            currentPlayerIndex = CurrentPlayerIndex,
            roundNumber = RoundNumber,
            gameMode = Mode.ToString(),
            potAmount = PotAmount,
            isFinalDiscardRound = IsFinalDiscardRound,
            finalDiscardRemainingPlayers = IsFinalDiscardRound
                ? Players.Count(p => !p.IsEliminated &&
                    (!p.IsDisconnected || p.IsSystemControlled) &&
                    !FinalDiscardedUserIds.Contains(p.UserId))
                : 0,
            players = Players.Select(p => new
            {
                name = p.Name,
                handCount = p.Hand.Count,
                position = p.Position,
                score = p.Score,
                isEliminated = p.IsEliminated,
                isDisconnected = p.IsDisconnected,
                isSystemControlled = p.IsSystemControlled,
                disconnectGraceRemainingSeconds = p.DisconnectGraceDeadlineUtc.HasValue
                    ? Math.Max(0, (int)Math.Ceiling((p.DisconnectGraceDeadlineUtc.Value - DateTime.UtcNow).TotalSeconds))
                    : 0,
                profileImage = p.ProfileImage ?? "/assets/characters/default.png"
            }).ToArray()
        };
    }
}
public class RoomListItem
{
    public string RoomId { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string CreatorName { get; set; } = "";
    public int PlayerCount { get; set; }
    public int MaxPlayers { get; set; }
    public decimal EntryFee { get; set; }
    public string GameMode { get; set; } = "";
    public bool IsPrivate { get; set; }
    public bool IsGameStarted { get; set; }
}

// ========== ROOM MANAGER ==========
public class OkeyRoomManager
{
    private readonly ConcurrentDictionary<string, OkeyRoom> _rooms = new();
    private static int _roomCounter = 1;
    public OkeyRoom? CreateRoom(
        string roomName,
        string creatorName,
        int creatorId,
        decimal entryFee,
        int maxPlayers,
        OkeyGameMode mode,
        bool isPrivate,
        string? password)
    {
        var room = new OkeyRoom
        {
            RoomName = roomName,
            CreatorName = creatorName,
            CreatorId = creatorId,
            EntryFee = entryFee,
            MaxPlayers = maxPlayers,
            Mode = mode,
            GameMode = mode.ToString(),
            IsPrivate = isPrivate,
            Password = password
        };

        return _rooms.TryAdd(room.RoomId, room) ? room : null;
    }

    public bool RoomExistsByName(string roomName)
    {
        return _rooms.Values.Any(r => r.RoomName == roomName);
    }

    public bool JoinableRoomExistsByName(string roomName)
    {
        return _rooms.Values.Any(r =>
        {
            lock (r.StateLock)
            {
                return r.RoomName == roomName &&
                    !r.IsGameStarted &&
                    r.Players.Count < r.MaxPlayers;
            }
        });
    }

    public bool RoomExists(string roomName)
    {
        return JoinableRoomExistsByName(roomName);
    }
    public OkeyRoom? CreateRoomWithSameFee(decimal entryFee, OkeyGameMode mode, int maxplayer)
    {
        string roomName = GetRoomNameByFee(entryFee);
        roomName += $" #{_roomCounter++}";

        return CreateRoom(
            roomName: roomName,
            creatorName: "System",
            creatorId: 0,
            entryFee: entryFee,
            maxPlayers: maxplayer,
            mode: mode,
            isPrivate: false,
            password: null
        );
    }
    public List<OkeyRoom> GetAllRooms()
    {
        return _rooms.Values.ToList();
    }

    public class DisconnectRecord
    {
        public int UserId { get; set; }
        public string RoomId { get; set; }
        public int PlayerPosition { get; set; }
        public DateTime DisconnectTime { get; set; }
        public int ReconnectTimeoutSeconds { get; set; } = 25;

    }

    private string GetRoomNameByFee(decimal entryFee)
    {
        return entryFee switch
        {
            //0.20m => "Başlanğıc",
            0.50m => "Orta",
            1m => "Peşəkar",
            2m => "VIP",
            5m => "Master",
            10m => "Elite",
            20m => "Pro",
            50m => "Champion",
            100m => "Legend",
            _ => $"Otaq {entryFee}₼"
        };
    }

    public List<RoomListItem> GetAvailableRooms()
    {
        return _rooms.Values
            .Select(r => new RoomListItem
            {
                RoomId = r.RoomId,
                RoomName = r.RoomName,
                CreatorName = r.CreatorName,
                PlayerCount = r.Players.Count,
                MaxPlayers = r.MaxPlayers,
                EntryFee = r.EntryFee,
                GameMode = r.Mode.ToString(),
                IsPrivate = r.IsPrivate,
                IsGameStarted = r.IsGameStarted
            })
            .ToList();
    }

    public OkeyRoom? GetRoom(string roomId)
    {
        _rooms.TryGetValue(roomId, out var room);
        return room;
    }

    public OkeyRoom? GetRoomByUser(int userId)
    {
        foreach (var room in _rooms.Values)
        {
            lock (room.StateLock)
            {
                if (room.Players.Any(p =>
                    p.UserId == userId &&
                    !p.IsSystemControlled &&
                    (!p.DisconnectGraceDeadlineUtc.HasValue ||
                     DateTime.UtcNow <= p.DisconnectGraceDeadlineUtc.Value)))
                {
                    return room;
                }
            }
        }

        return null;
    }

    public OkeyRoom? FindJoinableRoom(string roomName, decimal entryFee, OkeyGameMode mode, int maxPlayers)
    {
        foreach (var room in _rooms.Values)
        {
            lock (room.StateLock)
            {
                if (room.RoomName == roomName &&
                    room.EntryFee == entryFee &&
                    room.Mode == mode &&
                    room.MaxPlayers == maxPlayers &&
                    !room.IsGameStarted &&
                    room.Players.Count < room.MaxPlayers)
                {
                    return room;
                }
            }
        }

        return null;
    }

    public bool AddPlayerToRoom(string roomId, OkeyPlayer player, string? password)
    {
        var room = GetRoom(roomId);
        if (room == null) return false;

        lock (room.StateLock)
        {
            if (room.Players.Count >= room.MaxPlayers) return false;
            if (room.IsPrivate && room.Password != password) return false;
            if (room.IsGameStarted) return false;

            room.Players.Add(player);
            return true;
        }
    }

    public void RemovePlayerFromRoom(string roomId, int userId)
    {
        var room = GetRoom(roomId);
        if (room == null) return;

        lock (room.StateLock)
        {
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player != null)
            {
                room.Players.Remove(player);
            }
        }

        if (room.Players.Count == 0 && room.CreatorId != 0)
        {
            DeleteRoom(roomId);
        }
    }

    public void DeleteRoom(string roomId)
    {
        _rooms.TryRemove(roomId, out _);
    }
}

// ========== GAME ENGINE ==========
public static class OkeyGameEngine
{
    private static readonly string[] Colors = { "Red", "Yellow", "Blue", "Black" };

    public static (List<OkeyTile> stock, List<OkeyTile>[] hands, OkeyTile indicator, int startIndex, int dealerIndex) DealTiles(int playerCount)
    {
        var allTiles = GenerateTileSet();
        ValidateTileSetIntegrity(allTiles, "generated deck");
        var random = new Random();

        // ✅ 1. GÖSTƏRİCİNİ ƏVVƏLCƏDƏN SEÇ (joker olmayacaq)
        OkeyTile indicator;
        do
        {
            indicator = allTiles[random.Next(allTiles.Count)];
        } while (indicator.IsFakeJoker); // Fake joker göstərici ola bilməz

        allTiles.Remove(indicator);
        allTiles = allTiles.OrderBy(x => random.Next()).ToList();

        var hands = new List<OkeyTile>[playerCount];
        int currentIndex = 0;
        int dealerIndex = random.Next(0, playerCount);

        for (int i = 0; i < playerCount; i++)
        {
            if (i == dealerIndex)
            {
                hands[i] = allTiles.Skip(currentIndex).Take(15).ToList();
                currentIndex += 15;
            }
            else
            {
                hands[i] = allTiles.Skip(currentIndex).Take(14).ToList();
                currentIndex += 14;
            }
        }

        var stock = allTiles.Skip(currentIndex).ToList();
        ValidateDealIntegrity(stock, hands, indicator, "dealt game");
        int startIndex = dealerIndex;

        return (stock, hands, indicator, startIndex, dealerIndex);
    }

    private static List<OkeyTile> GenerateTileSet()
    {
        var tiles = new List<OkeyTile>();
        int id = 1;

        for (int set = 0; set < 2; set++)
        {
            foreach (var color in Colors)
            {
                for (int num = 1; num <= 13; num++)
                {
                    tiles.Add(new OkeyTile
                    {
                        Id = id++,
                        Color = color,
                        Number = num,
                        IsFakeJoker = false,
                        IsJoker = false
                    });
                }
            }
        }

        tiles.Add(new OkeyTile { Id = id++, Color = "FakeJoker", Number = 0, IsFakeJoker = true });
        tiles.Add(new OkeyTile { Id = id++, Color = "FakeJoker", Number = 0, IsFakeJoker = true });

        ValidateTileSetIntegrity(tiles, "generated deck");
        return tiles;
    }

    public static void ValidateDealIntegrity(List<OkeyTile> stock, List<OkeyTile>[] hands, OkeyTile indicator, string context)
    {
        var allTiles = hands.SelectMany(h => h)
            .Concat(stock)
            .Append(indicator)
            .ToList();

        ValidateTileSetIntegrity(allTiles, context);

        for (int i = 0; i < hands.Length; i++)
        {
            var fakeJokerCount = hands[i].Count(t => t.IsFakeJoker);
            if (fakeJokerCount > 2)
            {
                throw new InvalidOperationException(
                    $"Okey hand integrity failed ({context}): playerIndex={i}, fakeJokers={fakeJokerCount}");
            }
        }
    }

    private static void ValidateTileSetIntegrity(IEnumerable<OkeyTile> tiles, string context)
    {
        var tileList = tiles.ToList();
        var fakeJokerCount = tileList.Count(t => t.IsFakeJoker);
        var duplicateIds = tileList
            .GroupBy(t => t.Id)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}x{g.Count()}")
            .ToList();

        if (tileList.Count != 106 || fakeJokerCount != 2 || duplicateIds.Count > 0)
        {
            throw new InvalidOperationException(
                $"Okey tile integrity failed ({context}): total={tileList.Count}, fakeJokers={fakeJokerCount}, duplicateIds=[{string.Join(", ", duplicateIds)}]");
        }
    }

    public static OkeyTile CalculateJoker(OkeyTile indicator)
    {
        if (indicator.IsFakeJoker)
        {
            return new OkeyTile { Id = -1, Color = "Red", Number = 1, IsJoker = true };
        }

        int jokerNumber = indicator.Number == 13 ? 1 : indicator.Number + 1;
        return new OkeyTile
        {
            Id = -1,
            Color = indicator.Color,
            Number = jokerNumber,
            IsJoker = true
        };
    }

    public static (bool IsValid, List<List<OkeyTile>> Melds, string WinType) ValidateWin(List<OkeyTile> hand, OkeyTile jokerTile)
    {
        if (hand.Count != 14)
        {
            Console.WriteLine($"❌ ValidateWin: Əldə {hand.Count} daş var (14 olmalı)");
            return (false, new List<List<OkeyTile>>(), "");
        }

        var pairWin = CheckPairWin(hand, jokerTile);
        if (pairWin.IsValid)
            return (true, pairWin.Melds, "Çift");

        var normalWin = CheckNormalWin(hand, jokerTile);
        if (normalWin.IsValid)
            return (true, normalWin.Melds, "Normal");

        return (false, new List<List<OkeyTile>>(), "");
    }

    private static (bool IsValid, List<List<OkeyTile>> Melds) CheckPairWin(List<OkeyTile> hand, OkeyTile jokerTile)
    {
        var melds = new List<List<OkeyTile>>();
        var usedTiles = new HashSet<int>();
        var jokers = hand.Where(t => IsJokerTile(t, jokerTile)).ToList();

        var tileGroups = hand
            .Where(t => !IsJokerTile(t, jokerTile))
            .GroupBy(t => new { Color = GetEffectiveColor(t, jokerTile), Number = GetEffectiveNumber(t, jokerTile) })
            .OrderBy(g => g.Key.Number)
            .ThenBy(g => g.Key.Color)
            .Select(g => g.ToList())
            .ToList();

        int pairCount = 0;
        int jokerUsed = 0;

        foreach (var tiles in tileGroups)
        {
            while (tiles.Count >= 2 && pairCount < 7)
            {
                var pair = tiles.Take(2).ToList();
                melds.Add(pair);
                foreach (var t in pair)
                {
                    usedTiles.Add(t.Id);
                    tiles.Remove(t);
                }
                pairCount++;
            }

            while (tiles.Count >= 1 && jokerUsed < jokers.Count && pairCount < 7)
            {
                var pair = new List<OkeyTile> { tiles[0], jokers[jokerUsed] };
                melds.Add(pair);
                usedTiles.Add(tiles[0].Id);
                usedTiles.Add(jokers[jokerUsed].Id);
                tiles.RemoveAt(0);
                jokerUsed++;
                pairCount++;
            }
        }

        while (jokerUsed + 1 < jokers.Count && pairCount < 7)
        {
            var pair = new List<OkeyTile> { jokers[jokerUsed], jokers[jokerUsed + 1] };
            melds.Add(pair);
            usedTiles.Add(jokers[jokerUsed].Id);
            usedTiles.Add(jokers[jokerUsed + 1].Id);
            jokerUsed += 2;
            pairCount++;
        }

        if (pairCount == 7 && usedTiles.Count == 14)
        {
            return (true, melds);
        }

        return (false, new List<List<OkeyTile>>());
    }

    private static (bool IsValid, List<List<OkeyTile>> Melds) CheckNormalWin(List<OkeyTile> hand, OkeyTile jokerTile)
    {
        var patterns = new[]
        {
            new[] { 3, 3, 3, 5 },
            new[] { 3, 3, 4, 4 },
            new[] { 5, 5, 4 },
        };

        foreach (var pattern in patterns)
        {
            var result = TryPattern(hand, jokerTile, pattern);
            if (result.IsValid) return result;
        }

        return (false, new List<List<OkeyTile>>());
    }

    private static (bool IsValid, List<List<OkeyTile>> Melds) TryPattern(
        List<OkeyTile> hand,
        OkeyTile jokerTile,
        int[] pattern)
    {
        var melds = new List<List<OkeyTile>>();
        var usedTiles = new HashSet<int>();

        bool Backtrack(int groupIndex)
        {
            if (groupIndex >= pattern.Length)
            {
                return usedTiles.Count == hand.Count;
            }

            int requiredSize = pattern[groupIndex];
            var availableTiles = hand.Where(t => !usedTiles.Contains(t.Id)).ToList();
            var candidates = BuildMeldCandidates(availableTiles, jokerTile, requiredSize);

            foreach (var candidate in candidates)
            {
                foreach (var tile in candidate) usedTiles.Add(tile.Id);
                melds.Add(candidate);

                if (Backtrack(groupIndex + 1))
                    return true;

                foreach (var tile in candidate) usedTiles.Remove(tile.Id);
                melds.RemoveAt(melds.Count - 1);
            }

            return false;
        }

        bool isValid = Backtrack(0);
        return (isValid, isValid ? melds : new List<List<OkeyTile>>());
    }

    private static List<List<OkeyTile>> BuildMeldCandidates(List<OkeyTile> tiles, OkeyTile jokerTile, int targetSize)
    {
        return BuildSetCandidates(tiles, jokerTile, targetSize)
            .Concat(BuildRunCandidates(tiles, jokerTile, targetSize))
            .GroupBy(candidate => string.Join(",", candidate.Select(t => t.Id).OrderBy(id => id)))
            .Select(g => g.First())
            .ToList();
    }

    private static List<List<OkeyTile>> BuildSetCandidates(List<OkeyTile> tiles, OkeyTile jokerTile, int targetSize)
    {
        var candidates = new List<List<OkeyTile>>();
        if (targetSize > Colors.Length)
            return candidates;

        var jokers = tiles.Where(t => IsJokerTile(t, jokerTile)).ToList();

        for (int number = 1; number <= 13; number++)
        {
            var colorOptions = tiles
                .Where(t => !IsJokerTile(t, jokerTile) && GetEffectiveNumber(t, jokerTile) == number)
                .GroupBy(t => GetEffectiveColor(t, jokerTile))
                .Select(g => g.ToList())
                .ToList();

            for (int realCount = Math.Min(targetSize, colorOptions.Count); realCount >= 0; realCount--)
            {
                int neededJokers = targetSize - realCount;
                if (neededJokers > jokers.Count)
                    continue;

                foreach (var selectedColorGroups in GetCombinations(colorOptions, realCount))
                {
                    foreach (var selectedTiles in PickOneFromEach(selectedColorGroups))
                    {
                        foreach (var selectedJokers in GetCombinations(jokers, neededJokers))
                        {
                            candidates.Add(selectedTiles.Concat(selectedJokers).ToList());
                        }
                    }
                }
            }
        }

        return candidates;
    }

    private static List<List<OkeyTile>> BuildRunCandidates(List<OkeyTile> tiles, OkeyTile jokerTile, int targetSize)
    {
        var candidates = new List<List<OkeyTile>>();

        foreach (var color in Colors)
        {
            for (int startNum = 1; startNum <= 15 - targetSize; startNum++)
            {
                var positionOptions = new List<List<OkeyTile>>();

                for (int i = 0; i < targetSize; i++)
                {
                    int number = startNum + i;
                    if (number == 14)
                    {
                        number = 1;
                    }

                    var exactTiles = tiles
                        .Where(t =>
                            !IsJokerTile(t, jokerTile) &&
                            GetEffectiveColor(t, jokerTile) == color &&
                            GetEffectiveNumber(t, jokerTile) == number)
                        .ToList();

                    positionOptions.Add(exactTiles.Count > 0
                        ? exactTiles
                        : tiles.Where(t => IsJokerTile(t, jokerTile)).ToList());
                }

                foreach (var run in PickOneFromEach(positionOptions))
                {
                    if (run.Select(t => t.Id).Distinct().Count() == targetSize)
                    {
                        candidates.Add(run);
                    }
                }
            }
        }

        return candidates;
    }

    private static List<List<T>> GetCombinations<T>(List<T> items, int count)
    {
        var result = new List<List<T>>();

        void Combine(int start, List<T> current)
        {
            if (current.Count == count)
            {
                result.Add(new List<T>(current));
                return;
            }

            for (int i = start; i < items.Count; i++)
            {
                current.Add(items[i]);
                Combine(i + 1, current);
                current.RemoveAt(current.Count - 1);
            }
        }

        Combine(0, new List<T>());
        return result;
    }

    private static List<List<OkeyTile>> PickOneFromEach(List<List<OkeyTile>> optionGroups)
    {
        var result = new List<List<OkeyTile>>();

        void Pick(int index, List<OkeyTile> current, HashSet<int> usedIds)
        {
            if (index >= optionGroups.Count)
            {
                result.Add(new List<OkeyTile>(current));
                return;
            }

            foreach (var tile in optionGroups[index])
            {
                if (!usedIds.Add(tile.Id))
                    continue;

                current.Add(tile);
                Pick(index + 1, current, usedIds);
                current.RemoveAt(current.Count - 1);
                usedIds.Remove(tile.Id);
            }
        }

        Pick(0, new List<OkeyTile>(), new HashSet<int>());
        return result;
    }

    public static int CalculatePenalty(List<OkeyTile> hand, OkeyTile jokerTile)
    {
        int penalty = 0;

        foreach (var tile in hand)
        {
            if (IsJokerTile(tile, jokerTile))
            {
                penalty += 0;
            }
            else if (tile.IsFakeJoker)
            {
                var fakeJokerNumber = GetEffectiveNumber(tile, jokerTile);
                penalty += fakeJokerNumber >= 10 ? 10 : fakeJokerNumber;
            }
            else if (tile.Number >= 10)
            {
                penalty += 10;
            }
            else
            {
                penalty += tile.Number;
            }
        }

        return penalty;
    }

    public static bool IsJokerTile(OkeyTile tile, OkeyTile jokerTile)
    {
        if (tile.IsFakeJoker) return false;
        return tile.Color == jokerTile.Color && tile.Number == jokerTile.Number;
    }

    public static string DescribeTile(OkeyTile tile, OkeyTile jokerTile)
    {
        if (tile.IsFakeJoker)
        {
            return $"FakeJoker=>{GetEffectiveColor(tile, jokerTile)} {GetEffectiveNumber(tile, jokerTile)}";
        }

        var suffix = IsJokerTile(tile, jokerTile) ? " (okey)" : "";
        return $"{tile.Color} {tile.Number}{suffix}";
    }

    private static string GetEffectiveColor(OkeyTile tile, OkeyTile jokerTile)
    {
        return tile.IsFakeJoker ? GetIndicatorColor(jokerTile) : tile.Color;
    }

    private static int GetEffectiveNumber(OkeyTile tile, OkeyTile jokerTile)
    {
        return tile.IsFakeJoker ? GetIndicatorNumber(jokerTile) : tile.Number;
    }

    private static string GetIndicatorColor(OkeyTile jokerTile)
    {
        return jokerTile.Color;
    }

    private static int GetIndicatorNumber(OkeyTile jokerTile)
    {
        return jokerTile.Number == 1 ? 13 : jokerTile.Number - 1;
    }

    private static List<int[]> GetMeldSizePatterns(int tileCount)
    {
        var patterns = new List<int[]>();

        void Build(int remaining, int minSize, List<int> current)
        {
            if (remaining == 0)
            {
                patterns.Add(current.ToArray());
                return;
            }

            for (int size = minSize; size <= Math.Min(13, remaining); size++)
            {
                if (remaining - size > 0 && remaining - size < 3)
                    continue;

                current.Add(size);
                Build(remaining - size, size, current);
                current.RemoveAt(current.Count - 1);
            }
        }

        Build(tileCount, 3, new List<int>());

        return patterns
            .OrderByDescending(pattern => pattern.Length)
            .ThenBy(pattern => pattern.Max())
            .ToList();
    }

    public static object GetHint(List<OkeyTile> hand, OkeyTile jokerTile)
    {
        var hints = new List<string>();

        var numberCounts = new Dictionary<int, int>();
        foreach (var tile in hand)
        {
            if (!IsJokerTile(tile, jokerTile))
            {
                var number = GetEffectiveNumber(tile, jokerTile);
                if (!numberCounts.ContainsKey(number))
                    numberCounts[number] = 0;
                numberCounts[number]++;
            }
        }

        int pairCount = numberCounts.Count(kvp => kvp.Value >= 2);
        if (pairCount >= 4)
        {
            hints.Add($"ÇİFT imkanı: {pairCount} cüt var");
        }

        foreach (var kvp in numberCounts)
        {
            if (kvp.Value >= 3)
                hints.Add($"Rəqəm {kvp.Key}: {kvp.Value} daş - SET düzəlt");
        }

        foreach (var color in Colors)
        {
            var colorTiles = hand
                .Where(t => GetEffectiveColor(t, jokerTile) == color && !IsJokerTile(t, jokerTile))
                .OrderBy(t => GetEffectiveNumber(t, jokerTile))
                .ToList();

            for (int i = 0; i < colorTiles.Count - 1; i++)
            {
                if (GetEffectiveNumber(colorTiles[i + 1], jokerTile) == GetEffectiveNumber(colorTiles[i], jokerTile) + 1)
                {
                    hints.Add($"{color}: {GetEffectiveNumber(colorTiles[i], jokerTile)}-{GetEffectiveNumber(colorTiles[i + 1], jokerTile)} - SIRA düzəlt");
                }
            }
        }

        return new
        {
            hints = hints.Take(3).ToArray(),
            advice = hints.Any() ? "Kombinasiyaları tamamla" : "Daşları sırala"
        };
    }


}
public class OkeyRoomInitializer : IHostedService
{
    private readonly OkeyRoomManager _roomManager;
    private Timer _roomCheckTimer;

    public OkeyRoomInitializer(OkeyRoomManager roomManager)
    {
        _roomManager = roomManager;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("🎮 Initializing preset Okey rooms...");
        CreatePresetRooms();
        _roomCheckTimer = new Timer(CheckAndRecreateRooms, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _roomCheckTimer?.Dispose();
        return Task.CompletedTask;
    }

    private void CreatePresetRooms()
    {
        // ✅ HƏR QIYMƏT ÜÇÜN 2, 3, 4 NƏFƏRLIK OTAQLAR
        var presetRooms = new[]
        {
            // ✅ 0.20₼ - Başlanğıc
            //new { Name = "Başlanğıc 2x", EntryFee = 0.20m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            //new { Name = "Başlanğıc 3x", EntryFee = 0.20m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            //new { Name = "Başlanğıc 4x", EntryFee = 0.20m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // ✅ 0.50₼ - Orta Səviyyə
            new { Name = "Orta 2x", EntryFee = 0.50m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            new { Name = "Orta 3x", EntryFee = 0.50m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            new { Name = "Orta 4x", EntryFee = 0.50m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // ✅ 1₼ - Peşəkar
            new { Name = "Peşəkar 2x", EntryFee = 1m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            new { Name = "Peşəkar 3x", EntryFee = 1m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            new { Name = "Peşəkar 4x", EntryFee = 1m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // ✅ 2₼ - VIP (Okey51)
            new { Name = "VIP 2x", EntryFee = 2m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "VIP 3x", EntryFee = 2m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "VIP 4x", EntryFee = 2m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // ✅ 5₼ - Master
            new { Name = "Master 2x", EntryFee = 5m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Master 3x", EntryFee = 5m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Master 4x", EntryFee = 5m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // ✅ 10₼ - Elite
            new { Name = "Elite 2x", EntryFee = 10m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Elite 3x", EntryFee = 10m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Elite 4x", EntryFee = 10m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // ✅ 20₼ - Pro
            new { Name = "Pro 2x", EntryFee = 20m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Pro 3x", EntryFee = 20m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Pro 4x", EntryFee = 20m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // ✅ 50₼ - Champion
            new { Name = "Champion 2x", EntryFee = 50m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Champion 3x", EntryFee = 50m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Champion 4x", EntryFee = 50m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // ✅ 100₼ - Legend
            new { Name = "Legend 2x", EntryFee = 100m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Legend 3x", EntryFee = 100m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Legend 4x", EntryFee = 100m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 }
        };

        foreach (var preset in presetRooms)
        {
            // ✅ Yalnız join edilə bilən waiting preset varsa yenisini yaratma
            if (!_roomManager.JoinableRoomExistsByName(preset.Name))
            {
                _roomManager.CreateRoom(
                    roomName: preset.Name,
                    creatorName: "System",
                    creatorId: 0,
                    entryFee: preset.EntryFee,
                    maxPlayers: preset.MaxPlayers,
                    mode: preset.Mode,
                    isPrivate: false,
                    password: null
                );

                Console.WriteLine($"✅ Created room: {preset.Name} ({preset.EntryFee}₼ | {preset.MaxPlayers}P) - {preset.Mode}");
            }
        }
    }

    private void CheckAndRecreateRooms(object state)
    {
        // ✅ EYNI PRESET SIYAHISI
        var presetRooms = new[]
        {
            // 0.20₼
            //new { Name = "Başlanğıc 2x", EntryFee = 0.20m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            //new { Name = "Başlanğıc 3x", EntryFee = 0.20m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            //new { Name = "Başlanğıc 4x", EntryFee = 0.20m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // 0.50₼
            new { Name = "Orta 2x", EntryFee = 0.50m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            new { Name = "Orta 3x", EntryFee = 0.50m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            new { Name = "Orta 4x", EntryFee = 0.50m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // 1₼
            new { Name = "Peşəkar 2x", EntryFee = 1m, MaxPlayers = 2, Mode = OkeyGameMode.Okey101 },
            new { Name = "Peşəkar 3x", EntryFee = 1m, MaxPlayers = 3, Mode = OkeyGameMode.Okey101 },
            new { Name = "Peşəkar 4x", EntryFee = 1m, MaxPlayers = 4, Mode = OkeyGameMode.Okey101 },

            // 2₼
            new { Name = "VIP 2x", EntryFee = 2m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "VIP 3x", EntryFee = 2m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "VIP 4x", EntryFee = 2m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // 5₼
            new { Name = "Master 2x", EntryFee = 5m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Master 3x", EntryFee = 5m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Master 4x", EntryFee = 5m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // 10₼
            new { Name = "Elite 2x", EntryFee = 10m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Elite 3x", EntryFee = 10m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Elite 4x", EntryFee = 10m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // 20₼
            new { Name = "Pro 2x", EntryFee = 20m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Pro 3x", EntryFee = 20m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Pro 4x", EntryFee = 20m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // 50₼
            new { Name = "Champion 2x", EntryFee = 50m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Champion 3x", EntryFee = 50m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Champion 4x", EntryFee = 50m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 },

            // 100₼
            new { Name = "Legend 2x", EntryFee = 100m, MaxPlayers = 2, Mode = OkeyGameMode.Okey51 },
            new { Name = "Legend 3x", EntryFee = 100m, MaxPlayers = 3, Mode = OkeyGameMode.Okey51 },
            new { Name = "Legend 4x", EntryFee = 100m, MaxPlayers = 4, Mode = OkeyGameMode.Okey51 }
        };

        foreach (var preset in presetRooms)
        {
            if (!_roomManager.JoinableRoomExistsByName(preset.Name))
            {
                _roomManager.CreateRoom(
                    roomName: preset.Name,
                    creatorName: "System",
                    creatorId: 0,
                    entryFee: preset.EntryFee,
                    maxPlayers: preset.MaxPlayers,
                    mode: preset.Mode,
                    isPrivate: false,
                    password: null
                );

                Console.WriteLine($"🔄 Recreated room: {preset.Name} ({preset.MaxPlayers}P)");
            }
        }
    }



    public class OkeyRoomCleanupService : IHostedService
    {
        private Timer _cleanupTimer;
        private readonly OkeyRoomManager _roomManager;
        private readonly IHubContext<OkeyHub> _hubContext;

        public OkeyRoomCleanupService(OkeyRoomManager roomManager, IHubContext<OkeyHub> hubContext)
        {
            _roomManager = roomManager;
            _hubContext = hubContext;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Console.WriteLine("🧹 Room Cleanup Service başladı");

            _cleanupTimer = new Timer(async _ =>
            {
                try
                {
                    await CleanupRooms();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Cleanup error: {ex.Message}");
                }
            }, null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _cleanupTimer?.Dispose();
            return Task.CompletedTask;
        }

        private async Task CleanupRooms()
        {
            var allRooms = _roomManager.GetAllRooms();

            foreach (var room in allRooms.ToList())
            {
                lock (room.StateLock)
                {
                    // Bitirilmiş otaqları sil (5 dəqiqə)
                    if (room.IsGameFinished &&
                        (DateTime.UtcNow - room.GameFinishedTime).TotalMinutes > 5)
                    {
                        _roomManager.DeleteRoom(room.RoomId);
                        Console.WriteLine($"🗑️ Bitirilmiş otaq silindi: {room.RoomName}");
                    }

                    // Boş otaqları sil (10 dəqiqə)
                    if (!room.IsGameStarted && room.Players.Count == 0 &&
                        (DateTime.UtcNow - room.CreatedTime).TotalMinutes > 10 &&
                        room.CreatorId != 0)
                    {
                        _roomManager.DeleteRoom(room.RoomId);
                        Console.WriteLine($"🗑️ Boş otaq silindi: {room.RoomName}");
                    }
                }
            }
        }
    }

}

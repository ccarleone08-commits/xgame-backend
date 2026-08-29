using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs
{
    public class DominoRoomManager
    {
        private readonly ConcurrentDictionary<string, DominoRoom> _rooms = new();
        private Timer? _cleanupTimer;
        private readonly IDbContextFactory<BlogAppDbContext>? _dbContextFactory;
        public DominoRoom? FindOrCreateRoom(string gameType, int playerCount, int scoreToWin, decimal entryFee)
        {
            var existingRoom = _rooms.Values.FirstOrDefault(r =>
                r.GameType == gameType &&
                r.PlayerCount == playerCount &&
                r.ScoreToWin == scoreToWin &&
                r.EntryFee == entryFee &&
                !r.IsGameStarted &&
                r.Players.Count < playerCount
            );

            if (existingRoom != null)
            {
                Console.WriteLine($"✅ Found existing room: {existingRoom.RoomId} ({existingRoom.Players.Count}/{playerCount})");
                return existingRoom;
            }

            var room = new DominoRoom
            {
                RoomId = Guid.NewGuid().ToString(),
                RoomName = $"{GetGameDisplayName(gameType)} ({playerCount}P)",
                GameType = gameType,
                PlayerCount = playerCount,
                ScoreToWin = scoreToWin,
                EntryFee = entryFee,
                CreatedAt = DateTime.UtcNow
            };

            if (playerCount == 4)
            {
                room.TeamScores = new int[] { 0, 0 };
            }

            if (_rooms.TryAdd(room.RoomId, room))
            {
                Console.WriteLine($"✅ Room created: {room.RoomId} | {gameType} | {playerCount}P | {scoreToWin}pt | {entryFee}💰");
                return room;
            }

            return null;
        }
        public DominoRoom? GetRoomByUser(int userId)
        {
            var room = _rooms.Values.FirstOrDefault(r => r.Players.Any(p =>
                p.UserId == userId &&
                !p.IsSystemControlled));
            return room;
        }
        public DominoRoomManager(IDbContextFactory<BlogAppDbContext>? dbContextFactory = null)
        {
            _dbContextFactory = dbContextFactory;

            // 🔥 Hər 2 dəqiqədən bir köhnə otaqları yoxla
            _cleanupTimer = new Timer(CleanupExpiredRooms, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }

        // 🔥 YENİ METOD: Köhnə və boş otaqları təmizlə
        private async void CleanupExpiredRooms(object? state)
        {
            var now = DateTime.UtcNow;
            var roomsToDelete = new List<(string roomId, DominoRoom room, bool shouldRefund)>();

            foreach (var kvp in _rooms)
            {
                var room = kvp.Value;
                var roomAge = now - room.CreatedAt;

                // ❌ Oyun başlamayıb və 10 dəqiqədən çoxdur - REFUND VER
                if (!room.IsGameStarted && roomAge.TotalMinutes > 10)
                {
                    roomsToDelete.Add((kvp.Key, room, shouldRefund: true));
                    Console.WriteLine($"🗑️ Cleanup: Room {room.RoomId} (not started, {roomAge.TotalMinutes:F1} min old) - REFUND");
                }
                // ❌ Oyun bitib və 5 dəqiqədən çoxdur - REFUND LAZIM DEYIL (oyun bitib)
                else if (room.IsGameFinished && roomAge.TotalMinutes > 5)
                {
                    roomsToDelete.Add((kvp.Key, room, shouldRefund: false));
                    Console.WriteLine($"🗑️ Cleanup: Room {room.RoomId} (finished, {roomAge.TotalMinutes:F1} min old)");
                }
                // ❌ Oyunçu sayı 0 - REFUND LAZIM DEYIL (artıq edilib)
                else if (room.Players.Count == 0)
                {
                    roomsToDelete.Add((kvp.Key, room, shouldRefund: false));
                    Console.WriteLine($"🗑️ Cleanup: Room {room.RoomId} (no players)");
                }
                // ❌ Oyun başlayıb amma 30 dəqiqədən çoxdur və hərəkət yoxdur - REFUND VER
                else if (room.IsGameStarted && roomAge.TotalMinutes > 30 && room.LastActivityAt.HasValue)
                {
                    var inactiveTime = now - room.LastActivityAt.Value;
                    if (inactiveTime.TotalMinutes > 10)
                    {
                        roomsToDelete.Add((kvp.Key, room, shouldRefund: true));
                        Console.WriteLine($"🗑️ Cleanup: Room {room.RoomId} (inactive for {inactiveTime.TotalMinutes:F1} min) - REFUND");
                    }
                }
            }

            // 💰 REFUND və SİLMƏ
            foreach (var (roomId, room, shouldRefund) in roomsToDelete)
            {
                if (shouldRefund && room.Players.Count > 0)
                {
                    await RefundRoomPlayers(room);
                }
                DeleteRoom(roomId);
            }

            if (roomsToDelete.Count > 0)
            {
                Console.WriteLine($"✅ Cleanup completed: {roomsToDelete.Count} rooms deleted");
            }
        }

        // 🔥 YENİ METOD: Otaqdakı oyunçulara refund ver
        private async Task RefundRoomPlayers(DominoRoom room)
        {
            var db = _dbContextFactory?.CreateDbContext();
            if (db == null)
            {
                Console.WriteLine("❌ Cannot refund: DbContext not available");
                return;
            }

            try
            {
                foreach (var player in room.Players)
                {
                    var user = await db.Users.FindAsync(player.UserId);
                    if (user != null)
                    {
                        user.Balance += room.EntryFee;
                        Console.WriteLine($"💰 REFUND: {user.Name} +{room.EntryFee} coin (room cleanup)");
                    }
                }

                await db.SaveChangesAsync();
                Console.WriteLine($"✅ Refunded {room.Players.Count} players from room {room.RoomId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Refund error: {ex.Message}");
            }
            finally
            {
                await db.DisposeAsync();
            }
        }

        // 🔥 YENİ METOD: Otağın son aktivliyini yenilə
        public void UpdateRoomActivity(string roomId)
        {
            if (_rooms.TryGetValue(roomId, out var room))
            {
                room.LastActivityAt = DateTime.UtcNow;
            }
        }

        // 🔥 YENİ METOD: Bütün otaqları təmizlə (manual)
        public int CleanupAllInactiveRooms()
        {
            CleanupExpiredRooms(null);
            return _rooms.Count;
        }
        public DominoRoom? GetRoom(string roomId)
        {
            _rooms.TryGetValue(roomId, out var room);
            return room;
        }

        public bool DeleteRoom(string roomId)
        {
            if (_rooms.TryRemove(roomId, out var room))
            {
                Console.WriteLine($"🗑️ Room deleted: {room.RoomId} ({room.RoomName})");
                return true;
            }
            return false;
        }

        public List<object> GetAvailableLobbies()
        {
            var lobbies = new List<object>();

            // CLASSIC 101
            //2 neferlik
            //lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 0.20m));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 0.50m));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 1));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 2));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 5));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 10));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 20));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 50));
            lobbies.Add(CreateLobbyOption("Classic101", 2, 101, 100));
            //3 neferlik
            //lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 0.20m));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 0.50m));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 1));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 2));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 5));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 10));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 20));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 50));
            lobbies.Add(CreateLobbyOption("Classic101", 3, 101, 100));
            //4 neferlik
            //lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 0.20m));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 0.50m));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 1));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 2));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 5));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 10));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 20));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 50));
            lobbies.Add(CreateLobbyOption("Classic101", 4, 101, 100));

            // QUICK 5
            //2 neferlik
            //lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 0.20m));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 0.50m));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 1));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 2));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 5));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 10));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 20));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 50));
            lobbies.Add(CreateLobbyOption("Quick5", 2, 51, 100));

            //3 neferlik                               
            //lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 0.20m));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 0.50m));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 1));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 2));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 5));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 10));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 20));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 50));
            lobbies.Add(CreateLobbyOption("Quick5", 3, 51, 100));

            //4 neferlik                               
            //lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 0.20m));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 0.50m));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 1));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 2));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 5));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 10));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 20));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 50));
            lobbies.Add(CreateLobbyOption("Quick5", 4, 51, 100));



            // ALL FIVES
            //2neferlik
            //lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 185, 100));

            //lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 2, 365, 100));

            //3 neferlik
            //lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 185, 100));

            //lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 3, 365, 100));

            //4 neferlik                               
            //lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 185, 100));

            //lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 0.20m));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 0.50m));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 1));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 2));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 5));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 10));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 20));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 50));
            lobbies.Add(CreateLobbyOption("AllFives", 4, 365, 100));
            return lobbies;
        }

        private object CreateLobbyOption(string gameType, int playerCount, int scoreToWin, decimal entryFee)
        {
            int waitingPlayers = _rooms.Values
                .Where(r => r.GameType == gameType &&
                           r.PlayerCount == playerCount &&
                           r.ScoreToWin == scoreToWin &&
                           r.EntryFee == entryFee &&
                           !r.IsGameStarted)
                .Sum(r => r.Players.Count);

            return new
            {
                gameType,
                playerCount,
                scoreToWin,
                entryFee,
                waitingPlayers,
                displayName = GetLobbyDisplayName(gameType, playerCount, scoreToWin, entryFee)
            };
        }

        private string GetLobbyDisplayName(string gameType, int playerCount, int scoreToWin, decimal entryFee)
        {
            string icon = gameType switch
            {
                "Classic101" => "🎯",
                "Quick5" => "⚡",
                "AllFives" => "🔥",
                _ => "🎲"
            };

            string name = GetGameDisplayName(gameType);
            return $"{icon} {name} • {playerCount}P • {scoreToWin}pt • {entryFee}💰";
        }

        private string GetGameDisplayName(string gameType) => gameType switch
        {
            "Classic101" => "Klassik 101",
            "Quick5" => "Sürətli 5 Daş",
            "AllFives" => "All Fives",
            _ => "Domino"
        };

        public int GetActiveRoomCount() => _rooms.Count;
        public int GetActivePlayers() => _rooms.Values.Sum(r => r.Players.Count);

        public List<RoomStatsDto> GetRoomStats()
        {
            return _rooms.Values.Select(r => new RoomStatsDto
            {
                RoomId = r.RoomId,
                RoomName = r.RoomName,
                GameType = r.GameType,
                CurrentPlayers = r.Players.Count,
                MaxPlayers = r.PlayerCount,
                ScoreToWin = r.ScoreToWin,
                EntryFee = r.EntryFee,
                IsStarted = r.IsGameStarted,
                CurrentRound = r.CurrentRound,
                CreatedAt = r.CreatedAt
            }).ToList();
        }
    }

    public class RoomStatsDto
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public string GameType { get; set; } = string.Empty;
        public int CurrentPlayers { get; set; }
        public int MaxPlayers { get; set; }
        public int ScoreToWin { get; set; }
        public decimal EntryFee { get; set; }
        public bool IsStarted { get; set; }
        public int CurrentRound { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    // ========== MODELS ==========
    public class DominoRoom
    {
        private const int DisconnectReconnectSeconds = 25;
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public string GameType { get; set; } = "Classic101";
        public int PlayerCount { get; set; } = 2;
        public int ScoreToWin { get; set; } = 101;
        public decimal EntryFee { get; set; } = 10m;

        public List<DominoPlayer> Players { get; set; } = new();
        public List<DominoTile> Stock { get; set; } = new();
        public DominoChain Chain { get; set; } = new();
        public HashSet<int> ReadyPlayers { get; set; } = new();
        public int RoundNum { get; set; } = 1;
        public int CurrentPlayerIndex { get; set; } = 0;
        public int CurrentRound { get; set; } = 1;
        public bool IsGameStarted { get; set; } = false;
        public bool IsRoundFinished { get; set; } = false;
        public bool IsGameFinished { get; set; } = false;
        public bool ForceOpeningRuleAfterLeave { get; set; } = false;
        public bool IsRoundEndProcessing { get; set; } = false;
        public int RoundEndProcessingRound { get; set; } = 0;

        private Dictionary<int, CancellationTokenSource> _disconnectTimers = new();
        public int CurrentTurnUserId { get; set; } = -1;
        public DateTime? TurnStartedAtUtc { get; set; }
        public DateTime? TurnDeadlineUtc { get; set; }
        public int TurnDurationSeconds { get; set; } = 30;
        public bool IsAutoPassTurnTimer { get; set; } = false;

        public DominoPlayer? RoundWinner { get; set; }
        public int[] TeamScores { get; set; } = Array.Empty<int>();

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        // 🔥 YENİ: Son aktivlik tarixi
        public DateTime? LastActivityAt { get; set; } = DateTime.UtcNow;

        public object StateLock { get; } = new();

        public string CurrentPlayerId => Players.Count > CurrentPlayerIndex
            ? Players[CurrentPlayerIndex].ConnectionId
            : string.Empty;

        public DominoPlayer? GetCurrentPlayer() =>
            Players.Count > CurrentPlayerIndex ? Players[CurrentPlayerIndex] : null;

        public DominoPlayer? GetPlayer(string connectionId) =>
            Players.FirstOrDefault(p => p.ConnectionId == connectionId);

        public void NextTurn()
        {
            CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
        }

        public void StartTurnTimerState(int durationSeconds, bool isAutoPass)
        {
            var now = DateTime.UtcNow;
            TurnStartedAtUtc = now;
            TurnDeadlineUtc = now.AddSeconds(durationSeconds);
            TurnDurationSeconds = durationSeconds;
            IsAutoPassTurnTimer = isAutoPass;
        }

        public void ClearTurnTimerState()
        {
            TurnStartedAtUtc = null;
            TurnDeadlineUtc = null;
            TurnDurationSeconds = 30;
            IsAutoPassTurnTimer = false;
        }

        public int GetTurnTimeRemainingSeconds()
        {
            if (TurnDeadlineUtc == null)
                return 0;

            return Math.Max(0, (int)Math.Ceiling((TurnDeadlineUtc.Value - DateTime.UtcNow).TotalSeconds));
        }

        public bool AllPlayersPassed()
        {
            return Players.All(p => p.HasPassed || p.Hand.Count == 0);
        }

        public int GetReservedStockCount()
        {
            return GameType switch
            {
                "Classic101" => 1,
                "AllFives" => 1,
                _ => 0
            };
        }

        public bool CanDrawFromStock()
        {
            return Stock.Count > GetReservedStockCount();
        }

        public bool CanDrawFromStockForPlayer(DominoPlayer player)
        {
            if (!IsGameStarted || IsRoundFinished)
                return false;

            if (Chain.Tiles.Count == 0)
                return false;

            if (GameType == "Quick5")
                return false;

            if (GameType == "AllFives" && PlayerCount == 4)
                return false;

            if (!CanDrawFromStock())
                return false;

            return !PlayerHasPlayableTile(player);
        }

        public bool PlayerHasPlayableTile(DominoPlayer player)
        {
            return player.Hand.Any(tile =>
            {
                var (canLeft, canRight, canCenterTop, canCenterBottom) = Chain.CanPlace(tile);
                return GameType == "AllFives"
                    ? canLeft || canRight || canCenterTop || canCenterBottom
                    : canLeft || canRight;
            });
        }

        public DominoPlayer? RemovePlayerAndAdjustTurn(int userId, out bool turnChanged)
        {
            turnChanged = false;

            var removedIndex = Players.FindIndex(p => p.UserId == userId);
            if (removedIndex == -1)
            {
                return null;
            }

            var removedPlayer = Players[removedIndex];
            bool removedWasCurrentTurn = IsGameStarted && !IsRoundFinished && removedPlayer.UserId == CurrentTurnUserId;

            Players.RemoveAt(removedIndex);

            if (Players.Count == 0)
            {
                CurrentPlayerIndex = 0;
                CurrentTurnUserId = -1;
                return removedPlayer;
            }

            if (removedWasCurrentTurn)
            {
                if (CurrentPlayerIndex >= Players.Count)
                {
                    CurrentPlayerIndex = 0;
                }

                turnChanged = true;
            }
            else if (removedIndex < CurrentPlayerIndex)
            {
                CurrentPlayerIndex--;
            }

            if (CurrentPlayerIndex < 0 || CurrentPlayerIndex >= Players.Count)
            {
                CurrentPlayerIndex = 0;
            }

            var currentPlayer = GetCurrentPlayer();
            CurrentTurnUserId = currentPlayer?.UserId ?? -1;

            foreach (var player in Players)
            {
                player.Status = player.UserId == CurrentTurnUserId
                    ? PlayerStatus.Playing
                    : PlayerStatus.Waiting;
            }

            return removedPlayer;
        }

        /// ✅ Oyunçu disconnect oldu - sadəcə status dəyiş
        public bool MarkDisconnected(int userId, string connectionId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    return false;
                }

                if (!string.Equals(player.ConnectionId, connectionId, StringComparison.Ordinal))
                {
                    Console.WriteLine($"ℹ️ Stale disconnect ignored for {player.Name}: {connectionId} != {player.ConnectionId}");
                    return false;
                }

                player.IsConnected = false;
                player.DisconnectedAt = DateTime.UtcNow;
                player.DisconnectGraceDeadlineUtc = player.DisconnectedAt.Value.AddSeconds(DisconnectReconnectSeconds);
                Console.WriteLine($"⚠️ {player.Name} marked as DISCONNECTED");
                return true;
            }
        }

        public int GetDisconnectGraceRemainingMilliseconds(int userId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player?.DisconnectGraceDeadlineUtc == null)
                {
                    return 0;
                }

                return Math.Max(0, (int)Math.Ceiling(
                    (player.DisconnectGraceDeadlineUtc.Value - DateTime.UtcNow).TotalMilliseconds));
            }
        }

        public bool MarkSystemControlled(int userId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.IsConnected || player.IsSystemControlled)
                {
                    return false;
                }

                player.IsSystemControlled = true;
                player.SystemControlledAtUtc = DateTime.UtcNow;
                Console.WriteLine($"🤖 {player.Name} is now system-controlled");
                return true;
            }
        }

        /// ✅ Oyunçu reconnect oldu - ConnectionId yenilə
        public void MarkReconnected(int userId, string newConnectionId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player != null)
                {
                    player.IsConnected = true;
                    player.ConnectionId = newConnectionId;
                    player.DisconnectedAt = null;
                    player.DisconnectGraceDeadlineUtc = null;
                    Console.WriteLine($"✅ {player.Name} RECONNECTED with new ConnectionId");
                }
            }
        }

        /// ✅ Disconnect timeri başlat
        public void StartDisconnectTimer(int userId, TimeSpan timeout, Func<int, Task>? onTimeout = null)
        {
            CancellationTokenSource cts;

            lock (StateLock)
            {
                if (_disconnectTimers.TryGetValue(userId, out var existingCts))
                {
                    existingCts.Cancel();
                }

                cts = new CancellationTokenSource();
                _disconnectTimers[userId] = cts;
                Console.WriteLine($"⏱️ Disconnect timer started for user {userId} ({timeout.TotalSeconds}s)");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(timeout, cts.Token);

                    if (onTimeout != null)
                    {
                        await onTimeout(userId);
                    }
                    else
                    {
                        HandleDisconnectTimeout(userId);
                    }
                }
                catch (TaskCanceledException)
                {
                    Console.WriteLine($"ℹ️ Disconnect timer cancelled for user {userId}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Disconnect timer callback error for user {userId}: {ex.Message}");
                }
                finally
                {
                    lock (StateLock)
                    {
                        if (_disconnectTimers.TryGetValue(userId, out var currentCts) && ReferenceEquals(currentCts, cts))
                        {
                            _disconnectTimers.Remove(userId);
                        }
                    }

                    cts.Dispose();
                }
            });
        }

        /// ✅ Timer ləğv et (reconnect olduqda)
        public void CancelDisconnectTimer(int userId)
        {
            lock (StateLock)
            {
                if (_disconnectTimers.TryGetValue(userId, out var cts))
                {
                    cts.Cancel();
                    _disconnectTimers.Remove(userId);
                    Console.WriteLine($"✅ Timer cancelled for user {userId}");
                }
            }
        }

        /// ✅ Timeout olduqda - oyunçuyu remove et
        private void HandleDisconnectTimeout(int userId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.IsConnected)
                {
                    Console.WriteLine($"ℹ️ Player {userId} already reconnected or not found");
                    return;
                }

                Console.WriteLine($"❌ TIMEOUT: {player.Name} will be removed");

                // ✅ Oyun davam ediyorsa auto-fold et
                if (IsGameStarted && !IsRoundFinished)
                {
                    if (CurrentTurnUserId == userId)
                    {
                        // Varsa oyunun-kı turn bu oyunçudur
                        player.HasPassed = true;
                        Console.WriteLine($"⏰ {player.Name} auto-passed due to timeout");
                    }
                }
            }
        }

        /// ✅ Full game state - yalnız reconnect üçün
        public object GetFullStateFor(int userId)
        {
            lock (StateLock)
            {
                var player = Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return new { };

                // 🔥 ÖNƏMLİ: isMyTurn HESABLA
                bool isMyTurn = (userId == CurrentTurnUserId);

                return new
                {
                    roomId = RoomId,
                    roomName = RoomName,
                    gameType = GameType,
                    isGameStarted = IsGameStarted,
                    isRoundFinished = IsRoundFinished,
                    currentRound = CurrentRound,
                    scoreToWin = ScoreToWin,
                    currentTurnUserId = CurrentTurnUserId,
                    turnDeadlineUtc = TurnDeadlineUtc,
                    turnStartedAtUtc = TurnStartedAtUtc,
                    turnTimeRemaining = GetTurnTimeRemainingSeconds(),
                    turnDurationSeconds = TurnDurationSeconds,
                    isAutoPassTimer = IsAutoPassTurnTimer,

                    // 🔥 YENİ: isMyTurn əlavə et
                    isMyTurn = isMyTurn,

                    // Oyunçunun öz məlumatları
                    myHand = player.Hand.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                    myScore = player.Score,
                    hasPlayableTile = PlayerHasPlayableTile(player),
                    canDrawFromStock = CanDrawFromStockForPlayer(player),

                    // Zəncir məlumatları
                    chainTiles = Chain.Tiles.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                    leftEnd = Chain.LeftEnd,
                    rightEnd = Chain.RightEnd,
                    centerDouble = Chain.CenterDouble != null
                        ? new { Chain.CenterDouble.Left, Chain.CenterDouble.Right, Chain.CenterDouble.Id }
                        : null,
                    centerTopTiles = Chain.CenterTop.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                    centerBottomTiles = Chain.CenterBottom.Select(t => new { t.Left, t.Right, t.Id }).ToList(),
                    stockCount = Stock.Count,

                    // Digər oyunçular
                    players = Players.Select(p => new
                    {
                        userId = p.UserId,
                        name = p.Name,
                        tileCount = p.Hand.Count,
                        score = p.Score,
                        isConnected = p.IsConnected,
                        isSystemControlled = p.IsSystemControlled,
                        isCurrentTurn = p.UserId == CurrentTurnUserId
                    }).ToList()
                };
            }
        }
    }


    public class DominoPlayer
    {
        public string ConnectionId { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<DominoTile> Hand { get; set; } = new();
        public int Score { get; set; } = 0;
        public PlayerStatus Status { get; set; } = PlayerStatus.Waiting;
        public bool HasPassed { get; set; } = false;


        public bool IsConnected { get; set; } = true;
        public DateTime? DisconnectedAt { get; set; }
        public DateTime? DisconnectGraceDeadlineUtc { get; set; }
        public bool IsSystemControlled { get; set; } = false;
        public DateTime? SystemControlledAtUtc { get; set; }



        public void RemoveTile(string tileId)
        {
            var tile = Hand.FirstOrDefault(t => t.Id == tileId);
            if (tile != null) Hand.Remove(tile);
        }

        public int GetHandValue()
        {
            // Əgər əldə tək [0|0] varsa = 10 xal
            if (Hand.Count == 1 && Hand[0].Left == 0 && Hand[0].Right == 0)
            {
                return 10;
            }

            // Digər hallarda adi cəm
            return Hand.Sum(t => t.Left + t.Right);
        }
    }

    public enum PlayerStatus { Waiting, Playing, Passed }

    public class DominoTile
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public int Left { get; set; }
        public int Right { get; set; }

        public bool IsDouble => Left == Right;

    }

    public class DominoChain
    {
        public List<DominoTile> Tiles { get; set; } = new();
        public int? LeftEnd { get; set; }
        public int? RightEnd { get; set; }
        public DominoTile? CenterDouble { get; set; }
        public List<DominoTile> CenterTop { get; set; } = new();
        public List<DominoTile> CenterBottom { get; set; } = new();

        public void AddFirst(DominoTile tile)
        {
            Tiles.Add(tile);
            LeftEnd = tile.Left;
            RightEnd = tile.Right;

            // ✅ İlk daş double olsa belə spinner DEYİL (hər iki tərəfdə daş lazım)
            if (tile.Left == tile.Right)
            {
                Console.WriteLine($"🎯 First tile [{tile.Left}|{tile.Right}] is double - NOT spinner yet (need tiles on both sides)");
            }
        }

        public void AddLeft(DominoTile tile)
        {
            if (tile.Right == LeftEnd)
            {
                Tiles.Insert(0, tile);
                LeftEnd = tile.Left;
            }
            else if (tile.Left == LeftEnd)
            {
                (tile.Left, tile.Right) = (tile.Right, tile.Left);
                Tiles.Insert(0, tile);
                LeftEnd = tile.Left;
            }

            // ✅ SPINNER AKTIVASIYASI: Hər hansı double-ın HƏR İKİ tərəfində daş varsa
            ActivateSpinnerIfReady();
        }

        public void AddRight(DominoTile tile)
        {
            if (tile.Left == RightEnd)
            {
                Tiles.Add(tile);
                RightEnd = tile.Right;
            }
            else if (tile.Right == RightEnd)
            {
                (tile.Left, tile.Right) = (tile.Right, tile.Left);
                Tiles.Add(tile);
                RightEnd = tile.Right;
            }

            // ✅ SPINNER AKTIVASIYASI: Hər hansı double-ın HƏR İKİ tərəfində daş varsa
            ActivateSpinnerIfReady();
        }

        // 🔥 YENİ METOD: Double-ın hər iki tərəfində daş varsa spinner olaraq təyin et
        private void ActivateSpinnerIfReady()
        {
            if (CenterDouble != null) return; // Artıq spinner təyin olunub

            // Bütün double-ları yoxla
            for (int i = 1; i < Tiles.Count - 1; i++)
            {
                var tile = Tiles[i];
                if (tile.Left == tile.Right)
                {
                    // Hər iki tərəfində daş var - SPINNER!
                    CenterDouble = tile;
                    Console.WriteLine($"🎯 SPINNER ACTIVATED! [{tile.Left}|{tile.Right}] at index {i} (has tiles on BOTH sides)");
                    return;
                }
            }
        }

        public (bool canLeft, bool canRight, bool canCenterTop, bool canCenterBottom) CanPlace(DominoTile tile)
        {
            if (Tiles.Count == 0)
                return (false, true, false, false);

            bool canLeft = tile.Left == LeftEnd || tile.Right == LeftEnd;
            bool canRight = tile.Left == RightEnd || tile.Right == RightEnd;
            bool canCenterTop = false;
            bool canCenterBottom = false;

            // 🔥 Mərkəz double ortada olduqda aktiv
            if (CenterDouble != null)
            {
                int centerIndex = Tiles.FindIndex(t => t.Id == CenterDouble.Id);
                bool centerIsInMiddle = centerIndex > 0 && centerIndex < Tiles.Count - 1;

                if (centerIsInMiddle)
                {
                    // Top tərəf
                    if (CenterTop.Count == 0)
                    {
                        canCenterTop = tile.Left == CenterDouble.Left || tile.Right == CenterDouble.Left;
                    }
                    else
                    {
                        var lastTop = CenterTop[^1];
                        int topEnd = GetTopEnd(lastTop);
                        canCenterTop = tile.Left == topEnd || tile.Right == topEnd;
                    }

                    // Bottom tərəf
                    if (CenterBottom.Count == 0)
                    {
                        canCenterBottom = tile.Left == CenterDouble.Right || tile.Right == CenterDouble.Right;
                    }
                    else
                    {
                        var lastBottom = CenterBottom[^1];
                        int bottomEnd = GetBottomEnd(lastBottom);
                        canCenterBottom = tile.Left == bottomEnd || tile.Right == bottomEnd;
                    }

                    Console.WriteLine($"🔍 Spinner active at index {centerIndex}: top={canCenterTop}, bottom={canCenterBottom}");
                }
            }

            return (canLeft, canRight, canCenterTop, canCenterBottom);
        }

        public int GetTopEnd(DominoTile tile)
        {
            if (CenterTop.Count == 1)
            {
                return (tile.Left == CenterDouble!.Left) ? tile.Right : tile.Left;
            }

            var prevTop = CenterTop[CenterTop.Count - 2];
            if (tile.Left == prevTop.Left || tile.Left == prevTop.Right)
                return tile.Right;
            return tile.Left;
        }

        public int GetBottomEnd(DominoTile tile)
        {
            if (CenterBottom.Count == 1)
            {
                return (tile.Left == CenterDouble!.Right) ? tile.Right : tile.Left;
            }

            var prevBottom = CenterBottom[CenterBottom.Count - 2];
            if (tile.Left == prevBottom.Left || tile.Left == prevBottom.Right)
                return tile.Right;
            return tile.Left;
        }

        public void AddCenterTop(DominoTile tile)
        {
            if (CenterDouble == null) return;

            if (CenterTop.Count == 0)
            {
                if (tile.Right != CenterDouble.Left && tile.Left == CenterDouble.Left)
                {
                    (tile.Left, tile.Right) = (tile.Right, tile.Left);
                }
                CenterTop.Add(tile);
            }
            else
            {
                var lastTop = CenterTop[^1];
                int topEnd = GetTopEnd(lastTop);

                if (tile.Left != topEnd && tile.Right == topEnd)
                {
                    (tile.Left, tile.Right) = (tile.Right, tile.Left);
                }

                CenterTop.Add(tile);
            }

            Console.WriteLine($"✅ Added to CENTER TOP: [{tile.Left}|{tile.Right}] (Total: {CenterTop.Count})");
        }

        public void AddCenterBottom(DominoTile tile)
        {
            if (CenterDouble == null) return;

            if (CenterBottom.Count == 0)
            {
                if (tile.Right != CenterDouble.Right && tile.Left == CenterDouble.Right)
                {
                    (tile.Left, tile.Right) = (tile.Right, tile.Left);
                }
                CenterBottom.Add(tile);
            }
            else
            {
                var lastBottom = CenterBottom[^1];
                int bottomEnd = GetBottomEnd(lastBottom);

                if (tile.Left != bottomEnd && tile.Right == bottomEnd)
                {
                    (tile.Left, tile.Right) = (tile.Right, tile.Left);
                }

                CenterBottom.Add(tile);
            }

            Console.WriteLine($"✅ Added to CENTER BOTTOM: [{tile.Left}|{tile.Right}] (Total: {CenterBottom.Count})");
        }
    }
}


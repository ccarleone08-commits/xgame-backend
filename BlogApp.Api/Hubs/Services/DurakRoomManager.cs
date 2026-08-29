using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs.Services
{
    public class DurakRoomManager
    {
        private readonly IHubContext<DurakHub> _hubContext;
        private readonly Dictionary<int, string> _playerConnections = new();
        private readonly ConcurrentDictionary<string, DurakRoom> _rooms = new();
        private readonly ConcurrentDictionary<string, DurakRoom> _quickRooms = new();

        // ✅ YENİ STRUKTUR
        private readonly ConcurrentDictionary<string, RoomTemplate> _templates = new();
        private readonly ConcurrentDictionary<string, DurakRoom> _activeRooms = new();

        // ✅ Field əlavə et
        private Timer? _cleanupTimer;

        public DurakRoomManager(IHubContext<DurakHub> hubContext)
        {
            _hubContext = hubContext ?? throw new ArgumentNullException(nameof(hubContext));
            InitializeTemplates();
            StartCleanupTimer();
        }

        public class RoomTemplate
        {
            public string TemplateId { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
            public int MaxPlayers { get; set; }
            public int[] AvailableDeckSizes { get; set; } = { 24, 36, 52 };
            public decimal[] AvailableBets { get; set; }
            public GameMode[] AvailableGameModes { get; set; } = Array.Empty<GameMode>();
            public AttackMode[] AvailableAttackModes { get; set; } = Array.Empty<AttackMode>();
            public bool IsPassingAvailable { get; set; }
        }

        private void InitializeTemplates()
        {
            // 2 nəfərlik
            CreateTemplate(new RoomTemplate
            {
                TemplateId = "2P",
                Name = "2 Oyunçu",
                MaxPlayers = 2,
                AvailableDeckSizes = new[] { 24, 36, 52 },
                AvailableBets = new decimal[] { 0.5m, 1, 2, 5, 10, 20, 50, 100 },
                AvailableGameModes = new[] { GameMode.Classic, GameMode.Draw },
                AvailableAttackModes = new[] { AttackMode.Neighbour },
                IsPassingAvailable = true
            });

            // 3 nəfərlik
            CreateTemplate(new RoomTemplate
            {
                TemplateId = "3P",
                Name = "3 Oyunçu",
                MaxPlayers = 3,
                AvailableDeckSizes = new[] { 24, 36, 52 },
                AvailableBets = new decimal[] { 0.5m, 1, 2, 5, 10, 20, 50, 100 },
                AvailableGameModes = new[] { GameMode.Classic, GameMode.Draw },
                AvailableAttackModes = new[] { AttackMode.Neighbour },
                IsPassingAvailable = true
            });

            // 4 nəfərlik
            CreateTemplate(new RoomTemplate
            {
                TemplateId = "4P",
                Name = "4 Oyunçu",
                MaxPlayers = 4,
                AvailableDeckSizes = new[] { 24, 36, 52 },
                AvailableBets = new decimal[] { 0.5m, 1, 2, 5, 10, 20, 50, 100 },
                AvailableGameModes = new[] { GameMode.Classic, GameMode.Draw },
                AvailableAttackModes = new[] { AttackMode.All, AttackMode.Neighbour },
                IsPassingAvailable = true
            });

            // 6 nəfərlik
            CreateTemplate(new RoomTemplate
            {
                TemplateId = "6P",
                Name = "6 Oyunçu",
                MaxPlayers = 6,
                AvailableDeckSizes = new[] { 36, 52 },
                AvailableBets = new decimal[] { 0.5m, 1, 2, 5, 10, 20, 50, 100 },
                AvailableGameModes = new[] { GameMode.Classic, GameMode.Draw },
                AvailableAttackModes = new[] { AttackMode.All, AttackMode.Neighbour },
                IsPassingAvailable = true
            });

            Console.WriteLine($"✅ {_templates.Count} templates initialized");
        }

        private void CreateTemplate(RoomTemplate template)
        {
            _templates.TryAdd(template.TemplateId, template);
        }
        public RoomTemplate GetTemplate(int playerCount)
        {
            var template = _templates.Values.FirstOrDefault(t => t.MaxPlayers == playerCount);

            if (template == null)
            {
                Console.WriteLine($"⚠️ Template not found for {playerCount}P");
                return null;
            }

            Console.WriteLine($"✅ Template found: {template.Name} ({template.TemplateId})");
            return template;
        }
        public List<RoomTemplate> GetTemplates()
        {
            return _templates.Values.OrderBy(t => t.MaxPlayers).ToList();
        }

        private void CreateQuickRoom(
            string id,
            string name,
            int maxPlayers,
            decimal entryFee,
            int deckSize,
            GameSettings settings)
        {
            var room = new DurakRoom
            {
                RoomId = id,
                RoomName = name,
                CreatorUserId = 0,
                MaxPlayers = maxPlayers,
                EntryFee = entryFee,
                DeckSize = deckSize,
                GameSettings = settings,
                IsQuickRoom = true,
                CreatedAt = DateTime.Now
            };
            _quickRooms.TryAdd(room.RoomId, room);
        }

        /// <summary>
        /// Otaq yarat - YENİ PARAMETRLƏR
        /// </summary>
        public DurakRoom? CreateRoom(
            string roomName,
            int creatorUserId,
            int maxPlayers,
            decimal entryFee = 0,
            int deckSize = 36,
            AttackMode attackMode = AttackMode.All,
            bool isThrowInEnabled = true,
            bool isTransferEnabled = false,
            GameMode gameMode = GameMode.Classic)
        {
            var room = new DurakRoom
            {
                RoomId = Guid.NewGuid().ToString(),
                RoomName = roomName,
                CreatorUserId = creatorUserId,
                MaxPlayers = maxPlayers,
                EntryFee = entryFee,
                DeckSize = deckSize,
                GameSettings = new GameSettings
                {
                    AttackMode = attackMode,
                    IsThrowInEnabled = isThrowInEnabled,
                    IsTransferEnabled = isTransferEnabled,
                    GameMode = gameMode
                },
                IsQuickRoom = false,
                CreatedAt = DateTime.Now
            };

            if (_rooms.TryAdd(room.RoomId, room))
            {
                Console.WriteLine($"✅ Room created: {roomName}");
                Console.WriteLine($"   Players: {maxPlayers}");
                Console.WriteLine($"   Deck: {deckSize}");
                Console.WriteLine($"   Attack: {attackMode}");
                Console.WriteLine($"   Throw-in: {isThrowInEnabled}");
                Console.WriteLine($"   Transfer: {isTransferEnabled}");
                Console.WriteLine($"   Mode: {gameMode}");
                return room;
            }

            return null;
        }

        public class RoomSettings
        {
            public int Players { get; set; }
            public int DeckSize { get; set; }
            public decimal Bet { get; set; }
            public GameMode GameMode { get; set; }
            public AttackMode AttackMode { get; set; }
            public bool IsPassingEnabled { get; set; }
            public bool IsTransferEnabled { get; set; }
        }
        public DurakRoom? CreateRoomFromUserSelection(int creatorUserId, RoomSettings settings)
        {
            // Validation
            var template = _templates.Values.FirstOrDefault(t => t.MaxPlayers == settings.Players);
            if (template == null)
            {
                Console.WriteLine($"❌ Invalid player count: {settings.Players}");
                return null;
            }

            // ✅ Array.IndexOf istifadə et
            if (Array.IndexOf(template.AvailableDeckSizes, settings.DeckSize) == -1)
            {
                Console.WriteLine($"❌ Invalid deck size: {settings.DeckSize} for {settings.Players}P");
                return null;
            }

            if (Array.IndexOf(template.AvailableBets, settings.Bet) == -1)
            {
                Console.WriteLine($"❌ Invalid bet: {settings.Bet} for {settings.Players}P");
                return null;
            }

            if (Array.IndexOf(template.AvailableGameModes, settings.GameMode) == -1)
            {
                Console.WriteLine($"❌ Invalid game mode: {settings.GameMode} for {settings.Players}P");
                return null;
            }

            if (Array.IndexOf(template.AvailableAttackModes, settings.AttackMode) == -1)
            {
                Console.WriteLine($"❌ Invalid attack mode: {settings.AttackMode} for {settings.Players}P");
                return null;
            }

            if (settings.IsPassingEnabled && !template.IsPassingAvailable)
            {
                Console.WriteLine($"❌ Passing unavailable for {settings.Players}P");
                return null;
            }

            // Generate unique room ID
            string roomId = $"ROOM_{settings.Players}P_{settings.DeckSize}C_{settings.Bet}AZN_{Guid.NewGuid().ToString().Substring(0, 6)}";

            // Room name
            string gameModeText = settings.GameMode switch
            {
                GameMode.Classic => "Classic",
                GameMode.Draw => "Draw",
                _ => "Classic"
            };

            string attackModeText = settings.AttackMode switch
            {
                AttackMode.All => "All",
                AttackMode.Neighbour => "Neighbour",
                _ => "All"
            };

            bool isPassingEnabled = template.IsPassingAvailable && settings.IsPassingEnabled;
            string roomName = $"{settings.Players}P - {settings.DeckSize} kart ({settings.Bet} AZN) - {gameModeText} [{attackModeText}]";

            if (isPassingEnabled)
                roomName += " + Passing";

            // Create room
            var room = new DurakRoom
            {
                RoomId = roomId,
                RoomName = roomName,
                CreatorUserId = creatorUserId,
                MaxPlayers = settings.Players,
                EntryFee = settings.Bet,
                DeckSize = settings.DeckSize,
                GameSettings = new GameSettings
                {
                    GameMode = settings.GameMode,
                    AttackMode = settings.AttackMode,
                    IsThrowInEnabled = true,
                    IsTransferEnabled = settings.IsTransferEnabled,
                    IsPassingEnabled = isPassingEnabled
                },
                IsQuickRoom = false,
                CreatedAt = DateTime.UtcNow
            };

            if (_activeRooms.TryAdd(room.RoomId, room))
            {
                Console.WriteLine($"✅ Room created: {roomName}");
                Console.WriteLine($"   ID: {roomId}");
                Console.WriteLine($"   Creator: {creatorUserId}");
                return room;
            }

            return null;
        }
        public DurakRoom? GetRoom(string roomId)
        {
            return _activeRooms.TryGetValue(roomId, out var room) ? room : null;
        }

        public DurakRoom? GetRoomByPlayerUserId(int userId)
        {
            return _activeRooms.Values.FirstOrDefault(room =>
                room.Players.Any(player => player.UserId == userId));
        }

        public List<DurakRoomSummary> GetAvailableRooms()
        {
            return _activeRooms.Values
                .Where(r => !r.IsGameActive && r.PlayerCount < r.MaxPlayers)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new DurakRoomSummary
                {
                    RoomId = r.RoomId,
                    RoomName = r.RoomName,
                    MaxPlayers = r.MaxPlayers,
                    CurrentPlayers = r.PlayerCount,
                    EntryFee = r.EntryFee,
                    DeckSize = r.DeckSize,
                    GameMode = r.GameMode.ToString(),
                    AttackMode = r.GameSettings.AttackMode.ToString(),
                    CreatedAt = r.CreatedAt,
                    Players = r.Players.Select(p => p.Name).ToList()
                })
                .ToList();
        }

        public List<DurakRoomSummary> GetActiveGames()
        {
            return _activeRooms.Values
                .Where(r => r.IsGameActive)
                .OrderByDescending(r => r.CreatedAt)
                .Select(r => new DurakRoomSummary
                {
                    RoomId = r.RoomId,
                    RoomName = r.RoomName,
                    MaxPlayers = r.MaxPlayers,
                    CurrentPlayers = r.PlayerCount,
                    EntryFee = r.EntryFee,
                    DeckSize = r.DeckSize,
                    GameMode = r.GameMode.ToString(),
                    AttackMode = r.GameSettings.AttackMode.ToString(),
                    CreatedAt = r.CreatedAt,
                    Players = r.Players.Select(p => p.Name).ToList()
                })
                .ToList();
        }
        public List<DurakRoom> GetQuickRooms()
        {
            return _quickRooms.Values
                .OrderBy(r => r.MaxPlayers)
                .ThenBy(r => r.EntryFee)
                .ToList();
        }
        public IClientProxy GetUserConnection(int userId)
        {
            if (_playerConnections.TryGetValue(userId, out var connectionId))
            {
                return _hubContext.Clients.Client(connectionId);
            }
            throw new InvalidOperationException($"User {userId} not found");
        }

        public void Dispose()
        {
            _cleanupTimer?.Dispose();
        }
        public DurakRoom? CreateQuickRoomInstance(DurakRoom template)
        {
            var newRoom = new DurakRoom
            {
                RoomId = $"{template.RoomId}_{Guid.NewGuid().ToString().Substring(0, 8)}",
                RoomName = template.RoomName,
                CreatorUserId = 0,
                MaxPlayers = template.MaxPlayers,
                EntryFee = template.EntryFee,
                DeckSize = template.DeckSize,
                GameSettings = new GameSettings
                {
                    AttackMode = template.GameSettings.AttackMode,
                    IsThrowInEnabled = template.GameSettings.IsThrowInEnabled,
                    IsTransferEnabled = template.GameSettings.IsTransferEnabled,
                    GameMode = template.GameSettings.GameMode
                },
                IsQuickRoom = true,
                CreatedAt = DateTime.Now
            };

            if (_quickRooms.TryAdd(newRoom.RoomId, newRoom))
            {
                Console.WriteLine($"✅ Quick room instance created: {newRoom.RoomName}");
                return newRoom;
            }

            return null;
        }

        public bool AddPlayerToRoom(string roomId, DurakPlayer player)
        {
            var room = GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"❌ Room not found: {roomId}");
                return false;
            }

            lock (room.StateLock)
            {
                // Reconnect
                if (room.Players.Any(p => p.UserId == player.UserId))
                {
                    Console.WriteLine($"🔄 Player reconnecting: {player.Name}");
                    var existingPlayer = room.Players.First(p => p.UserId == player.UserId);
                    existingPlayer.ConnectionId = player.ConnectionId;
                    existingPlayer.IsDisconnected = false;
                    existingPlayer.DisconnectedAt = null;
                    return true;
                }

                // Game active
                if (room.IsGameActive)
                {
                    Console.WriteLine($"❌ Game already active");
                    return false;
                }

                // Room full
                if (room.Players.Count >= room.MaxPlayers)
                {
                    Console.WriteLine($"❌ Room full ({room.PlayerCount}/{room.MaxPlayers})");
                    return false;
                }

                room.Players.Add(player);
                Console.WriteLine($"✅ Player added: {player.Name} ({room.PlayerCount}/{room.MaxPlayers})");
                return true;
            }
        }

        public bool RemovePlayerFromRoom(string roomId, int userId)
        {
            var room = GetRoom(roomId);
            if (room == null) return false;

            lock (room.StateLock)
            {
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player != null)
                {
                    room.Players.Remove(player);
                    Console.WriteLine($"❌ Player removed: {player.Name} ({room.PlayerCount}/{room.MaxPlayers})");

                    // Boş otağı sil
                    if (room.Players.Count == 0)
                    {
                        DeleteRoom(roomId);
                        return true;
                    }

                    // Oyun aktivdirsə və az oyunçu qalıbsa
                    if (room.IsGameActive && room.Players.Count < 2)
                    {
                        room.ResetGame();
                        Console.WriteLine($"⚠️ Game stopped: not enough players");
                    }

                    return true;
                }
            }

            return false;
        }
        private void StartCleanupTimer()
        {
            // Hər 5 dəqiqədə bir köhnə otaqları təmizlə
            _cleanupTimer = new Timer(
                callback: _ => CleanupOldRooms(),
                state: null,
                dueTime: TimeSpan.FromMinutes(1),
                period: TimeSpan.FromMinutes(1)
            );
        }

        public bool DeleteRoom(string roomId)
        {
            if (_activeRooms.TryRemove(roomId, out var room))
            {
                Console.WriteLine($"🗑️ Room deleted: {room.RoomName}");
                return true;
            }
            return false;
        }


        public void CleanupOldRooms()
        {
            var roomsToDelete = _activeRooms.Values
                .Where(r =>
                    // Boş otaqlar 1 dəqiqədən köhnə
                    (r.PlayerCount == 0 && (DateTime.UtcNow - r.CreatedAt).TotalMinutes > 1) ||
                    // Bitməmiş oyunlar 2 saatdan köhnə
                    (!r.IsGameActive && r.PlayerCount > 0 && (DateTime.UtcNow - r.CreatedAt).TotalHours > 2) ||
                    // Bitmiş oyunlar
                    (r.GameEndTime.HasValue && (DateTime.UtcNow - r.GameEndTime.Value).TotalMinutes > 1)
                )
                .ToList();

            foreach (var room in roomsToDelete)
            {
                DeleteRoom(room.RoomId);
            }

            if (roomsToDelete.Count > 0)
            {
                Console.WriteLine($"🧹 Cleaned up {roomsToDelete.Count} old rooms");
            }
        }
        public class GameStateSummary
        {
            public string RoomId { get; set; } = string.Empty;
            public string RoomName { get; set; } = string.Empty;
            public string? AttackerName { get; set; }
            public string? DefenderName { get; set; }
            public List<PlayerGameState> Players { get; set; } = new();
            public int TableCardCount { get; set; }
            public int DefendedPairCount { get; set; }
            public int DeckCount { get; set; }
            public CardData? TrumpCard { get; set; }
            public bool IsThrowInPhaseActive { get; set; }
            public bool IsGameActive { get; set; }
            public string GameMode { get; set; } = "Classic";
            public string AttackMode { get; set; } = "All";
            public decimal TotalPrize { get; set; }
            public decimal EntryFee { get; set; }
        }
        public class DurakRoomSummary
        {
            public string RoomId { get; set; } = string.Empty;
            public string RoomName { get; set; } = string.Empty;
            public int MaxPlayers { get; set; }
            public int CurrentPlayers { get; set; }
            public decimal EntryFee { get; set; }
            public int DeckSize { get; set; }
            public string GameMode { get; set; } = string.Empty;
            public string AttackMode { get; set; } = string.Empty;
            public DateTime CreatedAt { get; set; }
            public List<string> Players { get; set; } = new();
        }

        public class PlayerGameState
        {
            public int UserId { get; set; }
            public string Name { get; set; } = string.Empty;
            public int CardCount { get; set; }
            public bool IsAttacker { get; set; }
            public bool IsDefender { get; set; }
        }

        public class CardData
        {
            public string Rank { get; set; } = string.Empty;
            public string Suit { get; set; } = string.Empty;
        }
        public object GetStatistics()
        {
            var activeRooms = _activeRooms.Values;

            return new
            {
                TotalRooms = activeRooms.Count,
                WaitingRooms = activeRooms.Count(r => !r.IsGameActive && r.PlayerCount < r.MaxPlayers),
                ActiveGames = activeRooms.Count(r => r.IsGameActive),
                TotalPlayers = activeRooms.Sum(r => r.PlayerCount),
                Templates = _templates.Count,

                RoomsByPlayers = activeRooms
                    .GroupBy(r => r.MaxPlayers)
                    .Select(g => new { Players = g.Key, Count = g.Count() })
                    .OrderBy(x => x.Players)
                    .ToList()
            };
        }
    }
}

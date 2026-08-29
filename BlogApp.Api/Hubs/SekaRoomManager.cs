using BlogApp.Core.Entities;
using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs
{
    public class SekaRoomManager
    {
        private readonly ConcurrentDictionary<string, SekaRoom> _rooms = new();

        // ✅ Otaq şablonları
        private readonly List<RoomTemplate> _roomTemplates = new()
            {
                new RoomTemplate { Name = "🔥 Mikro (NL)", Fee = 0.20m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "⚡ Mini (NL)", Fee = 0.50m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "⭐ Kiçik (NL)", Fee = 1m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "💎 Orta (NL)", Fee = 2m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "👑 Böyük (NL)", Fee = 5m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "💰 Premium (NL)", Fee = 10m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "💎 Lüks (NL)", Fee = 20m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "🌟 Ultra (NL)", Fee = 50m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit },
                new RoomTemplate { Name = "👑 VIP (NL)", Fee = 100m, MaxPlayers = 6, LimitType = RoomLimitType.NoLimit }

                //new RoomTemplate { Name = "🔥 Mikro (PL)", Fee = 0.20m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "⚡ MMini (PL)", Fee = 0.50m, MaxPlayers = 2, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "⭐ Kiçik (PL)", Fee = 1m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "💎 Orta (PL)", Fee = 2m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "👑 Böyük (PL)", Fee = 5m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "💰 Premium (PL)", Fee = 10m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "💎 Lüks (PL)", Fee = 20m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "🌟 Ultra (PL)", Fee = 50m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },
                //new RoomTemplate { Name = "👑 VIP (PL)", Fee = 100m, MaxPlayers = 5, LimitType = RoomLimitType.PotLimit },

            };
        private readonly ConcurrentDictionary<decimal, int> _roomCounters = new();

        public List<RoomTemplate> RoomTemplates => _roomTemplates;

        public SekaRoomManager()
        {
            InitializeDefaultRooms();
        }

        public void InitializeDefaultRooms()
        {
            foreach (var template in RoomTemplates)
            {
                _roomCounters[template.Fee] = 0;
                CreateRoomFromTemplate(template);
            }
            Console.WriteLine($"✅ {RoomTemplates.Count} default SEKA otaqları yaradıldı");
        }

        private SekaRoom CreateRoomFromTemplate(RoomTemplate template)
        {
            int counter = _roomCounters.AddOrUpdate(template.Fee, 1, (key, old) => old + 1);

            var room = new SekaRoom
            {
                RoomId = Guid.NewGuid().ToString(),
                RoomName = counter == 1 ? $"{template.Name} #1" : $"{template.Name} #{counter}",
                CreatorName = "2@#JDVoVs",
                CreatorUserId = 0,
                EntryFee = template.Fee,
                MaxPlayers = template.MaxPlayers,
                IsPrivate = false,
                Password = null,
                IsGameStarted = false,
                IsGameFinished = false,
                PotAmount = 0,
                CurrentBet = 0,
                TemplateKey = template.Fee,
                LimitType = template.LimitType,
                MinBuyIn = template.Fee, // ✅ Minimum = entry fee
                MaxBuyIn = template.Fee * 20, // ✅ Maksimum = entry fee x20
                RoomCreatedTime = DateTime.UtcNow // ✅ Otaq yaradılma vaxtı
            };

            _rooms.TryAdd(room.RoomId, room);
            Console.WriteLine($"📦 Created: {room.RoomName} (Fee: {room.EntryFee}₼, BuyIn: {room.MinBuyIn}-{room.MaxBuyIn}₼)");

            return room;
        }
        public SekaRoom? FindOrCreateSuitableRoom(decimal preferredFee, int userId)
        {
            var availableRoom = _rooms.Values
                .Where(r => r.EntryFee == preferredFee
                         && r.CreatorUserId == 0
                         && r.Players.Count < r.MaxPlayers
                         && !r.IsGameStarted
                         && !r.Players.Any(p => p.UserId == userId))
                .OrderBy(r => r.Players.Count)
                .FirstOrDefault();

            if (availableRoom != null)
            {
                Console.WriteLine($"✅ Found available: {availableRoom.RoomName}");
                return availableRoom;
            }

            var template = RoomTemplates.FirstOrDefault(t => t.Fee == preferredFee);
            if (template != null)
            {
                var newRoom = CreateRoomFromTemplate(template);
                Console.WriteLine($"🆕 Created new: {newRoom.RoomName}");
                return newRoom;
            }

            Console.WriteLine($"❌ No template for fee: {preferredFee}₼");
            return null;
        }

        public SekaRoom? GetRoom(string roomId)
        {
            _rooms.TryGetValue(roomId, out var room);
            return room;
        }

        public List<SekaRoom> GetAllRooms()
        {
            return _rooms.Values
                .OrderBy(r => r.EntryFee)
                .ThenBy(r => r.RoomName)
                .ToList();
        }

        public List<RoomTemplate> GetRoomTemplates()
        {
            return RoomTemplates.ToList();
        }

        public bool AddPlayerToRoom(string roomId, SekaPlayer player, string? password)
        {
            var room = GetRoom(roomId);
            if (room == null)
                return false;

            lock (room.StateLock)
            {
                if (room.Players.Count >= room.MaxPlayers)
                {
                    Console.WriteLine($"❌ Room full: {room.RoomName}");
                    return false;
                }

                if (room.IsPrivate && room.Password != password)
                {
                    Console.WriteLine($"❌ Wrong password: {room.RoomName}");
                    return false;
                }

                if (room.Players.Any(p => p.UserId == player.UserId))
                {
                    Console.WriteLine($"❌ Player already in room: {player.Name}");
                    return false;
                }

                room.Players.Add(player);
                Console.WriteLine($"✅ Added: {player.Name} → {room.RoomName} ({room.Players.Count}/{room.MaxPlayers})");

                return true;
            }
        }

        public bool RemovePlayerFromRoom(string roomId, int userId)
        {
            var room = GetRoom(roomId);
            if (room == null)
                return false;

            lock (room.StateLock)
            {
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player != null)
                {
                    room.Players.Remove(player);
                    Console.WriteLine($"🚪 Removed: {player.Name} from {room.RoomName}");
                    return true;
                }
            }

            return false;
        }

        public void DeleteRoom(string roomId)
        {
            if (_rooms.TryRemove(roomId, out var room))
            {
                Console.WriteLine($"🗑️ Deleted: {room.RoomName}");
            }
        }

        public void CheckAndCreateNewRoomIfNeeded(string roomId)
        {
            var room = GetRoom(roomId);
            if (room == null || room.CreatorUserId != 0)
                return;

            if (room.Players.Count >= room.MaxPlayers || room.IsGameStarted)
            {
                var hasAvailable = _rooms.Values.Any(r =>
                    r.EntryFee == room.EntryFee
                    && r.CreatorUserId == 0
                    && r.Players.Count < r.MaxPlayers
                    && !r.IsGameStarted);

                if (!hasAvailable && room.TemplateKey.HasValue)
                {
                    var template = RoomTemplates.FirstOrDefault(t => t.Fee == room.TemplateKey.Value);
                    if (template != null)
                    {
                        CreateRoomFromTemplate(template);
                        Console.WriteLine($"🔄 Auto-created new room for {room.EntryFee}₼");
                    }
                }
            }
        }
    }

    public class RoomTemplate
    {
        public string Name { get; set; } = "";
        public decimal Fee { get; set; }
        public int MaxPlayers { get; set; }
        public RoomLimitType LimitType { get; set; } = RoomLimitType.NoLimit; // ✅ YENİ
    }
}
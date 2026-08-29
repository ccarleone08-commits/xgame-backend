using BlogApp.Api.Hubs.Services.BlogApp.Api.Hubs.Services;
using BlogApp.Core.Entities;
using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs.Services
{
    public class LotoRoomManager
    {
        private readonly ConcurrentDictionary<string, LotoRoom> _rooms = new();
        private readonly BotManager _botManager;
        private readonly BotBudgetService _budgetService;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _timerTasks = new();

        // ✅ Yenilənmiş konfiqurasiya: MaxTicketsPerPlayer = 6, MaxPlayers = 100
        private static readonly Dictionary<decimal, RoomConfig> RoomConfigs = new()
        {
            { 0.20m, new RoomConfig { EntryFee = 0.20m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = true } },
            { 0.50m, new RoomConfig { EntryFee = 0.50m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = true } },
            { 1.00m, new RoomConfig { EntryFee = 1.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 2.00m, new RoomConfig { EntryFee = 2.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 5.00m, new RoomConfig { EntryFee = 5.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 10.00m, new RoomConfig { EntryFee = 10.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 20.00m, new RoomConfig { EntryFee = 20.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 50.00m, new RoomConfig { EntryFee = 50.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 120, MaxTicketsPerPlayer = 6, RequiresFullCard = false } },
            { 100.00m, new RoomConfig { EntryFee = 100.00m, MinPlayers=25, MaxPlayers = 100, TimerSeconds = 100, MaxTicketsPerPlayer = 6, RequiresFullCard = false } }
        };

        public LotoRoomManager(BotManager botManager, BotBudgetService budgetService)
        {
            _botManager = botManager ?? throw new ArgumentNullException(nameof(botManager));
            _budgetService = budgetService ?? throw new ArgumentNullException(nameof(budgetService));
            InitializeFixedRooms();
            Console.WriteLine("✅ LotoRoomManager initialized (Max: 6 bilet/oyunçu, 100 bilet/otaq, 120s timer)");
        }

        private void InitializeFixedRooms()
        {
            foreach (var config in RoomConfigs)
            {
                var room = new LotoRoom
                {
                    RoomId = $"room_{config.Key:F2}",
                    RoomName = $"{config.Key} Coin Otaq",
                    CreatorName = "System",
                    CreatorUserId = 0,
                    EntryFee = config.Value.EntryFee,
                    MaxPlayers = config.Value.MaxPlayers,
                    MinPlayers = config.Value.MinPlayers,
                    MaxTicketsPerPlayer = config.Value.MaxTicketsPerPlayer,
                    TimerSeconds = config.Value.TimerSeconds,
                    IsPrivate = false,
                    Password = null,
                    IsFixedRoom = true,
                    JackpotPool = 0,
                    RequiresFullCard = config.Value.RequiresFullCard,
                    RoomCreatedTime = DateTime.UtcNow // ⚡ BU SƏTRİ ƏLAVƏ ET
                };

                _rooms.TryAdd(room.RoomId, room);
                string winRule = room.RequiresFullCard ? "TAM KART" : "BİR XƏTT";
                Console.WriteLine($"🏠 Fixed room: {room.RoomName} (Qayda: {winRule}, Timer: {room.TimerSeconds}s)");
            }
        }
        public LotoRoom? GetRoom(string roomId)
        {
            _rooms.TryGetValue(roomId, out var room);
            return room;
        }

        public List<RoomListItems> GetAvailableRooms()
        {
            return _rooms.Values
                .Where(r => !r.IsPrivate && !r.IsGameFinished)
                .OrderBy(r => r.EntryFee)
                .Select(r => new RoomListItems
                {
                    RoomId = r.RoomId,
                    RoomName = r.RoomName,
                    CreatorName = r.CreatorName,
                    PlayerCount = r.Players.Count,
                    MaxPlayers = r.MaxPlayers,
                    MinPlayers = r.MinPlayers,
                    EntryFee = r.EntryFee,
                    IsGameStarted = r.IsGameStarted,
                    IsPrivate = r.IsPrivate,
                    JackpotPool = r.JackpotPool,
                    TimerSeconds = r.TimerSeconds,
                    TimeRemaining = r.GetTimeRemaining()
                })
                .ToList();
        }

        public bool AddPlayerToRoom(string roomId, RoomPlayer player, string? password = null)
        {
            var room = GetRoom(roomId);
            if (room == null) return false;

            lock (room.StateLock)
            {
                if (room.Players.Count >= room.MaxPlayers)
                {
                    Console.WriteLine($"❌ Room full: {room.RoomName}");
                    return false;
                }

                if (room.IsGameStarted)
                {
                    Console.WriteLine($"❌ Game started: {room.RoomName}");
                    return false;
                }

                if (room.IsPrivate && room.Password != password)
                {
                    Console.WriteLine($"❌ Wrong password: {room.RoomName}");
                    return false;
                }

                var existingTickets = room.Players.Count(p => p.UserId == player.UserId);
                if (existingTickets >= room.MaxTicketsPerPlayer)
                {
                    Console.WriteLine($"❌ Max {room.MaxTicketsPerPlayer} tickets: {player.Name}");
                    return false;
                }

                room.Players.Add(player);
                room.JackpotPool += room.EntryFee;

                Console.WriteLine($"✅ Ticket bought: {player.Name} → {room.RoomName} (#{existingTickets + 1}/{room.MaxTicketsPerPlayer}, Total: {room.Players.Count}/{room.MaxPlayers})");

                // ✅ İLK oyunçu girdikdə timer başlat
                if (room.Players.Count == 1)
                {
                    room.RoomCreatedTime = DateTime.UtcNow; // ✅ Birbaşa set et
                    Console.WriteLine($"⏰ TIMER BAŞLADI: {room.RoomName} - {room.TimerSeconds} saniyə (Created: {room.RoomCreatedTime:HH:mm:ss})");
                }

                return true;
            }
        }
        //private void StartTimerMonitoring(string roomId)
        //{
        //    if (_timerTasks.ContainsKey(roomId))
        //    {
        //        return; // Artıq işləyir
        //    }

        //    var cts = new CancellationTokenSource();
        //    _timerTasks[roomId] = cts;

        //    _ = Task.Run(async () =>
        //    {
        //        try
        //        {
        //            while (!cts.Token.IsCancellationRequested)
        //            {
        //                await Task.Delay(1000, cts.Token);

        //                var room = GetRoom(roomId);
        //                if (room == null || room.IsGameStarted)
        //                {
        //                    break;
        //                }

        //                var remaining = room.GetTimeRemaining();

        //                // Timer bitdi VƏ minimum oyunçu var
        //                if (remaining <= 0 && room.Players.Count >= room.MinPlayers)
        //                {
        //                    Console.WriteLine($"⏰ TIMER BİTDİ: {room.RoomName} → Oyun başlamalıdır!");
        //                    break;
        //                }

        //                // 100 bilet doldu
        //                if (room.Players.Count >= 100)
        //                {
        //                    Console.WriteLine($"💯 100 bilet doldu: {room.RoomName} → Oyun başlamalıdır!");
        //                    break;
        //                }
        //            }
        //        }
        //        catch (TaskCanceledException) { }
        //        finally
        //        {
        //            _timerTasks.TryRemove(roomId, out _);
        //        }
        //    }, cts.Token);
        //}

        public bool RemovePlayerTicket(string roomId, int userId, string ticketId)
        {
            var room = GetRoom(roomId);
            if (room == null) return false;

            lock (room.StateLock)
            {
                var ticket = room.Players.FirstOrDefault(p => p.UserId == userId && p.TicketId == ticketId);
                if (ticket != null)
                {
                    room.Players.Remove(ticket);
                    room.JackpotPool -= room.EntryFee;
                    Console.WriteLine($"🗑️ Ticket deleted: {ticket.Name} - {ticketId}");

                    if (ticket.IsBot)
                    {
                        _botManager.RemoveBotTicket(roomId, ticketId);
                    }

                    if (room.Players.Count == 0 && !room.IsFixedRoom)
                    {
                        DeleteRoom(roomId);
                    }

                    return true;
                }
            }

            return false;
        }

        public bool DeleteRoom(string roomId)
        {
            var room = GetRoom(roomId);
            if (room == null) return false;

            if (room.IsFixedRoom)
            {
                Console.WriteLine($"⚠️ Cannot delete fixed room: {room.RoomName}");
                return false;
            }

            if (_rooms.TryRemove(roomId, out var removedRoom))
            {
                removedRoom.AutoDrawCts?.Cancel();
                removedRoom.AutoDrawCts?.Dispose();
                removedRoom.TimerCts?.Cancel();
                removedRoom.TimerCts?.Dispose();

                if (_timerTasks.TryRemove(roomId, out var timerCts))
                {
                    timerCts.Cancel();
                    timerCts.Dispose();
                }

                Console.WriteLine($"🗑️ Room deleted: {removedRoom.RoomName}");
                return true;
            }
            return false;
        }

        public void ResetFixedRoom(string roomId)
        {
            var room = GetRoom(roomId);
            if (room == null || !room.IsFixedRoom) return;

            lock (room.StateLock)
            {
                _botManager.ClearRoomBots(roomId);

                room.Players.Clear();
                room.DrawnNumbers.Clear();
                room.NumbersQueue = null;
                room.IsGameStarted = false;
                room.IsGameFinished = false;
                room.JackpotPool = 0;
                room.GameStartTime = null;

                // ✅ CRITICAL: Timer-i null et ki, növbəti oyunçu yeni timer başlatsın
                room.RoomCreatedTime = null;

                Console.WriteLine($"   ⏰ Timer sıfırlandı: RoomCreatedTime = null");

                room.Winners.Clear();
                room.BotsAdded = false; // ✅ Bot flag-ını sıfırla
                room.WinningTicket = null;

                room.AutoDrawCts?.Cancel();
                room.AutoDrawCts?.Dispose();
                room.AutoDrawCts = null;

                room.TimerCts?.Cancel();
                room.TimerCts?.Dispose();
                room.TimerCts = null;

                if (_timerTasks.TryRemove(roomId, out var timerCts))
                {
                    timerCts.Cancel();
                    timerCts.Dispose();
                }

                Console.WriteLine($"🔄 Fixed room reset: {room.RoomName}");
            }
        }
        public int GetRoomCount() => _rooms.Count;
        public int GetTotalPlayers() => _rooms.Values.Sum(r => r.Players.Count);
    }

    public class RoomConfig
    {
        public decimal EntryFee { get; set; }
        public int MaxPlayers { get; set; }
        public int MinPlayers { get; set; }
        public int TimerSeconds { get; set; }
        public int MaxTicketsPerPlayer { get; set; }
        public bool RequiresFullCard { get; set; }
    }
}

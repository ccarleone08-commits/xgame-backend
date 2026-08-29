using BlogApp.Core.Entities;
using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs.Services
{
    public class BotManager
    {
        private readonly Random _random = new();
        private readonly ConcurrentDictionary<string, List<BotPlayer>> _roomBots = new();
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _roomTimers = new();
        private static int _botIdCounter = 1;

        private readonly List<string> _botNames = new()
        {
            "Emre", "Zeynep", "Mert", "Elif", "Burak", "Ayşe",
            "Can", "Merve", "Kaan", "Ece", "Onur", "Büşra",
            "Furkan", "Seda", "Umut", "Yasemin", "Kerem", "Esra",
            "Oğuz", "Derya", "Serkan", "Melis", "Tolga", "İrem",
            "Batuhan", "Tuğçe", "Barış", "Cansu", "Hakan", "Özge"
        };

        public class BotPlayer
        {
            public int BotId { get; set; }
            public string Name { get; set; } = "";
            public List<string> TicketIds { get; set; } = new();
        }

        public int GetBotCountForRoom(decimal entryFee)
        {
            return 3;
        }

        // ✅ Bir bot sessiyası insan kimi 1-4 bilet ala bilər
        public async Task<int> AddBotsGradually(
            LotoRoom room,
            Func<RoomPlayer, Task> onBotAdded,
            Func<Task> onBatchComplete,
            int? customBotCount = null)
        {
            var availableSlots = Math.Max(0, room.MaxPlayers - room.Players.Count);
            var neededTickets = customBotCount ?? availableSlots;

            if (neededTickets <= 0)
            {
                Console.WriteLine($"⚠️ Bot əlavə etməyə ehtiyac yoxdur: {room.RoomName}");
                return 0;
            }

            neededTickets = Math.Min(neededTickets, Math.Min(availableSlots, room.MaxTicketsPerPlayer));

            var botId = Interlocked.Increment(ref _botIdCounter);
            var random = new Random();
            var botName = _botNames[random.Next(_botNames.Count)];
            var bot = new BotPlayer
            {
                BotId = botId,
                Name = botName
            };

            Console.WriteLine($"🤖 Bot sessiyası: {botName} {neededTickets} bilet alır → {room.RoomName}");

            var addedCount = 0;

            for (int i = 0; i < neededTickets; i++)
            {
                if (room.Players.Count >= room.MaxPlayers)
                {
                    Console.WriteLine($"⚠️ Otaq dolu ({room.MaxPlayers} bilet), bot əlavəsi dayandı");
                    break;
                }

                var ticket = new RoomPlayer
                {
                    ConnectionId = $"bot_{botId}_{Guid.NewGuid()}",
                    UserId = -botId,
                    Name = botName,
                    IsBot = true,
                    Balance = 0,
                    TicketId = Guid.NewGuid().ToString()
                };

                lock (room.StateLock)
                {
                    if (room.IsGameStarted || room.Players.Count >= room.MaxPlayers)
                    {
                        break;
                    }

                    room.Players.Add(ticket);
                    room.JackpotPool += room.EntryFee;
                }

                // ✅ Callback - card yaradılır və client-ə göndərilir
                if (onBotAdded != null)
                {
                    await onBotAdded(ticket);
                }

                bot.TicketIds.Add(ticket.TicketId);
                addedCount++;
                Console.WriteLine($"   ✅ {botName} ({addedCount}/{neededTickets} bilet)");

                await Task.Delay(Random.Shared.Next(350, 1450));
            }

            if (bot.TicketIds.Count > 0)
            {
                _roomBots.AddOrUpdate(
                    room.RoomId,
                    _ => new List<BotPlayer> { bot },
                    (_, bots) =>
                    {
                        lock (bots)
                        {
                            bots.Add(bot);
                            return bots;
                        }
                    });
            }

            // ✅ Callback - room update
            if (onBatchComplete != null)
            {
                await onBatchComplete();
            }

            Console.WriteLine($"✅ Bot əlavəsi tamamlandı: {addedCount} bilet, {room.Players.Count} ümumi");
            return addedCount;
        }

        // Köhnə metod - saxla (başqa yerdə istifadə olunarsa)
        public async Task<List<RoomPlayer>> AddBotsToRoom(
            LotoRoom room,
            Func<Task<bool>> addPlayerCallback)
        {
            var botPlayers = new List<RoomPlayer>();
            var botCount = GetBotCountForRoom(room.EntryFee);

            Console.WriteLine($"🤖 Adding {botCount} bots to {room.RoomName}");

            var usedNames = new HashSet<string>();
            var bots = new List<BotPlayer>();

            int totalBotTickets = 0;
            const int MAX_TOTAL_BOT_TICKETS = 25;

            for (int i = 0; i < botCount; i++)
            {
                string botName;
                do
                {
                    botName = _botNames[_random.Next(_botNames.Count())] + " (Bot)";
                } while (usedNames.Contains(botName));

                usedNames.Add(botName);

                var bot = new BotPlayer
                {
                    BotId = _botIdCounter++,
                    Name = botName,
                    TicketIds = new List<string>()
                };

                int maxTicketsForThisBot = Math.Min(10, MAX_TOTAL_BOT_TICKETS - totalBotTickets);
                if (maxTicketsForThisBot <= 0) maxTicketsForThisBot = 1;

                int ticketCount = _random.Next(1, maxTicketsForThisBot + 1);
                totalBotTickets += ticketCount;

                for (int t = 0; t < ticketCount; t++)
                {
                    var ticket = new RoomPlayer
                    {
                        ConnectionId = $"bot_{bot.BotId}_{Guid.NewGuid()}",
                        UserId = -bot.BotId,
                        Name = bot.Name,
                        Balance = 0,
                        Card = LotoHub.LotoCardGenerator.GenerateCard(),
                        TicketId = Guid.NewGuid().ToString(),
                        IsBot = true
                    };

                    lock (room.StateLock)
                    {
                        room.Players.Add(ticket);
                        room.JackpotPool += room.EntryFee;
                    }

                    bot.TicketIds.Add(ticket.TicketId);
                    botPlayers.Add(ticket);

                    await Task.Delay(50);
                }

                bots.Add(bot);
                Console.WriteLine($"   🤖 {bot.Name} → {ticketCount} bilet");

                if (totalBotTickets >= MAX_TOTAL_BOT_TICKETS)
                {
                    Console.WriteLine($"   ⚠️ Bot bilet limiti doldu: {totalBotTickets}/{MAX_TOTAL_BOT_TICKETS}");
                    break;
                }
            }

            _roomBots[room.RoomId] = bots;
            Console.WriteLine($"🎫 {totalBotTickets} bot tickets added to {room.RoomName}");
            return botPlayers;
        }

        public bool RemoveBotTicket(string roomId, string ticketId)
        {
            if (!_roomBots.TryGetValue(roomId, out var bots))
                return false;

            foreach (var bot in bots)
            {
                if (bot.TicketIds.Remove(ticketId))
                {
                    if (bot.TicketIds.Count == 0)
                    {
                        bots.Remove(bot);
                    }
                    return true;
                }
            }

            return false;
        }

        public void ClearRoomBots(string roomId)
        {
            _roomBots.TryRemove(roomId, out _);

            // Timer-i də dayandır
            if (_roomTimers.TryRemove(roomId, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        public int GetBotCountInRoom(string roomId)
        {
            if (!_roomBots.TryGetValue(roomId, out var bots))
                return 0;
            return bots.Sum(b => b.TicketIds.Count);
        }
    }
}

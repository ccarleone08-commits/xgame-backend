using System.Collections.Concurrent;

namespace BlogApp.Core.Entities
{
    public class BackgammonPlayer
    {
        public string ConnectionId { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; }
        public string Name { get; set; }
        public decimal Balance { get; set; }
        public string Color { get; set; } // "white" or "black"
    }

    public class BackgammonRoom
    {
        public string RoomId { get; set; }
        public string RoomName { get; set; }
        public string CreatorName { get; set; }

        public string UserName { get; set; }

        public int CreatorUserId { get; set; }
        public decimal BetAmount { get; set; }
        public List<BackgammonPlayer> Players { get; set; } = new();
        public bool IsGameStarted { get; set; }
        public bool IsGameFinished { get; set; }
        public Dictionary<int, List<string>> Board { get; set; } = new();

        // BAR (vurulmuş daşlar üçün)
        public Dictionary<string, int> Bar { get; set; } = new()
        {
            { "white", 0 },
            { "black", 0 }
        };

        // HOME (çıxarılmış daşlar)
        public Dictionary<string, int> Home { get; set; } = new()
        {
            { "white", 0 },
            { "black", 0 }
        };

        public List<int> Dice { get; set; } = new();
        public List<int> RemainingMoves { get; set; } = new();
        public int CurrentPlayerIndex { get; set; } = 0;

        // JSON üçün serialize
        public object GetBoardForJson()
        {
            var boardDict = new Dictionary<string, List<string>>();
            foreach (var kvp in Board)
            {
                boardDict[kvp.Key.ToString()] = kvp.Value;
            }

            // BAR və HOME məlumatını da göndər
            var result = new
            {
                points = boardDict,
                bar = new Dictionary<string, int>
                {
                    { "white", Bar.ContainsKey("white") ? Bar["white"] : 0 },
                    { "black", Bar.ContainsKey("black") ? Bar["black"] : 0 }
                },
                home = new Dictionary<string, int>
                {
                    { "white", Home.ContainsKey("white") ? Home["white"] : 0 },
                    { "black", Home.ContainsKey("black") ? Home["black"] : 0 }
                }
            };

            System.Console.WriteLine($"📤 GetBoardForJson called:");
            System.Console.WriteLine($"   BAR: white={result.bar["white"]}, black={result.bar["black"]}");
            System.Console.WriteLine($"   HOME: white={result.home["white"]}, black={result.home["black"]}");
            System.Console.WriteLine($"   JSON: {System.Text.Json.JsonSerializer.Serialize(result)}");

            return result;
        }
    }

    public class BackgammonRoomManager
    {
        private readonly ConcurrentDictionary<string, BackgammonRoom> _rooms = new();
        private static int _autoRoomCounter = 1;

        // Auto matching üçün room yarat
        public BackgammonRoom CreateAutoRoom(string creatorName, int creatorUserId, decimal betAmount)
        {
            var room = new BackgammonRoom
            {
                RoomId = Guid.NewGuid().ToString(),
                RoomName = $"Otaq #{_autoRoomCounter++}",
                CreatorName = creatorName,
                CreatorUserId = creatorUserId,
                BetAmount = betAmount,
                IsGameStarted = false,
                IsGameFinished = false
            };
            _rooms[room.RoomId] = room;
            return room;
        }

        // Müəyyən mərc üçün boş otaq tap
        public BackgammonRoom? GetAvailableRoomForBet(decimal betAmount)
        {
            return _rooms.Values
                .FirstOrDefault(r => !r.IsGameStarted
                                  && r.Players.Count == 1
                                  && r.BetAmount == betAmount);
        }

        public BackgammonRoom GetRoom(string roomId)
        {
            _rooms.TryGetValue(roomId, out var room);
            return room;
        }

        public List<BackgammonRoom> GetAllRooms()
        {
            return _rooms.Values
                .OrderByDescending(r => r.BetAmount)
                .ToList();
        }

        public void DeleteRoom(string roomId)
        {
            _rooms.TryRemove(roomId, out _);
        }
    }
}
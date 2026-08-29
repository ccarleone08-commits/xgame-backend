namespace BlogApp.Core.Entities
{
    public class DominoTile
    {
        public int Left { get; set; }
        public int Right { get; set; }
        public string Id { get; set; }

        public DominoTile(int left, int right)
        {
            Left = left;
            Right = right;
            Id = Guid.NewGuid().ToString("N")[..8];
        }

        public DominoTile Flip()
        {
            return new DominoTile(Right, Left) { Id = this.Id };
        }

        public bool Equals(DominoTile other)
        {
            if (other == null) return false;
            return (Left == other.Left && Right == other.Right) ||
                   (Left == other.Right && Right == other.Left);
        }

        public bool IsDouble => Left == Right;
        public bool IsBlank => Left == 0 && Right == 0;

        public override string ToString() => $"[{Left}|{Right}]";
    }

    // ========== OYUN NÖVLƏRİ ==========
    public enum DominoGameType
    {
        Classic101,
        Quick5,
        PhoneDomino
    }

    // ========== OYUNÇU STATUSu ==========
    public enum PlayerStatus
    {
        Waiting,
        Playing,
        Passed,
        Finished
    }

    // ========== OYUNÇU ==========
    public class DominoPlayer
    {
        public string ConnectionId { get; set; }
        public int UserId { get; set; }
        public string Name { get; set; }
        public List<DominoTile> Hand { get; set; } = new();
        public PlayerStatus Status { get; set; } = PlayerStatus.Waiting;
        public int Score { get; set; } = 0; // ✅ Toplam xal (101-ə qədər)
        public bool HasPassed { get; set; } = false;
        public DateTime LastActionTime { get; set; } = DateTime.UtcNow;

        public int GetHandValue()
        {
            return Hand.Sum(t => t.Left + t.Right);
        }

        public bool HasTile(string tileId)
        {
            return Hand.Any(t => t.Id == tileId);
        }

        public DominoTile? RemoveTile(string tileId)
        {
            var tile = Hand.FirstOrDefault(t => t.Id == tileId);
            if (tile != null)
            {
                Hand.Remove(tile);
            }
            return tile;
        }
    }

    // ========== MASA (CHAIN) ==========
    public class DominoChain
    {
        public List<DominoTile> Tiles { get; set; } = new();

        public int? LeftEnd => Tiles.Count == 0 ? null : Tiles.First().Left;
        public int? RightEnd => Tiles.Count == 0 ? null : Tiles.Last().Right;

        public void AddLeft(DominoTile tile, bool flip)
        {
            var toAdd = flip ? tile.Flip() : tile;

            if (Tiles.Count > 0 && toAdd.Right != LeftEnd)
            {
                Console.WriteLine($"⚠️ AddLeft auto-flip: {toAdd} → LeftEnd={LeftEnd}");
                toAdd = toAdd.Flip();
            }

            Tiles.Insert(0, toAdd);
            Console.WriteLine($"✅ Chain: {string.Join("-", Tiles.Select(t => $"{t}"))}");
        }

        public void AddRight(DominoTile tile, bool flip)
        {
            var toAdd = flip ? tile.Flip() : tile;

            if (Tiles.Count > 0 && toAdd.Left != RightEnd)
            {
                Console.WriteLine($"⚠️ AddRight auto-flip: {toAdd} → RightEnd={RightEnd}");
                toAdd = toAdd.Flip();
            }

            Tiles.Add(toAdd);
            Console.WriteLine($"✅ Chain: {string.Join("-", Tiles.Select(t => $"{t}"))}");
        }

        public (bool canLeft, bool canRight) CanPlace(DominoTile tile)
        {
            if (Tiles.Count == 0)
                return (true, true);

            bool canLeft = tile.Left == LeftEnd || tile.Right == LeftEnd;
            bool canRight = tile.Left == RightEnd || tile.Right == RightEnd;

            return (canLeft, canRight);
        }
    }

    // ========== ROOM (FIXED - 101 QAYDALAR) ==========
    public class DominoRoom
    {
        public string RoomId { get; set; } = Guid.NewGuid().ToString();
        public string RoomName { get; set; }
        public string CreatorName { get; set; }
        public int CreatorUserId { get; set; }
        public DominoGameType GameType { get; set; } = DominoGameType.Classic101;
        public decimal EntryFee { get; set; } = 10m;
        public int MaxPlayers { get; set; } = 4;
        public bool IsPrivate { get; set; }
        public string? Password { get; set; }
        public List<DominoPlayer> Players { get; set; } = new();
        public int CurrentPlayerIndex { get; set; } = 0;
        public bool IsGameStarted { get; set; }

        public int RoundNumber { get; set; } = 1;
        // ✅ RAUND SİSTEMİ
        public int CurrentRound { get; set; } = 1;
        public bool IsRoundFinished { get; set; }
        public DominoPlayer? RoundWinner { get; set; }

        public DominoChain Chain { get; set; } = new();
        public List<DominoTile> Stock { get; set; } = new();
        public DominoPlayer? Winner { get; set; } // Final winner (101+ xal)
        public object StateLock { get; set; } = new();

        public int TilesPerPlayer => GameType switch
        {
            DominoGameType.Classic101 => 7,
            DominoGameType.Quick5 => 5,
            DominoGameType.PhoneDomino => 7,
            _ => 7
        };

        public string? CurrentPlayerId => Players.Count > 0 && CurrentPlayerIndex < Players.Count
            ? Players[CurrentPlayerIndex].ConnectionId
            : null;

        public void NextTurn()
        {
            CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;

            if (Players.All(p => p.HasPassed))
            {
                foreach (var p in Players)
                {
                    p.HasPassed = false;
                }
            }
        }

        public DominoPlayer? GetCurrentPlayer()
        {
            if (CurrentPlayerIndex >= 0 && CurrentPlayerIndex < Players.Count)
                return Players[CurrentPlayerIndex];
            return null;
        }

        public DominoPlayer? GetPlayer(string connectionId)
        {
            return Players.FirstOrDefault(p => p.ConnectionId == connectionId);
        }

        public bool IsFull => Players.Count >= MaxPlayers;
        public bool CanStartGame => Players.Count >= 2 && !IsGameStarted;
    }

    // ========== GAME GENERATOR (FIXED) ==========
    public static class DominoGameGenerator
    {
        public static List<DominoTile> GenerateFullSet()
        {
            var tiles = new List<DominoTile>();
            for (int i = 0; i <= 6; i++)
            {
                for (int j = i; j <= 6; j++)
                {
                    tiles.Add(new DominoTile(i, j));
                }
            }
            return tiles;
        }

        public static List<DominoTile> Shuffle(List<DominoTile> tiles)
        {
            return tiles.OrderBy(x => Guid.NewGuid()).ToList();
        }

        public static (List<DominoTile> stock, List<List<DominoTile>> hands)
            DealTiles(int playerCount, int tilesPerPlayer)
        {
            var allTiles = Shuffle(GenerateFullSet());
            var hands = new List<List<DominoTile>>();

            for (int i = 0; i < playerCount; i++)
            {
                var hand = allTiles.Take(tilesPerPlayer).ToList();
                allTiles = allTiles.Skip(tilesPerPlayer).ToList();
                hands.Add(hand);
            }

            return (allTiles, hands);
        }

        // ✅ EN KİÇİK DUPLET (0-0, 1-1, 2-2, ...)
        public static int FindPlayerWithSmallestDouble(List<DominoPlayer> players)
        {
            int minValue = int.MaxValue;
            int playerIndex = 0;

            for (int i = 0; i < players.Count; i++)
            {
                var doubles = players[i].Hand.Where(t => t.IsDouble).ToList();
                if (doubles.Any())
                {
                    var min = doubles.Min(t => t.Left);
                    if (min < minValue)
                    {
                        minValue = min;
                        playerIndex = i;
                    }
                }
            }

            // Heç kimdə duplet yoxdursa, birinci oyunçu
            return playerIndex;
        }
    }

    // ========== DTO-LAR ==========

    public class DominoRoomListItem
    {
        public string RoomId { get; set; }
        public string RoomName { get; set; }
        public string CreatorName { get; set; }
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public decimal EntryFee { get; set; }
        public bool IsPrivate { get; set; }
        public bool IsGameStarted { get; set; }
        public string GameTypeName { get; set; }
    }

    public class DominoPlayerInfo
    {
        public string Name { get; set; }
        public int TileCount { get; set; }
        public int Score { get; set; }
        public bool IsCurrentTurn { get; set; }
        public PlayerStatus Status { get; set; }
    }

    public class DominoGameState
    {
        public string RoomId { get; set; }
        public string RoomName { get; set; }
        public int CurrentRound { get; set; } // ✅ Raund nömrəsi
        public List<DominoPlayerInfo> Players { get; set; }
        public List<DominoTile> MyHand { get; set; }
        public List<DominoTile> ChainTiles { get; set; }
        public int? LeftEnd { get; set; }
        public int? RightEnd { get; set; }
        public int StockCount { get; set; }
        public bool IsMyTurn { get; set; }
        public string? CurrentPlayerName { get; set; }
    }
}

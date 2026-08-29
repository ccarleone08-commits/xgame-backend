using System.Collections.Concurrent;

namespace BlogApp.Api.Hubs.Services
{
    public class PokerRoomManager
    {
        private readonly ConcurrentDictionary<string, PokerRoom> _rooms = new();

        public PokerRoomManager()
        {
            CreateDefaultRooms();
        }

        private void CreateDefaultRooms()
        {
            var defaultRooms = new[]
            {
                // 🔴 NO-LIMIT OTAQLAR
                new { Name = "🔴 No-Limit ", BuyIn = 0.20m, SB = 0.20m, BB = 0.40m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 0.50m, SB = 0.50m, BB = 1m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 1m, SB = 1m, BB = 2m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 2m, SB = 2m, BB = 4m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 5m, SB = 2.5m, BB = 5m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 10m, SB = 5m, BB = 10m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 20m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit ", BuyIn = 50m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.NoLimit },
                new { Name = "🔴 No-Limit VIP", BuyIn = 100m, SB = 50m, BB = 100m, Max = 5, Type = PokerGameType.NoLimit }

                //// 🟢 LIMIT OTAQLAR
                // new { Name = "Limit $0.20/$0.40 ", BuyIn = 0.20m, SB = 0.20m, BB = 0.40m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $0.5/$1 ", BuyIn = 0.50m, SB = 0.50m, BB = 1m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $1/$2 ", BuyIn = 1m, SB = 1m, BB = 2m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $2/$4", BuyIn = 2m, SB = 2m, BB = 4m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $2.5/$5 ", BuyIn = 5m, SB = 2.5m, BB = 5m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $5/$10 ", BuyIn = 10m, SB = 5m, BB = 10m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $10/$20 ", BuyIn = 20m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $10/$20 ", BuyIn = 50m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.Limit },
                //new { Name = "Limit $50/$100", BuyIn = 100m, SB = 50m, BB = 100m, Max = 5, Type = PokerGameType.Limit },
                
                //// 🟡 POT-LIMIT OTAQLAR
                //  new { Name = "Pot-Limit Yüksək", BuyIn = 0.20m, SB = 0.20m, BB = 0.40m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək ", BuyIn = 0.50m, SB = 0.50m, BB = 1m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək ", BuyIn = 1m, SB = 1m, BB = 2m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 2m, SB = 2m, BB = 4m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 5m, SB = 2.5m, BB = 5m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 10m, SB = 5m, BB = 10m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 20m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 50m, SB = 10m, BB = 20m, Max = 5, Type = PokerGameType.PotLimit },
                //new { Name = "Pot-Limit Yüksək", BuyIn = 100m, SB = 50m, BB = 100m, Max = 5, Type = PokerGameType.PotLimit },
            };

            foreach (var roomConfig in defaultRooms)
            {
                CreateRoom(
                    roomConfig.Name,
                    "System",
                    0,
                    roomConfig.BuyIn,
                    roomConfig.SB,
                    roomConfig.BB,
                    roomConfig.Max,
                    roomConfig.Type
                );
            }

            Console.WriteLine("✅ Default poker rooms created (No-Limit, Limit, Pot-Limit)");
        }

        public PokerRoom? CreateRoom(string roomName, string creatorName, int creatorUserId,
            decimal buyIn, decimal smallBlind, decimal bigBlind, int maxPlayers, PokerGameType gameType = PokerGameType.NoLimit)
        {
            var roomId = Guid.NewGuid().ToString();
            var room = new PokerRoom
            {
                RoomId = roomId,
                RoomName = roomName,
                CreatorName = creatorName,
                CreatorUserId = creatorUserId,
                BuyIn = buyIn,
                SmallBlind = smallBlind,
                BigBlind = bigBlind,
                MaxPlayers = maxPlayers,
                GameType = gameType,
                Players = new List<RoomPlayers>(),
                IsGameActive = false
            };

            return _rooms.TryAdd(roomId, room) ? room : null;
        }

        public PokerRoom? GetRoom(string roomId)
        {
            _rooms.TryGetValue(roomId, out var room);
            return room;
        }

        public PokerRoom? GetRoomByUser(int userId)
        {
            foreach (var room in _rooms.Values)
            {
                lock (room.StateLock)
                {
                    if (room.Players.Any(p => p.UserId == userId))
                    {
                        return room;
                    }
                }
            }

            return null;
        }

        public bool AddPlayerToRoom(string roomId, RoomPlayers player)
        {
            var room = GetRoom(roomId);
            if (room == null) return false;

            lock (room.StateLock)
            {
                if (room.Players.Count >= room.MaxPlayers)
                    return false;

                if (room.Players.Any(p => p.UserId == player.UserId))
                    return false;

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
        }

        public void DeleteRoom(string roomId)
        {
            if (_rooms.TryGetValue(roomId, out var room))
            {
                if (room.CreatorUserId == 0) return;
            }

            _rooms.TryRemove(roomId, out _);
            Console.WriteLine($"🗑️ Room deleted: {roomId}");
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
                    BuyIn = r.BuyIn,
                    SmallBlind = r.SmallBlind,
                    BigBlind = r.BigBlind,
                    IsGameActive = r.IsGameActive,
                    GameType = r.GameType
                })
                .OrderBy(r => r.GameType)
                .ThenBy(r => r.BuyIn)
                .ToList();
        }
    }
    public class PotLevel
    {
        public decimal Amount { get; set; }
        public List<int> EligiblePlayerIds { get; set; } = new(); // Bu pot-a kim qatıla bilər
    }

    public class PokerRoom
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public int CreatorUserId { get; set; }
        public decimal BuyIn { get; set; }
        public decimal SmallBlind { get; set; }
        public decimal BigBlind { get; set; }
        public int MaxPlayers { get; set; }
        public PokerGameType GameType { get; set; } = PokerGameType.NoLimit;

        public List<PotLevel> Pots { get; set; } = new(); // Multi-way pot levels
        public bool HasAllInThisStreet { get; set; } = false;
        public bool IsAllInRunoutStarted { get; set; } = false;
        public List<RoomPlayers> Players { get; set; } = new();
        public bool IsGameActive { get; set; }
        public int LastRaiserIndex { get; set; } = -1;
        public int FirstPlayerOfRound { get; set; } = -1;

        // ✅ YENİ: Limit Poker üçün street başına raise sayı
        public int RaisesThisStreet { get; set; } = 0;
        public const int MAX_RAISES_LIMIT = 4; // Limit pokerdə maksimum raise

        public const int TURN_TIMEOUT_SECONDS = 45; // Hər növbə üçün 30 saniyə
        public const int ROOM_START_TIMEOUT_SECONDS = 5; // Otaq timer 5 dəqiqə

        // ✅ YENİ PROPERTY-LƏR
        public DateTime? TurnStartTime { get; set; }
        public DateTime? RoomCreatedTime { get; set; }


        public List<string> Deck { get; set; } = new();
        public List<string> CommunityCards { get; set; } = new();
        public HashSet<int> PlayersActedThisStreet { get; set; } = new();
        public decimal Pot { get; set; }
        public decimal CurrentBet { get; set; }
        public int DealerIndex { get; set; }
        public int CurrentPlayerIndex { get; set; }
        public string CurrentStreet { get; set; } = "preflop";

        public object StateLock { get; } = new object();

        /// <summary>
        /// ✅ Oyun növünə görə minimum raise məbləğini hesabla
        /// </summary>
        public decimal GetMinimumRaise()
        {
            switch (GameType)
            {
                case PokerGameType.Limit:
                    // Limit: Preflop və Flop = SB*2, Turn və River = BB*2
                    return (CurrentStreet == "preflop" || CurrentStreet == "flop")
                        ? SmallBlind
                        : BigBlind;

                case PokerGameType.PotLimit:
                    // Pot-Limit: Minimum = BB, amma həmişə current bet-dən az ola bilməz
                    return Math.Max(BigBlind, CurrentBet);

                case PokerGameType.NoLimit:
                default:
                    // No-Limit: Minimum = BB və ya current bet * 2
                    return CurrentBet == 0 ? BigBlind : CurrentBet;
            }
        }

        /// <summary>
        /// ✅ Oyun növünə görə maksimum raise məbləğini hesabla
        /// </summary>
        public decimal GetMaximumRaise(RoomPlayers player)
        {
            switch (GameType)
            {
                case PokerGameType.Limit:
                    // Limit: Raise yalnız fixed amount qədər (min raise ilə eyni)
                    return GetMinimumRaise();

                case PokerGameType.PotLimit:
                    // Pot-Limit: Maksimum = Pot + aktiv bet + call məbləği
                    decimal toCall = CurrentBet - player.CurrentBet;
                    decimal maxPotRaise = Pot + CurrentBet + toCall;
                    return Math.Min(maxPotRaise, player.Chips);

                case PokerGameType.NoLimit:
                default:
                    // No-Limit: Maksimum = bütün çiplər
                    return player.Chips;
            }
        }

        /// <summary>
        /// ✅ Raise-ə icazə verilir?
        /// </summary>
        public bool CanRaise()
        {
            if (GameType == PokerGameType.Limit)
            {
                // Limit pokerdə maksimum 4 raise per street
                return RaisesThisStreet < MAX_RAISES_LIMIT;
            }

            return true; // No-Limit və Pot-Limit-də həmişə raise edə bilərsən (çipin varsa)
        }

        public void StartNewHand()
        {
            var playersWithChips = Players.Where(p => p.Chips > 0 && !p.IsPausedAfterHand).ToList();

            if (playersWithChips.Count < 2)
            {
                Console.WriteLine($"⚠️ Need at least 2 players with chips (current: {playersWithChips.Count})");
                return;
            }

            IsGameActive = true;
            Deck = GenerateDeck();
            CommunityCards.Clear();
            Pot = 0;
            CurrentBet = 0;
            CurrentStreet = "preflop";
            LastRaiserIndex = -1;
            FirstPlayerOfRound = -1;
            PlayersActedThisStreet.Clear();
            RaisesThisStreet = 0; // ✅ YENİ
            HasAllInThisStreet = false;
            IsAllInRunoutStarted = false;

            foreach (var player in Players)
            {
                player.HoleCards.Clear();
                player.CurrentBet = 0;
                player.HasFolded = false;
                player.RaiseCount = 0;
                player.IsInHand = player.Chips > 0 && !player.IsPausedAfterHand;
                player.IsReBuyPending = false;
                player.ReBuyPendingAt = null;
                if (player.IsInHand)
                {
                    player.IsWaitingForNextHand = false;
                }
            }

            DealerIndex = (DealerIndex + 1) % Players.Count;

            int safety = 0;
            while (safety < Players.Count && (Players[DealerIndex].Chips <= 0 || Players[DealerIndex].IsPausedAfterHand))
            {
                DealerIndex = (DealerIndex + 1) % Players.Count;
                safety++;
            }

            CurrentPlayerIndex = (DealerIndex + 3) % Players.Count;

            safety = 0;
            while (safety < Players.Count)
            {
                var player = Players[CurrentPlayerIndex];

                if (player.IsInHand && !player.HasFolded && player.Chips > 0)
                {
                    break;
                }

                CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
                safety++;
            }

            Console.WriteLine($"✅ Hand started ({GameType}):");
            Console.WriteLine($"   Dealer = Index:{DealerIndex}, Name:{Players[DealerIndex].Name}");
            Console.WriteLine($"   First Player (UTG) = Index:{CurrentPlayerIndex}, Name:{Players[CurrentPlayerIndex].Name}");
        }

        public void MoveToNextPlayer()
        {
            if (Players.Count == 0) return;

            var activePlayers = Players.Where(p => p.IsInHand && !p.HasFolded).ToList();

            if (activePlayers.Count <= 1)
            {
                Console.WriteLine($"⚠️ MoveToNextPlayer: Only {activePlayers.Count} active player(s) left");

                if (activePlayers.Count == 1)
                {
                    CurrentPlayerIndex = Players.IndexOf(activePlayers[0]);
                    Console.WriteLine($"✅ Set current player to last active: {activePlayers[0].Name}");
                }
                return;
            }

            int startIndex = CurrentPlayerIndex;
            int attempts = 0;
            int maxAttempts = Players.Count + 1;

            do
            {
                CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
                attempts++;

                if (attempts >= maxAttempts)
                {
                    Console.WriteLine($"⚠️ MoveToNextPlayer: Max attempts reached");

                    for (int i = 0; i < Players.Count; i++)
                    {
                        if (Players[i].IsInHand && !Players[i].HasFolded)
                        {
                            CurrentPlayerIndex = i;
                            Console.WriteLine($"✅ Emergency: Set current player to {Players[i].Name}");
                            return;
                        }
                    }

                    CurrentPlayerIndex = 0;
                    return;
                }

                if (CurrentPlayerIndex >= 0 && CurrentPlayerIndex < Players.Count)
                {
                    var player = Players[CurrentPlayerIndex];
                    if (CanPlayerAct(player))
                    {
                        Console.WriteLine($"✅ Next player: {player.Name}");
                        return;
                    }
                }

            } while (true);
        }

        // PokerRoom class-ında
        public bool IsBettingRoundComplete()
        {
            var activePlayers = Players.Where(p => p.IsInHand && !p.HasFolded).ToList();

            // 1 oyuncu kaldıysa round bitti
            if (activePlayers.Count <= 1)
            {
                Console.WriteLine($"📊 BettingRoundComplete: Only 1 active player");
                return true;
            }

            // Tüm active oyuncular action aldı mı?
            var playersNotActed = activePlayers.Where(p =>
                !PlayersActedThisStreet.Contains(Players.IndexOf(p)) &&
                p.Chips > 0  // All-in olmayan
            ).ToList();

            if (playersNotActed.Count > 0)
            {
                Console.WriteLine($"📊 BettingRoundComplete: {playersNotActed.Count} players still need to act");
                return false;
            }

            // Tüm oyuncular aynı amount bet etti mi?
            decimal maxBet = activePlayers.Max(p => p.CurrentBet);
            var notEqualBet = activePlayers.Where(p => p.CurrentBet < maxBet && p.Chips > 0).ToList();

            if (notEqualBet.Count > 0)
            {
                Console.WriteLine($"📊 BettingRoundComplete: {notEqualBet.Count} players have unequal bets");
                return false;
            }

            if (IsAllInRunoutReady())
            {
                Console.WriteLine($"✅ BettingRoundComplete: All-in runout ready after all calls/folds");
                return true;
            }

            Console.WriteLine($"✅ BettingRoundComplete: All conditions met!");
            return true;
        }

        public bool IsAllInRunoutReady()
        {
            var activePlayers = Players.Where(p => p.IsInHand && !p.HasFolded).ToList();
            var actionCapablePlayers = activePlayers.Where(CanPlayerAct).ToList();

            return activePlayers.Count > 1 &&
                   activePlayers.Any(p => p.IsAllIn) &&
                   actionCapablePlayers.Count <= 1;
        }

        public bool CanPlayerAct(RoomPlayers player)
        {
            return player.IsInHand &&
                   !player.HasFolded &&
                   !player.IsAllIn &&
                   !player.IsPausedAfterHand &&
                   player.Chips > 0;
        }

        public void ResetBetsForNewStreet()
        {
            foreach (var player in Players)
            {
                player.CurrentBet = 0;
            }

            CurrentBet = 0;
            PlayersActedThisStreet.Clear();
            LastRaiserIndex = -1;
            RaisesThisStreet = 0;
            FirstPlayerOfRound = -1;

            // ✅ Dealer-dən sonra başla (small blind)
            CurrentPlayerIndex = (DealerIndex + 1) % Players.Count;

            // ✅ YENİ: Əldədə olmayan oyunçuları keç
            int attempts = 0;
            while (attempts < Players.Count && !CanPlayerAct(Players[CurrentPlayerIndex]))
            {
                CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
                attempts++;
            }

            Console.WriteLine($"🔄 Bets reset for new street. First to act: {Players[CurrentPlayerIndex].Name}");
        }
        public void MoveToNextActivePlayer()
        {
            if (Players.Count == 0) return;

            int attempts = 0;
            int startIndex = CurrentPlayerIndex;

            do
            {
                // Sonraki indekse git
                CurrentPlayerIndex = (CurrentPlayerIndex + 1) % Players.Count;
                attempts++;

                // Eğer oyuncu aktifse, fold/all-in değilse ve oynayacak çipi varsa dur
                if (CanPlayerAct(Players[CurrentPlayerIndex]))
                {
                    Console.WriteLine($"✅ MoveToNextActivePlayer: {Players[CurrentPlayerIndex].Name} (attempts: {attempts})");
                    return;
                }

                // Sonsuz loop'tan koru
                if (attempts > Players.Count * 2)
                {
                    Console.WriteLine($"⚠️ MoveToNextActivePlayer: Circular check, no active player found");
                    return;
                }

            } while (CurrentPlayerIndex != startIndex);

            Console.WriteLine($"❌ MoveToNextActivePlayer: No active player found!");
        }
        public void ResetForNewHand()
        {
            IsGameActive = false;
            Pot = 0;
            CurrentBet = 0;
            CommunityCards.Clear();
            Deck.Clear();
            CurrentStreet = "preflop";
            LastRaiserIndex = -1;
            FirstPlayerOfRound = -1;
            RaisesThisStreet = 0; // ✅ YENİ

            foreach (var player in Players)
            {
                player.HoleCards.Clear();
                player.CurrentBet = 0;
                player.IsInHand = false;
                player.HasFolded = player.IsPausedAfterHand;
                player.RaiseCount = 0;
                player.IsAllIn = false;
                player.ContributedToPot = 0;
                player.IsReBuyPending = false;
                player.ReBuyPendingAt = null;
            }

            HasAllInThisStreet = false;
            IsAllInRunoutStarted = false;
            Pots.Clear();
        }

        private List<string> GenerateDeck()
        {
            var suits = new[] { "♠", "♥", "♦", "♣" };
            var ranks = new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "J", "Q", "K", "A" };
            var deck = new List<string>();

            foreach (var suit in suits)
            {
                foreach (var rank in ranks)
                {
                    deck.Add(rank + suit);
                }
            }

            var rng = new Random();
            return deck.OrderBy(x => rng.Next()).ToList();
        }
    }

    public class RoomPlayers
    {
        public string ConnectionId { get; set; } = "";
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public string UserName { get; set; } = string.Empty;
        public decimal Balance { get; set; }
        public string? ProfileImage { get; set; }
        public int RaiseCount { get; set; } = 0;
        public decimal Chips { get; set; }
        public List<string> HoleCards { get; set; } = new();
        public decimal CurrentBet { get; set; }
        public bool IsInHand { get; set; }
        public bool HasFolded { get; set; }
        public bool IsAllIn { get; set; } = false;
        public decimal ContributedToPot { get; set; } = 0;
        public bool IsWaitingForNextHand { get; set; } = false;
        public bool IsPausedAfterHand { get; set; } = false;
        public bool ShouldLeaveAfterHand { get; set; } = false;
        public bool IsReBuyPending { get; set; } = false;
        public DateTime? ReBuyPendingAt { get; set; }
        public PokerHandPauseChoice HandPauseChoice { get; set; } = PokerHandPauseChoice.None;
        public DateTime? HandPauseDecisionAt { get; set; }
    }

    public enum PokerHandPauseChoice
    {
        None,
        ContinuePlaying,
        Timeout
    }

    public class RoomListItem
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public decimal BuyIn { get; set; }
        public decimal SmallBlind { get; set; }
        public decimal BigBlind { get; set; }
        public bool IsGameActive { get; set; }
        public PokerGameType GameType { get; set; }
    }

    public enum PokerGameType
    {
        NoLimit = 0,
        Limit = 1,
        PotLimit = 2
    }
}

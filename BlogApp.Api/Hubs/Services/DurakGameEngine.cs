using static BlogApp.Api.Hubs.Services.DurakRoomManager;
using System.Threading;
namespace BlogApp.Api.Hubs.Services
{
    public class DurakGameEngine
    {
        private readonly DurakRoom _room;

        public DurakGameEngine(DurakRoom room)
        {
            _room = room ?? throw new ArgumentNullException(nameof(room));
        }

        #region Game Initialization

        public void InitializeGame()
        {
            Console.WriteLine($"🎮 OYUN BAŞLANIYOR: {_room.RoomName}");
            _room.GameStatus = "Active";

            // ✅ STEP 1: Desk yaratıl
            _room.Deck = CreateDeck(_room.DeckSize);
            Console.WriteLine($"✅ Desk yaradıldı: {_room.Deck.Count} kart");

            // ✅ STEP 2: Desk qarışdırıl
            ShuffleDeck();
            Console.WriteLine($"✅ Desk qarışdırıldı");

            // ✅ STEP 3: KOZ KARTI ƏVVƏL SEÇİLİR - deck-də son kart kimi qalır
            SetupTrumpCard();

            Console.WriteLine($"✅ Koz kartı seçildi: {_room.TrumpCard.Rank} of {_room.TrumpCard.Suit}");
            Console.WriteLine($"   Trump suit təyin olundu, paylama başlayır");

            // ✅ STEP 4: Oyunçu sayı + deck ölçüsünə görə kartlar paylanır
            int initialCardsPerPlayer = GetInitialCardsPerPlayer();
            DealInitialCards(initialCardsPerPlayer);
            Console.WriteLine($"✅ Əsas kartlar paylandı: {initialCardsPerPlayer} × {_room.Players.Count}");

            // ✅ STEP 5: Roller təyin olunur
            AssignInitialRoles();
            Console.WriteLine($"✅ Roller təyin olundu");

            // ✅ STEP 6: Oyun state reset
            _room.TableCards.Clear();
            _room.DefendedPairs.Clear();
            _room.IsGameActive = true;

            Console.WriteLine($"");
            Console.WriteLine($"═══════════════════════════════════════════════════════");
            Console.WriteLine($"✅ OYUN BAŞLADI!");
            Console.WriteLine($"═══════════════════════════════════════════════════════");
            Console.WriteLine($"🃏 Koz: {_room.TrumpCard.Rank} of {_room.TrumpCard.Suit}");
            bool trumpStillInDeck = _room.Deck.Contains(_room.TrumpCard);
            Console.WriteLine(
                $"📊 Deckinde qalıb: {_room.Deck.Count} kart{(trumpStillInDeck ? " (Koz DAXİL)" : "")}");
            Console.WriteLine($"");

            foreach (var player in _room.Players)
            {
                bool hasTrump = player.Hand.Any(c => c.Rank == _room.TrumpCard.Rank && c.Suit == _room.TrumpCard.Suit);
                Console.WriteLine($"🃏 {player.Name}: {player.Hand.Count} kart {(hasTrump ? "(KÖZ VAR)" : "")}");
            }

            Console.WriteLine($"");
            int totalInHands = _room.Players.Sum(p => p.Hand.Count);
            Console.WriteLine($"📊 KONTROL:");
            Console.WriteLine($"   Oyunçularda: {totalInHands} kart");
            Console.WriteLine($"   Deckinde: {_room.Deck.Count} kart");
            Console.WriteLine($"   TOPLAM: {totalInHands + _room.Deck.Count} = {_room.DeckSize} ✅");
        }

        private void SetupTrumpCard()
        {
            if (_room.Deck.Count == 0)
                throw new InvalidOperationException("Deck boşdur - Koz kartı seçilə bilinmir");

            _room.TrumpCard = _room.Deck.Last();
        }

        private int GetInitialCardsPerPlayer()
        {
            if (_room.Players.Count == 0)
                throw new InvalidOperationException("Oyunçu yoxdur");

            int cardsPerPlayer = 6;
            int requiredCards = _room.Players.Count * cardsPerPlayer;

            if (_room.Deck.Count < requiredCards)
                throw new InvalidOperationException(
                    $"Bu deck ölçüsü ilə hər oyunçuya {cardsPerPlayer} kart paylama mümkün deyil");

            return cardsPerPlayer;
        }
        private List<Card> CreateDeck(int deckSize)
        {
            var deck = new List<Card>();
            var suits = new[] { "Hearts", "Diamonds", "Clubs", "Spades" };

            string[] ranks = deckSize switch
            {
                24 => new[] { "9", "10", "Jack", "Queen", "King", "Ace" },  // ✅ 6 × 4 = 24 KART
                36 => new[] { "6", "7", "8", "9", "10", "Jack", "Queen", "King", "Ace" },  // ✅ 9 × 4 = 36 KART
                52 => new[] { "2", "3", "4", "5", "6", "7", "8", "9", "10", "Jack", "Queen", "King", "Ace" },  // ✅ 13 × 4 = 52 KART
                _ => throw new ArgumentException($"Səhv deskin ölçüsü: {deckSize}")
            };

            foreach (var suit in suits)
            {
                foreach (var rank in ranks)
                {
                    deck.Add(new Card { Rank = rank, Suit = suit });
                }
            }

            Console.WriteLine($"   📊 Deskin tamamlanması: {ranks.Length} rank × 4 suit = {deck.Count} kart ✅");
            return deck;
        }

        private void ShuffleDeck()
        {
            var rng = new Random();
            int n = _room.Deck.Count;
            while (n > 1)
            {
                n--;
                int k = rng.Next(n + 1);
                var temp = _room.Deck[k];
                _room.Deck[k] = _room.Deck[n];
                _room.Deck[n] = temp;
            }
        }

        private void DealInitialCards(int cardsPerPlayer)
        {
            foreach (var player in _room.Players)
            {
                player.Hand.Clear();
                for (int i = 0; i < cardsPerPlayer && _room.Deck.Count > 0; i++)
                {
                    player.Hand.Add(_room.Deck[0]);
                    _room.Deck.RemoveAt(0);
                }
            }

            Console.WriteLine($"   ✅ Əsas kartlar paylandı: {_room.Players.Sum(p => p.Hand.Count)} kart");
            Console.WriteLine($"      ({cardsPerPlayer} kart × {_room.Players.Count} oyunçu)");
            Console.WriteLine($"      Deskin qalıb: {_room.Deck.Count} kart (Trump daxil)");
        }

        private void AssignInitialRoles()
        {
            if (_room.TrumpCard == null || _room.Players.Count == 0)
            {
                _room.AttackerId = _room.Players[0].UserId;
                _room.DefenderId = _room.Players.Count > 1 ? _room.Players[1].UserId : _room.Players[0].UserId;
                return;
            }

            var trumpRanks = new Dictionary<string, int>
                {
                    {"6", 6}, {"7", 7}, {"8", 8}, {"9", 9}, {"10", 10},
                    {"Jack", 11}, {"Queen", 12}, {"King", 13}, {"Ace", 14}
                };

            DurakPlayer? smallestTrumpPlayer = null;
            int smallestTrumpValue = int.MaxValue;

            foreach (var player in _room.Players)
            {
                var trumpCards = player.Hand.Where(c => c.Suit == _room.TrumpCard.Suit).ToList();
                if (trumpCards.Any())
                {
                    foreach (var card in trumpCards)
                    {
                        if (trumpRanks.TryGetValue(card.Rank, out int value) && value < smallestTrumpValue)
                        {
                            smallestTrumpValue = value;
                            smallestTrumpPlayer = player;
                        }
                    }
                }
            }

            if (smallestTrumpPlayer != null)
            {
                int attackerIndex = _room.Players.IndexOf(smallestTrumpPlayer);
                _room.AttackerId = smallestTrumpPlayer.UserId;
                _room.DefenderId = _room.Players[(attackerIndex + 1) % _room.Players.Count].UserId;

                var trumpInHand = smallestTrumpPlayer.Hand.FirstOrDefault(c =>
                    c.Suit == _room.TrumpCard.Suit && trumpRanks[c.Rank] == smallestTrumpValue);
                Console.WriteLine($"      🎯 Başdan hücum: {smallestTrumpPlayer.Name} ({trumpInHand?.Rank} of {_room.TrumpCard.Suit})");
            }
            else
            {
                _room.AttackerId = _room.Players[0].UserId;
                _room.DefenderId = _room.Players.Count > 1 ? _room.Players[1].UserId : _room.Players[0].UserId;
                Console.WriteLine($"      🎯 Başdan hücum: {_room.Players[0].Name} (kozu yoxdur - random)");
            }
        }
        #endregion

        #region Attack Logic - DÜZƏLDİLMİŞ

        public AttackValidationResult ValidateAttack(int userId, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return AttackValidationResult.Error("Ən azı 1 kart seçin");

            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
                return AttackValidationResult.Error("Oyunçu tapılmadı");

            if (_room.DefenderId == userId)
                return AttackValidationResult.Error("Müdafiəçi hücum edə bilməz");

            if (_room.IsThrowInPhaseActive)
                return ValidateThrowInAttack(userId, player, cards);

            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender == null)
                return AttackValidationResult.Error("Müdafiəçi tapılmadı");

            bool isMainAttacker = (_room.AttackerId == userId);

            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ MAIN ATTACKER - HƏMİŞƏ HÜCUM EDƏ BİLƏR, heç bir queue yoxlaması yoxdur
            // ═══════════════════════════════════════════════════════════════════════════════
            if (isMainAttacker)
            {

                if (_room.TableCards.Count == 0 && _room.DefendedPairs.Count == 0)
                {
                    if (cards.Count < 1 || cards.Count > 6)
                        return AttackValidationResult.Error("1-6 kart arasında seçin");
                    return ValidateCardsInHand(player, cards);
                }

                return ValidateAttackCommon(player, cards, defender);
            }

            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ 2 OYUNÇU - Yalnız Main Attacker (artıq yuxarıda keçdi)
            // ═══════════════════════════════════════════════════════════════════════════════
            if (_room.Players.Count == 2)
                return AttackValidationResult.Error("2 oyunçuda yalnız Main Attacker hücum edə bilər");

            // ═══════════════════════════════════════════════════════════════════════════════
            // ✅ 3+ OYUNÇU - QUEUE OYUNÇULARI
            // ═══════════════════════════════════════════════════════════════════════════════

            if (!_room.IsBeatenPhaseActive && !_room.IsTakeCardPhaseActive && !_room.IsBrokenBeatenPhaseActive)
            {
                Console.WriteLine($"❌ Queue oyunçusu hücum cəhdi RƏDD");
                return AttackValidationResult.Error(
                    "⏳ Əvvəlcə Main Attacker hücumunu bitirməli - Beaten ya da Take Cards basmalı");
            }
            // Queue yoxdursa - defender hələ müdafiə etməyib
            if (_room.AttackerQueue.Count == 0)
                return AttackValidationResult.Error("⏳ Gözləyin — əvvəlcə müdafiəçi cavab verməlidir");

            var currentAttacker = GetCurrentAttackerInQueue();

            // Queue bitibsə - sıra keçib
            if (currentAttacker == null || currentAttacker == 0)
                return AttackValidationResult.Error("⏳ Sizin sıranız keçib");

            if (currentAttacker != userId)
            {
                var currentPlayer = _room.Players.FirstOrDefault(p => p.UserId == currentAttacker);
                return AttackValidationResult.Error(
                    $"⏳ Sizin sıranız deyil — {currentPlayer?.Name ?? "digər oyunçu"} hücum etməlidir");
            }

            if (_room.PlayersWhoPassedThisRound.Contains(userId))
                return AttackValidationResult.Error("❌ Bu raundda pas etdiniz");

            if (_room.GameSettings.AttackMode == AttackMode.Neighbour)
            {
                if (_room.AttackerQueue.Count > 0 && _room.AttackerQueue[0] != userId)
                    return AttackValidationResult.Error(
                        "Neighbour Mode: Yalnız müdafiəçinin yanındakı oyunçu hücum edə bilər");
            }

            return ValidateAttackCommon(player, cards, defender);
        }
        private AttackValidationResult ValidateThrowInAttack(int userId, DurakPlayer player, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return AttackValidationResult.Error("Ən azı 1 kart seçin");

            if (_room.DefenderId == userId)
                return AttackValidationResult.Error("Müdafiəçi throw-in edə bilməz");

            foreach (var card in cards)
            {
                var cardInHand = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardInHand == null)
                    return AttackValidationResult.Error($"Kart əlinizdə yoxdur");
            }

            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender == null)
                return AttackValidationResult.Error("Müdafiəçi tapılmadı");

            int openCards = _room.TableCards.Count;
            int maxOpenCards = defender.Hand.Count;  // ✅ Defender əldəki kartlar
            int currentAttackCardCount = GetCurrentAttackCardCount();
            int totalAttackCardCount = currentAttackCardCount + cards.Count;

            if (totalAttackCardCount > 6)
                return AttackValidationResult.Error(
                    $"❌ Maksimum 6 hücum kartı ola bilər. Cəhd: {totalAttackCardCount}");

            if (openCards + cards.Count > maxOpenCards)
                return AttackValidationResult.Error(
                    $"Müdafiəçi {openCards + cards.Count} kartı müdafiə edə bilmir (əlində {maxOpenCards} var)");

            var allTableRanks = _room.TableCards.Select(c => c.Rank)
                .Concat(_room.DefendedPairs.SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank }))
                .ToHashSet();

            foreach (var card in cards)
            {
                if (!allTableRanks.Contains(card.Rank))
                    return AttackValidationResult.Error("Throw-in: Yalnız masadakı rank-lara uyğun kartlar");
            }

            return AttackValidationResult.Success();
        }
        private string GetPlayerName(int userId)
        {
            return _room.Players.FirstOrDefault(p => p.UserId == userId)?.Name ?? "Unknown";
        }

        private AttackValidationResult ValidateCardsInHand(DurakPlayer player, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return AttackValidationResult.Error("Ən azı 1 kart seçin");

            foreach (var card in cards)
            {
                var cardInHand = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardInHand == null)
                    return AttackValidationResult.Error("Kart əlinizdə yoxdur");
            }

            if (cards.Count < 1 || cards.Count > 6)
                return AttackValidationResult.Error("1-6 kart arasında seçin");

            // İlk hücumda defender limiti - masada hələ kart yoxdur
            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender != null && cards.Count > defender.Hand.Count)
                return AttackValidationResult.Error(
                    $"❌ Defender əlində {defender.Hand.Count} kart var, " +
                    $"maksimum {defender.Hand.Count} kart ata bilərsiniz");

            return AttackValidationResult.Success();
        }
        private AttackValidationResult ValidateAttackCommon(DurakPlayer player, List<Card> cards, DurakPlayer defender)
        {
            if (cards == null || cards.Count == 0)
                return AttackValidationResult.Error("Ən azı 1 kart seçin");

            // STEP 1: Kartlar əldə varmı?
            foreach (var card in cards)
            {
                var cardInHand = player.Hand.FirstOrDefault(c =>
                    c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardInHand == null)
                    return AttackValidationResult.Error(
                        $"Kart əlinizdə yoxdur: {card.Rank} of {card.Suit}");
            }

            int openCards = _room.TableCards.Count;
            int defendedCards = _room.DefendedPairs.Count;
            int defendersCards = defender.Hand.Count;
            int totalWillBeOpen = openCards + cards.Count;
            int totalAttackCardCount = openCards + defendedCards + cards.Count;


            Console.WriteLine($"🎯 ValidateAttackCommon:");
            Console.WriteLine($"   Player: {player.Name}, IsMainAttacker: {_room.AttackerId == player.UserId}");
            Console.WriteLine($"   OpenCards: {openCards}, DefendedPairs: {defendedCards}");
            Console.WriteLine($"   DefenderCards: {defendersCards}, NewCards: {cards.Count}");
            Console.WriteLine($"   IsBeatenPhase: {_room.IsBeatenPhaseActive}");
            Console.WriteLine($"   IsTakeCardPhase: {_room.IsTakeCardPhaseActive}");

            // QAYDA 1: Maksimum 6 açıq kart
            if (totalAttackCardCount > 6)
                return AttackValidationResult.Error(
                    $"❌ Maksimum 6 hücum kartı masada ola bilər. Cəhd: {totalAttackCardCount}");

            // QAYDA 2: Defender əlindəki kartdan çox açıq kart ola bilməz
            // defendedCards + totalWillBeOpen — defender üçün toplam yük
            if (defendersCards <= 0)
                return AttackValidationResult.Error("❌ Defender əlində kart yoxdur");

            if (totalWillBeOpen > defendersCards)
                return AttackValidationResult.Error(
                    $"❌ Defender əlində {defendersCards} kart var, " +
                    $"masada {openCards} açıq kart var. " +
                    $"Maksimum {Math.Max(0, defendersCards - openCards)} yeni kart ata bilərsiniz");

            // QAYDA 3: Rank uyğunluğu

            // TakeCard fazası — masa boşdursa ilk hücum kimi qəbul et
            if (_room.IsTakeCardPhaseActive)
            {
                if (openCards == 0 && defendedCards == 0)
                {
                    Console.WriteLine($"   ✅ TakeCard fazası - masa boş, ilk hücum kimi qəbul edilir");
                    return AttackValidationResult.Success();
                }

                var allowedRanksTake = _room.TableCards.Select(c => c.Rank)
                    .Concat(_room.DefendedPairs.SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank }))
                    .ToHashSet();

                foreach (var card in cards)
                {
                    if (!allowedRanksTake.Contains(card.Rank))
                        return AttackValidationResult.Error(
                            $"❌ TakeCard fazası: '{card.Rank}' uyğun deyil. " +
                            $"İcazə verilən: {string.Join(", ", allowedRanksTake)}");
                }

                Console.WriteLine($"   ✅ TakeCard fazası rank yoxlaması keçdi");
                return AttackValidationResult.Success();
            }

            // Masa tamamilə boşdursa - istənilən kart (ilk hücum)
            if (openCards == 0 && defendedCards == 0)
            {
                Console.WriteLine($"   ✅ İlk hücum - rank yoxlaması yoxdur");
                return AttackValidationResult.Success();
            }

            // Beaten fazası - DefendedPairs rank-larına uyğun olmalı
            if (_room.IsBeatenPhaseActive)
            {
                var allowedRanksBeaten = _room.DefendedPairs
                    .SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank })
                    .ToHashSet();

                foreach (var card in cards)
                {
                    if (!allowedRanksBeaten.Contains(card.Rank))
                        return AttackValidationResult.Error(
                            $"❌ Beaten fazası: '{card.Rank}' uyğun deyil. " +
                            $"İcazə verilən: {string.Join(", ", allowedRanksBeaten)}");
                }

                Console.WriteLine($"   ✅ Beaten fazası rank yoxlaması keçdi");
                return AttackValidationResult.Success();
            }

            // Normal faza - mövcud rank-lara uyğun olmalı
            if (openCards > 0 || defendedCards > 0)
            {
                var existingRanks = _room.TableCards.Select(c => c.Rank)
                    .Concat(_room.DefendedPairs.SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank }))
                    .ToHashSet();

                foreach (var card in cards)
                {
                    if (!existingRanks.Contains(card.Rank))
                        return AttackValidationResult.Error(
                            $"❌ Kart masadakı rank-larla uyğun deyil: {card.Rank}. " +
                            $"İcazə verilən: {string.Join(", ", existingRanks)}");
                }
            }

            Console.WriteLine($"   ✅ Validation OK");
            return AttackValidationResult.Success();
        }
        public void ExecuteAttack(int userId, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return;

            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            foreach (var card in cards)
            {
                var cardToRemove = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardToRemove != null)
                {
                    player.Hand.Remove(cardToRemove);
                    _room.TableCards.Add(cardToRemove);
                }
            }

            if (!_room.IsThrowInPhaseActive && _room.Players.Count > 2)
            {
                if (_room.DefendedPairs.Count > 0 && _room.AttackerQueue.Count == 0)
                {
                    InitializeAttackerQueue();
                }
            }
        }
        #endregion

        #region Defend Logic

        public DefendValidationResult ValidateDefend(int userId, List<DefendPair> defenses)
        {
            if (_room.DefenderId != userId)
            {
                return DefendValidationResult.Error("Sizin müdafiə növbəniz deyil");
            }

            var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (defender == null)
            {
                return DefendValidationResult.Error("Oyunçu tapılmadı");
            }

            if (defenses == null || defenses.Count == 0)
            {
                return DefendValidationResult.Error("Ən azı 1 kartla müdafiə etməlisiniz");
            }

            var remainingTableCards = _room.TableCards.ToList();
            var remainingHandCards = defender.Hand.ToList();

            foreach (var defense in defenses)
            {
                var attackCardInTable = remainingTableCards.FirstOrDefault(c =>
                    c.Rank == defense.AttackCard.Rank && c.Suit == defense.AttackCard.Suit);

                if (attackCardInTable == null)
                {
                    return DefendValidationResult.Error("Hücum kartı masada yoxdur və ya artıq seçilib");
                }

                var defendCardInHand = remainingHandCards.FirstOrDefault(c =>
                    c.Rank == defense.DefendCard.Rank && c.Suit == defense.DefendCard.Suit);

                if (defendCardInHand == null)
                {
                    return DefendValidationResult.Error("Müdafiə kartı əlinizdə yoxdur və ya artıq istifadə edilib");
                }

                if (!CanDefend(attackCardInTable, defendCardInHand))
                {
                    return DefendValidationResult.Error("Bu kartla müdafiə edə bilməzsiniz");
                }

                remainingTableCards.Remove(attackCardInTable);
                remainingHandCards.Remove(defendCardInHand);
            }

            return DefendValidationResult.Success();
        }
        public (bool valid, string error, List<Card>? cards) ValidateAttackClean(
    int userId,
    List<Card> attackCards)
        {
            try
            {
                if (attackCards == null || attackCards.Count == 0)
                    return (false, "Ən azı 1 kart seçin", null);

                // 1️⃣ Player exists?
                var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                    return (false, "❌ Oyunçu tapılmadı", null);

                // 2️⃣ Not defender?
                if (_room.DefenderId == userId)
                    return (false, "❌ Müdafiəçi hücum edə bilməz", null);

                // 3️⃣ 2P special case
                if (_room.Players.Count == 2 && _room.AttackerId != userId)
                    return (false, "❌ 2 oyunçuda yalnız attacker hücum edə bilər", null);

                // 4️⃣ Cards in hand?
                var validatedCards = new List<Card>();
                foreach (var card in attackCards)
                {
                    var cardInHand = player.Hand.FirstOrDefault(c =>
                        c.Rank == card.Rank && c.Suit == card.Suit);

                    if (cardInHand == null)
                        return (false, $"❌ Kart əlinizdə yoxdur: {card.Rank} of {card.Suit}", null);

                    validatedCards.Add(cardInHand);
                }

                // 5️⃣ Max 6 cards total
                int totalCards = _room.TableCards.Count +
                                _room.DefendedPairs.Count +
                                validatedCards.Count;
                int openCardsToDefend = _room.TableCards.Count + validatedCards.Count;

                if (totalCards > 6)
                    return (false, "❌ Maksimum 6 kart masada ola bilər", null);

                // 6️⃣ Defender has cards to defend?
                var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
                if (defender != null && openCardsToDefend > defender.Hand.Count)
                    return (false, "❌ Müdafiəçinin kifayət qədər kartı yoxdur", null);

                // ✅ All good
                return (true, "", validatedCards);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ValidateAttackClean error: {ex.Message}");
                return (false, "❌ Sistem xətası", null);
            }
        }
        public (bool valid, string error) ValidateDefenseClean(
    int userId,
    List<(Card attackCard, Card defenseCard)> pairs)
        {
            try
            {
                // 1️⃣ Is defender?
                if (_room.DefenderId != userId)
                    return (false, "❌ Sizin müdafiə növbəniz deyil");

                // 2️⃣ Defender exists?
                var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
                if (defender == null)
                    return (false, "❌ Müdafiəçi tapılmadı");

                // 3️⃣ Cards to defend?
                if (_room.TableCards.Count == 0)
                    return (false, "❌ Müdafiə edəcək kart yoxdur");

                // 4️⃣ Each pair valid?
                foreach (var (attackCard, defenseCard) in pairs)
                {
                    // Attack card on table?
                    var tableCard = _room.TableCards.FirstOrDefault(c =>
                        c.Rank == attackCard.Rank && c.Suit == attackCard.Suit);

                    if (tableCard == null)
                        return (false, $"❌ Hücum kartı masada yoxdur: {attackCard}");

                    // Defense card in hand?
                    var handCard = defender.Hand.FirstOrDefault(c =>
                        c.Rank == defenseCard.Rank && c.Suit == defenseCard.Suit);

                    if (handCard == null)
                        return (false, $"❌ Müdafiə kartı əlinizdə yoxdur: {defenseCard}");

                    // Can defend with this card?
                    if (!CanDefend(attackCard, defenseCard))
                        return (false, $"❌ {defenseCard} ilə {attackCard} müdafiə edə bilməzsiniz");
                }

                // ✅ All good
                return (true, "");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ValidateDefenseClean error: {ex.Message}");
                return (false, "❌ Sistem xətası");
            }
        }

        public (bool valid, string error) ValidateTransferClean(int userId, Card card)
        {
            try
            {
                // 1️⃣ Transfer enabled?
                if (!_room.GameSettings.IsTransferEnabled)
                    return (false, "❌ Transfer bu otaqda aktiv deyil");

                // 2️⃣ Is defender?
                if (_room.DefenderId != userId)
                    return (false, "❌ Yalnız müdafiəçi transfer edə bilər");

                // 3️⃣ Only before defense starts
                if (_room.DefendedPairs.Count > 0)
                    return (false, "❌ Transfer yalnız ilk hücumda mümkündür");

                // 4️⃣ Cards on table?
                if (_room.TableCards.Count == 0)
                    return (false, "❌ Masada kart yoxdur");

                // 5️⃣ Card in hand?
                var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
                if (defender == null)
                    return (false, "❌ Müdafiəçi tapılmadı");

                if (!defender.Hand.Any(c => c.Rank == card.Rank && c.Suit == card.Suit))
                    return (false, $"❌ Kart əlinizdə yoxdur: {card}");

                if (defender.Hand.Count <= 1)
                    return (false, "❌ Son kartla pass/transfer etmək olmaz");

                // 6️⃣ Card matches table rank?
                var tableRanks = _room.TableCards.Select(c => c.Rank).ToHashSet();
                if (!tableRanks.Contains(card.Rank))
                    return (false, "❌ Transfer kartı masadakı kartlarla eyni rank-da olmalıdır");

                var nextDefender = GetNextDefenderAfter(userId);
                int attackCardsAfterTransfer = _room.TableCards.Count + 1;
                if (nextDefender == null || nextDefender.Hand.Count < attackCardsAfterTransfer)
                    return (false,
                        $"❌ Növbəti müdafiəçinin əlində {nextDefender?.Hand.Count ?? 0} kart var, " +
                        $"{attackCardsAfterTransfer} kartlıq hücumu müdafiə edə bilməz");

                // ✅ All good
                return (true, "");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ValidateTransferClean error: {ex.Message}");
                return (false, "❌ Sistem xətası");
            }
        }
        public GameStateSummary GetGameStateSummary()
        {
            try
            {
                var attacker = _room.AttackerId > 0
                    ? _room.Players.FirstOrDefault(p => p.UserId == _room.AttackerId)
                    : null;

                var defender = _room.DefenderId > 0
                    ? _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId)
                    : null;

                var allPlayers = _room.Players.Select(p => new PlayerGameState
                {
                    UserId = p.UserId,
                    Name = p.Name,
                    CardCount = p.Hand.Count,
                    IsAttacker = p.UserId == _room.AttackerId,
                    IsDefender = p.UserId == _room.DefenderId
                }).ToList();

                return new GameStateSummary
                {
                    RoomId = _room.RoomId,
                    RoomName = _room.RoomName,
                    AttackerName = attacker?.Name,
                    DefenderName = defender?.Name,
                    Players = allPlayers,
                    TableCardCount = _room.TableCards.Count,
                    DefendedPairCount = _room.DefendedPairs.Count,
                    DeckCount = _room.Deck?.Count ?? 0,
                    TrumpCard = _room.TrumpCard != null ? new CardData
                    {
                        Rank = _room.TrumpCard.Rank,
                        Suit = _room.TrumpCard.Suit
                    } : null,
                    IsThrowInPhaseActive = _room.IsThrowInPhaseActive,
                    IsGameActive = _room.IsGameActive,
                    GameMode = _room.GameSettings.GameMode.ToString(),
                    AttackMode = _room.GameSettings.AttackMode.ToString(),
                    TotalPrize = _room.TotalPrize,
                    EntryFee = _room.EntryFee
                };
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetGameStateSummary error: {ex.Message}");
                return new GameStateSummary();
            }
        }
        public bool CanDefend(Card attackCard, Card defendCard)
        {
            if (_room.TrumpCard == null) return false;

            bool attackIsTrump = attackCard.Suit == _room.TrumpCard.Suit;
            bool defendIsTrump = defendCard.Suit == _room.TrumpCard.Suit;

            // Trump kartı yalnız trump ilə
            if (attackIsTrump && !defendIsTrump)
                return false;

            // Trump olmayan kartı trump ilə vurmaq olar
            if (!attackIsTrump && defendIsTrump)
                return true;

            // Eyni suit - daha böyük rank
            if (attackCard.Suit == defendCard.Suit)
            {
                return GetCardValue(defendCard.Rank) > GetCardValue(attackCard.Rank);
            }

            return false;
        }

        public void ExecuteDefend(int userId, List<DefendPair> defenses)
        {
            var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (defender == null) return;

            foreach (var defense in defenses)
            {
                var attackCardInTable = _room.TableCards.FirstOrDefault(c =>
                    c.Rank == defense.AttackCard.Rank && c.Suit == defense.AttackCard.Suit);

                if (attackCardInTable == null)
                {
                    continue;
                }

                var defendCardInHand = defender.Hand.FirstOrDefault(c =>
                    c.Rank == defense.DefendCard.Rank && c.Suit == defense.DefendCard.Suit);

                if (defendCardInHand == null)
                {
                    continue;
                }

                _room.TableCards.Remove(attackCardInTable);
                defender.Hand.Remove(defendCardInHand);

                _room.DefendedPairs.Add(new DefendPair
                {
                    AttackCard = attackCardInTable,
                    DefendCard = defendCardInHand
                });
            }

            Console.WriteLine($"🛡️ {defender.Name} defended {defenses.Count} card(s)");

            // ✅ İLK MÜDAFİƏDƏN SONRA queue yarat (3+ oyunçu)
            if (_room.Players.Count > 2 && _room.DefendedPairs.Count == 1 && _room.AttackerQueue.Count == 0)
            {
                InitializeAttackerQueue();
                Console.WriteLine($"✅ Defender ilk kartı vurdu - Queue aktivləşdi");
            }
        }

        #endregion

        #region Transfer Logic

        public TransferValidationResult ValidateTransfer(int userId, Card card)
        {
            if (!_room.GameSettings.IsTransferEnabled)
                return TransferValidationResult.Error("Transfer bu otaqda aktiv deyil");

            if (_room.Players.Count < 2)
                return TransferValidationResult.Error("Transfer üçün minimum 2 oyunçu lazımdır");

            if (_room.DefenderId != userId)
                return TransferValidationResult.Error("Yalnız müdafiəçi transfer edə bilər");

            if (_room.DefendedPairs.Count > 0)
                return TransferValidationResult.Error("Transfer yalnız ilk hücumda mümkündür");

            if (_room.TableCards.Count == 0)
                return TransferValidationResult.Error("Masada kart yoxdur");

            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
                return TransferValidationResult.Error("Oyunçu tapılmadı");

            if (player.Hand.Count <= 1)
                return TransferValidationResult.Error(
                    $"❌ Transfer mümkün deyil! Əlinizdə {player.Hand.Count} kart var. Son kartla transfer etmək olmaz");

            var cardInHand = player.Hand.FirstOrDefault(c =>
                c.Rank == card.Rank && c.Suit == card.Suit);
            if (cardInHand == null)
                return TransferValidationResult.Error("Bu kart əlinizdə yoxdur");

            var allRanks = _room.TableCards.Select(c => c.Rank).ToHashSet();
            if (!allRanks.Contains(card.Rank))
                return TransferValidationResult.Error(
                    "Transfer kartı masadakı kartlarla eyni rank-da olmalıdır");

            var nextDefender = GetNextDefenderAfter(userId);
            int attackCardsAfterTransfer = _room.TableCards.Count + 1;
            if (nextDefender == null || nextDefender.Hand.Count < attackCardsAfterTransfer)
                return TransferValidationResult.Error(
                    $"❌ Növbəti müdafiəçinin əlində {nextDefender?.Hand.Count ?? 0} kart var, " +
                    $"{attackCardsAfterTransfer} kartlıq hücumu müdafiə edə bilməz");

            return TransferValidationResult.Success();
        }
        public int ExecuteTransfer(int userId, Card card)
        {
            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return 0;

            var cardInHand = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
            if (cardInHand != null)
            {
                player.Hand.Remove(cardInHand);
                _room.TableCards.Add(cardInHand);
            }

            int transfererId = userId;
            int oldAttackerId = _room.AttackerId;
            int currentDefenderIndex = _room.Players.FindIndex(p => p.UserId == _room.DefenderId);
            int nextDefenderIndex = (currentDefenderIndex + 1) % _room.Players.Count;

            if (_room.Players.Count > 2 && _room.Players[nextDefenderIndex].UserId == oldAttackerId)
                nextDefenderIndex = (nextDefenderIndex + 1) % _room.Players.Count;

            int newDefenderId = _room.Players[nextDefenderIndex].UserId;

            _room.AttackerId = transfererId;
            _room.DefenderId = newDefenderId;
            _room.LastTransferedPlayerId = userId;

            ResetAttackRound();

            var transferer = player;
            var newDefender = _room.Players[nextDefenderIndex];

            Console.WriteLine($"🔄 TRANSFER: {transferer.Name} → {newDefender.Name}");

            return _room.DefenderId;
        }

        #endregion

        #region Passing Logic

        public PassingValidationResult ValidatePass(int userId, Card card)
        {
            // STEP 1: Passing enabled?
            if (!_room.GameSettings.IsPassingEnabled)
                return PassingValidationResult.Error("Passing bu otaqda aktiv deyil");

            // STEP 2: Yalnız DEFENDER pass edə bilər (kartı növbəti oyunçuya ötürür)
            if (_room.DefenderId != userId)
                return PassingValidationResult.Error("Yalnız müdafiəçi pass edə bilər");

            // STEP 3: Masada kart varmı?
            if (_room.TableCards.Count == 0 && _room.DefendedPairs.Count == 0)
                return PassingValidationResult.Error("Passing yalnız hücumdan sonra mümkündür");

            // STEP 4: Hələ heç nə müdafiə edilməyib — passing yalnız ilk hücumda mümkündür
            if (_room.DefendedPairs.Count > 0)
                return PassingValidationResult.Error("Passing yalnız ilk hücumda mümkündür — artıq müdafiə etdiniz");

            // STEP 5: Oyunçu tapıl
            var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (defender == null)
                return PassingValidationResult.Error("Oyunçu tapılmadı");

            // STEP 6: Kart əldə varmı?
            var cardInHand = defender.Hand.FirstOrDefault(c =>
                c.Rank == card.Rank && c.Suit == card.Suit);

            if (cardInHand == null)
                return PassingValidationResult.Error("Bu kart əlinizdə yoxdur");

            if (defender.Hand.Count <= 1)
                return PassingValidationResult.Error(
                    $"❌ Passing mümkün deyil! Əlinizdə {defender.Hand.Count} kart var. Son kartla pass etmək olmaz");

            var tableRanks = _room.TableCards.Select(c => c.Rank).ToHashSet();
            if (!tableRanks.Contains(card.Rank))
                return PassingValidationResult.Error(
                    "Passing kartı masadakı kartlarla eyni rank-da olmalıdır");

            // STEP 8: Növbəti oyunçu varmı? (2P də dəstəklənir)
            if (_room.Players.Count < 2)
                return PassingValidationResult.Error("Passing üçün minimum 2 oyunçu lazımdır");

            var nextDefender = GetNextDefenderAfter(userId);
            int attackCardsAfterPass = _room.TableCards.Count + 1;
            if (nextDefender == null || nextDefender.Hand.Count < attackCardsAfterPass)
                return PassingValidationResult.Error(
                    $"❌ Növbəti müdafiəçinin əlində {nextDefender?.Hand.Count ?? 0} kart var, " +
                    $"{attackCardsAfterPass} kartlıq hücumu müdafiə edə bilməz");

            Console.WriteLine($"   ✅ Pass mümkündür");
            return PassingValidationResult.Success();
        }

        private DurakPlayer? GetNextDefenderAfter(int currentDefenderId)
        {
            int currentDefenderIndex = _room.Players.FindIndex(p => p.UserId == currentDefenderId);
            if (currentDefenderIndex < 0 || _room.Players.Count < 2)
                return null;

            int oldAttackerId = _room.AttackerId;
            int nextDefenderIndex = (currentDefenderIndex + 1) % _room.Players.Count;

            if (_room.Players.Count > 2 && _room.Players[nextDefenderIndex].UserId == oldAttackerId)
                nextDefenderIndex = (nextDefenderIndex + 1) % _room.Players.Count;

            return _room.Players[nextDefenderIndex];
        }
        public bool IsMainAttacker(int userId)
        {
            return _room.AttackerId == userId;
        }
        public bool IsInAttackerQueue(int userId)
        {
            return _room.AttackerQueue.Contains(userId);
        }
        public bool HasQueueBug()
        {
            if (_room.AttackerQueue.Contains(_room.AttackerId))
            {
                Console.WriteLine("🚫 BUG: Main Attacker queue-də olarsa!");
                return true;
            }
            return false;
        }
        public int ExecutePassing(int userId, Card card)
        {
            var defender = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (defender == null) return 0;

            var cardInHand = defender.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
            if (cardInHand != null)
            {
                defender.Hand.Remove(cardInHand);
                _room.TableCards.Add(cardInHand);
            }

            int oldDefenderId = _room.DefenderId;
            int oldAttackerId = _room.AttackerId;

            // Köhnə defender-dən sonrakı oyunçu → yeni defender
            int oldDefenderIndex = _room.Players.FindIndex(p => p.UserId == oldDefenderId);
            int newDefenderIndex = (oldDefenderIndex + 1) % _room.Players.Count;

            // 3P+ oyunda yeni defender köhnə attacker olmamalıdır.
            // 2P oyunda isə passing-dən sonra köhnə attacker yeni defender olmalıdır.
            if (_room.Players.Count > 2 && _room.Players[newDefenderIndex].UserId == oldAttackerId)
                newDefenderIndex = (newDefenderIndex + 1) % _room.Players.Count;

            int newDefenderId = _room.Players[newDefenderIndex].UserId;

            _room.AttackerId = oldDefenderId;  // Köhnə defender → yeni attacker
            _room.DefenderId = newDefenderId;  // Növbəti oyunçu → yeni defender

            ResetAttackRound();

            Console.WriteLine($"✅ PASSING:");
            Console.WriteLine($"   Old Attacker: {_room.Players.FirstOrDefault(p => p.UserId == oldAttackerId)?.Name} → normal oyunçu");
            Console.WriteLine($"   Old Defender ({defender.Name}) → YENİ ATTACKER");
            Console.WriteLine($"   YENİ DEFENDER: {_room.Players[newDefenderIndex].Name}");

            return newDefenderId;
        }
        #endregion

        #region Pass Logic

        public PassValidationResult ValidatePass(int userId)
        {
            if (_room.DefenderId == userId)
            {
                return PassValidationResult.Error("Müdafiəçi pas edə bilməz");
            }

            if (_room.TableCards.Count == 0 && _room.DefendedPairs.Count == 0)
            {
                return PassValidationResult.Error("İlk hücumdan əvvəl pas edilə bilməz");
            }

            if (_room.Players.Count == 2)
            {
                return PassValidationResult.Error("2 oyunçuda pas mümkün deyil");
            }

            if (_room.AttackerQueue.Count == 0)
            {
                return PassValidationResult.Error("Defender hələ müdafiə etməyib");
            }

            var currentAttacker = GetCurrentAttackerInQueue();
            if (currentAttacker == null || currentAttacker != userId)
            {
                return PassValidationResult.Error("Sırada deyilsiniz");
            }

            if (_room.PlayersWhoPassedThisRound.Contains(userId))
            {
                return PassValidationResult.Error("Artıq pas etdiniz");
            }

            return PassValidationResult.Success();
        }

        public int? ExecutePass(int userId)
        {
            PlayerPassThisRound(userId);
            MoveToNextAttackerInQueue();

            var nextAttacker = GetCurrentAttackerInQueue();

            if (nextAttacker == null || nextAttacker == 0)
            {
                Console.WriteLine($"🛑 Hamı pas etti");
                return null;
            }

            return nextAttacker;
        }

        #endregion

        #region Beaten Logic

        public bool CanBeat(int userId)
        {
            Console.WriteLine($"");
            Console.WriteLine($"🔥 BEATEN CHECK:");

            // ❌ Main Attacker deyilsə
            if (_room.AttackerId != userId)
            {
                Console.WriteLine($"   ❌ Main Attacker deyil");
                return false;
            }

            Console.WriteLine($"   ✅ Main Attacker");

            // ❌ Masada açıq kart varsa
            if (_room.TableCards.Count > 0)
            {
                Console.WriteLine($"   ❌ Masada {_room.TableCards.Count} açıq kart var");
                return false;
            }

            Console.WriteLine($"   ✅ Masada açıq kart yoxdur");

            // ❌ Müdafiə edən kart yoksa
            if (_room.DefendedPairs.Count == 0)
            {
                Console.WriteLine($"   ❌ Müdafiə edən kart yoxdur");
                return false;
            }

            Console.WriteLine($"   ✅ Müdafiə edən {_room.DefendedPairs.Count} kart var");
            Console.WriteLine($"   ✅✅✅ BEATEN MÜMKÜN!");

            return true;
        }
        public void ExecuteBeat()
        {
            Console.WriteLine($"");
            int openCards = _room.TableCards.Count;
            int defendedPairs = _room.DefendedPairs.Count;
            int discardedCards = openCards + (defendedPairs * 2);

            _room.TableCards.Clear();
            _room.DefendedPairs.Clear();

            Console.WriteLine($"🔥🔥🔥 BEATEN TAMAMLANDI 🔥🔥🔥");
            Console.WriteLine($"   OpenCards: {openCards}");
            Console.WriteLine($"   DefendedPairs: {defendedPairs}");
            Console.WriteLine($"   DiscardedCards: {discardedCards}");
            Console.WriteLine($"");
        }
        private void StartBrokenBeaten()
        {
            // ✅ STEP 1: Queue yaradıl
            CreateBrokenBeatenQueue();

            Console.WriteLine($"   ✅ Queue yaradıldı:");
            foreach (var userId in _room.AttackerQueue)
            {
                var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
                Console.WriteLine($"      - {player?.Name}");
            }

            Console.WriteLine($"   📊 Masada müdafiə edən: {_room.DefendedPairs.Count} kart çifti");
            Console.WriteLine($"   📊 Maksimum hücum: {GetMaxNewAttackCards()} kart");
        }
        private void CreateBrokenBeatenQueue()
        {
            _room.AttackerQueue.Clear();
            _room.PlayersWhoPassedThisRound.Clear();
            _room.CurrentAttackerQueueIndex = 0;

            int mainAttackerIndex = _room.Players.FindIndex(p => p.UserId == _room.AttackerId);
            int defenderIndex = _room.Players.FindIndex(p => p.UserId == _room.DefenderId);

            if (_room.GameSettings.AttackMode == AttackMode.Neighbour)
            {
                // ✅ NEIGHBOUR: Defender-in sağı + solu
                int rightIndex = (defenderIndex + 1) % _room.Players.Count;
                int leftIndex = (defenderIndex - 1 + _room.Players.Count) % _room.Players.Count;

                if (_room.Players[rightIndex].UserId != _room.AttackerId)
                    _room.AttackerQueue.Add(_room.Players[rightIndex].UserId);

                if (leftIndex != rightIndex && _room.Players[leftIndex].UserId != _room.AttackerId)
                    _room.AttackerQueue.Add(_room.Players[leftIndex].UserId);
            }
            else
            {
                // ✅ ALL: Bütün oyunçular (Main Attacker dışında)
                for (int i = 1; i < _room.Players.Count; i++)
                {
                    int index = (mainAttackerIndex + i) % _room.Players.Count;
                    if (_room.Players[index].UserId != _room.AttackerId)
                    {
                        _room.AttackerQueue.Add(_room.Players[index].UserId);
                    }
                }
            }
        }

        private int GetCurrentAttackCardCount()
        {
            return _room.DefendedPairs.Count + _room.TableCards.Count;
        }

        private int GetMaxNewAttackCards()
        {
            int currentAttackCardCount = GetCurrentAttackCardCount();
            int maxByTableLimit = 6 - currentAttackCardCount;

            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender == null)
                return Math.Max(0, maxByTableLimit);

            int openCards = _room.TableCards.Count;
            int maxByDefenderCards = defender.Hand.Count - openCards;

            int maxNew = Math.Min(maxByTableLimit, maxByDefenderCards);
            return Math.Max(0, maxNew);
        }
        private void InitializeBrokenBeatenQueue()
        {
            _room.AttackerQueue.Clear();
            _room.PlayersWhoPassedThisRound.Clear();

            int mainAttackerIndex = _room.Players.FindIndex(p => p.UserId == _room.AttackerId);

            // ✅ YENİ LOGIC: Main Attacker HÜCUM ETDİ, queue yoxdur
            // Queue yalnız digər oyunçular (Defender istisna)

            for (int i = 1; i < _room.Players.Count; i++)
            {
                int index = (mainAttackerIndex + i) % _room.Players.Count;
                if (_room.Players[index].UserId != _room.DefenderId)
                {
                    _room.AttackerQueue.Add(_room.Players[index].UserId);
                }
            }

            _room.CurrentAttackerQueueIndex = 0;
        }
        #endregion

        #region Round Management

        private int FindNextPlayerId(int afterUserId, params int[] excludedUserIds)
        {
            if (_room.Players.Count == 0) return 0;

            var excluded = excludedUserIds.ToHashSet();
            var startIndex = _room.Players.FindIndex(p => p.UserId == afterUserId);
            if (startIndex < 0) startIndex = 0;

            for (var offset = 1; offset <= _room.Players.Count; offset++)
            {
                var candidate = _room.Players[(startIndex + offset) % _room.Players.Count];
                if (!excluded.Contains(candidate.UserId))
                    return candidate.UserId;
            }

            return _room.Players[startIndex].UserId;
        }

        public void MoveToNextRound(bool defenderTookCards)
        {
            if (defenderTookCards)
            {
                int oldAttackerId = _room.AttackerId;
                int oldDefenderId = _room.DefenderId;

                if (_room.Players.Count > 2)
                {
                    var newAttackerId = FindNextPlayerId(oldDefenderId, oldDefenderId);
                    var newDefenderId = FindNextPlayerId(newAttackerId, oldDefenderId, newAttackerId);
                    _room.AttackerId = newAttackerId;
                    _room.DefenderId = newDefenderId;
                    Console.WriteLine($"🔄 TAKE (3P+): Defender skip edildi");
                }
                else
                {
                    _room.AttackerId = oldAttackerId;
                    _room.DefenderId = oldDefenderId;
                    Console.WriteLine($"🔄 TAKE (2P): Attacker davam edir");
                }
            }
            else
            {
                int oldDefenderId = _room.DefenderId;
                _room.AttackerId = oldDefenderId;
                _room.DefenderId = FindNextPlayerId(oldDefenderId, oldDefenderId);

                Console.WriteLine($"🔄 BEATEN: Old defender yeni attacker oldu");
            }

            ResetAttackRound();
        }
        public void RefillHands()
        {
            // Attacker birinci
            var attacker = _room.Players.FirstOrDefault(p => p.UserId == _room.AttackerId);
            if (attacker != null)
                while (attacker.Hand.Count < 6 && _room.Deck.Count > 0)
                {
                    attacker.Hand.Add(_room.Deck[0]);
                    _room.Deck.RemoveAt(0);
                }

            // Digər oyunçular
            foreach (var player in _room.Players
                .Where(p => p.UserId != _room.AttackerId && p.UserId != _room.DefenderId))
                while (player.Hand.Count < 6 && _room.Deck.Count > 0)
                {
                    player.Hand.Add(_room.Deck[0]);  // ✅ player, attacker deyil
                    _room.Deck.RemoveAt(0);
                }

            // Defender sonuncu
            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender != null)
                while (defender.Hand.Count < 6 && _room.Deck.Count > 0)
                {
                    defender.Hand.Add(_room.Deck[0]);
                    _room.Deck.RemoveAt(0);
                }
        }
        #endregion

        #region Game End Logic

        public GameEndResult? CheckGameOver()
        {
            if (_room.Deck.Count > 0)
            {
                Console.WriteLine($"📊 Deck-də qalan: {_room.Deck.Count} kart");
                return null;
            }

            var playersWithNoCards = _room.Players.Where(p => p.Hand.Count == 0).ToList();
            var playersWithCards = _room.Players.Where(p => p.Hand.Count > 0).ToList();

            if (playersWithNoCards.Count == 0)
                return null;

            // ✅ DRAW MODE
            if (_room.GameSettings.GameMode == GameMode.Draw)
            {
                var attacker = _room.Players.FirstOrDefault(p => p.UserId == _room.AttackerId);
                var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);

                bool attackerFinished = attacker.Hand.Count == 0;
                bool defenderFinished = defender.Hand.Count == 0;

                if (attackerFinished && defenderFinished)
                {
                    Console.WriteLine($"🤝 DRAW: Attacker və defender son kartlarını eyni əldə bitirdi!");
                    return new GameEndResult
                    {
                        Winners = new List<DurakPlayer> { attacker, defender },
                        Durak = null,
                        IsDraw = true
                    };
                }

                if (attackerFinished && !defenderFinished)
                {
                    // ✅ Masada kart varsa - defender hələ götürməyib, oyun bitməyib
                    if (_room.TableCards.Count > 0 || _room.DefendedPairs.Count > 0)
                    {
                        Console.WriteLine($"⏳ Attacker bitdi amma masada kart var - defender götürməyi gözlə");
                        return null;
                    }

                    Console.WriteLine($"⚔️ ATTACKER QALIB: {attacker.Name}");
                    return new GameEndResult
                    {
                        Winners = new List<DurakPlayer> { attacker },
                        Durak = defender,
                        IsDraw = false
                    };
                }

                if (!attackerFinished && defenderFinished)
                {
                    Console.WriteLine($"🛡️ DEFENDER QALIB: {defender.Name}");
                    return new GameEndResult
                    {
                        Winners = new List<DurakPlayer> { defender },
                        Durak = attacker,
                        IsDraw = false
                    };
                }
            }
            // ✅ CLASSIC MODE
            else
            {
                var winner = playersWithNoCards[0];
                var durak = playersWithCards.OrderByDescending(p => p.Hand.Count).FirstOrDefault();

                Console.WriteLine($"🏆 CLASSIC WIN: {winner.Name}");
                return new GameEndResult
                {
                    Winners = new List<DurakPlayer> { winner },
                    Durak = durak,
                    IsDraw = false
                };
            }

            return null;
        }
        #endregion

        #region Broken Beaten Attack Logic

        public bool CanAttackInBrokenBeaten(int userId)
        {
            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return false;

            // Queue yoxdursa
            if (_room.AttackerQueue.Count == 0) return false;

            // Növbə müdürse
            var currentAttacker = GetCurrentAttackerInQueue();
            if (currentAttacker == null || currentAttacker != userId) return false;

            // Pas etmişsə
            if (_room.PlayersWhoPassedThisRound.Contains(userId)) return false;

            // ✅ Əlində masadakı rank-lara uyğun kart varmı?
            var allowedRanks = _room.DefendedPairs
                .SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank })
                .Union(_room.TableCards.Select(c => c.Rank))
                .Distinct()
                .ToHashSet();

            if (allowedRanks.Count == 0) return false;

            foreach (var card in player.Hand)
            {
                if (allowedRanks.Contains(card.Rank))
                {
                    return true; // ✅ Ən azı 1 uyğun kart var
                }
            }

            return false; // ❌ Uyğun kart yoxdur
        }
        public AttackValidationResult ValidateBrokenBeatenAttack(int userId, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return AttackValidationResult.Error("Ən azı 1 kart seçin");

            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
                return AttackValidationResult.Error("Oyunçu tapılmadı");

            if (userId == _room.AttackerId)
                return AttackValidationResult.Error(
                    "❌ Main Attacker Broken Beaten-də hücum edə bilməz!");

            // 1️⃣ Queue yoxdursa
            if (_room.AttackerQueue.Count == 0)
                return AttackValidationResult.Error("Broken beaten queue aktiv deyil");

            // 2️⃣ Sırada olan oyunçu mu?
            var currentAttacker = GetCurrentAttackerInQueue();
            if (currentAttacker == null || currentAttacker == 0)
                return AttackValidationResult.Error("Queue bitib");
            if (currentAttacker != userId)
                return AttackValidationResult.Error("Şu an sizin sıranız deyil");

            // 3️⃣ Pas etmişsə?
            if (_room.PlayersWhoPassedThisRound.Contains(userId))
                return AttackValidationResult.Error("Siz bu raundda pas etdiniz");

            // 4️⃣ Kartlar əldə varmı?
            foreach (var card in cards)
            {
                var cardInHand = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardInHand == null)
                    return AttackValidationResult.Error("Kart əlinizdə yoxdur");
            }

            // 5️⃣ Yalnız masadakı rank-lara uyğun
            var allowedRanks = _room.DefendedPairs
                .SelectMany(p => new[] { p.AttackCard.Rank, p.DefendCard.Rank })
                .Union(_room.TableCards.Select(c => c.Rank))
                .Distinct()
                .ToHashSet();

            if (allowedRanks.Count == 0)
                return AttackValidationResult.Error("Masada kart yoxdur");

            foreach (var card in cards)
            {
                if (!allowedRanks.Contains(card.Rank))
                    return AttackValidationResult.Error(
                        "Broken Beaten: Yalnız masadakı rank-lara uyğun kartlar");
            }

            // 6️⃣ Defender yoxlaması
            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender == null)
                return AttackValidationResult.Error("Müdafiəçi tapılmadı");

            int defenderCards = defender.Hand.Count;
            if (defenderCards <= 0)
                return AttackValidationResult.Error("Defender əlində kart yoxdur");

            // 7️⃣ Raundda maksimum 6 hücum kartı ola bilər
            int openCards = _room.TableCards.Count;
            int currentAttackCardCount = GetCurrentAttackCardCount();
            int maxNewCardsByTableLimit = 6 - currentAttackCardCount;

            if (maxNewCardsByTableLimit <= 0)
                return AttackValidationResult.Error(
                    $"Masada artıq maksimum hücum kartı var ({currentAttackCardCount}/6)");

            if (cards.Count > maxNewCardsByTableLimit)
                return AttackValidationResult.Error(
                    $"❌ Maksimum {maxNewCardsByTableLimit} yeni kart ata bilərsiniz (ümumi limit 6)");

            // 8️⃣ Defender əlindəki kart limiti
            // Yalnız açıq qalan kartlar defender-in qalan əli ilə bağlanmalıdır
            int maxNewCardsByDefender = defenderCards - openCards;
            if (maxNewCardsByDefender <= 0)
                return AttackValidationResult.Error(
                    $"❌ Defender əlində {defenderCards} kart var, masada {openCards} açıq kart var");

            if (cards.Count > maxNewCardsByDefender)
                return AttackValidationResult.Error(
                    $"❌ Defender əlində {defenderCards} kart var, " +
                    $"masada {openCards} açıq var. " +
                    $"Maksimum {Math.Max(0, maxNewCardsByDefender)} yeni kart ata bilərsiniz");

            Console.WriteLine(
                $"   ✅ Broken Beaten Attack OK - {cards.Count} kart, openCards={openCards}, totalAttackCards={currentAttackCardCount}, maxNew={Math.Min(maxNewCardsByTableLimit, maxNewCardsByDefender)}");
            return AttackValidationResult.Success();
        }
        public void ExecuteBrokenBeatenAttack(int userId, List<Card> cards)
        {
            if (cards == null || cards.Count == 0)
                return;

            var player = _room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            // ✅ Kartları əldən çıxart və MASAYA QOY
            // DefendedPairs-ə TOXUNMA - onlar masada qalmalıdır
            foreach (var card in cards)
            {
                var cardToRemove = player.Hand.FirstOrDefault(c => c.Rank == card.Rank && c.Suit == card.Suit);
                if (cardToRemove != null)
                {
                    player.Hand.Remove(cardToRemove);
                    _room.TableCards.Add(cardToRemove); // ✅ TableCards-a əlavə et
                }
            }

            Console.WriteLine($"⚔️ BROKEN BEATEN - {player.Name} {cards.Count} kart əlavə etdi");
            Console.WriteLine($"   TableCards: {_room.TableCards.Count}");
            Console.WriteLine($"   DefendedPairs: {_room.DefendedPairs.Count} (TOXUNULMADI)");
        }

        public PassValidationResult ValidateBrokenBeatenPass(int userId)
        {
            Console.WriteLine($"🛑 BB Pass validation - userId: {userId}, AttackerId: {_room.AttackerId}");

            // ❌ KRITIK: Main Attacker pas YAPMAZ!
            if (userId == _room.AttackerId)
            {
                return PassValidationResult.Error(
                    "❌ Main Attacker Broken Beaten-də pas YAPMAZ!");
            }

            // 1️⃣ Queue aktif olmalı
            if (_room.AttackerQueue.Count == 0)
                return PassValidationResult.Error("Broken beaten queue aktiv deyil");

            // 2️⃣ Sırada olan oyunçu mu?
            var currentAttacker = GetCurrentAttackerInQueue();
            if (currentAttacker == null || currentAttacker == 0)
                return PassValidationResult.Error("Queue bitib");

            if (currentAttacker != userId)
                return PassValidationResult.Error("Şu an sizin sıranız deyil");

            // 3️⃣ Artıq pas etmişsə?
            if (_room.PlayersWhoPassedThisRound.Contains(userId))
                return PassValidationResult.Error("Artıq pas etdiniz");

            return PassValidationResult.Success();
        }
        public int? ExecuteBrokenBeatenPass(int userId)
        {
            PlayerPassThisRound(userId);
            MoveToNextAttackerInQueue();

            var nextAttacker = GetCurrentAttackerInQueue();

            // ✅ HAMISI PAS ETDİMİ?
            if (nextAttacker == null || nextAttacker == 0)
            {
                Console.WriteLine($"🛑 HAMISI PAS ETDİ - Defender müdafiə edə bilərmi yoxla");
                return null;  // NULL = hamı pas etti
            }

            return nextAttacker;
        }
        public void CheckBrokenBeatenDefenderStatus()
        {
            var defender = _room.Players.FirstOrDefault(p => p.UserId == _room.DefenderId);
            if (defender == null) return;

            Console.WriteLine($"");
            Console.WriteLine($"🔍 Defender müdafiə edə biləcəkmi?");
            Console.WriteLine($"   Defender əlində: {defender.Hand.Count} kart");
            Console.WriteLine($"   Masada müdafiə edən: {_room.DefendedPairs.Count}");

            // ✅ Defender bütün müdafiə edə bilərsə
            bool canDefendAll = true;

            foreach (var attackCard in _room.DefendedPairs.Select(p => p.AttackCard))
            {
                bool canDefendThis = false;

                foreach (var defenseCard in defender.Hand)
                {
                    if (CanDefend(attackCard, defenseCard))
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

            if (canDefendAll)
            {
                Console.WriteLine($"   ✅ Defender müdafiə edə bilər → BEATEN BAŞLAYACAQ");
                // ExecuteBrokenBeatenBeaten() çağır
            }
            else
            {
                Console.WriteLine($"   ❌ Defender müdafiə edə bilmir → TAKE CARDS");
                // ExecuteDefenderTakesCards() çağır
            }
        }
        public void CompleteBrokenBeaten()
        {
            Console.WriteLine($"🔥 BROKEN BEATEN TAMAMLANDI - Kartlar yandırılır");
            Console.WriteLine($"   Yandırılan kartlar: {_room.TableCards.Count}");

            // ✅ Bütün kartları yandır
            _room.TableCards.Clear();
            _room.DefendedPairs.Clear();

            // ✅ Queue təmizlə
            ResetAttackRound();

            Console.WriteLine($"✅ Raund tamamlandı");
        }

        #endregion

        public bool AreAllCardsDefended()
        {
            return _room.TableCards.Count == 0 && _room.DefendedPairs.Count > 0;
        }

        public void InitializeAttackerQueue()
        {
            _room.AttackerQueue.Clear();
            _room.PlayersWhoPassedThisRound.Clear();
            _room.CurrentAttackerQueueIndex = 0;

            int mainAttackerIndex = _room.Players.FindIndex(p => p.UserId == _room.AttackerId);
            if (mainAttackerIndex == -1) return;

            if (_room.GameSettings.AttackMode == AttackMode.Neighbour)
            {
                int defenderIndex = _room.Players.FindIndex(p => p.UserId == _room.DefenderId);
                if (defenderIndex == -1) return;

                int neighbourIndex = (defenderIndex + 1) % _room.Players.Count;
                int neighbourUserId = _room.Players[neighbourIndex].UserId;

                if (neighbourUserId != _room.DefenderId && neighbourUserId != _room.AttackerId)
                {
                    _room.AttackerQueue.Add(neighbourUserId); // ✅ YALNIZ KOMŞU
                }
            }
            else
            {
                // ✅ Main Attacker ƏLAVƏ ETMƏ! O HÜCUM ETDİ!
                // Queue = Main Attacker DÖŞÜNDƏKİ DİĞƏR oyunçular

                for (int i = 1; i < _room.Players.Count; i++)
                {
                    int index = (mainAttackerIndex + i) % _room.Players.Count;

                    // ✅ Defender istisna
                    if (_room.Players[index].UserId != _room.DefenderId)
                    {
                        _room.AttackerQueue.Add(_room.Players[index].UserId);
                    }
                }
            }

            Console.WriteLine($"✅ AttackerQueue başlandı:");
            Console.WriteLine($"   Main Attacker: {_room.Players.First(p => p.UserId == _room.AttackerId).Name}");
            Console.WriteLine($"   Queue: {string.Join(" → ", _room.AttackerQueue.Select(id => _room.Players.First(p => p.UserId == id).Name))}");
            Console.WriteLine($"   Defender: {_room.Players.First(p => p.UserId == _room.DefenderId).Name}");
        }

        public int? GetCurrentAttackerInQueue()
        {
            if (_room.AttackerQueue.Count == 0) return null;
            if (_room.CurrentAttackerQueueIndex < 0) return null;
            if (_room.CurrentAttackerQueueIndex >= _room.AttackerQueue.Count) return null;

            return _room.AttackerQueue[_room.CurrentAttackerQueueIndex];
        }


        public void MoveToNextAttackerInQueue()
        {
            if (_room.AttackerQueue.Count == 0) return;

            int nextIndex = _room.CurrentAttackerQueueIndex + 1;

            if (nextIndex >= _room.AttackerQueue.Count)
            {
                _room.CurrentAttackerQueueIndex = -1;
                return;
            }

            int attempts = 0;
            while (attempts < _room.AttackerQueue.Count)
            {
                var nextPlayer = _room.AttackerQueue[nextIndex];

                if (!_room.PlayersWhoPassedThisRound.Contains(nextPlayer))
                {
                    _room.CurrentAttackerQueueIndex = nextIndex;
                    return;
                }

                nextIndex++;
                attempts++;

                if (nextIndex >= _room.AttackerQueue.Count)
                {
                    _room.CurrentAttackerQueueIndex = -1;
                    return;
                }
            }

            _room.CurrentAttackerQueueIndex = -1;
        }
        public void PlayerPassThisRound(int userId)
        {
            _room.PlayersWhoPassedThisRound.Add(userId);
        }

        public void ResetAttackRound()
        {
            _room.AttackerQueue.Clear();
            _room.CurrentAttackerQueueIndex = 0;
            _room.PlayersWhoPassedThisRound.Clear();
            _room.MainAttackerFinished = false;
            _room.IsThrowInPhaseActive = false;
            _room.IsBrokenBeatenPhaseActive = false;
            _room.IsBeatenPhaseActive = false;
            _room.IsTakeCardPhaseActive = false;
        }


        private int GetCardValue(string rank)
        {
            return rank switch
            {
                "2" => 2,
                "3" => 3,
                "4" => 4,
                "5" => 5,
                "6" => 6,
                "7" => 7,
                "8" => 8,
                "9" => 9,
                "10" => 10,
                "Jack" => 11,
                "Queen" => 12,
                "King" => 13,
                "Ace" => 14,
                _ => 0
            };
        }
    }

    #region Validation Result Classes

    public class AttackValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public static AttackValidationResult Success() => new() { IsValid = true };
        public static AttackValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    public class DefendValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public static DefendValidationResult Success() => new() { IsValid = true };
        public static DefendValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    public class TransferValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public static TransferValidationResult Success() => new() { IsValid = true };
        public static TransferValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    public class PassValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public static PassValidationResult Success() => new() { IsValid = true };
        public static PassValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    #endregion

    #region Enums

    public enum GameMode { Classic, Draw }
    public enum AttackMode { All, Neighbour }

    #endregion

    #region Models

    public class DurakRoom
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public int MaxPlayers { get; set; }
        public int PlayerCount => Players?.Count ?? 0;
        public decimal EntryFee { get; set; }
        public decimal TotalPrize { get; set; }
        public bool IsGameActive { get; set; }
        public bool IsQuickRoom { get; set; }
        public int CreatorUserId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public bool TransferEnabled { get; set; } = true;
        public GameMode GameMode => GameSettings.GameMode;
        public GameSettings GameSettings { get; set; } = new();
        public int DeckSize { get; set; } = 36;


        public bool IsWaitingForAttackerApproval { get; set; } = false;
        public bool IsThrowInPhaseActive { get; set; } = false;
        public bool IsBrokenBeatenPhaseActive { get; set; } = false;

        public bool IsBeatenPhaseActive { get; set; } = false;
        public bool IsTakeCardPhaseActive { get; set; } = false;

        public List<DurakPlayer> Players { get; set; } = new();
        public List<Card> Deck { get; set; } = new();
        public Card? TrumpCard { get; set; }
        public List<Card> TableCards { get; set; } = new();
        public List<DefendPair> DefendedPairs { get; set; } = new();

        public int WinnerId { get; set; } = 0;
        public DateTime? FinishedAt { get; set; }
        public string GameStatus { get; set; } = "Waiting";
        public decimal Balance { get; set; } = 0;


        public HashSet<int> ReadyPlayers { get; set; } = new();
        public bool AllPlayersReady
        {
            get
            {
                var result = ReadyPlayers.Count == Players.Count && Players.Count >= 2;
                Console.WriteLine($"🔍 AllPlayersReady check: {ReadyPlayers.Count}/{Players.Count} players, result={result}");
                return result;
            }
        }


        public int AttackerId { get; set; }
        public int DefenderId { get; set; }

        public List<int> AttackerQueue { get; set; } = new();
        public int CurrentAttackerQueueIndex { get; set; } = 0;
        public HashSet<int> PlayersWhoPassedThisRound { get; set; } = new();
        public bool MainAttackerFinished { get; set; } = false;
        public HashSet<int> TakeCardsVotes { get; set; } = new();
        public HashSet<int> RematchVotes { get; set; } = new();
        public HashSet<int> RematchDeclines { get; set; } = new();
        public DateTime? RematchDeadlineUtc { get; set; }
        public CancellationTokenSource? RematchTimerCts { get; set; }
        public HashSet<int> BeatenVotes { get; set; } = new();
        public int? LastTransferedPlayerId { get; set; }
        public CancellationTokenSource? TurnTimerCts { get; set; }
        public DateTime? TurnDeadlineUtc { get; set; }
        public int? TurnPlayerId { get; set; }
        public string? TurnActionKind { get; set; }
        public string? TurnStateKey { get; set; }
        public int TurnDurationSeconds { get; set; }
        public int TurnTimerSequence { get; set; }
        public Dictionary<int, int> ExtraTimeRemaining { get; set; } = new();

        // ✅ YENİ FLAG
        public string? LastWinner { get; set; }
        public string? LastDurak { get; set; }
        public DateTime? GameEndTime { get; set; }

        public object StateLock { get; } = new object();

        private DurakGameEngine? _gameEngine;
        public DurakGameEngine GameEngine => _gameEngine ??= new DurakGameEngine(this);

        public void StartNewGame() => GameEngine.InitializeGame();

        public void MarkMainAttackerFinished()
        {
            MainAttackerFinished = true;
            Console.WriteLine($"✅ Main Attacker finish oldu - Queue restart başlaya bilər");
        }


        public void ResetGame()
        {
            TurnTimerCts?.Cancel();
            TurnTimerCts?.Dispose();
            TurnTimerCts = null;
            TurnDeadlineUtc = null;
            TurnPlayerId = null;
            TurnActionKind = null;
            TurnStateKey = null;
            TurnDurationSeconds = 0;
            TurnTimerSequence = 0;
            ExtraTimeRemaining.Clear();
            GameStatus = "Waiting";
            IsGameActive = false;
            Deck.Clear();
            TrumpCard = null;
            TableCards.Clear();
            DefendedPairs.Clear();
            TakeCardsVotes.Clear();
            RematchVotes.Clear();
            RematchDeclines.Clear();
            RematchDeadlineUtc = null;
            RematchTimerCts?.Cancel();
            RematchTimerCts?.Dispose();
            RematchTimerCts = null;
            BeatenVotes.Clear();
            PlayersWhoPassedThisRound.Clear();
            AttackerQueue.Clear();
            LastTransferedPlayerId = null;
            MainAttackerFinished = false;
            IsBeatenPhaseActive = false;
            IsTakeCardPhaseActive = false;
            CurrentAttackerQueueIndex = 0;
            IsThrowInPhaseActive = false;
            IsBrokenBeatenPhaseActive = false;

            foreach (var player in Players)
                player.Hand.Clear();

            Console.WriteLine($"🔄 Game reset: {RoomName}");
        }

        public GameEndResult? CheckGameOver() => GameEngine.CheckGameOver();
        public bool CanDefend(Card attackCard, Card defendCard) => GameEngine.CanDefend(attackCard, defendCard);
        public bool AreAllCardsDefended() => GameEngine.AreAllCardsDefended();
        public void InitializeAttackerQueue() => GameEngine.InitializeAttackerQueue();
        public int? GetCurrentAttackerInQueue() => GameEngine.GetCurrentAttackerInQueue();
        public void MoveToNextAttackerInQueue() => GameEngine.MoveToNextAttackerInQueue();
        public void PlayerPassThisRound(int userId) => GameEngine.PlayerPassThisRound(userId);
        public void ResetAttackRound() => GameEngine.ResetAttackRound();
        public void MoveToNextRound(bool defenderTookCards) => GameEngine.MoveToNextRound(defenderTookCards);
        public void RefillHands() => GameEngine.RefillHands();
        public void ResetVotes() { BeatenVotes.Clear(); TakeCardsVotes.Clear(); }
    }

    public class PassingValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;

        public static PassingValidationResult Success() => new() { IsValid = true };
        public static PassingValidationResult Error(string message) => new() { IsValid = false, ErrorMessage = message };
    }

    public class GameSettings
    {
        public AttackMode AttackMode { get; set; } = AttackMode.All;
        public bool IsThrowInEnabled { get; set; } = true;
        public bool IsTransferEnabled { get; set; } = true;
        public GameMode GameMode { get; set; } = GameMode.Classic;
        public bool IsPassingEnabled { get; set; } = true;
    }

    public class DurakPlayer
    {
        public string ConnectionId { get; set; } = string.Empty;
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public List<Card> Hand { get; set; } = new();
        public bool IsReady { get; set; } = false;

        public string? ProfileImage { get; set; }
        public bool IsAttacker { get; set; } = false;
        public bool IsDefender { get; set; } = false;
        public bool IsDisconnected { get; set; } = false;
        public DateTime? DisconnectedAt { get; set; }
    }

    public class Card
    {
        public string Rank { get; set; } = string.Empty;
        public string Suit { get; set; } = string.Empty;

        public int GetValue() => Rank switch
        {
            "2" => 2,
            "3" => 3,
            "4" => 4,
            "5" => 5,
            "6" => 6,
            "7" => 7,
            "8" => 8,
            "9" => 9,
            "10" => 10,
            "Jack" => 11,
            "Queen" => 12,
            "King" => 13,
            "Ace" => 14,
            _ => 0
        };

        public override bool Equals(object? obj) => obj is Card other && Rank == other.Rank && Suit == other.Suit;
        public override int GetHashCode() => HashCode.Combine(Rank, Suit);
    }

    public class DefendPair
    {
        public Card AttackCard { get; set; } = new();
        public Card DefendCard { get; set; } = new();
    }

    public class GameEndResult
    {
        public List<DurakPlayer> Winners { get; set; } = new();
        public DurakPlayer? Durak { get; set; }
        public bool IsDraw { get; set; }
    }

    #endregion
}

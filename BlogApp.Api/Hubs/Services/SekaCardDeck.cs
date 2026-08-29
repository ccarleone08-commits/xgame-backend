using BlogApp.Core.Entities;

namespace BlogApp.Api.Hubs.Services
{
    public static class SekaCardDeck
    {
        private static readonly string[] Suits = { "♠", "♥", "♦", "♣" };
        private static readonly Dictionary<string, int> RankValues = new()
        {
            { "6", 6 },
            { "7", 7 },
            { "8", 8 },
            { "9", 9 },
            { "10", 10 },
            { "J", 10 },  // Vələt
            { "Q", 10 },  // Xanım
            { "K", 10 },  // Şah
            { "A", 11 }   // Tuz
        };

        public static List<SekaCard> CreateShuffledDeck()
        {
            var deck = new List<SekaCard>();

            foreach (var suit in Suits)
            {
                foreach (var rank in RankValues.Keys)
                {
                    deck.Add(new SekaCard(suit, rank, RankValues[rank]));
                }
            }

            // Qarışdır
            var rng = new Random();
            return deck.OrderBy(x => rng.Next()).ToList();
        }

        public static int GetCardValue(string rank)
        {
            return RankValues.TryGetValue(rank, out int value) ? value : 0;
        }

        public static int GetCardNumericRank(string rank)
        {
            return rank switch
            {
                "A" => 14,
                "K" => 13,
                "Q" => 12,
                "J" => 11,
                "10" => 10,
                "9" => 9,
                "8" => 8,
                "7" => 7,
                "6" => 6,
                _ => 0
            };
        }
    }
}

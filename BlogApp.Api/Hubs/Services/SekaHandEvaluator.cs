using BlogApp.Core.Entities;

namespace BlogApp.Api.Hubs.Services
{
    public static class SekaHandEvaluator
    {
        // ✅ SEKA (33) Xal Sistemi
        private const int SEKA_33 = 10;           // 33 xal (A+10+Q/K/J eyni növdən)
        private const int TRIPLE_ACE = 9;         // 3 Tuz
        private const int TRIPLE_KING = 8;        // 3 Şah
        private const int TRIPLE_QUEEN = 7;       // 3 Dama
        private const int TRIPLE_JACK = 6;        // 3 Valet
        private const int TRIPLE = 5;             // 3 eyni kart
        private const int POINTS = 4;             // Xal sistemi (2-32)

        public static SekaHandValue EvaluateHand(List<SekaCard> hand)
        {
            if (hand == null || hand.Count != 3)
            {
                return new SekaHandValue { Rank = -1, HighCard = 0, HandName = "Invalid" };
            }

            // ✅ 1. ÜÇLÜ YOXLA (3 eyni kart)
            var rankGroups = hand.GroupBy(c => c.Rank).ToList();
            var triple = rankGroups.FirstOrDefault(g => g.Count() == 3);

            if (triple != null)
            {
                string rank = triple.Key;

                if (rank == "A")
                {
                    return new SekaHandValue
                    {
                        Rank = TRIPLE_ACE,
                        HighCard = 33, // 3 As ən yüksək
                        HandName = "3 Tuz (SEKA)"
                    };
                }
                else if (rank == "K")
                {
                    return new SekaHandValue
                    {
                        Rank = TRIPLE_KING,
                        HighCard = 32,
                        HandName = "3 Şah (SEKA)"
                    };
                }
                else if (rank == "Q")
                {
                    return new SekaHandValue
                    {
                        Rank = TRIPLE_QUEEN,
                        HighCard = 31,
                        HandName = "3 Dama (SEKA)"
                    };
                }
                else if (rank == "J")
                {
                    return new SekaHandValue
                    {
                        Rank = TRIPLE_JACK,
                        HighCard = 30,
                        HandName = "3 Valet (SEKA)"
                    };
                }
                else
                {
                    int cardValue = GetCardValue(rank);
                    return new SekaHandValue
                    {
                        Rank = TRIPLE,
                        HighCard = cardValue * 3,
                        HandName = $"3 {rank}"
                    };
                }
            }

            // ✅ 2. XAL HESABLA (eyni növdən olanları topla)
            int maxScore = CalculateHandScore(hand);

            // ✅ 3. 33 XAL YOXLA
            if (maxScore == 33)
            {
                return new SekaHandValue
                {
                    Rank = SEKA_33,
                    HighCard = 33,
                    HandName = "SEKA (33)"
                };
            }

            // ✅ 4. ÜMUMI XAL
            return new SekaHandValue
            {
                Rank = POINTS,
                HighCard = maxScore,
                HandName = $"{maxScore} xal"
            };
        }

        public static int CalculateHandScore(List<SekaCard> hand)
        {
            if (hand == null || hand.Count != 3)
                return 0;

            int aceCount = hand.Count(c => c.Rank == "A");
            if (aceCount >= 2)
            {
                return 22;
            }

            // ✅ NÖVLƏRƏ GÖRƏ QRUPLA
            var suitGroups = hand.GroupBy(c => c.Suit).ToList();

            int maxScore = 0;

            foreach (var group in suitGroups)
            {
                // ✅ Bu növdən olan kartların cəmi
                int score = group.Sum(c => GetCardValue(c.Rank));

                if (score > maxScore)
                {
                    maxScore = score;
                }
            }

            // ✅ Əgər heç eyni növ yoxdursa, ən yüksək kartı götür
            if (maxScore == 0 || suitGroups.All(g => g.Count() == 1))
            {
                maxScore = hand.Max(c => GetCardValue(c.Rank));
            }

            return maxScore;
        }

        private static int GetCardValue(string rank)
        {
            return rank switch
            {
                "A" => 11,
                "K" => 10,
                "Q" => 10,
                "J" => 10,
                "10" => 10,
                "9" => 9,
                "8" => 8,
                "7" => 7,
                "6" => 6,
                "5" => 5,
                "4" => 4,
                "3" => 3,
                "2" => 2,
                _ => 0
            };
        }

        public static string GetHandName(SekaHandValue handValue)
        {
            return handValue.HandName;
        }

        // ✅ QALIB SEÇİMİ ÜÇÜN MÜQAYİSƏ
        public static int CompareHands(SekaHandValue hand1, SekaHandValue hand2)
        {
            // Rank böyükdürsə qalib
            if (hand1.Rank != hand2.Rank)
            {
                return hand1.Rank.CompareTo(hand2.Rank);
            }

            // Rank eynirsə, xala bax
            return hand1.HighCard.CompareTo(hand2.HighCard);
        }
    }

}

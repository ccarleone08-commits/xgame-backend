namespace BlogApp.Api.Hubs.Services
{
    public class PokerHandEvaluator
    {
        public sealed record HandEvaluation(int Rank, string HandName, List<string> BestCards);
        private sealed record FiveCardEvaluation(int Rank, List<string> BestCards);

        private static readonly Dictionary<string, int> RankValues = new()
        {
            {"2", 2}, {"3", 3}, {"4", 4}, {"5", 5}, {"6", 6}, {"7", 7}, {"8", 8},
            {"9", 9}, {"10", 10}, {"J", 11}, {"Q", 12}, {"K", 13}, {"A", 14}
        };

        public int EvaluateHand(List<string> cards)
        {
            return Evaluate(cards).Rank;
        }

        public HandEvaluation Evaluate(List<string> cards)
        {
            if (cards.Count < 5)
                return new HandEvaluation(0, "High Card", new List<string>());

            var evaluation = GetBestFiveCardEvaluation(cards);
            return new HandEvaluation(evaluation.Rank, GetHandNameFromRank(evaluation.Rank), evaluation.BestCards);
        }

        private FiveCardEvaluation GetBestFiveCardEvaluation(List<string> cards)
        {
            if (cards.Count == 5)
                return CalculateFiveCardEvaluation(cards);

            // 7 kartdan 5 seçmək üçün bütün kombinasiyaları yoxla
            var combinations = GetCombinations(cards, 5);
            FiveCardEvaluation? bestEvaluation = null;

            foreach (var combo in combinations)
            {
                var evaluation = CalculateFiveCardEvaluation(combo);
                if (bestEvaluation == null || evaluation.Rank > bestEvaluation.Rank)
                {
                    bestEvaluation = evaluation;
                }
            }

            return bestEvaluation!;
        }

        private List<List<string>> GetCombinations(List<string> cards, int k)
        {
            var result = new List<List<string>>();

            void Combine(int start, List<string> current)
            {
                if (current.Count == k)
                {
                    result.Add(new List<string>(current));
                    return;
                }

                for (int i = start; i < cards.Count; i++)
                {
                    current.Add(cards[i]);
                    Combine(i + 1, current);
                    current.RemoveAt(current.Count - 1);
                }
            }

            Combine(0, new List<string>());
            return result;
        }

        private FiveCardEvaluation CalculateFiveCardEvaluation(List<string> hand)
        {
            var ranks = hand.Select(GetRank).OrderByDescending(r => r).ToList();
            var suits = hand.Select(GetSuit).ToList();

            bool isFlush = suits.Distinct().Count() == 1;
            int straightHighCard = GetStraightHighCard(ranks);
            bool isStraight = straightHighCard > 0;

            var rankGroups = ranks.GroupBy(r => r)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .ToList();

            // Royal Flush (10-J-Q-K-A eyni rəngdə)
            if (isFlush && ranks.SequenceEqual(new List<int> { 14, 13, 12, 11, 10 }))
                return new FiveCardEvaluation(BuildHandRank(10, 14), OrderStraightCards(hand, 14));

            // Straight Flush
            if (isFlush && isStraight)
                return new FiveCardEvaluation(BuildHandRank(9, straightHighCard), OrderStraightCards(hand, straightHighCard));

            // Four of a Kind
            if (rankGroups[0].Count() == 4)
            {
                int fourKind = rankGroups[0].Key;
                int kicker = rankGroups[1].Key;
                return new FiveCardEvaluation(BuildHandRank(8, fourKind, kicker), OrderGroupedCards(hand));
            }

            // Full House
            if (rankGroups[0].Count() == 3 && rankGroups[1].Count() == 2)
            {
                int threeKind = rankGroups[0].Key;
                int pair = rankGroups[1].Key;
                return new FiveCardEvaluation(BuildHandRank(7, threeKind, pair), OrderGroupedCards(hand));
            }

            // Flush
            if (isFlush)
                return new FiveCardEvaluation(BuildHandRank(6, ranks.ToArray()), OrderHighCards(hand));

            // Straight
            if (isStraight)
                return new FiveCardEvaluation(BuildHandRank(5, straightHighCard), OrderStraightCards(hand, straightHighCard));

            // Three of a Kind
            if (rankGroups[0].Count() == 3)
            {
                int threeKind = rankGroups[0].Key;
                int kicker1 = rankGroups[1].Key;
                int kicker2 = rankGroups[2].Key;
                return new FiveCardEvaluation(BuildHandRank(4, threeKind, kicker1, kicker2), OrderGroupedCards(hand));
            }

            // Two Pair
            if (rankGroups[0].Count() == 2 && rankGroups[1].Count() == 2)
            {
                int pair1 = Math.Max(rankGroups[0].Key, rankGroups[1].Key);
                int pair2 = Math.Min(rankGroups[0].Key, rankGroups[1].Key);
                int kicker = rankGroups[2].Key;
                return new FiveCardEvaluation(BuildHandRank(3, pair1, pair2, kicker), OrderGroupedCards(hand));
            }

            // One Pair
            if (rankGroups[0].Count() == 2)
            {
                int pair = rankGroups[0].Key;
                int kicker1 = rankGroups[1].Key;
                int kicker2 = rankGroups[2].Key;
                int kicker3 = rankGroups[3].Key;
                return new FiveCardEvaluation(BuildHandRank(2, pair, kicker1, kicker2, kicker3), OrderGroupedCards(hand));
            }

            // High Card
            return new FiveCardEvaluation(BuildHandRank(1, ranks.ToArray()), OrderHighCards(hand));
        }

        private int BuildHandRank(int category, params int[] tieBreakers)
        {
            int encodedTieBreakers = 0;

            for (int i = 0; i < 5; i++)
            {
                int value = i < tieBreakers.Length ? tieBreakers[i] : 0;
                encodedTieBreakers = encodedTieBreakers * 15 + value;
            }

            return category * 1_000_000 + encodedTieBreakers;
        }

        private List<string> OrderStraightCards(List<string> hand, int straightHighCard)
        {
            return hand
                .OrderBy(card => GetStraightDisplayOrder(GetRank(card), straightHighCard))
                .ToList();
        }

        private int GetStraightDisplayOrder(int rank, int straightHighCard)
        {
            if (straightHighCard == 5 && rank == 14)
                return 5;

            return straightHighCard - rank;
        }

        private List<string> OrderHighCards(List<string> hand)
        {
            return hand
                .OrderByDescending(GetRank)
                .ToList();
        }

        private List<string> OrderGroupedCards(List<string> hand)
        {
            return hand
                .GroupBy(GetRank)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Key)
                .SelectMany(g => g)
                .ToList();
        }

        private int GetStraightHighCard(List<int> ranks)
        {
            var uniqueRanks = ranks.Distinct().OrderByDescending(r => r).ToList();

            if (uniqueRanks.Count != 5)
                return 0;

            // A-2-3-4-5 wheel straight: burada Ace 14 yox, 5-high kimi sayılır.
            if (uniqueRanks.SequenceEqual(new List<int> { 14, 5, 4, 3, 2 }))
                return 5;

            for (int i = 0; i < uniqueRanks.Count - 1; i++)
            {
                if (uniqueRanks[i] - uniqueRanks[i + 1] != 1)
                    return 0;
            }

            return uniqueRanks[0];
        }

        private int GetRank(string card)
        {
            string rank = card.Length == 3 ? "10" : card[0].ToString();
            return RankValues[rank];
        }

        private string GetSuit(string card)
        {
            return card[^1].ToString();
        }

        public string GetHandName(List<string> cards)
        {
            return Evaluate(cards).HandName;
        }

        private string GetHandNameFromRank(int rank)
        {
            if (rank >= 10_000_000) return "Royal Flush 👑";
            if (rank >= 9_000_000) return "Straight Flush 💎";
            if (rank >= 8_000_000) return "Four of a Kind 🔥";
            if (rank >= 7_000_000) return "Full House 🏠";
            if (rank >= 6_000_000) return "Flush 💧";
            if (rank >= 5_000_000) return "Straight ➡️";
            if (rank >= 4_000_000) return "Three of a Kind 🎯";
            if (rank >= 3_000_000) return "Two Pair ✌️";
            if (rank >= 2_000_000) return "One Pair 👥";
            return "High Card 🎴";
        }
    }
}

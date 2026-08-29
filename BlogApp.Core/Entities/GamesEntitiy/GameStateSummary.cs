namespace BlogApp.Core.Entities.GamesEntitiy
{
    public class GameStateSummary
    {
        public string RoomId { get; set; } = string.Empty;
        public string RoomName { get; set; } = string.Empty;
        public string? AttackerName { get; set; }
        public string? DefenderName { get; set; }
        public List<PlayerGameState> Players { get; set; } = new();
        public int TableCardCount { get; set; }
        public int DefendedPairCount { get; set; }
        public int DeckCount { get; set; }
        public CardData? TrumpCard { get; set; }
        public bool IsThrowInPhaseActive { get; set; }
        public bool IsGameActive { get; set; }
        public string GameMode { get; set; } = "Classic";
        public string AttackMode { get; set; } = "All";
        public decimal TotalPrize { get; set; }
        public decimal EntryFee { get; set; }
    }

    public class PlayerGameState
    {
        public int UserId { get; set; }
        public string Name { get; set; } = string.Empty;
        public int CardCount { get; set; }
        public bool IsAttacker { get; set; }
        public bool IsDefender { get; set; }
    }

    public class CardData
    {
        public string Rank { get; set; } = string.Empty;
        public string Suit { get; set; } = string.Empty;
    }
}

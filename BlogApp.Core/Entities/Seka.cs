namespace BlogApp.Core.Entities;
public class SekaCard
{
    public string Suit { get; set; } = string.Empty; // ♠ ♥ ♦ ♣
    public string Rank { get; set; } = string.Empty; // 6,7,8,9,10,J,Q,K,A
    public int Value { get; set; } // Xal dəyəri

    public SekaCard(string suit, string rank, int value)
    {
        Suit = suit;
        Rank = rank;
        Value = value;
    }
}

// ========== SEKA PLAYER ==========
public class SekaPlayer
{
    public string ConnectionId { get; set; } = string.Empty;
    public int UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Balance { get; set; }
    public string ProfileImage { get; set; }
    public List<SekaCard> Hand { get; set; } = new();
    public decimal CurrentBet { get; set; }
    public decimal TotalBet { get; set; }
    public bool HasFolded { get; set; }
    public bool ShowdownCall { get; set; }
    public bool IsActive { get; set; } = true;
    public bool HasChecked { get; set; }
    public bool IsAllIn { get; set; }
    public bool CanBeBuy { get; set; }
    public bool IsWaitingForNextRound { get; set; } = false; // ✅ YENİ
    public bool HasPaidEntryFee { get; set; } = false;
    public bool IsPausedAfterHand { get; set; } = false;
    public HandPauseChoice HandPauseChoice { get; set; } = HandPauseChoice.None;
    public DateTime? HandPauseDecisionAt { get; set; }

}

// ========== SEKA ROOM ==========
public class SekaRoom
{
    public string RoomId { get; set; } = "";
    public string RoomName { get; set; } = "";
    public string CreatorName { get; set; } = "";
    public int CreatorUserId { get; set; }
    public decimal EntryFee { get; set; }
    public int MaxPlayers { get; set; }
    public bool IsPrivate { get; set; }
    public string? Password { get; set; }
    public bool CanBeBuy { get; set; } = false;
    // ✅ YENİ - Otaq şablonu açarı (system rooms üçün)
    public decimal? TemplateKey { get; set; }

    public decimal NextCallAmount { get; set; } = 0;

    public List<SekaPlayer> Players { get; set; } = new();
    public List<SekaCard> Deck { get; set; } = new();

    public bool IsGameStarted { get; set; }
    public bool IsGameFinished { get; set; }
    public decimal PotAmount { get; set; }
    public decimal CurrentBet { get; set; }
    public int CurrentRound { get; set; }
    public int CurrentTurnUserId { get; set; }
    public decimal LastRaiseAmount { get; set; } = 0;

    public int DealerIndex { get; set; } = -1;
    public int CurrentPlayerIndex { get; set; } = 0;
    // ✅ YENİ - Timer sistemi
    public DateTime? TurnStartTime { get; set; }
    public const int TURN_TIMEOUT_SECONDS = 40;

    public bool ShowdownCallActivated { get; set; } = false;

    // ✅ YENİ - Otaq başlanğıc sistemi
    public DateTime? RoomCreatedTime { get; set; }
    public const int ROOM_START_TIMEOUT_SECONDS = 10; // 1 dəqiqə

    // ✅ YENİ - Gözləyən oyunçular
    public List<SekaPlayer> WaitingPlayers { get; set; } = new();

    // ✅ YENİ - Minimum/Maksimum buy-in
    public decimal MinBuyIn { get; set; }
    public decimal MaxBuyIn { get; set; }

    public RoomLimitType LimitType { get; set; } = RoomLimitType.NoLimit; // ✅ YENİ
    public int RaiseCount { get; set; } = 0;
    public int LastRaiserId { get; set; } = 0;
    public int LastCallerId { get; set; } = 0;
    public int LastFolderId { get; set; } = 0;


    public readonly object StateLock = new();

    public GamePhase CurrentPhase { get; set; }
    public decimal FrozenPot { get; set; }
    public List<int> SvaraParticipants { get; set; }
    public int SvaraRound { get; set; }

}

public enum GamePhase
{
    Waiting,
    Playing,
    Showdown,
    Svara,     // NEW
    Finished,
    Normal
}

public enum RoomLimitType
{
    NoLimit,
    PotLimit
}

public enum HandPauseChoice
{
    None,
    ContinuePlaying,
    Timeout
}
// ========== HAND EVALUATION ==========
public class SekaHandValue
{
    public int Rank { get; set; } // El gücü (0-6)
    public int HighCard { get; set; } // Ən yüksək kart
    public string HandName { get; set; } = string.Empty;
}

public class RankedPlayer
{
    public SekaPlayer Player { get; set; }
    public SekaHandValue Hand { get; set; }
}

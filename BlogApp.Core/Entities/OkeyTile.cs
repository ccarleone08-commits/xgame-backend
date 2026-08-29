//namespace BlogApp.Core.Entities;

//// Daş (Taş)
//// ========== MODELS ==========

//public class OkeyPlayer
//{
//    public string ConnectionId { get; set; } = "";
//    public int UserId { get; set; }
//    public string Name { get; set; } = "";
//    public decimal Balance { get; set; }
//    public List<OkeyTile> Hand { get; set; } = new();
//    public int Position { get; set; }
//}

//public class OkeyRoom
//{
//    public string RoomId { get; set; } = Guid.NewGuid().ToString();
//    public string RoomName { get; set; }
//    public string CreatorName { get; set; }
//    public int CreatorId { get; set; }
//    public decimal EntryFee { get; set; }
//    public int MaxPlayers { get; set; }
//    public int TurnNumber { get; set; }
//    public bool IsPrivate { get; set; }
//    public string? Password { get; set; }
//    public List<OkeyPlayer> Players { get; set; } = new();
//    public object StateLock { get; } = new();

//    // Game state
//    public bool IsGameStarted { get; set; }
//    public bool IsGameFinished { get; set; }
//    public List<OkeyTile> Stock { get; set; } = new();
//    public List<OkeyTile> DiscardPile { get; set; } = new();
//    public OkeyTile? Indicator { get; set; }
//    public int CurrentPlayerIndex { get; set; }
//}

//public class OkeyTile
//{
//    public int Id { get; set; }
//    public string Color { get; set; } // Red, Black, Blue, Yellow
//    public int Number { get; set; } // 1-13
//    public bool IsFakeJoker { get; set; }
//}

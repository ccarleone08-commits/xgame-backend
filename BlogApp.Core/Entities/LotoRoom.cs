namespace BlogApp.Core.Entities
{
    public class LotoRoom
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public int CreatorUserId { get; set; }
        public decimal EntryFee { get; set; }
        public int MaxPlayers { get; set; }
        public int MinPlayers { get; set; }
        public int MaxTicketsPerPlayer { get; set; }
        public int TimerSeconds { get; set; }
        public bool IsPrivate { get; set; }
        public string? Password { get; set; }
        public bool IsFixedRoom { get; set; }

        public List<RoomPlayer> Players { get; set; } = new();
        public decimal JackpotPool { get; set; }
        public bool IsGameStarted { get; set; }
        public bool IsGameFinished { get; set; }

        public List<int> DrawnNumbers { get; set; } = new();
        public Queue<int>? NumbersQueue { get; set; }
        public DateTime? GameStartTime { get; set; }

        public List<WinnerInfo> Winners { get; set; } = new();
        public object StateLock { get; } = new();

        public CancellationTokenSource? AutoDrawCts { get; set; }
        public CancellationTokenSource? TimerCts { get; set; }

        public bool RequiresFullCard { get; set; }
        public bool BotsAdded { get; set; }
        public RoomPlayer? WinningTicket { get; set; }

        public DateTime? RoomCreatedTime { get; set; }

        public int GetTimeRemaining()
        {
            // Otaq boşdursa timer saymasın
            if (Players.Count == 0)
            {
                return TimerSeconds; // Full vaxtı göstər
            }

            // Timer heç başlamayıbsa
            if (RoomCreatedTime == null)
            {
                return TimerSeconds;
            }

            // Oyun başlayıbsa
            if (IsGameStarted || IsGameFinished)
            {
                return 0;
            }

            var elapsed = (DateTime.UtcNow - RoomCreatedTime.Value).TotalSeconds;
            var remaining = TimerSeconds - elapsed;

            return (int)Math.Max(0, Math.Ceiling(remaining));
        }
        public void StartTimer()
        {
            if (RoomCreatedTime == null)
            {
                RoomCreatedTime = DateTime.UtcNow;
                Console.WriteLine($"⏰ TIMER BAŞLADI: {RoomName} - {RoomCreatedTime:HH:mm:ss}");
            }
            else
            {
                Console.WriteLine($"⚠️ Timer artıq işləyir: {RoomName}");
            }
        }

        public bool ShouldAutoStart()
        {
            if (IsGameStarted) return false;
            if (Players.Count == 0) return false;

            // Maksimum bilet dolubsa
            if (Players.Count >= MaxPlayers)
            {
                return true;
            }

            // Minimum oyunçu var VƏ timer bitibsə
            if (Players.Count >= MinPlayers && GetTimeRemaining() <= 0)
            {
                return true;
            }

            return false;
        }
    }

    public class RoomPlayer
    {
        public string ConnectionId { get; set; } = "";
        public int UserId { get; set; }
        public string Name { get; set; } = "";
        public decimal Balance { get; set; }
        public int?[][] Card { get; set; } = Array.Empty<int?[]>();
        public string TicketId { get; set; } = "";
        public bool HasWon { get; set; }
        public List<int> CompletedLines { get; set; } = new();
        public bool IsBot { get; set; }
    }

    public class WinnerInfo
    {
        public string Name { get; set; } = "";
        public int UserId { get; set; }
        public decimal Prize { get; set; }
        public DateTime WinTime { get; set; }
    }

    public class RoomListItems
    {
        public string RoomId { get; set; } = "";
        public string RoomName { get; set; } = "";
        public string CreatorName { get; set; } = "";
        public int PlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public int MinPlayers { get; set; }
        public decimal EntryFee { get; set; }
        public bool IsGameStarted { get; set; }
        public bool IsPrivate { get; set; }
        public decimal JackpotPool { get; set; }
        public int TimerSeconds { get; set; }
        public int TimeRemaining { get; set; }
    }
}

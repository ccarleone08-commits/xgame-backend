namespace BlogApp.Core.Entities;

public class SupportTicket : BaseEntity
{
    // Ticket açan oyunçu
    public int? UserId { get; set; }
    public User? User { get; set; }

    // Ticket məlumatları
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public TicketCategory Category { get; set; }
    public TicketPriority Priority { get; set; } = TicketPriority.Medium;
    public TicketStatus Status { get; set; } = TicketStatus.Open;

    // Claim edən worker
    public int? AssignedWorkerId { get; set; }
    public User? AssignedWorker { get; set; }
    public DateTime? ClaimedAt { get; set; }

    // Həll məlumatı
    public DateTime? SolvedAt { get; set; }

    // Ticket nömrəsi: TK-1001
    public string TicketNumber { get; set; } = string.Empty;

    public ICollection<SupportTicketMessage> Messages { get; set; } = new List<SupportTicketMessage>();
}

public enum TicketStatus
{
    Open = 1,
    Claimed = 2,
    InProgress = 3,
    Solved = 4,
    Closed = 5
}

public enum TicketPriority
{
    Low = 1,
    Medium = 2,
    High = 3
}

public enum TicketCategory
{
    Payments = 1,
    Gameplay = 2,
    Profile = 3,
    Authentication = 4,
    Chat = 5,
    Connection = 6,
    Wallet = 7,
    Ranking = 8,
    Notifications = 9,
    Mobile = 10,
    Bonuses = 11,
    Settings = 12,
    Audio = 13,
    Other = 14
}
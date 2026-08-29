namespace BlogApp.Core.Entities;

public class CoinLedger : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Amount { get; set; }
    public string Type { get; set; } = null!;
    public string? ReferenceId { get; set; }
}

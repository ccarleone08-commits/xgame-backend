namespace BlogApp.BusinnesLayer.DTOs.WalletDTOs;

public class CoinLedgerDto
{
    public int Id { get; set; }
    public decimal Amount { get; set; }
    public string Type { get; set; } = null!;
    public string? ReferenceId { get; set; }
    public DateTime CreateDate { get; set; }
}

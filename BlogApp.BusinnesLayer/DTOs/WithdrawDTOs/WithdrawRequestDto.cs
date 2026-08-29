namespace BlogApp.BusinnesLayer.DTOs.WithdrawDTOs
{
    public class WithdrawRequestDto
    {
        public decimal Amount { get; set; }
        public string WalletAddress { get; set; } = string.Empty;
    }
}

namespace BlogApp.BusinnesLayer.DTOs.WithdrawDTOs
{
    public class WithdrawRequestResponseDto
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string UserName { get; set; } = string.Empty;
        public decimal Amount { get; set; }
        public string WalletAddress { get; set; } = string.Empty;
        public int Status { get; set; }
        public decimal UserBalance { get; set; }
        public string StatusText { get; set; } = string.Empty;
        public string? WorkerNote { get; set; }
        public string? BankNote { get; set; }
        public string? ReviewedByWorkerName { get; set; }
        public DateTime CreateDate { get; set; }
    }
}

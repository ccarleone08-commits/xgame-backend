namespace BlogApp.Core.Entities
{
    public class WithdrawRequest
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public User? User { get; set; }

        public decimal Amount { get; set; }
        public string WalletAddress { get; set; } = string.Empty;

        public int Status { get; set; } = (int)WithdrawStatus.Pending;

        public string? WorkerNote { get; set; }
        public string? BankNote { get; set; }

        public int? ReviewedByWorkerId { get; set; }
        public User? ReviewedByWorker { get; set; }

        public int? ReviewedByBankId { get; set; }
        public User? ReviewedByBank { get; set; }

        public DateTime? WorkerReviewedAt { get; set; }
        public DateTime? BankReviewedAt { get; set; }

        public DateTime CreateDate { get; set; } = DateTime.UtcNow;
    }

    public enum WithdrawStatus
    {
        Pending = 0,
        WorkerApproved = 1,
        WorkerRejected = 2,
        BankApproved = 3,
        BankRejected = 4
    }
}

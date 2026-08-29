namespace BlogApp.BusinnesLayer.DTOs.DepositDTOs
{
    public class DepositRequestResponseDto
    {
        public int Id { get; set; }
        public int? UserId { get; set; }
        public string UserName { get; set; }
        public decimal Amount { get; set; }
        public string ReceiptImagePath { get; set; }
        public int Status { get; set; }
        public string StatusText { get; set; }
        public string? WorkerNote { get; set; }
        public string? BankNote { get; set; }
        public int? ReviewedByWorkerId { get; set; }
        public string? ReviewedByWorkerName { get; set; }
        public DateTime CreateDate { get; set; } = DateTime.Now;
    }

}

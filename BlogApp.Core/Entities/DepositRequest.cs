namespace BlogApp.Core.Entities;

public class DepositRequest : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; }

    public decimal Amount { get; set; }
    public string ReceiptImagePath { get; set; }  // upload edilmiş şəkil

    // Status: 0=Pending, 1=WorkerApproved, 2=WorkerRejected, 3=BankApproved, 4=BankRejected
    public int Status { get; set; } = 0;

    public string? WorkerNote { get; set; }
    public int? ReviewedByWorkerId { get; set; }
    public User? ReviewedByWorker { get; set; }
    public DateTime? WorkerReviewedAt { get; set; }

    public string? BankNote { get; set; }
    public int? ReviewedByBankId { get; set; }
    public User? ReviewedByBank { get; set; }
    public DateTime? BankReviewedAt { get; set; }
}
public enum DepositStatus
{
    Pending = 0,
    WorkerApproved = 1,
    WorkerRejected = 2,
    BankApproved = 3,
    BankRejected = 4
}
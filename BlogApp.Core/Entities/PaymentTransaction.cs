namespace BlogApp.Core.Entities;

public class PaymentTransaction : BaseEntity
{
    public int UserId { get; set; }
    public User User { get; set; } = null!;

    public int? CoinPackageId { get; set; }
    public CoinPackage? CoinPackage { get; set; }

    public string OrderId { get; set; } = null!;
    public string? NowPaymentsPaymentId { get; set; }
    public string? NowPaymentsInvoiceId { get; set; }
    public string Status { get; set; } = "created";
    public string? PayCurrency { get; set; }
    public decimal CoinAmount { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = "usd";
    public decimal? ActuallyPaid { get; set; }
    public string? PayAddress { get; set; }
    public string? PaymentUrl { get; set; }
    public bool CoinsGranted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string? RawIpnPayload { get; set; }
}

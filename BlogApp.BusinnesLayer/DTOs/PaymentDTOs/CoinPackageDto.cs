namespace BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

public class CoinPackageDto
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public decimal CoinAmount { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = null!;
}

public class CoinPaymentRequestDto
{
    public int Id { get; set; }
    public int? CoinPackageId { get; set; }
    public string? CoinPackageName { get; set; }
    public string OrderId { get; set; } = null!;
    public string Status { get; set; } = null!;
    public bool CoinsGranted { get; set; }
    public decimal CoinAmount { get; set; }
    public decimal PriceAmount { get; set; }
    public string PriceCurrency { get; set; } = null!;
    public string? PayCurrency { get; set; }
    public string? PaymentUrl { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

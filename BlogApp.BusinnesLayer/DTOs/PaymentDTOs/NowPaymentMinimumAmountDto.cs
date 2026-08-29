namespace BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

public class NowPaymentMinimumAmountDto
{
    public string PriceCurrency { get; set; } = string.Empty;
    public string PayCurrency { get; set; } = string.Empty;
    public decimal MinimumPriceAmount { get; set; }
    public decimal MinimumCoinAmount { get; set; }
    public decimal MinimumPayAmount { get; set; }
}

namespace BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

public class CreateNowPaymentRequestDto
{
    public int? CoinPackageId { get; set; }
    public decimal? CoinAmount { get; set; }
    public string? PayCurrency { get; set; }
}

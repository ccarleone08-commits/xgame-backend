using System.Text.Json.Serialization;

namespace BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

public class NowPaymentsIpnDto
{
    [JsonPropertyName("payment_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? PaymentId { get; set; }

    [JsonPropertyName("invoice_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? InvoiceId { get; set; }

    [JsonPropertyName("payment_status")]
    public string? PaymentStatus { get; set; }

    [JsonPropertyName("pay_address")]
    public string? PayAddress { get; set; }

    [JsonPropertyName("price_amount")]
    public decimal? PriceAmount { get; set; }

    [JsonPropertyName("price_currency")]
    public string? PriceCurrency { get; set; }

    [JsonPropertyName("actually_paid")]
    public decimal? ActuallyPaid { get; set; }

    [JsonPropertyName("pay_currency")]
    public string? PayCurrency { get; set; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; set; }
}
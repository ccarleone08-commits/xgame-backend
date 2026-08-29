using System.Text.Json.Serialization;

namespace BlogApp.BusinnesLayer.ExternalServices.Interfaces;

public class CreateInvoiceRequest
{
    [JsonPropertyName("price_amount")]
    public decimal PriceAmount { get; set; }

    [JsonPropertyName("price_currency")]
    public string PriceCurrency { get; set; } = null!;

    [JsonPropertyName("pay_currency")]
    public string? PayCurrency { get; set; }

    [JsonPropertyName("ipn_callback_url")]
    public string IpnCallbackUrl { get; set; } = null!;

    [JsonPropertyName("order_id")]
    public string OrderId { get; set; } = null!;

    [JsonPropertyName("order_description")]
    public string OrderDescription { get; set; } = null!;

    [JsonPropertyName("success_url")]
    public string SuccessUrl { get; set; } = null!;

    [JsonPropertyName("cancel_url")]
    public string CancelUrl { get; set; } = null!;

    [JsonPropertyName("is_fixed_rate")]
    public bool IsFixedRate { get; set; } = true;
}

public class CreateInvoiceResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = null!;

    [JsonPropertyName("invoice_url")]
    public string InvoiceUrl { get; set; } = null!;
}

public class MinimumPaymentAmountResponse
{
    [JsonPropertyName("currency_from")]
    public string? CurrencyFrom { get; set; }

    [JsonPropertyName("currency_to")]
    public string? CurrencyTo { get; set; }

    [JsonPropertyName("min_amount")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal MinAmount { get; set; }

    [JsonPropertyName("fiat_equivalent")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? FiatEquivalent { get; set; }
}

public class NowPaymentsAuthRequest
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = null!;

    [JsonPropertyName("password")]
    public string Password { get; set; } = null!;
}

public class NowPaymentsAuthResponse
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = null!;
}

public class PaymentStatusResponse
{
    [JsonPropertyName("payment_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? PaymentId { get; set; }
    [JsonPropertyName("payment_status")]
    public string? PaymentStatus { get; set; }

    [JsonPropertyName("pay_address")]
    public string? PayAddress { get; set; }

    [JsonPropertyName("price_amount")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? PriceAmount { get; set; }

    [JsonPropertyName("price_currency")]
    public string? PriceCurrency { get; set; }

    [JsonPropertyName("actually_paid")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public decimal? ActuallyPaid { get; set; }

    [JsonPropertyName("pay_currency")]
    public string? PayCurrency { get; set; }

    [JsonPropertyName("order_id")]
    public string? OrderId { get; set; }

    [JsonPropertyName("invoice_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString | JsonNumberHandling.WriteAsString)]
    public long? InvoiceId { get; set; }
}

public class PaymentListResponse
{
    [JsonPropertyName("data")]
    public List<PaymentStatusResponse> Data { get; set; } = [];
}

public interface INowPaymentsClient
{
    Task<CreateInvoiceResponse> CreateInvoiceAsync(CreateInvoiceRequest request);
    Task<MinimumPaymentAmountResponse> GetMinimumPaymentAmountAsync(
        string currencyFrom,
        string currencyTo,
        string fiatEquivalent,
        bool isFixedRate);
    Task<PaymentStatusResponse> GetPaymentStatusAsync(string paymentId);
    Task<IReadOnlyList<PaymentStatusResponse>> GetPaymentsByInvoiceIdAsync(string invoiceId);
}
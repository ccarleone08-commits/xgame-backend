using BlogApp.BusinnesLayer.DTOs.Options;
using BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;
using BlogApp.BusinnesLayer.ExternalServices.Interfaces;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace BlogApp.BusinnesLayer.ExternalServices.Implements;

public class NowPaymentsClient : INowPaymentsClient
{
    private readonly HttpClient _http;
    private readonly NowPaymentsOptions _options;

    public NowPaymentsClient(HttpClient http, IOptions<NowPaymentsOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public async Task<CreateInvoiceResponse> CreateInvoiceAsync(CreateInvoiceRequest request)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("NOWPayments ApiKey is not configured.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "invoice")
        {
            Content = JsonContent.Create(request)
        };
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
            throw new NowPaymentsApiException(response.StatusCode, "NOWPayments invoice request failed.");

        return await response.Content.ReadFromJsonAsync<CreateInvoiceResponse>()
            ?? throw new InvalidOperationException("NOWPayments returned an empty invoice response.");
    }

    public async Task<MinimumPaymentAmountResponse> GetMinimumPaymentAmountAsync(
        string currencyFrom,
        string currencyTo,
        string fiatEquivalent,
        bool isFixedRate)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("NOWPayments ApiKey is not configured.");

        var path =
            $"min-amount?currency_from={Uri.EscapeDataString(currencyFrom)}" +
            $"&currency_to={Uri.EscapeDataString(currencyTo)}" +
            $"&fiat_equivalent={Uri.EscapeDataString(fiatEquivalent)}" +
            $"&is_fixed_rate={isFixedRate.ToString().ToLowerInvariant()}";

        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
            throw new NowPaymentsApiException(response.StatusCode, "NOWPayments minimum amount request failed.");

        return await response.Content.ReadFromJsonAsync<MinimumPaymentAmountResponse>()
            ?? throw new InvalidOperationException("NOWPayments returned an empty minimum amount response.");
    }

    public async Task<PaymentStatusResponse> GetPaymentStatusAsync(string paymentId)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("NOWPayments ApiKey is not configured.");

        if (string.IsNullOrWhiteSpace(paymentId))
            throw new ArgumentException("Payment id is required.", nameof(paymentId));

        using var message = new HttpRequestMessage(
            HttpMethod.Get,
            $"payment/{Uri.EscapeDataString(paymentId)}");
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);

        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
            throw new NowPaymentsApiException(response.StatusCode, "NOWPayments payment status request failed.");

        return await response.Content.ReadFromJsonAsync<PaymentStatusResponse>()
            ?? throw new InvalidOperationException("NOWPayments returned an empty payment status response.");
    }

    public async Task<IReadOnlyList<PaymentStatusResponse>> GetPaymentsByInvoiceIdAsync(string invoiceId)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new InvalidOperationException("NOWPayments ApiKey is not configured.");

        if (string.IsNullOrWhiteSpace(invoiceId))
            throw new ArgumentException("Invoice id is required.", nameof(invoiceId));

        var path = $"payment/?limit=10&page=0&invoiceid={Uri.EscapeDataString(invoiceId)}";
        var response = await SendPaymentListRequestAsync(path, bearerToken: null);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            var token = await GetAuthTokenAsync();
            response = await SendPaymentListRequestAsync(path, token);
        }

        if (!response.IsSuccessStatusCode)
            throw new NowPaymentsApiException(response.StatusCode, "NOWPayments payment list request failed.");

        var result = await response.Content.ReadFromJsonAsync<PaymentListResponse>()
            ?? throw new InvalidOperationException("NOWPayments returned an empty payment list response.");
        return result.Data;
    }

    private async Task<HttpResponseMessage> SendPaymentListRequestAsync(string path, string? bearerToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        message.Headers.TryAddWithoutValidation("x-api-key", _options.ApiKey);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

        return await _http.SendAsync(message);
    }

    private async Task<string> GetAuthTokenAsync()
    {
        if (string.IsNullOrWhiteSpace(_options.AuthEmail) ||
            string.IsNullOrWhiteSpace(_options.AuthPassword))
            throw new InvalidOperationException("NOWPayments AuthEmail/AuthPassword are required to search payments by invoice id.");

        using var message = new HttpRequestMessage(HttpMethod.Post, "auth")
        {
            Content = JsonContent.Create(new NowPaymentsAuthRequest
            {
                Email = _options.AuthEmail,
                Password = _options.AuthPassword
            })
        };

        var response = await _http.SendAsync(message);
        if (!response.IsSuccessStatusCode)
            throw new NowPaymentsApiException(response.StatusCode, "NOWPayments auth request failed.");

        var result = await response.Content.ReadFromJsonAsync<NowPaymentsAuthResponse>()
            ?? throw new InvalidOperationException("NOWPayments returned an empty auth response.");
        return result.Token;
    }
}

using BlogApp.BusinnesLayer.DTOs.Options;
using BlogApp.BusinnesLayer.DTOs.PaymentDTOs;
using BlogApp.BusinnesLayer.Exceptions.PaymentExceptions;
using BlogApp.BusinnesLayer.ExternalServices.Interfaces;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Data;

namespace BlogApp.BusinnesLayer.Services.Implements;

public class PaymentService : IPaymentService
{
    private const string FinishedStatus = "finished";
    private const string PurchaseLedgerType = "Purchase";
    private const string ManualPaymentPriceCurrency = "usd";

    private readonly BlogAppDbContext _context;
    private readonly INowPaymentsClient _nowPaymentsClient;
    private readonly NowPaymentsOptions _options;
    private readonly IMemoryCache _cache;

    public PaymentService(
        BlogAppDbContext context,
        INowPaymentsClient nowPaymentsClient,
        IOptions<NowPaymentsOptions> options,
        IMemoryCache cache)
    {
        _context = context;
        _nowPaymentsClient = nowPaymentsClient;
        _options = options.Value;
        _cache = cache;
    }

    public async Task<PaymentCreateResultDto> CreateNowPaymentAsync(int userId, int? packageId, decimal? requestedCoinAmount, string? payCurrency)
    {
        if (string.IsNullOrWhiteSpace(_options.IpnCallbackUrl) ||
            string.IsNullOrWhiteSpace(_options.SuccessUrl) ||
            string.IsNullOrWhiteSpace(_options.CancelUrl))
            throw new PaymentProviderException("Payment provider is not configured.");

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
            throw new PaymentProviderException("Payment provider is not configured.");

        CoinPackage? package = null;
        decimal coinAmount;
        decimal priceAmount;
        string priceCurrency;
        string description;

        if (requestedCoinAmount.HasValue)
        {
            coinAmount = decimal.Round(requestedCoinAmount.Value, 2);
            if (coinAmount < 1)
                throw new PaymentValidationException("Minimum payment amount is 1 coin.");

            priceAmount = coinAmount;
            priceCurrency = ManualPaymentPriceCurrency;
            description = $"{coinAmount:0.##} coin";
        }
        else
        {
            if (!packageId.HasValue)
                throw new PaymentValidationException("Coin amount or coin package is required.");

            package = await _context.CoinPackages
                .SingleOrDefaultAsync(x => x.Id == packageId.Value && x.IsActive);

            if (package == null)
                throw new PaymentValidationException("Coin package not found or inactive.");

            coinAmount = package.CoinAmount;
            priceAmount = package.PriceAmount;
            priceCurrency = package.PriceCurrency;
            description = package.Name;
        }

        var normalizedPayCurrency = string.IsNullOrWhiteSpace(payCurrency)
            ? null
            : payCurrency.Trim().ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(normalizedPayCurrency))
        {
            var minimum = await GetNowPaymentMinimumAmountAsync(normalizedPayCurrency);
            if (priceAmount < minimum.MinimumPriceAmount)
                throw new PaymentValidationException(
                    $"{normalizedPayCurrency.ToUpperInvariant()} üçün minimum {minimum.MinimumCoinAmount:0.##} coindir.");
        }

        var orderId = $"coin-{userId}-{Guid.NewGuid():N}";
        var payment = new PaymentTransaction
        {
            UserId = userId,
            CoinPackageId = package?.Id,
            OrderId = orderId,
            Status = "created",
            CoinAmount = coinAmount,
            PriceAmount = priceAmount,
            PriceCurrency = priceCurrency,
            PayCurrency = normalizedPayCurrency
        };

        _context.PaymentTransactions.Add(payment);
        await _context.SaveChangesAsync();

        CreateInvoiceResponse invoice;
        try
        {
            invoice = await _nowPaymentsClient.CreateInvoiceAsync(new CreateInvoiceRequest
            {
                PriceAmount = priceAmount,
                PriceCurrency = priceCurrency,
                PayCurrency = payment.PayCurrency,
                IpnCallbackUrl = _options.IpnCallbackUrl,
                OrderId = orderId,
                OrderDescription = description,
                SuccessUrl = _options.SuccessUrl,
                CancelUrl = _options.CancelUrl,
                IsFixedRate = true
            });
        }
        catch (Exception ex) when (ex is NowPaymentsApiException or HttpRequestException or TaskCanceledException)
        {
            payment.Status = "provider_failed";
            payment.UpdatedAt = DateTime.UtcNow;
            await _context.SaveChangesAsync();
            throw new PaymentProviderException("Payment provider is temporarily unavailable.", ex);
        }

        payment.NowPaymentsInvoiceId = invoice.Id;
        payment.PaymentUrl = invoice.InvoiceUrl;
        payment.Status = "waiting";
        payment.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync();

        return new PaymentCreateResultDto(payment.Id, invoice.InvoiceUrl);
    }

    public async Task<NowPaymentMinimumAmountDto> GetNowPaymentMinimumAmountAsync(string payCurrency)
    {
        if (string.IsNullOrWhiteSpace(payCurrency))
            throw new PaymentValidationException("Pay currency is required.");

        var normalizedPayCurrency = payCurrency.Trim().ToLowerInvariant();
        if (!IsSupportedPayCurrency(normalizedPayCurrency))
            throw new PaymentValidationException("Selected crypto currency is not supported.");

        var cacheKey = $"nowpayments:min-amount:{normalizedPayCurrency}:fixed";
        if (_cache.TryGetValue(cacheKey, out NowPaymentMinimumAmountDto? cached) && cached != null)
            return cached;

        MinimumPaymentAmountResponse minimum;
        try
        {
            minimum = await _nowPaymentsClient.GetMinimumPaymentAmountAsync(
                normalizedPayCurrency,
                normalizedPayCurrency,
                ManualPaymentPriceCurrency,
                isFixedRate: true);
        }
        catch (Exception ex) when (ex is NowPaymentsApiException or HttpRequestException or TaskCanceledException)
        {
            throw new PaymentProviderException("Payment provider minimum amount check is temporarily unavailable.", ex);
        }

        var minimumPriceAmount = minimum.FiatEquivalent ?? minimum.MinAmount;

        var result = new NowPaymentMinimumAmountDto
        {
            PriceCurrency = ManualPaymentPriceCurrency,
            PayCurrency = normalizedPayCurrency,
            MinimumPriceAmount = minimumPriceAmount,
            MinimumCoinAmount = minimumPriceAmount,
            MinimumPayAmount = minimum.MinAmount
        };

        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(Math.Max(1, _options.MinimumAmountCacheMinutes)));
        return result;
    }

    private bool IsSupportedPayCurrency(string payCurrency)
    {
        return _options.SupportedPayCurrencies.Contains(payCurrency, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<PaymentStatusDto> GetStatusAsync(int userId, int paymentId)
    {
        var payment = await _context.PaymentTransactions
            .SingleOrDefaultAsync(x => x.Id == paymentId && x.UserId == userId);

        if (payment == null)
            throw new InvalidOperationException("Payment not found.");

        return new PaymentStatusDto
        {
            Id = payment.Id,
            OrderId = payment.OrderId,
            Status = payment.Status,
            CoinsGranted = payment.CoinsGranted,
            CoinAmount = payment.CoinAmount,
            PriceAmount = payment.PriceAmount,
            PriceCurrency = payment.PriceCurrency,
            PayCurrency = payment.PayCurrency,
            PaymentUrl = payment.PaymentUrl,
            CompletedAt = payment.CompletedAt
        };
    }

    public async Task<PaymentStatusDto> RefreshStatusAsync(int userId, int paymentId, string? nowPaymentsPaymentId = null)
    {
        var payment = await _context.PaymentTransactions
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == paymentId && x.UserId == userId);

        if (payment == null)
            throw new InvalidOperationException("Payment not found.");

        if (payment.CoinsGranted)
            return await GetStatusAsync(userId, paymentId);

        PaymentStatusResponse? providerPayment = null;
        try
        {
            var requestedPaymentId = string.IsNullOrWhiteSpace(nowPaymentsPaymentId)
                ? payment.NowPaymentsPaymentId
                : nowPaymentsPaymentId.Trim();

            if (!string.IsNullOrWhiteSpace(requestedPaymentId))
            {
                providerPayment = await _nowPaymentsClient.GetPaymentStatusAsync(requestedPaymentId);
            }
            else if (!string.IsNullOrWhiteSpace(payment.NowPaymentsInvoiceId))
            {
                var invoicePayments = await _nowPaymentsClient.GetPaymentsByInvoiceIdAsync(payment.NowPaymentsInvoiceId);
                providerPayment = invoicePayments
                    .OrderByDescending(x => string.Equals(x.OrderId, payment.OrderId, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();
            }
        }
        catch (Exception ex) when (ex is NowPaymentsApiException or HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            throw new PaymentProviderException("Payment provider status check is temporarily unavailable.", ex);
        }

        if (providerPayment == null)
            return await GetStatusAsync(userId, paymentId);

        if (!string.IsNullOrWhiteSpace(providerPayment.OrderId) &&
            !string.Equals(providerPayment.OrderId, payment.OrderId, StringComparison.OrdinalIgnoreCase))
            throw new PaymentValidationException("NOWPayments payment does not match this order.");

        await ApplyProviderStatusAsync(
            payment.OrderId,
            providerPayment.PaymentStatus,
providerPayment.PaymentId?.ToString(),
providerPayment.InvoiceId?.ToString(),
providerPayment.ActuallyPaid,
            providerPayment.PayAddress,
            providerPayment.PayCurrency,
            rawPayload: null);

        return await GetStatusAsync(userId, paymentId);
    }

    public bool VerifyIpn(string rawBody, string signature)
    {
        return NowPaymentsSignatureVerifier.Verify(rawBody, signature, _options.IpnSecret);
    }

    public async Task HandleIpnAsync(NowPaymentsIpnDto ipn, string rawBody)
    {
        if (string.IsNullOrWhiteSpace(ipn.OrderId))
            throw new InvalidOperationException("NOWPayments IPN does not contain order_id.");

        await ApplyProviderStatusAsync(
            ipn.OrderId,
            ipn.PaymentStatus,
            ipn.PaymentId?.ToString(),
ipn.InvoiceId?.ToString(),
ipn.ActuallyPaid,
            ipn.PayAddress,
            ipn.PayCurrency,
            rawBody);
    }

    private async Task ApplyProviderStatusAsync(
     string orderId,
     string? paymentStatus,
     string? paymentId,
     string? invoiceId,
     decimal? actuallyPaid,
     string? payAddress,
     string? payCurrency,
     string? rawPayload)
    {
        var strategy = _context.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var dbTransaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);

            var payment = await _context.PaymentTransactions
                .SingleOrDefaultAsync(x => x.OrderId == orderId);

            if (payment == null)
                throw new InvalidOperationException("Payment transaction not found.");

            payment.Status = string.IsNullOrWhiteSpace(paymentStatus) ? payment.Status : paymentStatus;
            payment.NowPaymentsPaymentId ??= paymentId;
            payment.NowPaymentsInvoiceId ??= invoiceId;

            if (actuallyPaid.HasValue)
                payment.ActuallyPaid = actuallyPaid;

            if (!string.IsNullOrWhiteSpace(payAddress))
                payment.PayAddress = payAddress;

            payment.PayCurrency = string.IsNullOrWhiteSpace(payCurrency) ? payment.PayCurrency : payCurrency;

            if (!string.IsNullOrWhiteSpace(rawPayload))
                payment.RawIpnPayload = rawPayload;

            payment.UpdatedAt = DateTime.UtcNow;

            if (string.Equals(paymentStatus, FinishedStatus, StringComparison.OrdinalIgnoreCase) && !payment.CoinsGranted)
            {
                var alreadyGranted = await _context.CoinLedgers.AnyAsync(x =>
                    x.UserId == payment.UserId &&
                    x.ReferenceId == payment.OrderId &&
                    x.Type == PurchaseLedgerType);

                if (!alreadyGranted)
                {
                    var user = await _context.Users.SingleAsync(x => x.Id == payment.UserId);
                    user.Balance += payment.CoinAmount;

                    _context.CoinLedgers.Add(new CoinLedger
                    {
                        UserId = user.Id,
                        Amount = payment.CoinAmount,
                        Type = PurchaseLedgerType,
                        ReferenceId = payment.OrderId
                    });
                }

                payment.CoinsGranted = true;
                payment.CompletedAt ??= DateTime.UtcNow;
            }

            await _context.SaveChangesAsync();
            await dbTransaction.CommitAsync();
        });
    }
}
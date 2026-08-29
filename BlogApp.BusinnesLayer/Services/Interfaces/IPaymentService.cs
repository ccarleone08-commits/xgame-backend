using BlogApp.BusinnesLayer.DTOs.PaymentDTOs;

namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface IPaymentService
{
    Task<PaymentCreateResultDto> CreateNowPaymentAsync(int userId, int? packageId, decimal? coinAmount, string? payCurrency);
    Task<NowPaymentMinimumAmountDto> GetNowPaymentMinimumAmountAsync(string payCurrency);
    Task<PaymentStatusDto> GetStatusAsync(int userId, int paymentId);
    Task<PaymentStatusDto> RefreshStatusAsync(int userId, int paymentId, string? nowPaymentsPaymentId = null);
    bool VerifyIpn(string rawBody, string signature);
    Task HandleIpnAsync(NowPaymentsIpnDto ipn, string rawBody);
}

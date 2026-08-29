using BlogApp.BusinnesLayer.DTOs.WithdrawDTOs;

namespace BlogApp.BusinnesLayer.Services.Interfaces
{
    public interface IWithdrawService
    {
        Task<WithdrawRequestResponseDto> CreateRequestAsync(int userId, WithdrawRequestDto dto);
        Task<List<WithdrawRequestResponseDto>> GetUserRequestsAsync(int userId);
        Task<List<WithdrawRequestResponseDto>> GetPendingRequestsAsync();
        Task<WithdrawRequestResponseDto> WorkerReviewAsync(WithdrawReviewDto dto, int workerId);
        Task<List<WithdrawRequestResponseDto>> GetWorkerApprovedRequestsAsync();
        Task<WithdrawRequestResponseDto> BankReviewAsync(WithdrawReviewDto dto, int bankId);
        Task<List<WithdrawRequestResponseDto>> GetWorkerHistoryAsync(int workerId, bool isAdmin);
        Task<List<WithdrawRequestResponseDto>> GetBankHistoryAsync();
    }
}

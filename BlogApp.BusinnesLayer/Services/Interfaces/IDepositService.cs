using BlogApp.BusinnesLayer.DTOs.DepositDTOs;
using Microsoft.AspNetCore.Http;

namespace BlogApp.BusinnesLayer.Services.Interfaces
{
    public interface IDepositService
    {
        Task<DepositRequestResponseDto> CreateRequestAsync(int userId, decimal amount, IFormFile receipt);
        Task<List<DepositRequestResponseDto>> GetPendingRequestsAsync();          // Worker görür
        Task<List<DepositRequestResponseDto>> GetWorkerApprovedRequestsAsync();   // Bank görür
        Task<DepositRequestResponseDto> WorkerReviewAsync(WorkerReviewDto dto, int workerId);
        Task<DepositRequestResponseDto> BankReviewAsync(BankReviewDto dto, int bankId);
        Task<List<DepositRequestResponseDto>> GetUserRequestsAsync(int userId);
        Task<List<DepositRequestResponseDto>> GetWorkerHistoryAsync(int workerId, bool isAdmin);
        Task<List<DepositRequestResponseDto>> GetBankHistoryAsync();
    }
}

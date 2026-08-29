using BlogApp.BusinnesLayer.DTOs.WithdrawDTOs;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.BusinnesLayer.Services.Implements
{
    public class WithdrawService : IWithdrawService
    {
        private readonly BlogAppDbContext _context;

        public WithdrawService(BlogAppDbContext context)
        {
            _context = context;
        }

        public async Task<WithdrawRequestResponseDto> CreateRequestAsync(int userId, WithdrawRequestDto dto)
        {
            var user = await _context.Users.FindAsync(userId);
            if (user == null) throw new Exception("İstifadəçi tapılmadı");
            if (user.Balance < dto.Amount) throw new Exception("Balans kifayət deyil");



            var request = new WithdrawRequest
            {
                UserId = userId,
                Amount = dto.Amount,
                WalletAddress = dto.WalletAddress,
                Status = (int)WithdrawStatus.Pending
            };

            _context.WithdrawRequests.Add(request);
            await _context.SaveChangesAsync();

            return MapToDto(request);
        }

        public async Task<List<WithdrawRequestResponseDto>> GetUserRequestsAsync(int userId)
        {
            var requests = await _context.WithdrawRequests
                .Where(w => w.UserId == userId)
                .OrderByDescending(w => w.CreateDate)
                .ToListAsync();
            return requests.Select(MapToDto).ToList();
        }

        public async Task<List<WithdrawRequestResponseDto>> GetPendingRequestsAsync()
        {
            var requests = await _context.WithdrawRequests
                .Include(w => w.User)
                .Where(w => w.Status == (int)WithdrawStatus.Pending)
                .OrderByDescending(w => w.CreateDate)
                .ToListAsync();
            return requests.Select(MapToDto).ToList();
        }

        public async Task<WithdrawRequestResponseDto> WorkerReviewAsync(WithdrawReviewDto dto, int workerId)
        {
            var request = await _context.WithdrawRequests
                .Include(w => w.User)
                .FirstOrDefaultAsync(w => w.Id == dto.WithdrawRequestId
                                       && w.Status == (int)WithdrawStatus.Pending);

            if (request == null) throw new Exception("Sorğu tapılmadı və ya artıq yoxlanılıb");

            request.Status = dto.IsApproved
                ? (int)WithdrawStatus.WorkerApproved
                : (int)WithdrawStatus.WorkerRejected;
            request.WorkerNote = dto.Note;
            request.ReviewedByWorkerId = workerId;
            request.WorkerReviewedAt = DateTime.UtcNow;



            await _context.SaveChangesAsync();
            return MapToDto(request);
        }

        public async Task<List<WithdrawRequestResponseDto>> GetWorkerApprovedRequestsAsync()
        {
            var requests = await _context.WithdrawRequests
                .Include(w => w.User)
                .Include(w => w.ReviewedByWorker)
                .Where(w => w.Status == (int)WithdrawStatus.WorkerApproved)
                .OrderByDescending(w => w.CreateDate)
                .ToListAsync();
            return requests.Select(MapToDto).ToList();
        }

        public async Task<WithdrawRequestResponseDto> BankReviewAsync(WithdrawReviewDto dto, int bankId)
        {
            var request = await _context.WithdrawRequests
                .Include(w => w.User)
                .FirstOrDefaultAsync(w => w.Id == dto.WithdrawRequestId
                                       && w.Status == (int)WithdrawStatus.WorkerApproved);

            if (request == null) throw new Exception("Sorğu tapılmadı və ya worker təsdiqi yoxdur");

            var user = await _context.Users.FindAsync(request.UserId);
            if (user == null) throw new Exception("İstifadəçi tapılmadı");

            if (dto.IsApproved)
            {
                // ✅ Yalnız bank təsdiq edəndə balansı azalt
                if (user.Balance < request.Amount)
                    throw new Exception("İstifadəçinin balansı kifayət deyil");

                user.Balance -= request.Amount;
                request.Status = (int)WithdrawStatus.BankApproved;
            }
            else
            {
                // ✅ Rədd edildikdə balansa toxunmayın — heç vaxt azaldılmayıb
                request.Status = (int)WithdrawStatus.BankRejected;
            }

            request.BankNote = dto.Note;
            request.ReviewedByBankId = bankId;
            request.BankReviewedAt = DateTime.UtcNow;

            await _context.SaveChangesAsync();
            return MapToDto(request);
        }
        public async Task<List<WithdrawRequestResponseDto>> GetWorkerHistoryAsync(int workerId, bool isAdmin)
        {
            var query = _context.WithdrawRequests
                .Include(w => w.User)
                .Include(w => w.ReviewedByWorker)
                .Where(w => w.Status == (int)WithdrawStatus.WorkerApproved
                         || w.Status == (int)WithdrawStatus.WorkerRejected
                         || w.Status == (int)WithdrawStatus.BankApproved
                         || w.Status == (int)WithdrawStatus.BankRejected);

            if (!isAdmin)
                query = query.Where(w => w.ReviewedByWorkerId == workerId);

            var requests = await query.OrderByDescending(w => w.CreateDate).ToListAsync();
            return requests.Select(MapToDto).ToList();
        }

        public async Task<List<WithdrawRequestResponseDto>> GetBankHistoryAsync()
        {
            var requests = await _context.WithdrawRequests
                .Include(w => w.User)
                .Include(w => w.ReviewedByWorker)
                .Where(w => w.Status == (int)WithdrawStatus.WorkerApproved
                         || w.Status == (int)WithdrawStatus.WorkerRejected
                         || w.Status == (int)WithdrawStatus.BankApproved
                         || w.Status == (int)WithdrawStatus.BankRejected)
                .OrderByDescending(w => w.CreateDate)
                .ToListAsync();
            return requests.Select(MapToDto).ToList();
        }

        private WithdrawRequestResponseDto MapToDto(WithdrawRequest w) => new()
        {
            Id = w.Id,
            UserId = w.UserId,
            UserName = w.User?.UserName ?? "",
            Amount = w.Amount,
            UserBalance = w.User?.Balance ?? 0,
            WalletAddress = w.WalletAddress,
            Status = w.Status,
            StatusText = ((WithdrawStatus)w.Status).ToString(),
            WorkerNote = w.WorkerNote,
            BankNote = w.BankNote,
            ReviewedByWorkerName = w.ReviewedByWorker?.UserName ?? "",
            CreateDate = w.CreateDate
        };
    }
}

using BlogApp.BusinnesLayer.DTOs.DepositDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlogApp.BusinnesLayer.Services.Implements;
public class DepositService : IDepositService
{
    private readonly BlogAppDbContext _context;
    private readonly IWebHostEnvironment _env;
    private readonly IConfiguration _configuration;

    public DepositService(BlogAppDbContext context, IWebHostEnvironment env, IConfiguration configuration)
    {
        _context = context;
        _env = env;
        _configuration = configuration;
    }

    public async Task<DepositRequestResponseDto> CreateRequestAsync(int userId, decimal amount, IFormFile receipt)
    {
        if (receipt == null || receipt.Length == 0)
            throw new ArgumentException("Receipt file is required.", nameof(receipt));

        var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp", ".pdf" };
        var extension = Path.GetExtension(receipt.FileName).ToLowerInvariant();
        if (!allowedExtensions.Contains(extension))
            throw new ArgumentException("Receipt file type is not supported.", nameof(receipt));

        if (receipt.Length > 5 * 1024 * 1024)
            throw new ArgumentException("Receipt file size cannot exceed 5MB.", nameof(receipt));

        var fileName = FileStoragePathHelper.BuildSafeFileName(receipt.FileName);
        var uploadPath = Path.Combine(FileStoragePathHelper.GetUploadsRoot(_env, _configuration), "receipts");

        Directory.CreateDirectory(uploadPath);
        var filePath = Path.Combine(uploadPath, fileName);

        using (var stream = new FileStream(filePath, FileMode.Create))
            await receipt.CopyToAsync(stream);

        var request = new DepositRequest
        {
            UserId = userId,
            Amount = amount,
            ReceiptImagePath = $"/uploads/receipts/{fileName}",
            Status = (int)DepositStatus.Pending
        };

        _context.DepositRequests.Add(request);
        await _context.SaveChangesAsync();

        return MapToDto(request);
    }

    public async Task<List<DepositRequestResponseDto>> GetPendingRequestsAsync()
    {
        var requests = await _context.DepositRequests
            .Include(d => d.User)
            .Where(d => d.Status == (int)DepositStatus.Pending)
            .OrderByDescending(d => d.CreateDate)
            .ToListAsync();

        return requests.Select(MapToDto).ToList();
    }

    public async Task<List<DepositRequestResponseDto>> GetWorkerApprovedRequestsAsync()
    {
        var requests = await _context.DepositRequests
            .Include(d => d.User)
            .Include(d => d.User)
            .Include(d => d.ReviewedByWorker)
            .Where(d => d.Status == (int)DepositStatus.WorkerApproved)
            .OrderByDescending(d => d.CreateDate)
            .ToListAsync();

        return requests.Select(MapToDto).ToList();
    }

    public async Task<DepositRequestResponseDto> WorkerReviewAsync(WorkerReviewDto dto, int workerId)
    {
        var request = await _context.DepositRequests
            .Include(d => d.User)
            .FirstOrDefaultAsync(d => d.Id == dto.DepositRequestId
                                   && d.Status == (int)DepositStatus.Pending);

        if (request == null) throw new Exception("Request not found or already reviewed");

        request.Status = dto.IsApproved
            ? (int)DepositStatus.WorkerApproved
            : (int)DepositStatus.WorkerRejected;
        request.WorkerNote = dto.Note;
        request.ReviewedByWorkerId = workerId;
        request.WorkerReviewedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return MapToDto(request);
    }

    public async Task<DepositRequestResponseDto> BankReviewAsync(BankReviewDto dto, int bankId)
    {
        var request = await _context.DepositRequests
            .Include(d => d.User)
            .FirstOrDefaultAsync(d => d.Id == dto.DepositRequestId
                                   && d.Status == (int)DepositStatus.WorkerApproved);

        if (request == null) throw new Exception("Request not found or not worker-approved");

        if (dto.IsApproved)
        {
            request.Status = (int)DepositStatus.BankApproved;
            // Balansı artır
            var user = await _context.Users.FindAsync(request.UserId);
            user!.Balance += request.Amount;
        }
        else
        {
            request.Status = (int)DepositStatus.BankRejected;
        }

        request.BankNote = dto.Note;
        request.ReviewedByBankId = bankId;
        request.BankReviewedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();
        return MapToDto(request);
    }

    public async Task<List<DepositRequestResponseDto>> GetUserRequestsAsync(int userId)
    {
        var requests = await _context.DepositRequests
            .Where(d => d.UserId == userId)
            .OrderByDescending(d => d.CreateDate)
            .ToListAsync();

        return requests.Select(MapToDto).ToList();
    }

    private DepositRequestResponseDto MapToDto(DepositRequest d) => new()
    {
        Id = d.Id,
        UserId = d.UserId,
        UserName = d.User?.UserName ?? "",
        Amount = d.Amount,
        ReceiptImagePath = d.ReceiptImagePath,
        Status = d.Status,
        StatusText = ((DepositStatus)d.Status).ToString(),
        WorkerNote = d.WorkerNote,
        BankNote = d.BankNote,
        CreateDate = d.CreateDate,
        ReviewedByWorkerId = d.ReviewedByWorkerId,
        ReviewedByWorkerName = d.ReviewedByWorker?.UserName ?? ""  // ← worker adı
    };
    public async Task<List<DepositRequestResponseDto>> GetWorkerHistoryAsync(int workerId, bool isAdmin)
    {
        var query = _context.DepositRequests
            .Include(d => d.User)
            .Include(d => d.User)
            .Include(d => d.ReviewedByWorker)
            .Where(d => d.Status == (int)DepositStatus.WorkerApproved
                     || d.Status == (int)DepositStatus.WorkerRejected
                     || d.Status == (int)DepositStatus.BankApproved    // ← əlavə et
                     || d.Status == (int)DepositStatus.BankRejected);  // ← əlavə et

        if (!isAdmin)
            query = query.Where(d => d.ReviewedByWorkerId == workerId);

        var requests = await query
            .OrderByDescending(d => d.CreateDate)
            .ToListAsync();

        return requests.Select(MapToDto).ToList();
    }

    public async Task<List<DepositRequestResponseDto>> GetBankHistoryAsync()
    {
        // Bank hamısını görür — worker + bank approved/rejected
        var requests = await _context.DepositRequests
            .Include(d => d.User)
            .Include(d => d.User)
            .Include(d => d.ReviewedByWorker)
            .Where(d => d.Status == (int)DepositStatus.WorkerApproved
                     || d.Status == (int)DepositStatus.WorkerRejected
                     || d.Status == (int)DepositStatus.BankApproved
                     || d.Status == (int)DepositStatus.WorkerRejected
                     || d.Status == (int)DepositStatus.BankRejected)
            .OrderByDescending(d => d.CreateDate)
            .ToListAsync();

        return requests.Select(MapToDto).ToList();
    }
}

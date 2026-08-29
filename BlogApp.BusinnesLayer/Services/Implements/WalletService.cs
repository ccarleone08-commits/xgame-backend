using BlogApp.BusinnesLayer.DTOs.WalletDTOs;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.BusinnesLayer.Services.Implements;

public class WalletService : IWalletService
{
    private readonly BlogAppDbContext _context;

    public WalletService(BlogAppDbContext context)
    {
        _context = context;
    }

    public async Task<WalletBalanceDto> GetBalanceAsync(int userId)
    {
        var balance = await _context.Users
            .Where(x => x.Id == userId)
            .Select(x => x.Balance)
            .SingleOrDefaultAsync();

        return new WalletBalanceDto { Balance = balance };
    }

    public async Task<List<CoinLedgerDto>> GetLedgerAsync(int userId, int take = 50)
    {
        take = Math.Clamp(take, 1, 200);

        return await _context.CoinLedgers
            .Where(x => x.UserId == userId)
            .OrderByDescending(x => x.CreateDate)
            .Take(take)
            .Select(x => new CoinLedgerDto
            {
                Id = x.Id,
                Amount = x.Amount,
                Type = x.Type,
                ReferenceId = x.ReferenceId,
                CreateDate = x.CreateDate
            })
            .ToListAsync();
    }
}

using BlogApp.BusinnesLayer.DTOs.WalletDTOs;

namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface IWalletService
{
    Task<WalletBalanceDto> GetBalanceAsync(int userId);
    Task<List<CoinLedgerDto>> GetLedgerAsync(int userId, int take = 50);
}

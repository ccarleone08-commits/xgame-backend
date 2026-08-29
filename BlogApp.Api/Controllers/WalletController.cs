using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlogApp.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/wallet")]
public class WalletController : ControllerBase
{
    private readonly IWalletService _walletService;

    public WalletController(IWalletService walletService)
    {
        _walletService = walletService;
    }

    [HttpGet("balance")]
    public async Task<IActionResult> Balance()
    {
        var userId = ClaimHelper.GetUserId(User);
        return Ok(await _walletService.GetBalanceAsync(userId));
    }

    [HttpGet("ledger")]
    public async Task<IActionResult> Ledger([FromQuery] int take = 50)
    {
        var userId = ClaimHelper.GetUserId(User);
        return Ok(await _walletService.GetLedgerAsync(userId, take));
    }
}

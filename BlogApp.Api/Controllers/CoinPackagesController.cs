using BlogApp.BusinnesLayer.DTOs.PaymentDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.Api.Controllers;

[ApiController]
[Route("api/coin-request")]
public class CoinPackagesController : ControllerBase
{
    private readonly BlogAppDbContext _context;

    public CoinPackagesController(BlogAppDbContext context)
    {
        _context = context;
    }

    [AllowAnonymous]
    [HttpGet]
    public async Task<IActionResult> GetActivePackages()
    {
        var userRequests = new List<CoinPaymentRequestDto>();
        if (User.Identity?.IsAuthenticated == true)
        {
            var userId = ClaimHelper.GetUserId(User);
            if (userId > 0)
            {
                userRequests = await _context.PaymentTransactions
                    .Where(x => x.UserId == userId && !x.IsDeleted)
                    .OrderByDescending(x => x.CreateDate)
                    .Select(x => new CoinPaymentRequestDto
                    {
                        Id = x.Id,
                        CoinPackageId = x.CoinPackageId,
                        CoinPackageName = x.CoinPackage == null ? null : x.CoinPackage.Name,
                        OrderId = x.OrderId,
                        Status = x.Status,
                        CoinsGranted = x.CoinsGranted,
                        CoinAmount = x.CoinAmount,
                        PriceAmount = x.PriceAmount,
                        PriceCurrency = x.PriceCurrency,
                        PayCurrency = x.PayCurrency,
                        PaymentUrl = x.PaymentUrl,
                        CreatedAt = x.CreateDate,
                        CompletedAt = x.CompletedAt
                    })
                    .ToListAsync();
            }
        }

        return Ok(userRequests);
    }
}

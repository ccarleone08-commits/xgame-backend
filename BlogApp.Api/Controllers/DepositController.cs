using BlogApp.BusinnesLayer.DTOs.DepositDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DepositController : ControllerBase
    {
        private readonly IDepositService _depositService;

        private int UserId => ClaimHelper.GetUserId(User);
        private bool IsDepositWorkerOrAdmin => ClaimHelper.IsDepositWorkerOrAdmin(User);
        private bool IsBankOrAdmin => ClaimHelper.IsBankOrAdmin(User);
        private bool IsUser => ClaimHelper.IsUser(User);

        public DepositController(IDepositService depositService)
        {
            _depositService = depositService;
        }

        // User: deposit sorğusu göndər
        [HttpPost("request")]
        [Authorize]
        public async Task<IActionResult> CreateRequest([FromForm] decimal amount, IFormFile receipt)
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _depositService.CreateRequestAsync(userId, amount, receipt);
            return Ok(result);
        }

        // User: öz sorğularını gör
        [HttpGet("my-requests")]
        [Authorize]
        public async Task<IActionResult> GetMyRequests()
        {
            var userId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _depositService.GetUserRequestsAsync(userId);
            return Ok(result);
        }

        // DepositWorker: pending sorğuları gör
        [HttpGet("worker/pending")]
        [Authorize]
        public async Task<IActionResult> GetPending()
        {
            var result = await _depositService.GetPendingRequestsAsync();
            return Ok(result);
        }

        [HttpGet("worker/history")]
        [Authorize]
        public async Task<IActionResult> GetWorkerHistory()
        {
            if (!IsDepositWorkerOrAdmin) return Forbid();
            var isAdmin = ClaimHelper.GetRole(User) == (int)Roles.DepositAdmin; // 256
            var result = await _depositService.GetWorkerHistoryAsync(UserId, isAdmin);
            return Ok(result);
        }

        [HttpGet("bank/history")]
        [Authorize]
        public async Task<IActionResult> GetBankHistory()
        {
            if (!IsBankOrAdmin) return Forbid();
            var result = await _depositService.GetBankHistoryAsync();
            return Ok(result);
        }

        // DepositWorker: qəbul/rədd et
        [HttpPost("worker/review")]
        [Authorize]
        public async Task<IActionResult> WorkerReview([FromBody] WorkerReviewDto dto)
        {
            var workerId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _depositService.WorkerReviewAsync(dto, workerId);
            return Ok(result);
        }

        // Bank: worker-approved sorğuları gör
        [HttpGet("bank/pending")]
        [Authorize]
        public async Task<IActionResult> GetBankPending()
        {
            var result = await _depositService.GetWorkerApprovedRequestsAsync();
            return Ok(result);
        }

        // Bank: qəbul/rədd et → balans artır
        [HttpPost("bank/review")]
        [Authorize]
        public async Task<IActionResult> BankReview([FromBody] BankReviewDto dto)
        {
            var bankId = int.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);
            var result = await _depositService.BankReviewAsync(dto, bankId);
            return Ok(result);
        }
    }
}

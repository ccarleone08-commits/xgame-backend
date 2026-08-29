using BlogApp.BusinnesLayer.DTOs.WithdrawDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class WithdrawController : ControllerBase
    {
        private readonly IWithdrawService _withdrawService;
        private int UserId => ClaimHelper.GetUserId(User);
        private bool IsUser => ClaimHelper.IsUser(User);
        private bool IsWithdrawWorkerOrAdmin => ClaimHelper.IsWithdrawWorkerOrAdmin(User);
        private bool IsWithdrawAdmin => ClaimHelper.IsWithdrawAdmin(User);
        private bool IsWithdrawBank => ClaimHelper.IsWithdrawBank(User);

        public WithdrawController(IWithdrawService withdrawService)
        {
            _withdrawService = withdrawService;
        }

        [HttpPost("request")]
        [Authorize]
        public async Task<IActionResult> CreateRequest([FromBody] WithdrawRequestDto dto)
        {
            if (!IsUser) return Forbid();
            try
            {
                var result = await _withdrawService.CreateRequestAsync(UserId, dto);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("my-requests")]
        [Authorize]
        public async Task<IActionResult> GetMyRequests()
        {
            var result = await _withdrawService.GetUserRequestsAsync(UserId);
            return Ok(result);
        }

        [HttpGet("worker/pending")]
        [Authorize]
        public async Task<IActionResult> GetPending()
        {
            if (!IsWithdrawWorkerOrAdmin) return Forbid();
            var result = await _withdrawService.GetPendingRequestsAsync();
            return Ok(result);
        }

        [HttpPost("worker/review")]
        [Authorize]
        public async Task<IActionResult> WorkerReview([FromBody] WithdrawReviewDto dto)
        {
            if (!IsWithdrawWorkerOrAdmin) return Forbid();
            try
            {
                var result = await _withdrawService.WorkerReviewAsync(dto, UserId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("bank/pending")]
        [Authorize]
        public async Task<IActionResult> GetBankPending()
        {
            if (!IsWithdrawBank) return Forbid();
            var result = await _withdrawService.GetWorkerApprovedRequestsAsync();
            return Ok(result);
        }


        [HttpPost("bank/review")]
        [Authorize]
        public async Task<IActionResult> BankReview([FromBody] WithdrawReviewDto dto)
        {
            if (!IsWithdrawBank) return Forbid();
            try
            {
                var result = await _withdrawService.BankReviewAsync(dto, UserId);
                return Ok(result);
            }
            catch (Exception ex)
            {
                return BadRequest(new { message = ex.Message });
            }
        }

        [HttpGet("worker/history")]
        [Authorize]
        public async Task<IActionResult> GetWorkerHistory()
        {
            if (!IsWithdrawWorkerOrAdmin) return Forbid();
            var isAdmin = ClaimHelper.GetRole(User) == (int)Roles.WithdrawAdmin;
            var result = await _withdrawService.GetWorkerHistoryAsync(UserId, isAdmin);
            return Ok(result);
        }

        [HttpGet("bank/history")]
        [Authorize]
        public async Task<IActionResult> GetBankHistory()
        {
            if (!IsWithdrawBank) return Forbid();
            var result = await _withdrawService.GetBankHistoryAsync();
            return Ok(result);
        }
    }
}

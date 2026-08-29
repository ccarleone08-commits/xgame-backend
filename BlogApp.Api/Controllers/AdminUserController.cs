using BlogApp.BusinnesLayer.DTOs.UserDTOs.BanDTO;
using BlogApp.BusinnesLayer.Services.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/admin/users")]
    public class AdminUserController(IAuthService _authService) : ControllerBase
    {
        [Authorize(Policy = "AdminOnly")]
        [HttpPost("ban")]
        public async Task<IActionResult> BanUser([FromBody] BanUserRequest request)
        {
            await _authService.BanUserAsync(request.Username, request.Deadline);
            return Ok(new { Message = $"User '{request.Username}' banned until {request.Deadline}" });
        }

        /// Remove ban from user
        [Authorize(Policy = "AdminOnly")]
        [HttpPost("unban")]
        public async Task<IActionResult> UnbanUser([FromBody] UnbanUserRequest request)
        {
            await _authService.UnbanUserAsync(request.Username);
            return Ok(new { Message = $"User '{request.Username}' unbanned successfully" });
        }
    }
}

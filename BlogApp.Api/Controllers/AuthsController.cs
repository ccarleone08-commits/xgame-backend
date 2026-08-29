using BlogApp.BusinnesLayer.DTOs.UserDTOs;
using BlogApp.BusinnesLayer.DTOs.UserDTOs.BalanceDTO;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace BlogApp.Api.Controllers
{
    [Route("api/[controller]/[action]")]
    [ApiController]
    public class AuthsController(
        IAuthService _service,
        IUserService _ser,
        IEmailService _emailService,
        IWebHostEnvironment _env,
        IConfiguration _configuration) : ControllerBase
    {
        [AllowAnonymous]
        [HttpPost()]
        public async Task<IActionResult> Login(LoginDto dto)
        {
            var token = await _service.LoginAsync(dto);

            var sameSite = Enum.TryParse<SameSiteMode>(
                _configuration["AuthCookie:SameSite"],
                ignoreCase: true,
                out var parsedSameSite)
                ? parsedSameSite
                : SameSiteMode.None;

            Response.Cookies.Append(_configuration["AuthCookie:Name"] ?? "AuthToken", token, new CookieOptions
            {
                HttpOnly = _configuration.GetValue("AuthCookie:HttpOnly", false),
                Secure = _configuration.GetValue("AuthCookie:Secure", !_env.IsDevelopment()),
                SameSite = sameSite,
                Expires = DateTime.UtcNow.AddHours(_configuration.GetValue("AuthCookie:Hours", 24))
            });

            return Ok(token);
        }

        [AllowAnonymous]
        [Consumes("multipart/form-data")]
        [HttpPost()]
        public async Task<IActionResult> Register([FromForm] RegisterCreateDto dto)
        {
            await _service.RegisterAsync(dto);
            return Ok("Registerasiya olundu");
        }

        [HttpDelete]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> Delete(string username)
        {
            await _ser.UserDeleteAsync(username);
            return NoContent();
        }

        [Authorize]
        [HttpPut("profile/image")]
        public async Task<IActionResult> UpdateProfileImage(IFormFile image)
        {
            try
            {
                // 1. Cari istifadəçini tap
                var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (string.IsNullOrEmpty(userIdClaim))
                    return Unauthorized();

                int userId = int.Parse(userIdClaim);

                // 2. User-i DB-dən gətir
                var currentUser = await _service.GetByUserIdAsync(userId);
                if (currentUser == null)
                    return NotFound(new { error = "İstifadəçi tapılmadı" });

                // 3. Fayl yoxlanışı
                if (image == null || image.Length == 0)
                    return BadRequest(new { error = "Şəkil seçilməyib" });

                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                var extension = Path.GetExtension(image.FileName).ToLowerInvariant();

                if (!allowedExtensions.Contains(extension))
                    return BadRequest(new { error = "Yalnız şəkil faylları yüklənə bilər" });

                if (image.Length > 5 * 1024 * 1024)
                    return BadRequest(new { error = "Şəkil maksimum 5MB ola bilər" });

                // 4. Köhnə şəkli sil (əgər varsa)
                if (!string.IsNullOrEmpty(currentUser.Image) &&
                    !currentUser.Image.EndsWith("/default.png", StringComparison.OrdinalIgnoreCase))
                {
                    var oldRelativePath = currentUser.Image.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var oldFilePath = oldRelativePath.StartsWith($"uploads{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(
                            FileStoragePathHelper.GetUploadsRoot(_env, _configuration),
                            oldRelativePath["uploads".Length..].TrimStart(Path.DirectorySeparatorChar))
                        : Path.Combine(
                            _env.WebRootPath ?? Path.Combine(_env.ContentRootPath, "wwwroot"),
                            oldRelativePath);
                    if (System.IO.File.Exists(oldFilePath))
                    {
                        System.IO.File.Delete(oldFilePath);
                    }
                }

                // 5. Yeni şəkli yüklə
                var fileName = FileStoragePathHelper.BuildSafeFileName(image.FileName);
                var uploadsFolder = Path.Combine(FileStoragePathHelper.GetUploadsRoot(_env, _configuration), "characters");

                if (!Directory.Exists(uploadsFolder))
                    Directory.CreateDirectory(uploadsFolder);

                var filePath = Path.Combine(uploadsFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await image.CopyToAsync(stream);
                }

                // 6. User-in profil şəklini yenilə
                string imageUrl = $"/uploads/characters/{fileName}";
                currentUser.Image = imageUrl;
                await _service.UpdateUserAsync(currentUser);

                return Ok(new
                {
                    message = "Profil şəkli uğurla yeniləndi",
                    imageUrl,
                    user = new
                    {
                        id = currentUser.Id,
                        name = currentUser.Name,
                        profileImage = currentUser.Image
                    }
                });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Xəta baş verdi", details = ex.Message });
            }
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> GetByUserName(string username)
        {
            return Ok(await _service.GetByUserNameAsync(username));
        }


        [HttpPut]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> GetByUserId(int id)
        {
            return Ok(await _service.GetByUserIdAsync(id));
        }

        [HttpPost]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> BalanceUpdate(BalanceDto balance)
        {
            await _service.UpdateBalance(balance.Id, balance.Amout);
            return Ok("Balansa" + " " + balance.Amout + " " + "coin elave olundu");
        }

        [HttpGet("current")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var username = User.Identity?.Name;
            if (username == null)
                return Unauthorized();
            var user = await _service.GetByUserNameAsync(username);
            return Ok(new
            {
                username = user.UserName,
                email = user.Email,
                Role = user.Role,
                Balance = user.Balance,
                isAdmin = user.Role == 1,
                image = user.Image
            });
        }

        [HttpGet]
        [Authorize(Policy = "AdminOnly")]
        public async Task<IActionResult> GetAllUser()
        {
            return Ok(await _service.GetAllAsync());
        }


        [HttpPost("forget-password")]
        public async Task<IActionResult> ForgetPassword([FromBody] ForgetPasswordRequest request)
        {
            var user = await _service.GetByEmailAsync(request.Email);
            if (user == null) return BadRequest(new { Message = "Email tapılmadı." });

            // Token yarat
            user.PasswordResetToken = Guid.NewGuid().ToString();
            user.TokenExpiry = DateTime.UtcNow.AddHours(1);

            // Update
            await _ser.Update(user);
            await _ser.SaveChangesAsync();

            var frontendBaseUrl = (_configuration["App:FrontendBaseUrl"] ??
                                   _configuration["App:PublicBaseUrl"] ??
                                   $"{Request.Scheme}://{Request.Host}")
                .TrimEnd('/');
            var passwordResetPath = _configuration["App:PasswordResetPath"] ?? "/reset-password";
            var resetLink =
                $"{frontendBaseUrl}{passwordResetPath}?token={Uri.EscapeDataString(user.PasswordResetToken)}&email={Uri.EscapeDataString(user.Email)}";
            await _emailService.SendPasswordResetEmail(user.Email, resetLink);

            return Ok(new { Message = "Şifrə sıfırlama linki emailinə göndərildi." });
        }


        [HttpPost("reset-password")]
        public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request)
        {
            var success = await _service.ResetPasswordAsync(request.Email, request.Token, request.NewPassword);

            if (!success)
                return BadRequest(new { Message = "Token səhvdir və ya müddəti bitib." });

            return Ok(new { Message = "Şifrə uğurla dəyişdirildi." });
        }

    }
}

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlogApp.BusinnesLayer.Helpers;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    [Authorize]
    public class ChatController : ControllerBase
    {
        private readonly IWebHostEnvironment _env;
        private readonly IConfiguration _configuration;

        public ChatController(IWebHostEnvironment env, IConfiguration configuration)
        {
            _env = env;
            _configuration = configuration;
        }

        [HttpPost("upload-image")]
        public async Task<IActionResult> UploadImage(IFormFile image)
        {
            try
            {
                if (image == null || image.Length == 0)
                    return BadRequest(new { error = "Şəkil seçilməyib" });

                // Allowed extensions
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
                var extension = Path.GetExtension(image.FileName).ToLowerInvariant();

                if (!allowedExtensions.Contains(extension))
                    return BadRequest(new { error = "Yalnız şəkil faylları yüklənə bilər" });

                // Max 5MB
                if (image.Length > 5 * 1024 * 1024)
                    return BadRequest(new { error = "Şəkil maksimum 5MB ola bilər" });

                // Unique filename
                var fileName = $"{Guid.NewGuid()}{extension}";
                var uploadsFolder = Path.Combine(FileStoragePathHelper.GetUploadsRoot(_env, _configuration), "chat");

                // Folder yaradırıq
                if (!Directory.Exists(uploadsFolder))
                    Directory.CreateDirectory(uploadsFolder);

                var filePath = Path.Combine(uploadsFolder, fileName);

                // Save file
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await image.CopyToAsync(stream);
                }

                // URL return et
                var imageUrl = $"/uploads/chat/{fileName}";

                return Ok(new { imageUrl });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = "Şəkil yüklənmədi", details = ex.Message });
            }
        }
    }
}

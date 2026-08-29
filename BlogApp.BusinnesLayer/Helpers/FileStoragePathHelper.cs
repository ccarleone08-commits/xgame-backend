using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

namespace BlogApp.BusinnesLayer.Helpers;

public static class FileStoragePathHelper
{
    public static string GetUploadsRoot(IWebHostEnvironment environment, IConfiguration configuration)
    {
        var configuredPath = configuration["App:UploadsPath"];
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(Path.IsPathRooted(configuredPath)
                ? configuredPath
                : Path.Combine(environment.ContentRootPath, configuredPath));
        }

        var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
        return Path.Combine(webRoot, "uploads");
    }

    public static string BuildSafeFileName(string originalFileName)
    {
        var extension = Path.GetExtension(originalFileName).ToLowerInvariant();
        return $"{Guid.NewGuid():N}{extension}";
    }
}

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class HealthCheckController : ControllerBase
    {
        private readonly HealthCheckService _healthCheckService;

        public HealthCheckController(HealthCheckService healthCheckService)
        {
            _healthCheckService = healthCheckService;
        }

        /// <summary>
        /// Bütün servislər statusu - Database, API və SignalR'ın sağlamlığını yoxlayır
        /// </summary>
        /// <returns>Bütün servislər haqqında detalı məlumat JSON formatında</returns>
        /// <response code="200">Tüm servisler sağlam</response>
        /// <response code="503">Bir veya daha fazla servis çalışmıyor</response>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
        public async Task<IActionResult> GetAllHealthStatus()
        {
            var report = await _healthCheckService.CheckHealthAsync();

            var services = new Dictionary<string, object>();

            // Database statusu
            if (report.Entries.ContainsKey("Database"))
            {
                var dbStatus = report.Entries["Database"];
                services["Database"] = new
                {
                    status = dbStatus.Status.ToString(),
                    description = dbStatus.Description,
                    duration = dbStatus.Duration.TotalMilliseconds,
                    type = "Database Connection"
                };
            }

            // API statusu
            if (report.Entries.ContainsKey("API"))
            {
                var apiStatus = report.Entries["API"];
                services["API"] = new
                {
                    status = apiStatus.Status.ToString(),
                    description = apiStatus.Description,
                    duration = apiStatus.Duration.TotalMilliseconds,
                    type = "API Server"
                };
            }

            // SignalR statusu
            if (report.Entries.ContainsKey("SignalR"))
            {
                var signalrStatus = report.Entries["SignalR"];
                services["SignalR"] = new
                {
                    status = signalrStatus.Status.ToString(),
                    description = signalrStatus.Description,
                    duration = signalrStatus.Duration.TotalMilliseconds,
                    type = "Real-time Connection"
                };
            }

            var response = new
            {
                status = report.Status.ToString(),
                message = report.Status == HealthStatus.Healthy 
                    ? "Bütün servislər normal işləyir" 
                    : "Bir və ya daha çox servisdə problem var",
                timestamp = DateTime.UtcNow,
                uptime = GC.GetTotalMemory(false) / 1024 / 1024, // MB
                services = services,
                summary = new
                {
                    totalChecks = services.Count,
                    healthyChecks = services.Values.Count(s => s.GetType().GetProperty("status")?.GetValue(s)?.ToString() == "Healthy"),
                    unhealthyChecks = services.Values.Count(s => s.GetType().GetProperty("status")?.GetValue(s)?.ToString() != "Healthy")
                }
            };

            var statusCode = report.Status == HealthStatus.Healthy 
                ? StatusCodes.Status200OK 
                : StatusCodes.Status503ServiceUnavailable;
            
            return StatusCode(statusCode, response);
        }
    }
}

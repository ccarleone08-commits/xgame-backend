//using BlogApp.Core.Entities;
//using BlogApp.DAL.DALs;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.EntityFrameworkCore;
//using System.Security.Claims;

//namespace BlogApp.Api.Controllers
//{
//    [ApiController]
//    [Route("api/[controller]")]
//    public class SupportController : ControllerBase
//    {
//        private readonly BlogAppDbContext _context;
//        private readonly ILogger<SupportController> _logger;

//        public SupportController(BlogAppDbContext context, ILogger<SupportController> logger)
//        {
//            _context = context;
//            _logger = logger;
//        }

//        // User - New ticket
//        [HttpPost]
//        [Authorize]
//        public async Task<ActionResult<object>> CreateTicket([FromBody] CreateTicketRequest request, [FromQuery] int? id)
//        {
//            if (!ModelState.IsValid)
//                return BadRequest(ModelState);

//            try
//            {
//                // User ID-ni token-dən al
//                var userIdFromToken = User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
//                var userName = User?.FindFirst(ClaimTypes.Name)?.Value;
//                var userEmail = User?.FindFirst(ClaimTypes.Email)?.Value;

//                // Query parametrə əlavə olaraq ID göndərilibsə, onu istifadə et
//                int userId = id ?? (string.IsNullOrEmpty(userIdFromToken) ? 0 : int.Parse(userIdFromToken));

//                if (userId <= 0)
//                    return BadRequest(new { message = "İstifadəçi ID tapılmadı" });

//                // User məlumatlarını database-dən al
//                var user = await _context.Users.FindAsync(userId);

//                if (user == null)
//                    return NotFound(new { message = "İstifadəçi tapılmadı" });

//                // Ticket yaratma
//                var ticket = new SupportTicket
//                {
//                    UserId = userIdFromToken,
//                    FullName = user.Name ?? userName,
//                    Email = user.Email ?? userEmail ?? request.Email,
//                    Subject = request.Subject.Trim(),
//                    Message = request.Message.Trim(),
//                    Status = TicketStatus.Open,
//                    CreatedAt = DateTime.UtcNow
//                };

//                _context.SupportTickets.Add(ticket);
//                await _context.SaveChangesAsync();

//                _logger.LogInformation($"New support ticket created: {ticket.Id} from {ticket.Email}");

//                return Created($"/api/support/{ticket.Id}", new
//                {
//                    message = "Müraciətiniz uğurla göndərildi. Id: " + ticket.Id,
//                    ticketId = ticket.Id
//                });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError($"Support ticket creation error: {ex.Message}");
//                return StatusCode(500, new { message = "Müraciət yaradıla bilədi" });
//            }
//        }

//        // Admin - Get all tickets
//        [HttpGet("admin/list")]
//        [Authorize(Policy = "AdminOnly")]
//        public async Task<ActionResult<IEnumerable<object>>> GetAllTickets([FromQuery] int status = 0)
//        {
//            var query = _context.SupportTickets.AsQueryable();

//            if (status > 0)
//                query = query.Where(t => (int)t.Status == status);

//            var tickets = await query
//                .OrderByDescending(t => t.CreatedAt)
//                .Select(t => new
//                {
//                    t.Id,
//                    t.FullName,
//                    t.Email,
//                    t.Subject,
//                    t.Status,
//                    t.CreatedAt
//                })
//                .ToListAsync();

//            return Ok(tickets);
//        }

//        // Admin - Get single ticket details
//        [HttpGet("admin/{id}")]
//        [Authorize(Policy = "AdminOnly")]
//        public async Task<ActionResult<object>> GetTicketDetails(int id)
//        {
//            var ticket = await _context.SupportTickets.FindAsync(id);

//            if (ticket == null)
//                return NotFound(new { message = "Ticket tapılmadı" });

//            return Ok(new
//            {
//                ticket.Id,
//                ticket.FullName,
//                ticket.Email,
//                ticket.Subject,
//                ticket.Message,
//                ticket.Status,
//                ticket.CreatedAt,
//                ticket.UserId
//            });
//        }

//        // Admin - Close ticket
//        [HttpPut("admin/{id}/close")]
//        [Authorize(Policy = "AdminOnly")]
//        public async Task<ActionResult> CloseTicket(int id)
//        {
//            var ticket = await _context.SupportTickets.FindAsync(id);

//            if (ticket == null)
//                return NotFound(new { message = "Ticket tapılmadı" });

//            ticket.Status = TicketStatus.Closed;
//            await _context.SaveChangesAsync();

//            _logger.LogInformation($"Support ticket closed: {id}");

//            return Ok(new { message = "Ticket bağlanmışdır" });
//        }

//        // Admin - Change ticket status
//        [HttpPut("admin/{id}/status")]
//        [Authorize(Policy = "AdminOnly")]
//        public async Task<ActionResult> UpdateStatus(int id, [FromBody] UpdateStatusRequest request)
//        {
//            var ticket = await _context.SupportTickets.FindAsync(id);

//            if (ticket == null)
//                return NotFound(new { message = "Ticket tapılmadı" });

//            ticket.Status = request.Status;
//            await _context.SaveChangesAsync();

//            return Ok(new { message = "Status yenilənmişdir" });
//        }
//    }

//    // Request Models
//    public class CreateTicketRequest
//    {
//        public string Subject { get; set; }
//        public string Message { get; set; }
//        public string? Email { get; set; } // Fallback email
//    }

//    public class UpdateStatusRequest
//    {
//        public TicketStatus Status { get; set; }
//    }
//}
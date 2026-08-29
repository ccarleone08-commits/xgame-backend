using BlogApp.API.Hubs;
using BlogApp.BusinnesLayer.DTOs.SupportsDTO;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace BlogApp.Api.Controllers
{
    [ApiController]
    [Route("api/support/tickets")]
    [Authorize]
    public class SupportTicketsController(
       ISupportTicketService _service,
       IHubContext<SupportHub> _hub) : ControllerBase
    {
        private int UserId => ClaimHelper.GetUserId(User);
        private string Username => ClaimHelper.GetUsername(User);
        private bool IsAdmin => ClaimHelper.IsSupportAdmin(User);
        private bool IsWorkerOrAdmin => ClaimHelper.IsSupportWorkerOrAdmin(User);

        [HttpGet]
        public async Task<IActionResult> GetAll([FromQuery] TicketStatus? status)
        {
            if (!IsWorkerOrAdmin) return Forbid();
            var result = await _service.GetAllAsync(UserId, IsAdmin, status);
            return Ok(result);
        }

        [HttpGet("{id}")]
        public async Task<IActionResult> GetById(int id)
        {
            if (IsWorkerOrAdmin)
            {
                var result = await _service.GetByIdAsync(id, UserId, IsAdmin);
                return Ok(result);
            }
            else
            {
                // Adi user yalnız öz ticketini görə bilər
                var result = await _service.GetByIdForUserAsync(id, UserId);
                if (result == null) return Forbid();
                return Ok(result);
            }
        }

        [HttpPost]
        public async Task<IActionResult> Create([FromBody] CreateTicketDto dto)
        {
            var ticketNumber = await _service.CreateAsync(dto, UserId == 0 ? null : UserId);

            await _hub.Clients.Group("support-staff").SendAsync("NewTicket", new
            {
                ticketNumber,
                subject = dto.Subject,
                priority = dto.Priority.ToString(),
                createdAt = DateTime.UtcNow
            });

            return Ok(new { ticketNumber, message = "Ticket yaradıldı" });
        }

        [HttpPost("{id}/claim")]
        public async Task<IActionResult> Claim(int id)
        {
            if (!IsWorkerOrAdmin) return Forbid();
            await _service.ClaimAsync(id, UserId, Username);

            await _hub.Clients.Group("support-staff").SendAsync("TicketClaimed", new
            {
                ticketId = id,
                workerName = Username,
                claimedAt = DateTime.UtcNow
            });

            return Ok(new { message = "Ticket götürüldü" });
        }

        [HttpPost("{id}/solve")]
        public async Task<IActionResult> Solve(int id, [FromBody] SolveTicketDto dto)
        {
            if (!IsWorkerOrAdmin) return Forbid();
            await _service.SolveAsync(id, UserId, Username, dto.ReplyMessage, IsAdmin);

            await _hub.Clients.Group($"ticket-{id}").SendAsync("TicketSolved", new
            {
                ticketId = id,
                solvedBy = Username,
                replyMessage = dto.ReplyMessage,
                solvedAt = DateTime.UtcNow
            });

            await _hub.Clients.Group("support-staff").SendAsync("TicketSolved", new
            {
                ticketId = id,
                solvedBy = Username
            });

            return Ok(new { message = "Ticket həll edildi" });
        }

        [HttpPost("{id}/reopen")]
        public async Task<IActionResult> Reopen(int id)
        {
            if (!IsAdmin) return Forbid();
            await _service.ReopenAsync(id, Username);

            await _hub.Clients.Group("support-staff").SendAsync("TicketReopened", new
            {
                ticketId = id,
                reopenedBy = Username
            });

            return Ok(new { message = "Ticket yenidən açıldı" });
        }

        [HttpGet("{id}/messages")]
        public async Task<IActionResult> GetMessages(int id)
        {
            if (!IsWorkerOrAdmin) return Forbid();
            var messages = await _service.GetMessagesAsync(id, UserId, IsAdmin);
            return Ok(messages);
        }
        [HttpGet("my")]
        public async Task<IActionResult> GetMyTickets()
        {
            if (UserId == 0) return Forbid();
            var result = await _service.GetMyTicketsAsync(UserId);
            return Ok(result);
        }
        [HttpGet("/api/support/stats")]
        public async Task<IActionResult> GetStats()
        {
            if (!IsAdmin) return Forbid();
            var stats = await _service.GetStatsAsync();
            return Ok(stats);
        }
    }
}

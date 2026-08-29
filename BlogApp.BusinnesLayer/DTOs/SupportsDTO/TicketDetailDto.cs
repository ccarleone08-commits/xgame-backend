using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class TicketDetailDto
    {
        public int Id { get; set; }

        public int? UserId { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public TicketCategory Category { get; set; }
        public TicketPriority Priority { get; set; }
        public TicketStatus Status { get; set; }
        public DateTime CreateDate { get; set; }
        public string? AssignedWorkerName { get; set; }
        public DateTime? ClaimedAt { get; set; }
        public DateTime? SolvedAt { get; set; }
        public List<MessageDto> Messages { get; set; } = new();
    }
}

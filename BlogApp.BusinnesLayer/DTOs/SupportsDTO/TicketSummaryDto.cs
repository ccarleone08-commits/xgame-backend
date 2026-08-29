using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class TicketSummaryDto
    {
        public int Id { get; set; }
        public string TicketNumber { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public TicketStatus Status { get; set; }
        public TicketPriority Priority { get; set; }
        public TicketCategory Category { get; set; }
        public DateTime CreateDate { get; set; }
        public DateTime? SolvedAt { get; set; }
    }
}

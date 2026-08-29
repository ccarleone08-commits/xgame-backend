using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class CreateTicketDto
    {
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public TicketCategory Category { get; set; }
        public TicketPriority Priority { get; set; }
    }
}

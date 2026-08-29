namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class ClaimResultDto
    {
        public string Message { get; set; } = string.Empty;
        public string TicketNumber { get; set; } = string.Empty;
        public DateTime ClaimedAt { get; set; }
    }
}

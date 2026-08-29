namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class SolveResultDto
    {
        public string Message { get; set; } = string.Empty;
        public string TicketNumber { get; set; } = string.Empty;
        public string SolvedBy { get; set; } = string.Empty;
        public DateTime SolvedAt { get; set; }
    }

}

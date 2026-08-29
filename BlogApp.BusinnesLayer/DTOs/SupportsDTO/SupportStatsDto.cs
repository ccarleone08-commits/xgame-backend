namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class SupportStatsDto
    {
        public List<StatusStatDto> TicketStats { get; set; } = new();
        public List<WorkerStatDto> WorkerStats { get; set; } = new();
    }

    public class StatusStatDto
    {
        public string Status { get; set; } = string.Empty;
        public int Count { get; set; }
    }

    public class WorkerStatDto
    {
        public string WorkerName { get; set; } = string.Empty;
        public int Total { get; set; }
        public int Solved { get; set; }
    }
}

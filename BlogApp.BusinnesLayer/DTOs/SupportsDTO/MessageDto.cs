namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class MessageDto
    {
        public int Id { get; set; }
        public int SenderId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTime SentAt { get; set; }
        public bool IsInternal { get; set; }
    }
}

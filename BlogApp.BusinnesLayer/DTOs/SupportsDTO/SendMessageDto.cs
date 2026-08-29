namespace BlogApp.BusinnesLayer.DTOs.SupportsDTO
{
    public class SendMessageDto
    {
        public string Content { get; set; } = string.Empty;
        public bool IsInternal { get; set; } = false;
    }
}

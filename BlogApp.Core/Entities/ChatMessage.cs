using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace BlogApp.Core.Entities
{
    [Table("ChatMessages")]
    public class ChatMessage
    {
        [Key]
        public int Id { get; set; }
        [Required]
        [MaxLength(100)]
        public string Sender { get; set; }
        [Required]
        [MaxLength(100)]
        public string Receiver { get; set; }
        [MaxLength(2000)]
        public string Content { get; set; }
        [MaxLength(500)]
        public string? ImageUrl { get; set; }
        [MaxLength(20)]
        public string Type { get; set; } = "text";
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
        public bool IsRead { get; set; } = false;

    }
}
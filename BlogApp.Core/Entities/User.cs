using System.ComponentModel.DataAnnotations.Schema;

namespace BlogApp.Core.Entities;

public class User : BaseEntity
{
    public string UserName { get; set; }
    [Column("Image")]
    public string? Image { get; set; }
    public string? Name { get; set; }
    public string? Surname { get; set; }
    public string PasswordHash { get; set; }
    public string Email { get; set; }
    public bool IsMale { get; set; } //bbb
    public string? PhoneNum { get; set; }
    public decimal Balance { get; set; } = 0;
    public int Role { get; set; } = 8;
    public DateTime? BanDeadline { get; set; }


    // Forget/Reset Password üçün əlavə sahələr
    public string? PasswordResetToken { get; set; }
    public DateTime? TokenExpiry { get; set; }

    // Support sistemi üçün
    public ICollection<SupportTicket> OpenedTickets { get; set; } = new List<SupportTicket>();
    public ICollection<SupportTicket> AssignedTickets { get; set; } = new List<SupportTicket>();

}

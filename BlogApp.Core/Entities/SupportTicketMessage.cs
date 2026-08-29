namespace BlogApp.Core.Entities
{
    public class SupportTicketMessage : BaseEntity
    {
        public int TicketId { get; set; }
        public SupportTicket Ticket { get; set; } = null!;

        // Mesaj göndərən (worker və ya admin)
        public int SenderId { get; set; }
        public User Sender { get; set; } = null!;

        public string Content { get; set; } = string.Empty;

        // Admin bütün mesajları görür, worker yalnız özününkünü
        public bool IsInternal { get; set; } = false;
    }
}

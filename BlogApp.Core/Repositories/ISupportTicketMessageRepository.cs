using BlogApp.Core.Entities;

namespace BlogApp.Core.Repositories
{
    public interface ISupportTicketMessageRepository : IGenericRepository<SupportTicketMessage>
    {
        IQueryable<SupportTicketMessage> GetByTicket(int ticketId);
        Task SaveChangesAsync();
    }
}

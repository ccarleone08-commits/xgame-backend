using BlogApp.Core.Entities;

namespace BlogApp.Core.Repositories
{
    public interface ISupportTicketRepository : IGenericRepository<SupportTicket>
    {
        IQueryable<SupportTicket> GetAllWithIncludes();
        IQueryable<SupportTicket> GetByWorker(int workerId);
        Task<SupportTicket?> GetByIdWithDetailsAsync(int id);
        Task<string> GenerateTicketNumberAsync();
        Task SaveChangesAsync();
        void Update(SupportTicket ticket);
    }
}

using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.DAL.Repositories
{
    public class SupportTicketMessageRepository(BlogAppDbContext _context)
      : GenericRepository<SupportTicketMessage>(_context), ISupportTicketMessageRepository
    {
        public IQueryable<SupportTicketMessage> GetByTicket(int ticketId)
            => _context.SupportTicketMessages
                .Include(m => m.Sender)
                .Where(m => m.TicketId == ticketId && !m.IsDeleted)
                .OrderBy(m => m.CreateDate)
                .AsQueryable();

        public async Task SaveChangesAsync()
            => await _context.SaveChangesAsync();
    }

}

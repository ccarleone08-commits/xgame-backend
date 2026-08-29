using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.DAL.Repositories
{
    public class SupportTicketRepository(BlogAppDbContext _context)
    : GenericRepository<SupportTicket>(_context), ISupportTicketRepository
    {
        // Bütün ticketlər — AssignedWorker ilə
        public IQueryable<SupportTicket> GetAllWithIncludes()
            => _context.SupportTickets
                .Include(t => t.AssignedWorker)
                .Where(t => !t.IsDeleted)
                .AsQueryable();

        // Worker-ə aid ticketlər (açıq olanlar + özününkülər)
        public IQueryable<SupportTicket> GetByWorker(int workerId)
            => _context.SupportTickets
                .Include(t => t.AssignedWorker)
                .Where(t => !t.IsDeleted &&
                            (t.Status == TicketStatus.Open ||
                             t.AssignedWorkerId == workerId))
                .AsQueryable();

        // Ticket + bütün mesajlar + sender
        public async Task<SupportTicket?> GetByIdWithDetailsAsync(int id)
            => await _context.SupportTickets
                .Include(t => t.AssignedWorker)
                .Include(t => t.Messages.Where(m => !m.IsDeleted))
                    .ThenInclude(m => m.Sender)
                .FirstOrDefaultAsync(t => t.Id == id && !t.IsDeleted);

        // TK-1001, TK-1002 ... avtomatik generate
        public async Task<string> GenerateTicketNumberAsync()
        {
            var last = await _context.SupportTickets
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync();

            int next = 1001;
            if (last is not null)
            {
                var parts = last.TicketNumber.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[1], out int num))
                    next = num + 1;
            }
            return $"TK-{next}";
        }

        public async Task SaveChangesAsync()
            => await _context.SaveChangesAsync();

        public void Update(SupportTicket ticket)
        {
            _context.SupportTickets.Update(ticket);
            _context.SaveChanges();
        }
    }
}

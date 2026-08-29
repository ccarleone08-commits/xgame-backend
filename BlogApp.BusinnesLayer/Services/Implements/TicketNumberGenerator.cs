using BlogApp.DAL.DALs;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.BusinnesLayer.Services.Implements
{
    public class TicketNumberGenerator
    {
        private readonly BlogAppDbContext _db;

        public TicketNumberGenerator(BlogAppDbContext db) => _db = db;

        public async Task<string> GenerateAsync()
        {
            var lastTicket = await _db.SupportTickets
                .OrderByDescending(t => t.Id)
                .FirstOrDefaultAsync();

            int nextNum = 1001;
            if (lastTicket is not null)
            {
                // "TK-1001" → 1001
                var parts = lastTicket.TicketNumber.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[1], out int last))
                    nextNum = last + 1;
            }

            return $"TK-{nextNum}";
        }
    }
}

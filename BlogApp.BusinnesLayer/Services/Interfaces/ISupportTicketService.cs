using BlogApp.BusinnesLayer.DTOs.SupportsDTO;
using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.Services.Interfaces
{
    public interface ISupportTicketService
    {
        Task<List<TicketListDto>> GetAllAsync(int requesterId, bool isAdmin, TicketStatus? status);
        Task<TicketDetailDto> GetByIdAsync(int id, int requesterId, bool isAdmin);
        Task<string> CreateAsync(CreateTicketDto dto, int? userId);
        Task ClaimAsync(int ticketId, int workerId, string workerName);
        Task SolveAsync(int ticketId, int solverId, string solverName, string replyMessage, bool isAdmin);
        Task ReopenAsync(int ticketId, string adminName);
        Task<List<TicketSummaryDto>> GetMyTicketsAsync(int userId);
        Task<TicketDetailDto?> GetByIdForUserAsync(int ticketId, int userId);
        // Mesaj əməliyyatları
        Task<List<MessageDto>> GetMessagesAsync(int ticketId, int requesterId, bool isAdmin);
        Task<MessageDto> AddMessageAsync(int ticketId, int senderId, string senderName,
            string content, bool isInternal, bool isAdmin, bool isWorkerOrAdmin);
        // Statistika
        Task<SupportStatsDto> GetStatsAsync();
    }
}

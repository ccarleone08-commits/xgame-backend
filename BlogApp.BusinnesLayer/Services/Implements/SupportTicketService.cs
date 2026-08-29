using BlogApp.BusinnesLayer.DTOs.SupportsDTO;
using BlogApp.BusinnesLayer.Exceptions.Common;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;

namespace BlogApp.BusinnesLayer.Services.Implements;
public class SupportTicketService(
     ISupportTicketRepository _ticketRepo,
     ISupportTicketMessageRepository _messageRepo) : ISupportTicketService
{
    // ─────────────────────────────────────────
    // Bütün ticketlər
    // Admin → hamısı | Worker → open + özününkülər
    // ─────────────────────────────────────────
    public async Task<List<TicketListDto>> GetAllAsync(
        int requesterId, bool isAdmin, TicketStatus? status)
    {
        var query = isAdmin
            ? _ticketRepo.GetAllWithIncludes()
            : _ticketRepo.GetByWorker(requesterId);

        if (status.HasValue)
            query = query.Where(t => t.Status == status.Value);

        return await query
            .OrderByDescending(t => t.CreateDate)
           .Select(t => new TicketListDto
           {
               Id = t.Id,
               TicketNumber = t.TicketNumber,
               FullName = t.FullName,
               Email = t.Email,
               Subject = t.Subject,
               Category = t.Category,
               Priority = t.Priority,
               Status = t.Status,
               CreateDate = t.CreateDate,
               AssignedWorkerName = t.AssignedWorker != null
    ? t.AssignedWorker.UserName
    : null,
               ClaimedAt = t.ClaimedAt,
               SolvedAt = t.SolvedAt
           })
            .ToListAsync();
    }

    // ─────────────────────────────────────────
    // Ticket detalı
    // ─────────────────────────────────────────
    public async Task<TicketDetailDto> GetByIdAsync(int id, int requesterId, bool isAdmin)
    {
        var ticket = await _ticketRepo.GetByIdWithDetailsAsync(id);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        // Worker yalnız öz ticketini görə bilər
        if (!isAdmin &&
            ticket.AssignedWorkerId != requesterId &&
            ticket.Status != TicketStatus.Open)
            throw new UnauthorizedAccessException("Bu ticketə giriş icazəniz yoxdur");

        var messages = ticket.Messages
     .Where(m => isAdmin || !m.IsInternal)
     .OrderBy(m => m.CreateDate)
     .Select(m => new MessageDto
     {
         Id = m.Id,
         SenderId = m.SenderId,
         SenderName = m.Sender.UserName,
         Content = m.Content,
         SentAt = m.CreateDate,
         IsInternal = m.IsInternal
     })
     .ToList();

        return new TicketDetailDto
        {
            Id = ticket.Id,
            UserId = ticket.UserId,
            TicketNumber = ticket.TicketNumber,
            FullName = ticket.FullName,
            Email = ticket.Email,
            Subject = ticket.Subject,
            Message = ticket.Message,
            Category = ticket.Category,
            Priority = ticket.Priority,
            Status = ticket.Status,
            CreateDate = ticket.CreateDate,
            AssignedWorkerName = ticket.AssignedWorker?.UserName,
            ClaimedAt = ticket.ClaimedAt,   // DTO-da var, əvvəl ötürmüşdün
            SolvedAt = ticket.SolvedAt,
            Messages = messages
        };
    }
    public async Task<TicketDetailDto?> GetByIdForUserAsync(int ticketId, int userId)
    {
        var ticket = await _ticketRepo.GetByIdWithDetailsAsync(ticketId);
        if (ticket == null || ticket.UserId != userId) return null;

        var messages = ticket.Messages
            .Where(m => !m.IsInternal)
            .OrderBy(m => m.CreateDate)
            .Select(m => new MessageDto
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = m.Sender.UserName,
                Content = m.Content,
                SentAt = m.CreateDate,
                IsInternal = false
            }).ToList();

        return new TicketDetailDto
        {
            Id = ticket.Id,
            UserId = ticket.UserId,
            TicketNumber = ticket.TicketNumber,
            FullName = ticket.FullName,
            Email = ticket.Email,
            Subject = ticket.Subject,
            Message = ticket.Message,
            Category = ticket.Category,
            Priority = ticket.Priority,
            Status = ticket.Status,
            CreateDate = ticket.CreateDate,
            AssignedWorkerName = ticket.AssignedWorker?.UserName,
            ClaimedAt = ticket.ClaimedAt,
            SolvedAt = ticket.SolvedAt,
            Messages = messages
        };
    }
    // ─────────────────────────────────────────
    // Ticket yarat
    // ─────────────────────────────────────────
    public async Task<string> CreateAsync(CreateTicketDto dto, int? userId)
    {
        var ticketNumber = await _ticketRepo.GenerateTicketNumberAsync();

        var ticket = new SupportTicket
        {
            TicketNumber = ticketNumber,
            UserId = userId,
            FullName = dto.FullName,
            Email = dto.Email,
            Subject = dto.Subject,
            Message = dto.Message,
            Category = dto.Category,
            Priority = dto.Priority,
            Status = TicketStatus.Open
        };

        await _ticketRepo.AddAsync(ticket);
        await _ticketRepo.SaveChangesAsync();

        return ticketNumber;
    }

    // ─────────────────────────────────────────
    // Ticket claim et
    // ─────────────────────────────────────────
    public async Task ClaimAsync(int ticketId, int workerId, string workerName)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        if (ticket.Status != TicketStatus.Open)
            throw new InvalidOperationException("Bu ticket artıq götürülüb");

        ticket.AssignedWorkerId = workerId;
        ticket.Status = TicketStatus.Claimed;
        ticket.ClaimedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

        _ticketRepo.Update(ticket);
        await _ticketRepo.SaveChangesAsync();
    }

    // ─────────────────────────────────────────
    // Ticket həll et
    // ─────────────────────────────────────────
    public async Task SolveAsync(
        int ticketId, int solverId, string solverName,
        string replyMessage, bool isAdmin)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        if (!isAdmin && ticket.AssignedWorkerId != solverId)
            throw new UnauthorizedAccessException("Bu ticketi həll etmək icazəniz yoxdur");

        if (ticket.Status == TicketStatus.Solved)
            throw new InvalidOperationException("Ticket artıq həll edilib");

        ticket.Status = TicketStatus.Solved;
        ticket.SolvedAt = DateTime.UtcNow;
        ticket.UpdatedAt = DateTime.UtcNow;

        _ticketRepo.Update(ticket);

        // Həll mesajını əlavə et
        await _messageRepo.AddAsync(new SupportTicketMessage
        {
            TicketId = ticketId,
            SenderId = solverId,
            Content = replyMessage,
            IsInternal = false
        });

        await _ticketRepo.SaveChangesAsync();
    }

    // ─────────────────────────────────────────
    // Ticket reopen (yalnız Admin)
    // ─────────────────────────────────────────
    public async Task ReopenAsync(int ticketId, string adminName)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        ticket.Status = TicketStatus.Open;
        ticket.AssignedWorkerId = null;
        ticket.ClaimedAt = null;
        ticket.SolvedAt = null;
        ticket.UpdatedAt = DateTime.UtcNow;

        _ticketRepo.Update(ticket);
        await _ticketRepo.SaveChangesAsync();
    }

    // ─────────────────────────────────────────
    // Mesajlar
    // ─────────────────────────────────────────
    public async Task<List<MessageDto>> GetMessagesAsync(
        int ticketId, int requesterId, bool isAdmin)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        if (!isAdmin && ticket.AssignedWorkerId != requesterId)
            throw new UnauthorizedAccessException("Bu ticketə giriş icazəniz yoxdur");
        return await _messageRepo.GetByTicket(ticketId)
            .Where(m => isAdmin || !m.IsInternal)
            .Select(m => new MessageDto
            {
                Id = m.Id,
                SenderId = m.SenderId,
                SenderName = m.Sender.UserName,
                Content = m.Content,
                SentAt = m.CreateDate,
                IsInternal = m.IsInternal
            })
            .ToListAsync();
    }

    // Mesaj əlavə et
    public async Task<MessageDto> AddMessageAsync(
       int ticketId, int senderId, string senderName,
       string content, bool isInternal, bool isAdmin, bool isWorkerOrAdmin)
    {
        var ticket = await _ticketRepo.GetByIdAsync(ticketId);
        if (ticket is null) throw new NotFoundException<SupportTicket>();

        if (isWorkerOrAdmin)
        {
            // Yalnız admin internal mesaj yaza bilər
            if (!isAdmin && isInternal)
                throw new UnauthorizedAccessException("Internal mesaj göndərmək icazəniz yoxdur");

            // Worker yoxlamasını SILIRIK - istənilən worker cavab verə bilər
            // Yalnız claim edilmiş ticketlərə mesaj yazılsın
            if (!isAdmin && ticket.Status == TicketStatus.Open)
                throw new UnauthorizedAccessException("Əvvəlcə ticketi götürün");
        }
        else
        {
            // Adi user yalnız öz ticketinə mesaj yaza bilər
            if (ticket.UserId != senderId)
                throw new UnauthorizedAccessException();

            if (isInternal)
                throw new UnauthorizedAccessException();
        }
        var message = new SupportTicketMessage
        {
            TicketId = ticketId,
            SenderId = senderId,
            Content = content,
            IsInternal = isInternal
        };

        await _messageRepo.AddAsync(message);

        if (ticket.Status == TicketStatus.Claimed)
        {
            ticket.Status = TicketStatus.InProgress;
            ticket.UpdatedAt = DateTime.UtcNow;
            _ticketRepo.Update(ticket);
        }

        await _messageRepo.SaveChangesAsync();

        return new MessageDto
        {
            Id = message.Id,
            SenderId = senderId,
            SenderName = senderName,
            Content = message.Content,
            SentAt = message.CreateDate,
            IsInternal = message.IsInternal
        };
    }
    public async Task<List<TicketSummaryDto>> GetMyTicketsAsync(int userId)
    {
        return await _ticketRepo.GetAllWithIncludes()
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreateDate)
            .Select(t => new TicketSummaryDto
            {
                Id = t.Id,
                TicketNumber = t.TicketNumber,
                Subject = t.Subject,
                Status = t.Status,
                Priority = t.Priority,
                Category = t.Category,
                CreateDate = t.CreateDate,
                SolvedAt = t.SolvedAt
            })
            .ToListAsync();
    }
    // ─────────────────────────────────────────
    // Statistika (Admin)
    // ─────────────────────────────────────────
    public async Task<SupportStatsDto> GetStatsAsync()
    {
        var tickets = _ticketRepo.GetAllWithIncludes();

        var statusStats = await tickets
     .GroupBy(t => t.Status)
     .Select(g => new StatusStatDto
     {
         Status = g.Key.ToString(),
         Count = g.Count()
     })
     .ToListAsync();

        var workerStats = await tickets
            .Where(t => t.AssignedWorkerId != null)
            .GroupBy(t => new { t.AssignedWorkerId, t.AssignedWorker!.UserName })
            .Select(g => new WorkerStatDto
            {
                WorkerName = g.Key.UserName,
                Total = g.Count(),
                Solved = g.Count(t => t.Status == TicketStatus.Solved)
            })
            .ToListAsync();

        return new SupportStatsDto
        {
            TicketStats = statusStats,
            WorkerStats = workerStats
        };
    }
}

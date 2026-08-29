using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace BlogApp.API.Hubs;

[Authorize]
public class SupportHub : Hub
{
    private readonly ISupportTicketService _ticketService;

    public SupportHub(ISupportTicketService ticketService)
    {
        _ticketService = ticketService;
    }

    // ─── Claim helpers ───────────────────────────────────────
    private int UserId =>
        int.TryParse(
            Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? Context.User?.FindFirstValue("UserId"),
            out var id) ? id : 0;

    private string Username =>
        Context.User?.FindFirstValue(ClaimTypes.Name)
        ?? Context.User?.FindFirstValue("UserName")
        ?? "Unknown";

    private int UserRole =>
        int.TryParse(
            Context.User?.FindFirstValue(ClaimTypes.Role)
            ?? Context.User?.FindFirstValue("Role"),
            out var r) ? r : (int)Roles.User;

    private bool IsAdmin => UserRole == (int)Roles.SupportAdmin;

    private bool IsWorkerOrAdmin =>
        UserRole == (int)Roles.SupportAdmin ||
        UserRole == (int)Roles.SupportWorker;

    // ─── Qoşulma ─────────────────────────────────────────────
    public override async Task OnConnectedAsync()
    {
        if (IsWorkerOrAdmin)
            await Groups.AddToGroupAsync(Context.ConnectionId, "support-staff");

        if (IsAdmin)
            await Groups.AddToGroupAsync(Context.ConnectionId, "admin-panel");

        await base.OnConnectedAsync();
    }

    // ─── Ayrılma ─────────────────────────────────────────────
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "support-staff");
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "admin-panel");
        await base.OnDisconnectedAsync(exception);
    }

    // ─── Ticket otağına qoşul ─────────────────────────────────
    // Client çağırır: await connection.invoke("JoinTicket", ticketId)
    public async Task JoinTicket(int ticketId)
    {
        // Worker/admin hamısına icazə
        // Adi user yalnız öz ticketinə qoşula bilər
        if (!IsWorkerOrAdmin)
        {
            var ticket = await _ticketService.GetByIdForUserAsync(ticketId, UserId);
            if (ticket == null)
            {
                await Clients.Caller.SendAsync("Error", "Bu ticketə giriş icazəniz yoxdur");
                return;
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        await Clients.Caller.SendAsync("JoinedTicket", ticketId);
    }

    public async Task SendMessage(int ticketId, string content, bool isInternal = false)
    {
        // Adi user internal mesaj göndərə bilməz
        if (isInternal && !IsWorkerOrAdmin)
        {
            await Clients.Caller.SendAsync("Error", "İcazəniz yoxdur");
            return;
        }

        // Adi user yalnız öz ticketinə mesaj yaza bilər
        if (!IsWorkerOrAdmin)
        {
            var ticket = await _ticketService.GetByIdForUserAsync(ticketId, UserId);
            if (ticket == null)
            {
                await Clients.Caller.SendAsync("Error", "Bu ticketə giriş icazəniz yoxdur");
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            await Clients.Caller.SendAsync("Error", "Mesaj boş ola bilməz");
            return;
        }

        try
        {
            var messageDto = await _ticketService.AddMessageAsync(
                  ticketId, UserId, Username, content, isInternal, IsAdmin, IsWorkerOrAdmin);

            var payload = new
            {
                ticketId,
                messageId = messageDto.Id,
                senderId = messageDto.SenderId,
                senderName = messageDto.SenderName,
                content = messageDto.Content,
                isInternal = messageDto.IsInternal,
                sentAt = messageDto.SentAt
            };

            if (!isInternal)
                await Clients.Group($"ticket-{ticketId}").SendAsync("ReceiveMessage", payload);

            if (isInternal)
                await Clients.Group("admin-panel").SendAsync("ReceiveInternalMessage", payload);

            if (!isInternal)
                await Clients.Group("admin-panel").SendAsync("TicketActivity", new
                {
                    ticketId,
                    senderName = Username,
                    preview = content.Length > 50 ? content[..50] + "..." : content,
                    sentAt = messageDto.SentAt
                });
        }
        catch (UnauthorizedAccessException ex) { await Clients.Caller.SendAsync("Error", ex.Message); }
        catch (Exception ex) { await Clients.Caller.SendAsync("Error", $"Xəta: {ex.Message}"); }
    }
    // ─── Ticket otağından çıx ────────────────────────────────
    // Client çağırır: await connection.invoke("LeaveTicket", ticketId)
    public async Task LeaveTicket(int ticketId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, $"ticket-{ticketId}");
        await Clients.Caller.SendAsync("LeftTicket", ticketId);
    }
    // ─── Admin panelə qoşul (əl ilə çağırış) ─────────────────
    // Client çağırır: await connection.invoke("JoinAdminPanel")
    public async Task JoinAdminPanel()
    {
        if (!IsAdmin)
        {
            await Clients.Caller.SendAsync("Error", "Yalnız Admin panelə qoşula bilər");
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, "admin-panel");
        await Clients.Caller.SendAsync("JoinedAdminPanel", "Admin panelinə qoşuldunuz");
    }
}

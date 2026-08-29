using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BlogApp.Api.Hubs
{
    [Authorize] // JWT ilə qorunur
    public class AdminChatHub : Hub
    {
        private readonly BlogAppDbContext _db;

        // ConnectionId -> UserInfo mapping
        private static Dictionary<string, UserConnection> _userConnections = new();

        public AdminChatHub(BlogAppDbContext db)
        {
            _db = db;
        }

        // Connection qurulduqda avtomatik çağırılır
        public override async Task OnConnectedAsync()
        {
            string userName = Context.User?.Identity?.Name ?? "Unknown";
            string userId = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "0";
            string role = Context.User?.FindFirst(ClaimTypes.Role)?.Value ?? "0";
            bool isAdmin = role == "1" || role.Trim() == "admin";

            // User connection məlumatını saxla
            _userConnections[Context.ConnectionId] = new UserConnection
            {
                ConnectionId = Context.ConnectionId,
                UserName = userName,
                UserId = userId,
                IsAdmin = isAdmin,
                ConnectedAt = DateTime.UtcNow
            };

            Console.WriteLine($"User connected: {userName} (Admin: {isAdmin})");

            if (isAdmin)
            {
                // Admin üçün bütün online userləri göndər
                await SendOnlineUsersToAdmin();
            }
            else
            {
                // User üçün chat tarixini yüklə (Admin ilə olan mesajlar)
                await LoadUserChatHistory(userName);

                // Admin-ə bildir ki, yeni user online oldu
                await NotifyAdminUserOnline(userName);
            }

            await base.OnConnectedAsync();
        }

        // User chat tarixini yüklə
        private async Task LoadUserChatHistory(string userName)
        {
            try
            {
                var messages = await _db.ChatMessages
                    .Where(m => m.Sender == userName || m.Receiver == userName)
                    .OrderBy(m => m.Timestamp)
                    .Select(m => new
                    {
                        sender = m.Sender,
                        text = m.Content,
                        imageUrl = m.ImageUrl,
                        isAdmin = m.Sender == "Admin" || _db.Users.Any(u => u.UserName == m.Sender && u.Role == 1),
                        timestamp = m.Timestamp
                    })
                    .ToListAsync();

                await Clients.Caller.SendAsync("LoadChatHistory", messages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading chat history: {ex.Message}");
            }
        }

        // Admin üçün online userləri göndər
        private async Task SendOnlineUsersToAdmin()
        {
            var adminConnections = _userConnections.Values
                .Where(u => u.IsAdmin)
                .Select(u => u.ConnectionId)
                .ToList();

            if (!adminConnections.Any())
                return;

            // Admin özündən başqa bütün online userləri görür
            var onlineUsers = _userConnections.Values
                .Where(u => !u.IsAdmin)
                .Select(u => new
                {
                    username = u.UserName,
                    userId = u.UserId,
                    connectedAt = u.ConnectedAt,
                    unreadCount = GetUnreadCount(u.UserName)
                })
                .GroupBy(u => u.username)
                .Select(g => g.First()) // Duplicate userləri filtrələ
                .ToList();

            foreach (var adminConnId in adminConnections)
            {
                await Clients.Client(adminConnId).SendAsync("OnlineUsers", onlineUsers);
            }
        }

        // Typing bildirişi
        public async Task NotifyTyping(string targetUserName, bool isTyping)
        {
            string sender = Context.User?.Identity?.Name ?? "Unknown";

            try
            {
                // Receiver-ə typing bildirişini göndər
                var receiverConnections = _userConnections.Values
                    .Where(u => u.UserName == targetUserName)
                    .Select(u => u.ConnectionId)
                    .ToList();

                foreach (var connId in receiverConnections)
                {
                    if (isTyping)
                    {
                        await Clients.Client(connId).SendAsync("UserTyping", sender);
                    }
                    else
                    {
                        await Clients.Client(connId).SendAsync("UserStoppedTyping", sender);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Typing notification error: {ex.Message}");
            }
        }
        // Admin-ə user online olduğunu bildir
        private async Task NotifyAdminUserOnline(string userName)
        {
            var adminConnections = _userConnections.Values
                .Where(u => u.IsAdmin)
                .Select(u => u.ConnectionId)
                .ToList();

            foreach (var adminConnId in adminConnections)
            {
                await Clients.Client(adminConnId).SendAsync("UserOnline", userName);
            }

            // Admin listini yenilə
            await SendOnlineUsersToAdmin();
        }

        // Oxunmamış mesaj sayı
        private int GetUnreadCount(string userName)
        {
            try
            {
                return _db.ChatMessages
                    .Count(m => m.Receiver == userName && !m.IsRead);
            }
            catch
            {
                return 0;
            }
        }
        public async Task NotifyStoppedTyping(string toUser)
        {
            await Clients.User(toUser).SendAsync("UserStoppedTyping", Context.User.Identity.Name);
        }

        // Admin üçün spesifik user ilə chat tarixini yüklə
        public async Task LoadChatHistory(string targetUserName)
        {
            string currentUser = Context.User?.Identity?.Name ?? "Unknown";
            string role = Context.User?.FindFirst(ClaimTypes.Role)?.Value ?? "0";
            bool isAdmin = role == "1" || role.ToLower() == "admin";

            try
            {
                List<object> messages;

                if (isAdmin)
                {
                    // Admin spesifik user ilə mesajları görür
                    messages = await _db.ChatMessages
                        .Where(m => (m.Sender == targetUserName && m.Receiver == "Admin") ||
                                   (m.Sender == "Admin" && m.Receiver == targetUserName))
                        .OrderBy(m => m.Timestamp)
                        .Select(m => new
                        {
                            sender = m.Sender,
                            text = m.Content,
                            imageUrl = m.ImageUrl,
                            isAdmin = m.Sender == "Admin",
                            timestamp = m.Timestamp
                        })
                        .ToListAsync<object>();

                    // Oxundu işarət et
                    var unreadMessages = await _db.ChatMessages
                        .Where(m => m.Sender == targetUserName && m.Receiver == "Admin" && !m.IsRead)
                        .ToListAsync();

                    foreach (var msg in unreadMessages)
                    {
                        msg.IsRead = true;
                    }

                    if (unreadMessages.Any())
                    {
                        await _db.SaveChangesAsync();
                        await SendOnlineUsersToAdmin(); // Unread count yenilə
                    }
                }
                else
                {
                    // User yalnız Admin ilə mesajları görür
                    messages = await _db.ChatMessages
                        .Where(m => m.Sender == currentUser || m.Receiver == currentUser)
                        .OrderBy(m => m.Timestamp)
                        .Select(m => new
                        {
                            sender = m.Sender,
                            text = m.Content,
                            imageUrl = m.ImageUrl,
                            isAdmin = m.Sender == "Admin",
                            timestamp = m.Timestamp
                        })
                        .ToListAsync<object>();
                }

                await Clients.Caller.SendAsync("LoadChatHistory", messages);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading chat history: {ex.Message}");
                await Clients.Caller.SendAsync("LoadChatHistory", new List<object>());
            }
        }

        // Mesaj göndərmə (mətn və şəkil dəstəyi)
        public async Task SendMessage(string receiverUserName, string message, string imageUrl = null)
        {
            string sender = Context.User?.Identity?.Name ?? "Unknown";
            string role = Context.User?.FindFirst(ClaimTypes.Role)?.Value ?? "0";
            bool isAdmin = role == "1" || role.ToLower() == "admin";

            try
            {
                // Boş mesaj yoxla
                if (string.IsNullOrWhiteSpace(message) && string.IsNullOrWhiteSpace(imageUrl))
                {
                    return;
                }

                // DB-yə save et
                var chatMsg = new ChatMessage
                {
                    Sender = sender,
                    Receiver = receiverUserName,
                    Content = message ?? "",
                    ImageUrl = imageUrl,
                    Type = !string.IsNullOrWhiteSpace(imageUrl) ? "image" : "text",
                    Timestamp = DateTime.UtcNow,
                    IsRead = false
                };

                _db.ChatMessages.Add(chatMsg);
                await _db.SaveChangesAsync();

                Console.WriteLine($"Message saved: {sender} -> {receiverUserName}");

                // Receiver-ə mesajı göndər
                var receiverConnections = _userConnections.Values
                    .Where(u => u.UserName == receiverUserName)
                    .Select(u => u.ConnectionId)
                    .ToList();

                foreach (var connId in receiverConnections)
                {
                    await Clients.Client(connId).SendAsync(
                        "ReceiveMessage",
                        sender,
                        message,
                        imageUrl,
                        isAdmin
                    );
                }

                // Admin-dirsə online user listini yenilə
                if (isAdmin)
                {
                    await SendOnlineUsersToAdmin();
                }
                else
                {
                    // User mesaj göndərdikdə Admin-ə bildir
                    await NotifyAdminNewMessage(sender);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending message: {ex.Message}");
                await Clients.Caller.SendAsync("MessageError", "Mesaj göndərilmədi");
            }
        }

        // Admin-ə yeni mesaj bildirişi
        private async Task NotifyAdminNewMessage(string fromUser)
        {
            var adminConnections = _userConnections.Values
                .Where(u => u.IsAdmin)
                .Select(u => u.ConnectionId)
                .ToList();

            foreach (var adminConnId in adminConnections)
            {
                await Clients.Client(adminConnId).SendAsync("NewMessageNotification", fromUser);
            }

            await SendOnlineUsersToAdmin();
        }

        // Online userləri əldə et (manual çağırış)
        public async Task GetOnlineUsers()
        {
            string role = Context.User?.FindFirst(ClaimTypes.Role)?.Value ?? "0";
            bool isAdmin = role == "1" || role.ToLower() == "admin";

            if (isAdmin)
            {
                await SendOnlineUsersToAdmin();
            }
            else
            {
                // User yalnız Admin-i görür
                await Clients.Caller.SendAsync("OnlineUsers", new List<object>
                {
                    new { username = "Admin", userId = "admin", unreadCount = 0 }
                });
            }
        }

        // Disconnect
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            if (_userConnections.TryGetValue(Context.ConnectionId, out var userConn))
            {
                Console.WriteLine($"User disconnected: {userConn.UserName}");

                _userConnections.Remove(Context.ConnectionId);

                // Admin-ə bildir ki, user offline oldu
                if (!userConn.IsAdmin)
                {
                    await NotifyAdminUserOffline(userConn.UserName);
                }

                // Admin online list yenilə
                await SendOnlineUsersToAdmin();
            }

            await base.OnDisconnectedAsync(exception);
        }

        // Admin-ə user offline bildirişi
        private async Task NotifyAdminUserOffline(string userName)
        {
            var adminConnections = _userConnections.Values
                .Where(u => u.IsAdmin)
                .Select(u => u.ConnectionId)
                .ToList();

            foreach (var adminConnId in adminConnections)
            {
                await Clients.Client(adminConnId).SendAsync("UserOffline", userName);
            }
        }

        // Helper class
        private class UserConnection
        {
            public string ConnectionId { get; set; }
            public string UserName { get; set; }
            public string UserId { get; set; }
            public bool IsAdmin { get; set; }
            public DateTime ConnectedAt { get; set; }
        }
    }
}
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace BlogApp.Api.Hubs
{
    public class BackgammonHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly BackgammonRoomManager _roomManager;
        private readonly IRankService _rankService;

        public BackgammonHub(BlogAppDbContext db, BackgammonRoomManager roomManager, IRankService rankService)
        {
            _db = db;
            _roomManager = roomManager;
            _rankService = rankService;
        }

        private static readonly ConcurrentDictionary<string, string> _userRooms = new();
        private static readonly ConcurrentDictionary<int, DateTime> _lastMessageTime = new();
        private static readonly TimeSpan _messageCooldown = TimeSpan.FromSeconds(1);
        private const int TURN_TIMEOUT_SECONDS = 26;

        public override async Task OnConnectedAsync()
        {
            Console.WriteLine($"🎲 Backgammon Connection: {Context.ConnectionId}");

            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Context.Abort();
                return;
            }

            var userId = GetUserId();
            if (userId == 0)
            {
                Context.Abort();
                return;
            }

            try
            {
                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance, u.Image })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName = user.UserName,
                    balance = user.Balance,
                    profileImage = user.Image
                });

                Console.WriteLine($"✅ Backgammon Connected: {fullName}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Context.Abort();
            }

            await base.OnConnectedAsync();
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var connId = Context.ConnectionId;

            if (_userRooms.TryRemove(connId, out var roomId))
            {
                var room = _roomManager.GetRoom(roomId);
                if (room != null)
                {
                    var player = room.Players.FirstOrDefault(p => p.ConnectionId == connId);

                    if (player != null)
                    {
                        // ✅ Oyun başlamamışsa refund et
                        if (!room.IsGameStarted && room.Players.Count > 0)
                        {
                            var user = await _db.Users.FindAsync(player.UserId);
                            if (user != null)
                            {
                                user.Balance += room.BetAmount;
                                await _db.SaveChangesAsync();
                            }
                        }
                        // ✅ Oyun başlamışsa winner declare et
                        else if (room.IsGameStarted && !room.IsGameFinished)
                        {
                            var opponent = room.Players.FirstOrDefault(p => p.UserId != player.UserId);
                            if (opponent != null)
                            {
                                await DeclareOpponentWinner(room, opponent, player);
                            }
                        }

                        room.Players.Remove(player);
                        await Clients.Group(roomId).SendAsync("PlayerLeft", player.Name);
                    }

                    if (room.Players.Count == 0)
                    {
                        _roomManager.DeleteRoom(roomId);
                    }
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task<object> QuickMatch(decimal betAmount = 100)
        {
            var userId = GetUserId();
            if (userId == 0)
                return new { success = false, message = "İstifadəçi tapılmadı" };

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
                return new { success = false, message = "İstifadəçi tapılmadı" };

            if (user.Balance < betAmount)
                return new { success = false, message = $"Kifayət qədər balans yoxdur. Lazım: {betAmount}" };

            if (_userRooms.ContainsKey(Context.ConnectionId))
                return new { success = false, message = "Artıq oyundasınız" };

            var availableRoom = _roomManager.GetAvailableRoomForBet(betAmount);

            if (availableRoom == null)
            {
                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                availableRoom = _roomManager.CreateAutoRoom(fullName, userId, betAmount);
                Console.WriteLine($"🆕 Auto room created: {availableRoom.RoomId} for {betAmount} coins");
            }

            await JoinRoomInternal(availableRoom.RoomId, user);

            return new { success = true, roomId = availableRoom.RoomId };
        }

        public async Task<List<object>> GetAvailableRooms()
        {
            var rooms = _roomManager.GetAllRooms();
            return rooms.Select(r => new
            {
                roomId = r.RoomId,
                roomName = r.RoomName,
                betAmount = r.BetAmount,
                playerCount = r.Players.Count,
                maxPlayers = 2,
                isAvailable = !r.IsGameStarted && r.Players.Count < 2
            }).Cast<object>().ToList();
        }

        private async Task JoinRoomInternal(string roomId, User user)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinError", "Otaq tapılmadı");
                return;
            }

            if (room.Players.Count >= 2)
            {
                await Clients.Caller.SendAsync("JoinError", "Otaq doludur");
                return;
            }

            if (user.Balance < room.BetAmount)
            {
                await Clients.Caller.SendAsync("JoinError", $"Kifayət qədər balans yoxdur. Lazım: {room.BetAmount}");
                return;
            }

            string fullName = $"{user.Name} {user.Surname}".Trim();
            if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

            var player = new BackgammonPlayer
            {
                ConnectionId = Context.ConnectionId,
                UserId = user.Id,
                UserName = user.UserName,
                Name = fullName,
                Balance = user.Balance,
                Color = room.Players.Count == 0 ? "white" : "black"
            };

            room.Players.Add(player);

            user.Balance -= room.BetAmount;
            await _db.SaveChangesAsync();

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            _userRooms[Context.ConnectionId] = roomId;

            // ✅ PROFILE IMAGE ƏLAVƏ
            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId,
                roomName = room.RoomName,
                profileImage = user.Image,
                betAmount = room.BetAmount,
                balance = user.Balance,
                color = player.Color,
                waitingForOpponent = room.Players.Count < 2
            });

            // ✅ İkinci oyunçu qoşulanda rəqib məlumatını göndər
            if (room.Players.Count == 2)
            {
                var firstPlayer = room.Players[0];
                var firstUser = await _db.Users.FindAsync(firstPlayer.UserId);

                await Clients.Client(player.ConnectionId).SendAsync("OpponentInfo", new
                {
                    name = firstPlayer.UserName,
                    color = firstPlayer.Color,
                    profileImage = firstUser?.Image  // ✅ ƏLAVƏ
                });

                Console.WriteLine($"📤 Sent opponent info to {fullName}: {firstPlayer.Name} ({firstPlayer.Color})");
            }

            // ✅ PlayerJoined event-ə profile image əlavə
            await Clients.Group(roomId).SendAsync("PlayerJoined", new
            {
                name = user.UserName,
                color = player.Color,
                profileImage = user.Image  // ✅ ƏLAVƏ
            });

            Console.WriteLine($"✅ {fullName} joined room ({room.Players.Count}/2)");

            if (room.Players.Count == 2)
            {
                await Task.Delay(1500);
                await StartGameWithDiceRoll(roomId);
            }
        }

        public async Task SendChatMessage(string roomId, string message)
        {
            Console.WriteLine($"📨 SendChatMessage called - RoomId: {roomId}, Message: {message}");

            if (string.IsNullOrWhiteSpace(message))
            {
                Console.WriteLine($"❌ Empty message rejected");
                await Clients.Caller.SendAsync("Error", "Mesaj boş ola bilməz!");
                return;
            }

            if (message.Length > 200)
            {
                Console.WriteLine($"❌ Message too long: {message.Length} chars");
                await Clients.Caller.SendAsync("Error", "Mesaj çox uzundur!");
                return;
            }

            var userId = GetUserId();
            Console.WriteLine($"👤 UserId: {userId}");

            if (_lastMessageTime.TryGetValue(userId, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime) < _messageCooldown)
                {
                    Console.WriteLine($"⏰ Spam detected from user {userId}");
                    await Clients.Caller.SendAsync("Error", "Çox tez göndərirsiniz!");
                    return;
                }
            }

            if (!_userRooms.TryGetValue(Context.ConnectionId, out var userRoomId) || userRoomId != roomId)
            {
                Console.WriteLine($"❌ User not in room. ConnectionId: {Context.ConnectionId}, UserRoomId: {userRoomId}, RequestedRoomId: {roomId}");
                await Clients.Caller.SendAsync("Error", "Bu otaqda deyilsiniz!");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"❌ Room not found: {roomId}");
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null)
            {
                Console.WriteLine($"❌ Player not found in room. UserId: {userId}");
                await Clients.Caller.SendAsync("Error", "Oyunçu tapılmadı!");
                return;
            }

            _lastMessageTime[userId] = DateTime.UtcNow;
            var sanitizedMessage = System.Net.WebUtility.HtmlEncode(message);

            Console.WriteLine($"✅ Sending ChatMessage to group {roomId}: {player.Name} - {sanitizedMessage}");

            await Clients.Group(roomId).SendAsync("ChatMessage", new
            {
                sender = player.Name,
                message = sanitizedMessage,
                timestamp = DateTime.UtcNow
            });

            Console.WriteLine($"✅ ChatMessage sent successfully");
        }

        public async Task SendQuickEmoji(string roomId, string emoji)
        {
            Console.WriteLine($"📨 SendQuickEmoji called - RoomId: {roomId}, Emoji: {emoji}");

            if (string.IsNullOrWhiteSpace(emoji))
            {
                Console.WriteLine($"❌ Empty emoji rejected");
                await Clients.Caller.SendAsync("Error", "Emoji seçin!");
                return;
            }

            var userId = GetUserId();
            Console.WriteLine($"👤 UserId: {userId}");

            // ✅ Spam kontrol
            if (_lastMessageTime.TryGetValue(userId, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime) < _messageCooldown)
                {
                    Console.WriteLine($"⏰ Spam detected from user {userId}");
                    await Clients.Caller.SendAsync("Error", "Çox tez göndərirsiniz!");
                    return;
                }
            }

            // ✅ Otaq kontrol
            if (!_userRooms.TryGetValue(Context.ConnectionId, out var userRoomId) || userRoomId != roomId)
            {
                Console.WriteLine($"❌ User not in room. ConnectionId: {Context.ConnectionId}, UserRoomId: {userRoomId}, RequestedRoomId: {roomId}");
                await Clients.Caller.SendAsync("Error", "Bu otaqda deyilsiniz!");
                return;
            }

            // ✅ Otaq mövcud mu?
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"❌ Room not found: {roomId}");
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            // ✅ Oyunçu mövcud mu?
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null)
            {
                Console.WriteLine($"❌ Player not found in room. UserId: {userId}");
                await Clients.Caller.SendAsync("Error", "Oyunçu tapılmadı!");
                return;
            }

            // ✅ Spam kontrolu güncəllə
            _lastMessageTime[userId] = DateTime.UtcNow;

            Console.WriteLine($"📤 Sending QuickEmoji from {player.Name} ({player.UserName}): {emoji}");

            // ✅ USERNAME göndər (fullName deyil)
            await Clients.Group(roomId).SendAsync("QuickEmoji", new
            {
                sender = player.UserName,
                emoji = emoji,
                timestamp = DateTime.UtcNow
            });
            Console.WriteLine($"✅ QuickEmoji sent successfully");
        }
        public async Task SendQuickMessage(string roomId, string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                await Clients.Caller.SendAsync("Error", "Mesaj boş ola bilməz!");
                return;
            }

            var userId = GetUserId();

            if (_lastMessageTime.TryGetValue(userId, out var lastTime))
            {
                if ((DateTime.UtcNow - lastTime) < _messageCooldown)
                {
                    await Clients.Caller.SendAsync("Error", "Çox tez göndərirsiniz!");
                    return;
                }
            }

            if (!_userRooms.TryGetValue(Context.ConnectionId, out var userRoomId) || userRoomId != roomId)
            {
                await Clients.Caller.SendAsync("Error", "Bu otaqda deyilsiniz!");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            var player = room?.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null) return;

            _lastMessageTime[userId] = DateTime.UtcNow;
            var sanitizedMessage = System.Net.WebUtility.HtmlEncode(message);

            await Clients.Group(roomId).SendAsync("QuickMessage", new
            {
                sender = player.Name,
                message = sanitizedMessage,
                timestamp = DateTime.UtcNow
            });

            Console.WriteLine($"💬 {player.Name} sent quick message: {sanitizedMessage}");
        }

        public async Task LeaveRoom()
        {
            var connId = Context.ConnectionId;
            if (!_userRooms.TryGetValue(connId, out var roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room != null && room.IsGameStarted && !room.IsGameFinished)
            {
                await Clients.Caller.SendAsync("Error", "Oyun davam edərkən çıxa bilməzsiniz!");
                return;
            }

            var userId = GetUserId();
            var player = room?.Players.FirstOrDefault(p => p.UserId == userId);

            if (player != null && room != null)
            {
                // ✅ Oyun başlamamışsa refund et
                if (!room.IsGameStarted)
                {
                    var user = await _db.Users.FindAsync(userId);
                    if (user != null)
                    {
                        user.Balance += room.BetAmount;
                        await _db.SaveChangesAsync();
                        Console.WriteLine($"💰 Refund: {user.UserName} +{room.BetAmount} AZN");
                    }
                }

                room.Players.Remove(player);
                await Clients.Caller.SendAsync("LeftRoom");
            }

            await Groups.RemoveFromGroupAsync(connId, roomId);
            _userRooms.TryRemove(connId, out _);
        }

        private async Task StartGameWithDiceRoll(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null || room.IsGameStarted || room.Players.Count != 2) return;

            var random = new Random();
            int dice1 = random.Next(1, 7);
            int dice2 = random.Next(1, 7);

            while (dice1 == dice2)
            {
                dice1 = random.Next(1, 7);
                dice2 = random.Next(1, 7);
            }

            var startingPlayer = dice1 > dice2 ? room.Players[0] : room.Players[1];
            room.CurrentPlayerIndex = room.Players.IndexOf(startingPlayer);
            room.IsGameStarted = true;

            BackgammonBoardSetup.InitializeBoard(room);

            Console.WriteLine($"🎲 Game starting: {room.Players[0].Name}({dice1}) vs {room.Players[1].Name}({dice2})");
            Console.WriteLine($"🏁 {startingPlayer.Name} başlayır!");

            await Clients.Group(roomId).SendAsync("GameStarting", new
            {
                player1 = new { name = room.Players[0].Name, dice = dice1 },
                player2 = new { name = room.Players[1].Name, dice = dice2 },
                starter = startingPlayer.Name,
                message = $"{room.Players[0].Name} atdı {dice1}, {room.Players[1].Name} atdı {dice2}. {startingPlayer.Name} başlayır!"
            });

            await Task.Delay(3000);

            // ✅ GameStarted event-ə profile image ƏLAVƏ
            foreach (var player in room.Players)
            {
                var isMyTurn = player.UserId == startingPlayer.UserId;
                var playerUser = await _db.Users.FindAsync(player.UserId);

                await Clients.Client(player.ConnectionId).SendAsync("GameStarted", new
                {
                    board = room.GetBoardForJson(),
                    currentPlayer = startingPlayer.Name,
                    currentPlayerUserName = startingPlayer.UserName,
                    turnTimeoutSeconds = TURN_TIMEOUT_SECONDS,
                    isMyTurn,
                    myColor = player.Color,
                    players = await Task.WhenAll(room.Players.Select(async p =>
                    {
                        var u = await _db.Users.FindAsync(p.UserId);
                        return new
                        {
                            name = p.UserName,  // ✅ USERNAME (ad-soyad deyil)
                            color = p.Color,
                            profileImage = u?.Image
                        };
                    })),
                    message = $"Oyun başladı! {startingPlayer.Name} başlayır."
                });
            }
        }

        public async Task RollDice()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null || room.Players[room.CurrentPlayerIndex].UserId != userId)
            {
                await Clients.Caller.SendAsync("Error", "Sizin növbəniz deyil!");
                return;
            }

            if (room.RemainingMoves.Count > 0)
            {
                await Clients.Caller.SendAsync("Error", "Bu növbədə zər artıq atılıb!");
                return;
            }

            var random = new Random();
            var dice1 = random.Next(1, 7);
            var dice2 = random.Next(1, 7);

            room.Dice = dice1 == dice2
                ? new List<int> { dice1, dice2, dice1, dice2 }
                : new List<int> { dice1, dice2 };

            room.RemainingMoves = new List<int>(room.Dice);

            Console.WriteLine($"🎲 {player.Name} rolled: {dice1}-{dice2}");

            await Clients.Group(roomId).SendAsync("DiceRolled", new
            {
                dice = room.Dice,
                remainingMoves = room.RemainingMoves,
                player = player.Name
            });

            if (!BackgammonRules.HasLegalMove(room, player.Color))
            {
                Console.WriteLine($"⏳ {player.Name} üçün legal gediş yoxdur. Dice 3 saniyə görünəcək, sonra növbə keçəcək.");
                var rolledMoves = room.RemainingMoves.ToList();
                await Task.Delay(3000);

                if (room.IsGameFinished ||
                    room.Players.Count <= room.CurrentPlayerIndex ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId ||
                    !room.RemainingMoves.SequenceEqual(rolledMoves) ||
                    BackgammonRules.HasLegalMove(room, player.Color))
                {
                    return;
                }

                await AdvanceTurn(room, roomId, $"{player.Name} üçün mümkün gediş yoxdur. Növbə keçir.");
            }
        }

        public async Task MovePiece(int fromPoint, int toPoint)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            var player = room.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null || room.Players[room.CurrentPlayerIndex].UserId != userId)
            {
                await Clients.Caller.SendAsync("Error", "Sizin növbəniz deyil!");
                return;
            }

            var isValid = BackgammonRules.IsValidMove(room, fromPoint, toPoint, player.Color);
            if (!isValid)
            {
                await Clients.Caller.SendAsync("Error", "Yanlış hərəkət!");
                return;
            }

            BackgammonRules.ExecuteMove(room, fromPoint, toPoint, player.Color);

            string moveDesc = fromPoint == 0
                ? $"BAR → {toPoint}"
                : (toPoint < 1 || toPoint > 24)
                    ? $"{fromPoint} → HOME"
                    : $"{fromPoint} → {toPoint}";

            Console.WriteLine($"♟️ {player.Name} moved: {moveDesc}");
            Console.WriteLine($"📊 BAR after move - White: {room.Bar["white"]}, Black: {room.Bar["black"]}");
            Console.WriteLine($"🏠 HOME after move - White: {room.Home["white"]}, Black: {room.Home["black"]}");

            var boardData = room.GetBoardForJson();

            await Clients.Group(roomId).SendAsync("PieceMoved", new
            {
                fromPoint,
                toPoint,
                board = boardData,
                remainingMoves = room.RemainingMoves,
                player = player.Name
            });

            if (BackgammonRules.CheckWin(room, player.Color))
            {
                Console.WriteLine($"🏆 WIN DETECTED! {player.Name} has {room.Home[player.Color]} pieces at HOME");
                await DeclareWinner(room, player);
                return;
            }

            if (room.RemainingMoves.Count == 0)
            {
                await AdvanceTurn(room, roomId);
                return;
            }

            if (!BackgammonRules.HasLegalMove(room, player.Color))
            {
                Console.WriteLine($"⏭️ {player.Name} üçün qalan zərlərlə legal gediş yoxdur. Növbə avtomatik keçir.");
                await AdvanceTurn(room, roomId, $"{player.Name} üçün qalan zərlərlə mümkün gediş yoxdur. Növbə keçir.");
            }
        }

        public async Task EndTurn()
        {
            var roomId = GetCurrentRoom();
            Console.WriteLine($"🔄 EndTurn called - RoomId: {roomId}");

            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine("❌ EndTurn: No room found");
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine("❌ EndTurn: Room is null");
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            var userId = GetUserId();
            Console.WriteLine($"👤 UserId: {userId}");

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);

            if (player == null)
            {
                Console.WriteLine($"❌ EndTurn: Player null for userId {userId}");
                await Clients.Caller.SendAsync("Error", "Oyunçu tapılmadı!");
                return;
            }

            Console.WriteLine($"📊 Current player index: {room.CurrentPlayerIndex}");
            Console.WriteLine($"📊 Current player UserId: {room.Players[room.CurrentPlayerIndex].UserId}");
            Console.WriteLine($"📊 My UserId: {userId}");

            if (room.Players[room.CurrentPlayerIndex].UserId != userId)
            {
                Console.WriteLine($"❌ EndTurn: Not your turn! Current: {room.Players[room.CurrentPlayerIndex].Name}, Your: {player.Name}");
                await Clients.Caller.SendAsync("Error", "Sizin növbəniz deyil!");
                return;
            }

            if (room.Dice.Count == 0 && room.RemainingMoves.Count == 0)
            {
                Console.WriteLine($"❌ EndTurn: Dice not rolled yet for {player.Name}");
                await Clients.Caller.SendAsync("Error", "Əvvəlcə zər atmalısınız!");
                return;
            }

            Console.WriteLine($"✅ Clearing remaining moves: {string.Join(",", room.RemainingMoves)}");

            // ✅ Zərlər və hərəkətləri sıfırla
            room.RemainingMoves.Clear();
            room.Dice.Clear();

            // ✅ Növbəni keç
            int oldIndex = room.CurrentPlayerIndex;
            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % 2;
            var nextPlayer = room.Players[room.CurrentPlayerIndex];

            Console.WriteLine($"🔄 Turn changed: {player.Name} (Index {oldIndex}) → {nextPlayer.Name} (Index {room.CurrentPlayerIndex})");
            Console.WriteLine($"📤 Sending TurnChanged event with player: {nextPlayer.UserName}");

            // ✅ TurnChanged event-ə USERNAME göndər
            await Clients.Group(roomId).SendAsync("TurnChanged", new
            {
                currentPlayer = nextPlayer.UserName,  // ✅ USERNAME
                currentPlayerFullName = nextPlayer.Name,
                turnTimeoutSeconds = TURN_TIMEOUT_SECONDS
            });

            Console.WriteLine($"✅ EndTurn completed");
        }

        private async Task AdvanceTurn(BackgammonRoom room, string roomId, string? message = null)
        {
            room.Dice.Clear();
            room.RemainingMoves.Clear();

            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % 2;
            var nextPlayer = room.Players[room.CurrentPlayerIndex];

            await Clients.Group(roomId).SendAsync("TurnChanged", new
            {
                currentPlayer = nextPlayer.UserName,
                currentPlayerFullName = nextPlayer.Name,
                turnTimeoutSeconds = TURN_TIMEOUT_SECONDS,
                message = message ?? $"{nextPlayer.Name} zər atmağa başlayın!"
            });
        }

        public async Task ResolveTurnTimeout()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("Error", "Otaq tapılmadı!");
                return;
            }

            if (!room.IsGameStarted || room.IsGameFinished || room.Players.Count < 2)
            {
                return;
            }

            if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                return;
            }

            var userId = GetUserId();
            var timedOutPlayer = room.Players[room.CurrentPlayerIndex];
            if (timedOutPlayer.UserId != userId)
            {
                await Clients.Caller.SendAsync("Error", "Timeout yalnız növbəsi olan oyunçu üçün tətbiq oluna bilər!");
                return;
            }

            var winner = room.Players.FirstOrDefault(p => p.UserId != timedOutPlayer.UserId);
            if (winner == null)
            {
                return;
            }

            Console.WriteLine($"⏰ Backgammon turn timeout: {timedOutPlayer.Name} kicked, winner {winner.Name}");

            await DeclareOpponentWinner(room, winner, timedOutPlayer, "TIMEOUT");

            if (!string.IsNullOrWhiteSpace(timedOutPlayer.ConnectionId))
            {
                await Clients.Client(timedOutPlayer.ConnectionId).SendAsync("KickedForTimeout", new
                {
                    message = "Vaxtınız bitdi. Oyundan çıxarıldınız.",
                    reason = "TIMEOUT"
                });

                await Groups.RemoveFromGroupAsync(timedOutPlayer.ConnectionId, room.RoomId);
                _userRooms.TryRemove(timedOutPlayer.ConnectionId, out _);
            }
        }

        private async Task DeclareWinner(BackgammonRoom room, BackgammonPlayer winner)
        {
            room.IsGameFinished = true;
            room.IsGameStarted = false;
            decimal grossWinAmount = room.BetAmount * 2;
            decimal commissionAmount = grossWinAmount * 0.20m;
            decimal totalEarnings = grossWinAmount - commissionAmount;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
            if (user != null)
            {
                user.Balance += totalEarnings;

                await _db.SaveChangesAsync();

                try
                {
                    await _rankService.UpdateRankAfterGame(
                        userId: winner.UserId,
                        gameType: GameType.BackGammon,
                        isWin: true,
                        earnings: totalEarnings
                    );

                    Console.WriteLine($"✅ Rank updated for winner {winner.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Rank update error: {ex.Message}");
                }
            }

            var loser = room.Players.FirstOrDefault(p => p.UserId != winner.UserId);
            if (loser != null)
            {
                try
                {
                    await _rankService.UpdateRankAfterGame(
                        userId: loser.UserId,
                        gameType: GameType.BackGammon,
                        isWin: false,
                        earnings: -room.BetAmount
                    );

                    Console.WriteLine($"📊 Rank updated for loser {loser.Name}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Rank update error for loser: {ex.Message}");
                }
            }

            _roomManager.DeleteRoom(room.RoomId);

            await Clients.Group(room.RoomId).SendAsync("GameEnded", new
            {
                winner = winner.UserName,
                winnerFullName = winner.Name,
                winnerProfileImage = user?.Image,
                winAmount = totalEarnings,
                grossAmount = grossWinAmount,
                commissionAmount,
                message = $"🏆 {winner.UserName} qalib oldu! +{totalEarnings:0.##} coin"
            });

            Console.WriteLine($"🏆 {winner.Name} WON backgammon game");
        }

        private async Task DeclareOpponentWinner(BackgammonRoom room, BackgammonPlayer winner, BackgammonPlayer disconnected, string reason = "DISCONNECTED")
        {
            room.IsGameFinished = true;
            room.IsGameStarted = false;
            decimal grossWinAmount = room.BetAmount * 2;
            decimal commissionAmount = grossWinAmount * 0.20m;
            decimal totalEarnings = grossWinAmount - commissionAmount;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
            if (user != null)
            {
                user.Balance += totalEarnings;
                await _db.SaveChangesAsync();

                try
                {
                    await _rankService.UpdateRankAfterGame(
                        userId: winner.UserId,
                        gameType: GameType.BackGammon,
                        isWin: true,
                        earnings: totalEarnings
                    );

                    Console.WriteLine($"✅ Rank updated for winner {winner.Name} (opponent disconnected)");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Rank update error: {ex.Message}");
                }
            }

            try
            {
                await _rankService.UpdateRankAfterGame(
                    userId: disconnected.UserId,
                    gameType: GameType.BackGammon,
                    isWin: false,
                    earnings: -room.BetAmount
                );

                Console.WriteLine($"📊 Rank updated for disconnected player {disconnected.Name}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Rank update error for disconnected: {ex.Message}");
            }

            _roomManager.DeleteRoom(room.RoomId);

            await Clients.Group(room.RoomId).SendAsync("GameEnded", new
            {
                winner = winner.UserName,
                winnerFullName = winner.Name,
                winnerProfileImage = user?.Image,
                winAmount = totalEarnings,
                grossAmount = grossWinAmount,
                commissionAmount,
                reason,
                loser = disconnected.UserName,
                loserFullName = disconnected.Name,
                message = reason == "TIMEOUT"
                    ? $"🏆 {winner.UserName} qalib oldu! ({disconnected.UserName} vaxtı bitdiyi üçün oyundan çıxarıldı)"
                    : $"🏆 {winner.UserName} qalib oldu! ({disconnected.UserName} oyundan çıxdı)"
            });

            Console.WriteLine($"🏆 {winner.Name} WON by opponent {reason}");
        }

        private int GetUserId()
        {
            var userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            return int.TryParse(userIdStr, out int userId) ? userId : 0;
        }

        private string? GetCurrentRoom()
        {
            _userRooms.TryGetValue(Context.ConnectionId, out var roomId);
            return roomId;
        }
    }
}

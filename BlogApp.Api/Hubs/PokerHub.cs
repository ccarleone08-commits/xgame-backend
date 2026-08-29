using BlogApp.Api.Hubs.Services;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities.GamesEntitiy;
using BlogApp.DAL.DALs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Security.Claims;

namespace BlogApp.Api.Hubs
{
    public class PokerHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly PokerRoomManager _roomManager;
        private readonly IHubContext<PokerHub> _hubContext;
        public readonly IRankService _service;
        private const decimal COMMISSION_RATE = 0.03m;
        private const int HAND_PAUSE_TIMEOUT_SECONDS = 15;
        private const int HAND_PAUSE_FINALIZE_GRACE_MS = 4000;
        private const int REBUY_TIMEOUT_SECONDS = 20;

        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _turnTimers = new();
        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _roomStartTimers = new();
        private static readonly ConcurrentDictionary<string, byte> _handPauseActiveRooms = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _handPauseResponses = new();
        private static readonly ConcurrentDictionary<string, string> _handPauseSessionIds = new();


        public PokerHub(BlogAppDbContext db, PokerRoomManager roomManager, IHubContext<PokerHub> hubContext, IRankService service)
        {
            _db = db;
            _roomManager = roomManager;
            _hubContext = hubContext;
            _service = service;
        }

        private static readonly ConcurrentDictionary<string, string> _userRooms = new();

        public override async Task OnConnectedAsync()
        {
            if (Context.User?.Identity?.IsAuthenticated != true)
            {
                Console.WriteLine($"❌ Unauthorized connection attempt");
                Context.Abort();
                return;
            }

            string userIdStr = Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (string.IsNullOrEmpty(userIdStr) || !int.TryParse(userIdStr, out int userId))
            {
                Console.WriteLine($"❌ Invalid user ID");
                Context.Abort();
                return;
            }

            try
            {
                var user = _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance, u.Image })
                    .FirstOrDefault();

                if (user == null)
                {
                    Console.WriteLine($"⚠️ User not found: {userId}");
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                // ✅ Köhnə connection-ı tap və yenilə
                var oldConnId = _userRooms
                    .FirstOrDefault(kv =>
                    {
                        var r = _roomManager.GetRoom(kv.Value);
                        return r != null && r.Players.Any(p => p.UserId == userId);
                    }).Key;

                if (!string.IsNullOrEmpty(oldConnId) && oldConnId != Context.ConnectionId)
                {
                    Console.WriteLine($"♻️ {fullName} reconnecting - updating connection ID");

                    // Köhnə connection-ı _userRooms-dan sil
                    if (_userRooms.TryRemove(oldConnId, out var roomId))
                    {
                        // Yeni connection-ı əlavə et
                        _userRooms[Context.ConnectionId] = roomId;

                        // Otaqdakı player-in ConnectionId-sini yenilə
                        var room = _roomManager.GetRoom(roomId);
                        if (room != null)
                        {
                            lock (room.StateLock)
                            {
                                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                                if (player != null)
                                {
                                    player.ConnectionId = Context.ConnectionId;
                                    Console.WriteLine($"✅ {fullName} connection updated in room {room.RoomName}");
                                }
                            }

                            // Yeni connection-ı eyni group-a əlavə et
                            await Groups.RemoveFromGroupAsync(oldConnId, roomId);
                            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);

                            // Game state-i yenidən göndər
                            await BroadcastGameState(roomId);
                        }
                    }
                }

                await RestoreCurrentPokerStateForUser(userId);

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName,
                    balance = user.Balance,
                    profileImage = user.Image
                });

                Console.WriteLine($"✅ Poker Connected: {fullName} (Balance: {user.Balance})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnConnectedAsync error: {ex.Message}");
                Context.Abort();
            }

            await base.OnConnectedAsync();
        }

        public async Task RequestCurrentState()
        {
            var userId = GetUserId();
            if (userId == 0) return;

            await RestoreCurrentPokerStateForUser(userId);
        }

        private async Task RestoreCurrentPokerStateForUser(int userId)
        {
            var room = _roomManager.GetRoomByUser(userId);
            if (room == null) return;

            RoomPlayers? player;
            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                player.ConnectionId = Context.ConnectionId;
                _userRooms[Context.ConnectionId] = room.RoomId;
            }

            await Groups.AddToGroupAsync(Context.ConnectionId, room.RoomId);

            await Clients.Caller.SendAsync("RejoinedRoom", new
            {
                roomId = room.RoomId,
                roomName = room.RoomName,
                isGameActive = room.IsGameActive,
                message = "Poker otağına yenidən qoşuldunuz"
            });

            if (player.HoleCards.Count > 0)
            {
                await Clients.Caller.SendAsync("HoleCards", player.HoleCards.ToArray());
            }

            await BroadcastGameState(room.RoomId);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string connId = Context.ConnectionId;

            if (_userRooms.TryRemove(connId, out var roomId))
            {
                var room = _roomManager.GetRoom(roomId);
                if (room != null)
                {
                    RoomPlayers? player = null;
                    bool wasInActiveGame = false;
                    int playerIndexBeforeRemove = -1;
                    bool shouldDetermineWinner = false;
                    bool shouldAdvanceStreet = false;
                    bool gameEnded = false;
                    bool shouldKeepForPotAccounting = false;

                    lock (room.StateLock)
                    {
                        playerIndexBeforeRemove = room.Players.FindIndex(p => p.ConnectionId == connId);
                        player = room.Players.FirstOrDefault(p => p.ConnectionId == connId);

                        if (player == null)
                        {
                            Console.WriteLine($"♻️ Stale poker disconnect ignored: {connId}");
                            return;
                        }

                        StopTurnTimer(roomId);
                        CancelRoomStartTimer(roomId);

                        if (player != null)
                        {
                            wasInActiveGame = room.IsGameActive && player.IsInHand;
                            shouldKeepForPotAccounting = room.IsGameActive && player.ContributedToPot > 0;

                            if (wasInActiveGame)
                            {
                                player.HasFolded = true;
                                player.IsInHand = false;

                                // ✅ ÖNEMLI: Turn'deyse sonrakine geç
                                if (room.CurrentPlayerIndex >= 0 && room.CurrentPlayerIndex < room.Players.Count &&
                                    room.Players[room.CurrentPlayerIndex].ConnectionId == connId)
                                {
                                    Console.WriteLine($"⚠️ {player.Name} disconnected while in turn!");
                                    room.MoveToNextActivePlayer();
                                }
                                else if (playerIndexBeforeRemove >= 0 && playerIndexBeforeRemove < room.CurrentPlayerIndex)
                                {
                                    room.CurrentPlayerIndex = Math.Max(0, room.CurrentPlayerIndex - 1);
                                }

                                Console.WriteLine($"❌ {player.Name} disconnected - folded");
                            }

                            if (shouldKeepForPotAccounting)
                            {
                                player.ShouldLeaveAfterHand = true;
                            }

                            // Return only chips that were not already committed to the pot.
                            var user = _db.Users.FirstOrDefault(u => u.Id == player.UserId);
                            if (user != null && player.Chips > 0)
                            {
                                var chipsToReturn = player.Chips;
                                user.Balance += chipsToReturn;
                                player.Chips = 0;
                                _db.SaveChanges();
                                Console.WriteLine($"💰 Chips returned: {player.Name} +{chipsToReturn}");
                            }

                            if (!shouldKeepForPotAccounting)
                            {
                                room.Players.Remove(player);
                                Console.WriteLine($"❌ Player removed: {player.Name}");
                            }
                            else
                            {
                                Console.WriteLine($"❌ Player marked to leave after hand: {player.Name}");
                            }
                        }

                        // ✅ OYUN DURUMUNU KONTROL ET
                        if (room.IsGameActive)
                        {
                            var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();

                            if (activePlayers.Count == 1)
                            {
                                // Sadece 1 oyuncu kaldı → Qalib elan et
                                shouldDetermineWinner = true;
                                gameEnded = true;
                                Console.WriteLine($"🏆 Only 1 player active - determining winner");
                            }
                            else if (activePlayers.Count > 1 && room.IsBettingRoundComplete())
                            {
                                // Betting round bitti → Street'i ilerlet
                                shouldAdvanceStreet = true;
                                Console.WriteLine($"✅ Round complete - advancing street");
                            }
                            else if (activePlayers.Count > 1)
                            {
                                // Oyun devam et → Timer başlat
                                Console.WriteLine($"🎮 Game continues with {activePlayers.Count} players");
                            }
                            else
                            {
                                // Hiç kimse kalmadı
                                gameEnded = true;
                                Console.WriteLine($"❌ No players left!");
                            }
                        }
                    }

                    // ✅ HUB CONTEXT İÇİNDE ASYNC İŞLEMLER
                    try
                    {
                        await Clients.Group(roomId).SendAsync("PlayerLeft", player?.Name ?? "Unknown");
                        await BroadcastGameState(roomId);

                        if (wasInActiveGame && player != null)
                        {
                            await UpdateFoldRank(player, "disconnect");
                        }

                        // ✅ BEKLE, SONRA SIRAYLA İŞLEMLER YAP
                        if (shouldDetermineWinner)
                        {
                            await Task.Delay(500);
                            await DetermineWinner(roomId);
                        }
                        else if (shouldAdvanceStreet)
                        {
                            await Task.Delay(500);
                            var currentRoom = _roomManager.GetRoom(roomId);
                            if (currentRoom != null)
                            {
                                if (currentRoom.CurrentStreet == "river")
                                {
                                    await DetermineWinner(roomId);
                                }
                                else
                                {
                                    await AdvanceStreet(roomId);
                                }
                            }
                        }
                        else if (!gameEnded && room.IsGameActive)
                        {
                            // Oyun devam et → Timer başlat
                            await Task.Delay(300);

                            lock (room.StateLock)
                            {
                                if (TryGetActionReadyCurrentPlayer(room, out var nextPlayer) && nextPlayer != null)
                                {
                                    StartTurnTimer(roomId, nextPlayer.UserId);
                                    Console.WriteLine($"⏱️ Timer started for {nextPlayer.Name}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ OnDisconnected async error: {ex.Message}");
                        Console.WriteLine($"   Stack: {ex.StackTrace}");
                    }

                    // Otaq boşalıbsa sil
                    lock (room.StateLock)
                    {
                        if (room.Players.Count == 0)
                        {
                            _roomManager.DeleteRoom(roomId);
                            try
                            {
                                Clients.All.SendAsync("RoomDeleted", roomId);
                            }
                            catch
                            {
                                // Room zaten delete olubsa, ignore
                            }
                        }
                    }
                }
            }

            await base.OnDisconnectedAsync(exception);
        }

        public async Task<List<object>> GetRoomList()
        {
            var rooms = _roomManager.GetAvailableRooms();
            return rooms.Select(r => new
            {
                roomId = r.RoomId,
                roomName = r.RoomName,
                creatorName = r.CreatorName,
                playerCount = r.PlayerCount,
                maxPlayers = r.MaxPlayers,
                buyIn = r.BuyIn,
                minBuyIn = r.BigBlind * 20,           // ✅ YENİ
                maxBuyIn = r.BigBlind * 100,      // ✅ YENİ
                smallBlind = r.SmallBlind,
                bigBlind = r.BigBlind,
                isGameActive = r.IsGameActive
            }).ToList<object>();
        }
        public async Task JoinRoom(string roomId, decimal buyInAmount = 0)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            try
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                {
                    await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                    return;
                }

                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                {
                    await Clients.Caller.SendAsync("JoinError", "Room tapılmadı");
                    return;
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                // ✅ BUY-IN MƏBLƏĞ HESABLAMA
                decimal entryFee = room.BigBlind;
                decimal minBuyIn = entryFee * 20;
                decimal maxBuyIn = entryFee * 100;

                decimal actualBuyIn = (buyInAmount > 0) ? buyInAmount : minBuyIn;

                // ✅ BUY-IN LİMİT YOXLAMASI
                if (actualBuyIn < minBuyIn)
                {
                    await Clients.Caller.SendAsync("JoinError",
                        $"❌ Minimum buy-in: {minBuyIn}₼ (Entry Fee)");
                    return;
                }

                if (actualBuyIn > maxBuyIn)
                {
                    await Clients.Caller.SendAsync("JoinError",
                        $"❌ Maksimum buy-in: {maxBuyIn}₼ (Entry Fee × 50)");
                    return;
                }

                // ✅ BALANS YOXLAMASI
                if (user.Balance < actualBuyIn)
                {
                    await Clients.Caller.SendAsync("JoinError",
                        $"❌ Kifayət qədər balans yoxdur (lazım: {actualBuyIn}₼, balans: {user.Balance}₼)");
                    return;
                }

                // 🔥 YENİ LOQIKA: 
                // 1. Əgər otaq DOLDUBSA → YENİ OTAQ YAR
                // 2. Əgər otaq AÇIKSA (oyun davam etməsə də) → BU OTAĞA QOŞ
                // 3. Əgər otaq DOLARSA → YENİ OTAQ YAR

                bool shouldCreateNewRoom = false;
                PokerRoom targetRoom = room;

                lock (room.StateLock)
                {
                    // ✅ Əgər otaq artıq dolubsa - yeni yaradılacaq
                    if (room.Players.Count >= room.MaxPlayers)
                    {
                        Console.WriteLine($"⚠️ Room {room.RoomName} is FULL ({room.Players.Count}/{room.MaxPlayers}). Creating new room.");
                        shouldCreateNewRoom = true;
                    }
                    // ✅ Əgər oyunçu artıq otaqda varsa - yenidən əlavə etmə
                    else if (room.Players.FirstOrDefault(p => p.UserId == userId) != null)
                    {
                        Console.WriteLine($"⚠️ Player already in room");
                        Clients.Caller.SendAsync("JoinError", "Siz artıq otaqdasınız");
                        return;
                    }
                }

                // ✅ YENİ OTAQ YARATMA
                if (shouldCreateNewRoom)
                {
                    var newRoom = _roomManager.CreateRoom(
                        room.RoomName,
                        room.CreatorName,
                        room.CreatorUserId,
                        room.BuyIn,
                        room.SmallBlind,
                        room.BigBlind,
                        room.MaxPlayers
                    );

                    if (newRoom != null)
                    {
                        roomId = newRoom.RoomId;
                        targetRoom = newRoom;
                        Console.WriteLine($"✅ New room created: {newRoom.RoomName} ({newRoom.RoomId})");

                        await Clients.All.SendAsync("RoomCreated", new
                        {
                            roomId = newRoom.RoomId,
                            roomName = newRoom.RoomName
                        });
                    }
                }

                // ✅ OYUNÇU YARATMA VƏ OTAĞA ƏLAVƏ ETMƏ
                var player = new RoomPlayers
                {
                    ConnectionId = Context.ConnectionId,
                    UserId = user.Id,
                    Name = user.UserName,
                    UserName = user.UserName,
                    Balance = user.Balance,
                    Chips = actualBuyIn,
                    IsInHand = false,
                    HasFolded = false,
                    ProfileImage = user.Image
                };

                // ✅ BALANS ÖDƏNIŞI
                try
                {
                    user.Balance -= actualBuyIn;
                    await _db.SaveChangesAsync();
                    player.Balance = user.Balance;
                }
                catch (Exception ex)
                {
                    await Clients.Caller.SendAsync("JoinError", "Buy-in ödənişi alınmadı");
                    Console.WriteLine($"❌ Buy-in payment failed: {ex.Message}");
                    return;
                }

                if (!_roomManager.AddPlayerToRoom(roomId, player))
                {
                    user.Balance += actualBuyIn;
                    await _db.SaveChangesAsync();
                    await Clients.Caller.SendAsync("JoinError", "Room-a qoşulmaq alınmadı");
                    return;
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
                _userRooms[Context.ConnectionId] = roomId;
                await Clients.Caller.SendAsync("JoinedRoom", new
                {
                    roomId,
                    roomName = targetRoom.RoomName,
                    balance = user.Balance,
                    chips = player.Chips,
                    entryFee = entryFee,
                    minBuyIn = room.BigBlind * 20,  // ✅ Əlavə et
                    maxBuyIn = room.BigBlind * 100, // ✅ Əlavə et
                    bigBlind = targetRoom.BigBlind   // ✅ Əlavə et (optional)
                });
                await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
                await Clients.Group(roomId).SendAsync("PlayerJoined", user.UserName);
                await BroadcastGameState(roomId);

                Console.WriteLine($"✅ {fullName} joined room {targetRoom.RoomName}");
                Console.WriteLine($"   💰 Buy-in: {actualBuyIn}₼ (min: {minBuyIn}₼, max: {maxBuyIn}₼)");
                Console.WriteLine($"   👥 Room: {targetRoom.Players.Count}/{targetRoom.MaxPlayers}");
                Console.WriteLine($"   💳 Remaining balance: {user.Balance}₼");

                lock (targetRoom.StateLock)
                {
                    // 🔥 OYUN BAŞLATMA LOQIKASI:
                    // 1. Oyun DÜN BAŞLAMAYIBSA VƏ 2+ oyunçu varsa - timer başlat
                    // 2. Oyun BAŞLAMIŞSA - oyunçu sadəcə otağa qoşulur

                    if (!targetRoom.IsGameActive && targetRoom.Players.Count >= 2)
                    {
                        // ✅ Oyun hələ başlamamışsa
                        if (targetRoom.Players.Count == 2)
                        {
                            // Yalnız 2 nəfər - timer başlat
                            Console.WriteLine($"⏰ 2 players joined. Starting timer (120 seconds)...");
                            StartRoomStartTimer(roomId);
                        }
                        else if (targetRoom.Players.Count >= targetRoom.MaxPlayers)
                        {
                            // Otaq doldubsa - timer ləğv et və oyunu başlat
                            Console.WriteLine($"✅ Room is FULL! Starting game immediately...");
                            CancelRoomStartTimer(roomId);

                            Task.Run(async () =>
                            {
                                try
                                {
                                    await _hubContext.Clients.Group(roomId).SendAsync("RoomStarting",
                                        "✅ Otaq doldu! Oyun başlayır...");
                                    await Task.Delay(2000);
                                    await AutoStartGameBackground(roomId, _hubContext);
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"❌ Auto start failed: {ex.Message}");
                                }
                            });
                        }
                        // Əgər 2-dən çox ama doymamışsa - timer davam et
                    }
                    else if (targetRoom.IsGameActive)
                    {
                        // 🎮 OYUN DAVAM EDİRSƏ:
                        // Yeni oyunçu "waiting" vəziyyətində qoşulur (sonrakı əldə daxil olacaq)
                        Console.WriteLine($"🎮 Game is active. {fullName} will join next hand.");
                        player.IsInHand = false; // Sonrakı əldə qoşulacaq
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ JoinRoom error: {ex.Message}\n{ex.StackTrace}");
                await Clients.Caller.SendAsync("JoinError", "Xəta baş verdi");
            }
        }
        private async Task AddWaitingPlayersToNextHand(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                var waitingPlayers = room.Players.Where(p => !p.IsInHand && p.Chips > 0 && !p.IsPausedAfterHand).ToList();

                if (waitingPlayers.Any())
                {
                    foreach (var player in waitingPlayers)
                    {
                        player.IsInHand = true;
                        player.HasFolded = false;
                        player.IsWaitingForNextHand = false;
                        Console.WriteLine($"✅ {player.Name} joined the next hand");
                    }
                }
            }

            await BroadcastGameState(roomId);
        }

        private async Task AutoStartGameBackground(string roomId, IHubContext<PokerHub> hubContext)
        {
            try
            {
                var room = _roomManager.GetRoom(roomId);
                if (room == null) return;

                lock (room.StateLock)
                {
                    var eligiblePlayers = room.Players.Count(p => p.Chips > 0 && !p.IsPausedAfterHand);
                    if (room.IsGameActive || eligiblePlayers < 2)
                        return;
                    room.StartNewHand();
                }

                await hubContext.Clients.Group(roomId).SendAsync("GameStarted");
                await BroadcastGameStateBackground(roomId, hubContext);
                await Task.Delay(500);
                await PostBlindsBackground(roomId, hubContext);
                await Task.Delay(500);
                await DealHoleCardsBackground(roomId, hubContext);

                // ✅ İLK OYUNÇU ÜÇÜN TİMER BAŞLAT
                lock (room.StateLock)
                {
                    if (TryGetActionReadyCurrentPlayer(room, out var firstPlayer) && firstPlayer != null)
                    {
                        StartTurnTimer(roomId, firstPlayer.UserId);
                    }
                }

                // ✅ TİMER BAŞLADIQDAN SONRA 200ms GÖZLƏYİB YENİDƏN BROADCAST ET
                await Task.Delay(200);
                await BroadcastGameStateBackground(roomId, hubContext);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ AutoStartGameBackground: {ex.Message}");
            }
        }
        private void StartTurnTimer(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            StopTurnTimer(roomId); // Eski timer-i sil

            lock (room.StateLock)
            {
                if (!room.IsGameActive ||
                    room.CurrentPlayerIndex < 0 ||
                    room.CurrentPlayerIndex >= room.Players.Count ||
                    room.Players[room.CurrentPlayerIndex].UserId != userId ||
                    !room.CanPlayerAct(room.Players[room.CurrentPlayerIndex]))
                {
                    room.TurnStartTime = null;
                    Console.WriteLine($"⚠️ Turn timer not started: userId={userId} is not an action-ready current player");
                    return;
                }

                room.TurnStartTime = DateTime.UtcNow;
                Console.WriteLine($"⏱️ Turn timer started for userId: {userId} at {room.TurnStartTime}");
            }

            // ✅ 30 saniyə sonra timeout check et
            var timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    Console.WriteLine($"⏰ Timer callback for room: {roomId}, user: {userId}");
                    await CheckTurnTimeout(roomId, userId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Turn timer callback error: {ex.Message}");
                }
                finally
                {
                    _turnTimers.TryRemove(roomId, out var t);
                    t?.Dispose();
                }
            }, null, TimeSpan.FromSeconds(PokerRoom.TURN_TIMEOUT_SECONDS), Timeout.InfiniteTimeSpan);

            _turnTimers[roomId] = timer;
        }

        private bool TryGetActionReadyCurrentPlayer(PokerRoom room, out RoomPlayers? nextPlayer)
        {
            nextPlayer = null;

            if (!room.IsGameActive || room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                return false;
            }

            if (!room.CanPlayerAct(room.Players[room.CurrentPlayerIndex]))
            {
                room.MoveToNextActivePlayer();
            }

            if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
            {
                return false;
            }

            var candidate = room.Players[room.CurrentPlayerIndex];
            if (!room.CanPlayerAct(candidate))
            {
                return false;
            }

            nextPlayer = candidate;
            return true;
        }

        private void StopTurnTimer(string roomId)
        {
            if (_turnTimers.TryRemove(roomId, out var timer))
            {
                timer?.Dispose();
                Console.WriteLine($"⏹️ Turn timer stopped: {roomId}");
            }
        }

        private async Task CheckTurnTimeout(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                Console.WriteLine($"❌ Room not found: {roomId}");
                return;
            }

            bool shouldAutoFold = false;
            RoomPlayers? player = null;
            int nextPlayerIndex = -1;

            lock (room.StateLock)
            {
                if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
                {
                    Console.WriteLine($"❌ Invalid CurrentPlayerIndex: {room.CurrentPlayerIndex}");
                    return;
                }

                var currentPlayer = room.Players[room.CurrentPlayerIndex];

                if (currentPlayer.UserId == userId &&
                    room.TurnStartTime.HasValue &&
                    (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds >= PokerRoom.TURN_TIMEOUT_SECONDS)
                {
                    player = currentPlayer;

                    if (!player.HasFolded && player.IsInHand)
                    {
                        shouldAutoFold = true;
                        player.HasFolded = true;
                        player.IsInHand = false;

                        Console.WriteLine($"⏰ AUTO-FOLD: {player.Name} (timeout)");
                    }
                }
            }

            if (!shouldAutoFold || player == null)
            {
                Console.WriteLine($"⚠️ No auto-fold needed for user {userId}");
                return;
            }

            try
            {
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerTimeout", new
                {
                    playerName = player.Name,
                    message = $"{player.Name} vaxt bitdiyi üçün avtomatik fold oldu",
                    timeoutSeconds = PokerRoom.TURN_TIMEOUT_SECONDS
                });

                await _hubContext.Clients.Group(roomId).SendAsync("PlayerActioned", new
                {
                    playerName = player.Name,
                    action = "fold",
                    amount = (decimal?)null
                });

                await UpdateFoldRank(player, "auto-fold");

                // 🔥 YENİ: Async winner check
                lock (room.StateLock)
                {
                    room.MoveToNextPlayer();
                    nextPlayerIndex = room.CurrentPlayerIndex;

                    var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();
                    Console.WriteLine($"📊 Active players: {activePlayers.Count}");

                    if (activePlayers.Count == 1)
                    {
                        Console.WriteLine($"🏆 Only 1 player left - game ending");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            await DetermineWinner(roomId);
                        });
                        return;
                    }

                    if (activePlayers.Count == 0)
                    {
                        Console.WriteLine($"❌ No active players!");
                        return;
                    }

                    if (room.IsBettingRoundComplete())
                    {
                        Console.WriteLine($"✅ Betting round complete - advancing street");
                        _ = Task.Run(async () =>
                        {
                            await Task.Delay(500);
                            if (room.CurrentStreet == "river")
                                await DetermineWinner(roomId);
                            else
                                await AdvanceStreet(roomId);
                        });
                        return;
                    }
                }

                await BroadcastGameStateBackground(roomId, _hubContext);
                await Task.Delay(300);

                lock (room.StateLock)
                {
                    if (room.IsGameActive &&
                        nextPlayerIndex >= 0 &&
                        nextPlayerIndex < room.Players.Count)
                    {
                        var nextPlayer = room.Players[nextPlayerIndex];
                        if (room.CanPlayerAct(nextPlayer))
                        {
                            StartTurnTimer(roomId, nextPlayer.UserId);
                        }
                        else
                        {
                            Console.WriteLine($"⚠️ Timeout flow found no action-ready player - advancing");
                            _ = Task.Run(async () =>
                            {
                                await Task.Delay(300);
                                if (room.CurrentStreet == "river")
                                    await DetermineWinner(roomId);
                                else
                                    await AdvanceStreet(roomId);
                            });
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ CheckTurnTimeout error: {ex.Message}");
            }
        }

        private async Task UpdateFoldRank(RoomPlayers player, string reason)
        {
            try
            {
                await _service.UpdateRankAfterGame(
                    player.UserId,
                    GameType.Poker,
                    isWin: false,
                    earnings: player.ContributedToPot);

                var rankDetails = await _service.GetPlayerRankDetails(player.UserId, GameType.Poker);

                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("RankUpdated", new
                {
                    rank = rankDetails.CurrentRank,
                    level = rankDetails.RankLevel,
                    xp = rankDetails.ExperiencePoints,
                    requiredXP = rankDetails.RequiredXPForNextRank,
                    progress = rankDetails.ProgressPercentage
                });

                Console.WriteLine($"📊 Poker fold rank updated: {player.Name} | Reason: {reason} | Loss: {player.ContributedToPot}₼");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Poker fold rank update error: {ex.Message}");
            }
        }

        private async Task CheckAndDetermineWinner(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();
                if (activePlayers.Count != 1) return;

                Console.WriteLine($"🏆 Only 1 player left - determining winner");
            }

            // Async call
            await Task.Run(async () =>
            {
                await Task.Delay(500);
                await DetermineWinner(roomId);
            });
        }


        private void RestartTurnTimer(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            RoomPlayers? currentPlayer;
            lock (room.StateLock)
            {
                TryGetActionReadyCurrentPlayer(room, out currentPlayer);
            }

            if (currentPlayer != null)
            {
                StartTurnTimer(roomId, currentPlayer.UserId);
            }

            // ✅ TurnStartTime reset olduqdan SONRA broadcast et
            _ = Task.Run(async () => await BroadcastGameState(roomId));
        }
        private void StartRoomStartTimer(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            // Köhnə timer-i dayandır
            CancelRoomStartTimer(roomId);

            lock (room.StateLock)
            {
                room.RoomCreatedTime = DateTime.UtcNow;
            }

            Console.WriteLine($"⏰ Room start timer başladı: {roomId}");

            // ✅ 120 saniyə sonra otomatik başlat
            var timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    var currentRoom = _roomManager.GetRoom(roomId);
                    if (currentRoom == null || currentRoom.IsGameActive)
                    {
                        CancelRoomStartTimer(roomId);
                        return;
                    }

                    // ✅ 2 və ya daha çox oyunçu varsa oyunu başlat
                    if (currentRoom.Players.Count >= 2)
                    {
                        Console.WriteLine($"⏰ 2 dəqiqə bitdi! Oyunçular: {currentRoom.Players.Count}");

                        await _hubContext.Clients.Group(roomId).SendAsync("RoomStartTimeout",
                            "⏰ 2 dəqiqə bitdi! Oyun başlayır...");

                        await Task.Delay(2000);
                        await AutoStartGameBackground(roomId, _hubContext);
                    }
                    else
                    {
                        await _hubContext.Clients.Group(roomId).SendAsync("RoomTimerExpired",
                            "⏰ Vaxt bitdi! Başqa oyunçu gözlənilir...");
                        Console.WriteLine($"⏰ Timer bitdi amma yalnız 1 oyunçu var: {roomId}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Room timer error: {ex.Message}");
                }
                finally
                {
                    CancelRoomStartTimer(roomId);
                }
            }, null, TimeSpan.FromSeconds(PokerRoom.ROOM_START_TIMEOUT_SECONDS), Timeout.InfiniteTimeSpan);

            _roomStartTimers[roomId] = timer;

            // ✅ Hər saniyə frontend-ə göndər
            _ = Task.Run(async () =>
            {
                int remainingSeconds = PokerRoom.ROOM_START_TIMEOUT_SECONDS;

                while (remainingSeconds > 0)
                {
                    await Task.Delay(1000);
                    remainingSeconds--;

                    var currentRoom = _roomManager.GetRoom(roomId);
                    if (currentRoom == null || currentRoom.IsGameActive || currentRoom.Players.Count >= currentRoom.MaxPlayers)
                    {
                        return;
                    }

                    // Hər saniyə göndər
                    await _hubContext.Clients.Group(roomId).SendAsync("RoomStartTimer", remainingSeconds);
                }
            });
        }

        private void CancelRoomStartTimer(string roomId)
        {
            if (_roomStartTimers.TryRemove(roomId, out var timer))
            {
                timer?.Dispose();
                Console.WriteLine($"⏹️ Room start timer ləğv edildi: {roomId}");
            }
        }

        private int GetNextInHandPlayerIndex(PokerRoom room, int startIndex)
        {
            if (room.Players.Count == 0) return -1;

            for (int i = 1; i <= room.Players.Count; i++)
            {
                var index = (startIndex + i) % room.Players.Count;
                var player = room.Players[index];
                if (player.IsInHand && !player.HasFolded && player.Chips > 0 && !player.IsPausedAfterHand)
                {
                    return index;
                }
            }

            return -1;
        }

        private async Task PostBlindsBackground(string roomId, IHubContext<PokerHub> hubContext)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.Players.Count < 2) return;

                int sbIndex = GetNextInHandPlayerIndex(room, room.DealerIndex);
                int bbIndex = GetNextInHandPlayerIndex(room, sbIndex);
                if (sbIndex < 0 || bbIndex < 0 || sbIndex == bbIndex) return;

                var sbPlayer = room.Players[sbIndex];
                var bbPlayer = room.Players[bbIndex];

                decimal sbAmount = Math.Min(sbPlayer.Chips, room.SmallBlind);
                decimal bbAmount = Math.Min(bbPlayer.Chips, room.BigBlind);

                sbPlayer.Chips -= sbAmount;
                sbPlayer.CurrentBet = sbAmount;
                sbPlayer.ContributedToPot += sbAmount;
                room.Pot += sbAmount;
                if (sbPlayer.Chips == 0)
                {
                    sbPlayer.IsAllIn = true;
                    room.HasAllInThisStreet = true;
                }

                bbPlayer.Chips -= bbAmount;
                bbPlayer.CurrentBet = bbAmount;
                bbPlayer.ContributedToPot += bbAmount;
                room.Pot += bbAmount;
                if (bbPlayer.Chips == 0)
                {
                    bbPlayer.IsAllIn = true;
                    room.HasAllInThisStreet = true;
                }

                room.CurrentBet = bbAmount;
                room.LastRaiserIndex = -1;

                // ✅ BB-dən sonrakı oyunçudan başla (UTG)
                room.CurrentPlayerIndex = GetNextInHandPlayerIndex(room, bbIndex);
                if (room.CurrentPlayerIndex < 0) return;

                // ✅ PREFLOP üçün FirstPlayerOfRound = -1 qalır
                // BB hələ action görməyib
                room.FirstPlayerOfRound = -1;

                Console.WriteLine($"💰 Blinds posted: SB={sbAmount}, BB={bbAmount}");
                Console.WriteLine($"🎲 First to act: {room.Players[room.CurrentPlayerIndex].Name}");
                Console.WriteLine($"🎲 FirstPlayerOfRound: {room.FirstPlayerOfRound} (waiting for BB to act)");
            }

            await hubContext.Clients.Group(roomId).SendAsync("BlindsPosted", new
            {
                smallBlind = room.SmallBlind,
                bigBlind = room.BigBlind
            });

            await BroadcastGameStateBackground(roomId, hubContext);
        }
        private async Task DealHoleCardsBackground(string roomId, IHubContext<PokerHub> hubContext)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<(string connectionId, string name, string[] cards)> playersData;

            lock (room.StateLock)
            {
                playersData = new List<(string, string, string[])>();

                foreach (var player in room.Players)
                {
                    // ✅ Yalnız çipi olan və əldə olan oyunçulara kart ver
                    if (!player.IsInHand || player.Chips <= 0)
                    {
                        Console.WriteLine($"⚠️ Skipping {player.Name} - no chips or not in hand");
                        continue;
                    }

                    if (room.Deck.Count < 2) return;

                    player.HoleCards = room.Deck.Take(2).ToList();
                    room.Deck.RemoveRange(0, 2);

                    playersData.Add((player.ConnectionId, player.Name, player.HoleCards.ToArray()));
                }
            }

            foreach (var (connectionId, name, cards) in playersData)
            {
                try
                {
                    await hubContext.Clients.Client(connectionId).SendAsync("HoleCards", cards);
                    await Task.Delay(200);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Error sending to {name}: {ex.Message}");
                }
            }

            await Task.Delay(500);
            await BroadcastGameStateBackground(roomId, hubContext);
        }
        private async Task BroadcastGameStateBackground(string roomId, IHubContext<PokerHub> hubContext)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            try
            {
                List<object> playersData;
                int dealerIdx, currentPlayerIdx;
                decimal pot, currentBet, bigBlind;
                List<string> communityCards;
                string currentStreet;
                decimal minBuyIn, maxBuyIn;
                bool isGameActive;

                lock (room.StateLock)
                {
                    if (room.Players.Count == 0) return;

                    currentPlayerIdx = room.CurrentPlayerIndex;
                    if (currentPlayerIdx < 0 || currentPlayerIdx >= room.Players.Count)
                    {
                        room.CurrentPlayerIndex = 0;
                        currentPlayerIdx = 0;
                    }

                    int turnTimeRemaining = 0;
                    if (room.TurnStartTime.HasValue && room.IsGameActive)
                    {
                        var elapsed = (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds;
                        turnTimeRemaining = Math.Max(0, (int)(PokerRoom.TURN_TIMEOUT_SECONDS - elapsed));
                    }

                    var currentPlayer = room.Players[currentPlayerIdx];

                    playersData = room.Players.Select(p =>
                    {
                        var isCurrentTurn = room.IsGameActive &&
                                            p.UserId == currentPlayer.UserId &&
                                            p.IsInHand &&
                                            !p.HasFolded &&
                                            !p.IsAllIn &&
                                            !p.IsPausedAfterHand &&
                                            p.Chips > 0;

                        return (object)new
                        {
                            userId = p.UserId,                    // ✅ əlavə
                            userName = p.UserName,                // ✅ camelCase - JS ilə uyğun
                            name = p.Name,
                            balance = p.Balance,                  // ✅ əlavə
                            image = p.ProfileImage ?? "",         // ✅ əlavə
                            chips = p.Chips,
                            currentBet = p.CurrentBet,
                            isInHand = p.IsInHand,
                            hasFolded = p.HasFolded,
                            isWaitingForNextHand = p.IsWaitingForNextHand,
                            isPausedAfterHand = p.IsPausedAfterHand,
                            shouldLeaveAfterHand = p.ShouldLeaveAfterHand,
                            isActive = isCurrentTurn,
                            turnTimeRemaining = isCurrentTurn ? turnTimeRemaining : 0, // ✅ əlavə
                            profileImage = p.ProfileImage ?? ""
                        };
                    }).ToList();

                    dealerIdx = room.DealerIndex;
                    pot = room.Pot;
                    currentBet = room.CurrentBet;
                    bigBlind = room.BigBlind;
                    minBuyIn = room.BigBlind * 20;
                    maxBuyIn = room.BigBlind * 100;
                    isGameActive = room.IsGameActive;
                    communityCards = new List<string>(room.CommunityCards);
                    currentStreet = room.CurrentStreet;
                }

                await hubContext.Clients.Group(roomId).SendAsync("GameState", new
                {
                    players = playersData,
                    pot,
                    currentBet,
                    bigBlind,
                    minBuyIn,
                    maxBuyIn,
                    isGameActive,
                    communityCards,
                    dealerIndex = dealerIdx,
                    currentStreet
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ BroadcastGameState error: {ex.Message}");
            }
        }

        public async Task LeaveRoom()
        {
            var connId = Context.ConnectionId;
            if (!_userRooms.TryGetValue(connId, out var roomId))
                return;

            var userId = GetUserId();
            if (userId == 0) return;

            try
            {
                var room = _roomManager.GetRoom(roomId);
                RoomPlayers? player = null;
                bool shouldReturnChips = false;
                bool shouldDetermineWinner = false;
                bool wasInActiveGame = false;
                bool shouldKeepForPotAccounting = false;

                if (room != null)
                {
                    lock (room.StateLock)
                    {
                        player = room.Players.FirstOrDefault(p => p.UserId == userId);
                        if (player == null) return;

                        wasInActiveGame = room.IsGameActive && player.IsInHand;
                        shouldKeepForPotAccounting = room.IsGameActive && player.ContributedToPot > 0;
                        shouldReturnChips = player.Chips > 0;

                        if (wasInActiveGame)
                        {
                            player.HasFolded = true;
                            player.IsInHand = false;

                            if (room.CurrentPlayerIndex >= 0 &&
                                room.CurrentPlayerIndex < room.Players.Count &&
                                room.Players[room.CurrentPlayerIndex].UserId == userId)
                            {
                                room.MoveToNextActivePlayer();
                            }

                            var activePlayers = room.Players
                                .Where(p => p.IsInHand && !p.HasFolded && p.UserId != userId)
                                .ToList();

                            if (activePlayers.Count == 1)
                            {
                                shouldDetermineWinner = true;
                                Console.WriteLine($"🏆 {player.Name} left - 1 player remains, determining winner");
                            }
                            else if (activePlayers.Count > 1)
                            {
                                Console.WriteLine($"🎮 {player.Name} left - {activePlayers.Count} players remain, game continues");
                            }
                        }

                        if (shouldKeepForPotAccounting)
                        {
                            player.ShouldLeaveAfterHand = true;
                        }
                    }

                    // ✅ CreateExecutionStrategy ilə transaction
                    if (shouldReturnChips && player != null)
                    {
                        var chipsToReturn = player.Chips;
                        var strategy = _db.Database.CreateExecutionStrategy();
                        await strategy.ExecuteAsync(async () =>
                        {
                            using var transaction = await _db.Database.BeginTransactionAsync();
                            try
                            {
                                var freshUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                                if (freshUser != null)
                                {
                                    freshUser.Balance += chipsToReturn;
                                    _db.Users.Update(freshUser);
                                    await _db.SaveChangesAsync();
                                    await transaction.CommitAsync();
                                    player.Chips = 0;
                                    await _hubContext.Clients.Client(connId)
                                        .SendAsync("BalanceUpdated", freshUser.Balance);
                                    Console.WriteLine($"💰 Chips returned: {player.Name} +{chipsToReturn}");
                                }
                            }
                            catch
                            {
                                await transaction.RollbackAsync();
                                throw;
                            }
                        });
                    }
                }

                if (!shouldKeepForPotAccounting)
                {
                    _roomManager.RemovePlayerFromRoom(roomId, userId);
                }
                await Groups.RemoveFromGroupAsync(connId, roomId);
                _userRooms.TryRemove(connId, out _);

                await Clients.Caller.SendAsync("LeftRoom");
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", player?.Name ?? "Unknown");

                if (wasInActiveGame && player != null)
                {
                    await UpdateFoldRank(player, "leave-room");
                }

                if (shouldDetermineWinner)
                {
                    StopTurnTimer(roomId);
                    await Task.Delay(500);
                    await DetermineWinner(roomId);
                }
                else
                {
                    await BroadcastGameStateBackground(roomId, _hubContext);

                    if (room != null)
                    {
                        lock (room.StateLock)
                        {
                            if (TryGetActionReadyCurrentPlayer(room, out var nextPlayer) && nextPlayer != null)
                            {
                                StartTurnTimer(roomId, nextPlayer.UserId);
                                Console.WriteLine($"⏱️ Timer started for {nextPlayer.Name}");
                            }
                        }
                    }
                }

                if (room != null)
                {
                    lock (room.StateLock)
                    {
                        if (room.Players.Count == 0)
                        {
                            _roomManager.DeleteRoom(roomId);
                            Console.WriteLine($"🗑️ Empty room deleted: {roomId}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ LeaveRoom error: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
            }
        }
        public async Task PlayerAction(string action, decimal? amount = null)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            StopTurnTimer(roomId);

            var userId = GetUserId();
            RoomPlayers? player = null;
            bool allInOccurred = false;
            string? allInPlayerName = null;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                    return;
                }

                int currentPlayerIdx = room.CurrentPlayerIndex;
                if (currentPlayerIdx < 0 || currentPlayerIdx >= room.Players.Count)
                {
                    Clients.Caller.SendAsync("ActionError", "Sistemdə xəta");
                    return;
                }

                if (room.Players[currentPlayerIdx].UserId != userId)
                {
                    Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                // ✅ ACTION İŞLƏ
                switch (action.ToLower())
                {
                    case "fold":
                        var activeBeforeFold = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();
                        if (activeBeforeFold.Count <= 1)
                        {
                            Clients.Caller.SendAsync("ActionError", "Siz tək oyunçusunuz!");
                            return;
                        }

                        player.HasFolded = true;
                        player.IsInHand = false;
                        Console.WriteLine($"✅ {player.Name} FOLDED at index {currentPlayerIdx}");

                        if (!room.PlayersActedThisStreet.Contains(currentPlayerIdx))
                        {
                            room.PlayersActedThisStreet.Add(currentPlayerIdx);
                        }

                        room.MoveToNextActivePlayer();
                        break;

                    case "check":
                        if (player.CurrentBet < room.CurrentBet)
                        {
                            Clients.Caller.SendAsync("ActionError", "Check edə bilməzsiniz - mərc var");
                            return;
                        }

                        Console.WriteLine($"✅ {player.Name} CHECKED");

                        if (!room.PlayersActedThisStreet.Contains(currentPlayerIdx))
                        {
                            room.PlayersActedThisStreet.Add(currentPlayerIdx);
                        }

                        room.MoveToNextActivePlayer();
                        break;

                    case "call":
                        decimal callAmount = room.CurrentBet - player.CurrentBet;
                        if (callAmount <= 0)
                        {
                            Clients.Caller.SendAsync("ActionError", "Call edməyə ehtiyac yoxdur");
                            return;
                        }

                        decimal actualCallAmount = Math.Min(callAmount, player.Chips);
                        player.Chips -= actualCallAmount;
                        player.CurrentBet += actualCallAmount;
                        player.ContributedToPot += actualCallAmount;
                        room.Pot += actualCallAmount;
                        if (player.Chips == 0)
                        {
                            player.IsAllIn = true;
                            room.HasAllInThisStreet = true;
                            allInOccurred = true;
                            allInPlayerName = player.Name;
                        }

                        Console.WriteLine(actualCallAmount < callAmount
                            ? $"✅ {player.Name} CALLED ALL-IN {actualCallAmount} (needed {callAmount})"
                            : $"✅ {player.Name} CALLED {actualCallAmount}");

                        if (!room.PlayersActedThisStreet.Contains(currentPlayerIdx))
                        {
                            room.PlayersActedThisStreet.Add(currentPlayerIdx);
                        }

                        room.MoveToNextActivePlayer();
                        break;

                    case "raise":
                        if (!amount.HasValue || amount.Value <= 0)
                        {
                            Clients.Caller.SendAsync("ActionError", "Məbləğ daxil edin");
                            return;
                        }

                        decimal minRaise = room.GetMinimumRaise();
                        decimal toCallAmount = room.CurrentBet - player.CurrentBet;
                        decimal totalToAddAmount = toCallAmount + amount.Value;

                        if (totalToAddAmount > player.Chips)
                        {
                            Clients.Caller.SendAsync("ActionError", "Kifayət qədər çipiniz yoxdur");
                            return;
                        }

                        player.Chips -= totalToAddAmount;
                        player.CurrentBet += totalToAddAmount;
                        player.ContributedToPot += totalToAddAmount;
                        room.Pot += totalToAddAmount;
                        room.CurrentBet = player.CurrentBet;
                        room.LastRaiserIndex = currentPlayerIdx;
                        room.RaisesThisStreet++;
                        if (player.Chips == 0)
                        {
                            player.IsAllIn = true;
                            room.HasAllInThisStreet = true;
                            allInOccurred = true;
                            allInPlayerName = player.Name;
                        }

                        Console.WriteLine($"✅ {player.Name} RAISED {totalToAddAmount}");

                        if (!room.PlayersActedThisStreet.Contains(currentPlayerIdx))
                        {
                            room.PlayersActedThisStreet.Add(currentPlayerIdx);
                        }

                        room.MoveToNextActivePlayer();
                        break;

                    case "allin":
                        decimal allInAmount = player.Chips;
                        if (allInAmount <= 0)
                        {
                            Clients.Caller.SendAsync("ActionError", "Çipiniz yoxdur");
                            return;
                        }

                        player.CurrentBet += allInAmount;
                        player.ContributedToPot += allInAmount;
                        room.Pot += allInAmount;
                        player.Chips = 0;
                        player.IsAllIn = true;
                        room.HasAllInThisStreet = true;

                        if (player.CurrentBet > room.CurrentBet)
                        {
                            room.CurrentBet = player.CurrentBet;
                            room.LastRaiserIndex = currentPlayerIdx;
                            room.RaisesThisStreet++;
                        }

                        Console.WriteLine($"✅ {player.Name} ALL-IN with {allInAmount}");
                        allInOccurred = true;
                        allInPlayerName = player.Name;

                        if (!room.PlayersActedThisStreet.Contains(currentPlayerIdx))
                        {
                            room.PlayersActedThisStreet.Add(currentPlayerIdx);
                        }

                        room.MoveToNextActivePlayer();
                        TryAutoCommitHeadsUpShortStack(room, player, ref allInOccurred, ref allInPlayerName);
                        break;

                    default:
                        Clients.Caller.SendAsync("ActionError", "Bilinməyən action");
                        return;
                }

                // ✅ KONTROL: Heç kimə mi turn geçti?
                if (room.CurrentPlayerIndex < 0 || room.CurrentPlayerIndex >= room.Players.Count)
                {
                    Console.WriteLine($"⚠️ Invalid CurrentPlayerIndex: {room.CurrentPlayerIndex}");
                    room.CurrentPlayerIndex = 0;
                }
            }

            if (string.Equals(action, "fold", StringComparison.OrdinalIgnoreCase) && player != null)
            {
                await UpdateFoldRank(player, "manual-fold");
            }

            if (allInOccurred)
            {
                await Clients.Group(roomId).SendAsync("PlayerAllIn", new
                {
                    playerName = allInPlayerName,
                    message = $"{allInPlayerName} All-In etdi. Call/Fold qərarları gözlənir."
                });
            }

            // Broadcast state
            await BroadcastGameState(roomId);

            // 🔥 Timer başlat
            await Task.Delay(200);

            bool shouldDetermineWinner = false;
            bool shouldAdvanceStreet = false;
            bool shouldStartAllInRunout = false;

            lock (room.StateLock)
            {
                var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();

                Console.WriteLine($"📊 After action: {activePlayers.Count} active, Street: {room.CurrentStreet}");

                if (activePlayers.Count == 1)
                {
                    shouldDetermineWinner = true;
                    Console.WriteLine($"🏆 Only 1 active - determining winner");
                }
                else if (room.IsBettingRoundComplete())
                {
                    shouldAdvanceStreet = true;
                    shouldStartAllInRunout = room.IsAllInRunoutReady();
                    Console.WriteLine($"✅ Betting round complete - advancing");
                }
                else
                {
                    // Timer başlat
                    if (room.CurrentPlayerIndex >= 0 && room.CurrentPlayerIndex < room.Players.Count)
                    {
                        if (!room.CanPlayerAct(room.Players[room.CurrentPlayerIndex]))
                        {
                            room.MoveToNextActivePlayer();
                        }

                        var nextPlayer = room.Players[room.CurrentPlayerIndex];
                        if (room.CanPlayerAct(nextPlayer))
                        {
                            StartTurnTimer(roomId, nextPlayer.UserId);
                            Console.WriteLine($"⏱️ Timer for {nextPlayer.Name}");
                        }
                        else
                        {
                            shouldAdvanceStreet = true;
                            shouldStartAllInRunout = room.IsAllInRunoutReady();
                            Console.WriteLine($"⚠️ No action-ready player found - advancing to avoid stall");
                        }
                    }
                }
            }

            if (shouldDetermineWinner)
            {
                StopTurnTimer(roomId);
                await Task.Delay(500);
                await DetermineWinner(roomId);
            }
            else if (shouldAdvanceStreet)
            {
                StopTurnTimer(roomId);
                if (shouldStartAllInRunout)
                {
                    await NotifyAllInRunoutStartedIfNeeded(roomId, room);
                }

                if (room.CurrentStreet == "river")
                {
                    await Task.Delay(500);
                    await DetermineWinner(roomId);
                }
                else
                {
                    await AdvanceStreet(roomId);
                }
            }
        }

        private static void TryAutoCommitHeadsUpShortStack(
            PokerRoom room,
            RoomPlayers allInAggressor,
            ref bool allInOccurred,
            ref string? allInPlayerName)
        {
            var activePlayers = room.Players
                .Where(p => p.IsInHand && !p.HasFolded)
                .ToList();

            if (activePlayers.Count != 2)
                return;

            var opponent = activePlayers.FirstOrDefault(p => p.UserId != allInAggressor.UserId);
            if (opponent == null || !room.CanPlayerAct(opponent))
                return;

            decimal callAmount = room.CurrentBet - opponent.CurrentBet;
            if (callAmount <= 0 || opponent.Chips >= callAmount)
                return;

            decimal committed = opponent.Chips;
            if (committed <= 0)
                return;

            opponent.Chips = 0;
            opponent.CurrentBet += committed;
            opponent.ContributedToPot += committed;
            opponent.IsAllIn = true;
            room.Pot += committed;
            room.HasAllInThisStreet = true;

            var opponentIndex = room.Players.IndexOf(opponent);
            if (opponentIndex >= 0)
                room.PlayersActedThisStreet.Add(opponentIndex);

            allInOccurred = true;
            allInPlayerName = opponent.Name;

            Console.WriteLine(
                $"✅ HEADS-UP SHORT STACK AUTO ALL-IN: {opponent.Name} committed {committed} (needed {callAmount})");

            room.MoveToNextActivePlayer();
        }

        private async Task NotifyAllInRunoutStartedIfNeeded(string roomId, PokerRoom room)
        {
            bool shouldNotify = false;
            string message = "All-In call-lar tamamlandı. Kartlar avtomatik açılır.";

            lock (room.StateLock)
            {
                if (room.IsGameActive && room.IsAllInRunoutReady() && !room.IsAllInRunoutStarted)
                {
                    room.IsAllInRunoutStarted = true;
                    shouldNotify = true;

                    var allInNames = room.Players
                        .Where(p => p.IsInHand && !p.HasFolded && p.IsAllIn)
                        .Select(p => p.Name)
                        .ToList();

                    if (allInNames.Count > 0)
                    {
                        message = $"{string.Join(", ", allInNames)} All-In. Kartlar avtomatik açılır.";
                    }
                }
            }

            if (!shouldNotify)
                return;

            await Clients.Group(roomId).SendAsync("AllInRunoutStarted", new
            {
                message
            });
        }
        private void CreateOrUpdatePotLevel(PokerRoom room, RoomPlayers allInPlayer, decimal allInAmount)
        {
            // Bütün aktivy oyunçuları tap (all-in olmayan)
            var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded && !p.IsAllIn).ToList();

            if (activePlayers.Count == 0) return;

            // Yeni pot level yarat - bu pot-a kim qatıla bilər
            var potLevel = new PotLevel
            {
                Amount = allInAmount,
                EligiblePlayerIds = new List<int> { allInPlayer.UserId }
            };

            // Digər aktivy oyunçuları da əlavə et
            foreach (var p in activePlayers)
            {
                potLevel.EligiblePlayerIds.Add(p.UserId);
            }

            room.Pots.Add(potLevel);
            Console.WriteLine($"💰 Pot level created: {allInAmount} AZN with {potLevel.EligiblePlayerIds.Count} eligible players");
        }
        public async Task ReBuy(decimal buyInAmount = 0)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("ReBuyError", "İstifadəçi tapılmadı");
                return;
            }
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ReBuyError", "Otaqda deyilsiniz");
                return;
            }
            try
            {
                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                {
                    await Clients.Caller.SendAsync("ReBuyError", "Otaq tapılmadı");
                    return;
                }

                RoomPlayers? player;
                bool isGameActiveSnapshot, shouldLeaveSnapshot;
                decimal chipsSnapshot;
                lock (room.StateLock)
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == userId);
                    isGameActiveSnapshot = room.IsGameActive;
                    shouldLeaveSnapshot = player?.ShouldLeaveAfterHand ?? false;
                    chipsSnapshot = player?.Chips ?? 0;
                }

                if (player == null)
                {
                    await Clients.Caller.SendAsync("ReBuyError", "Oyunçu tapılmadı");
                    return;
                }

                decimal entryFee = room.BigBlind;
                decimal minBuyIn = entryFee * 20;
                decimal maxBuyIn = entryFee * 100;
                decimal minReBuy = minBuyIn;
                decimal maxReBuy = maxBuyIn;

                if (isGameActiveSnapshot)
                {
                    await Clients.Caller.SendAsync("ReBuyError", "❌ Re-buy yalnız əl bitdikdən sonra edilə bilər");
                    return;
                }
                if (shouldLeaveSnapshot)
                {
                    await Clients.Caller.SendAsync("ReBuyError", "❌ Otaqdan çıxan oyunçu re-buy edə bilməz");
                    return;
                }
                if (chipsSnapshot >= minBuyIn)
                {
                    await Clients.Caller.SendAsync("ReBuyError",
                        $"❌ Re-buy üçün stack minimumdan aşağı olmalıdır ({minBuyIn}₼)");
                    return;
                }

                decimal actualBuyIn = (buyInAmount > 0) ? buyInAmount : minReBuy;
                if (actualBuyIn < minReBuy)
                {
                    await Clients.Caller.SendAsync("ReBuyError", $"❌ Minimum re-buy: {minReBuy}₼");
                    return;
                }
                if (actualBuyIn > maxReBuy)
                {
                    await Clients.Caller.SendAsync("ReBuyError", $"❌ Maksimum re-buy: {maxReBuy}₼");
                    return;
                }

                // ✅ IsReBuyPending yoxlaması və set etməsi bir lock-da
                bool reBuyAlreadyPending;
                lock (room.StateLock)
                {
                    reBuyAlreadyPending = player.IsReBuyPending &&
                        player.ReBuyPendingAt.HasValue &&
                        DateTime.UtcNow - player.ReBuyPendingAt.Value < TimeSpan.FromSeconds(REBUY_TIMEOUT_SECONDS);
                    if (!reBuyAlreadyPending)
                    {
                        player.IsReBuyPending = true;
                        player.ReBuyPendingAt = DateTime.UtcNow;
                    }
                }
                if (reBuyAlreadyPending)
                {
                    await Clients.Caller.SendAsync("ReBuyError", "Re-buy artıq emal olunur");
                    return;
                }

                decimal chipsAfterReBuy;
                decimal balanceAfterReBuy;

                try
                {
                    // ✅ Hər dəfə fresh user fetch et — köhnə EF tracking problemi aradan qalxır
                    var freshUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                    if (freshUser == null)
                    {
                        lock (room.StateLock) { player.IsReBuyPending = false; player.ReBuyPendingAt = null; }
                        await Clients.Caller.SendAsync("ReBuyError", "İstifadəçi tapılmadı");
                        return;
                    }

                    if (freshUser.Balance < actualBuyIn)
                    {
                        lock (room.StateLock) { player.IsReBuyPending = false; player.ReBuyPendingAt = null; }
                        await Clients.Caller.SendAsync("ReBuyError",
                            $"❌ Kifayət qədər balans yoxdur (lazım: {actualBuyIn}₼, balans: {freshUser.Balance}₼)");
                        return;
                    }

                    // ✅ RepeatableRead — eyni anda iki re-buy race condition-ını önləyir
                    var strategy = _db.Database.CreateExecutionStrategy();
                    await strategy.ExecuteAsync(async () =>
                    {
                        using var transaction = await _db.Database.BeginTransactionAsync(
                            System.Data.IsolationLevel.RepeatableRead);
                        try
                        {
                            freshUser.Balance -= actualBuyIn;
                            _db.Users.Update(freshUser);
                            await _db.SaveChangesAsync();
                            await transaction.CommitAsync();
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    });
                    // ✅ DB commit uğurlu olduqdan sonra room state yenilə
                    lock (room.StateLock)
                    {
                        player.Chips += actualBuyIn;
                        player.Balance = freshUser.Balance;
                        player.IsInHand = false;
                        player.HasFolded = true;
                        player.IsAllIn = false;
                        player.CurrentBet = 0;
                        player.IsWaitingForNextHand = true;
                        chipsAfterReBuy = player.Chips;
                        balanceAfterReBuy = player.Balance;
                    }
                }
                catch (Exception ex)
                {
                    // ✅ Transaction rollback etdi — əlavə balance düzəlişinə ehtiyac yoxdur
                    lock (room.StateLock)
                    {
                        player.IsReBuyPending = false;
                        player.ReBuyPendingAt = null;
                    }
                    Console.WriteLine($"❌ ReBuy transaction failed: {ex.Message}");
                    await Clients.Caller.SendAsync("ReBuyError", "Re-buy ödənişi alınmadı");
                    return;
                }

                lock (room.StateLock)
                {
                    player.IsReBuyPending = false;
                    player.ReBuyPendingAt = null;
                }

                Console.WriteLine($"✅ {player.Name} re-bought {actualBuyIn}₼ (New chips: {chipsAfterReBuy}₼, Balance: {balanceAfterReBuy}₼)");

                try
                {
                    await Clients.Caller.SendAsync("ReBuySuccess", new
                    {
                        chips = chipsAfterReBuy,
                        balance = balanceAfterReBuy,
                        amount = actualBuyIn,
                        minReBuy,
                        maxReBuy,
                        minBuyIn,
                        maxBuyIn
                    });
                    await Clients.Caller.SendAsync("BalanceUpdated", balanceAfterReBuy);
                    await Clients.Group(roomId).SendAsync("PlayerReBought", player.Name);
                    await BroadcastGameState(roomId);
                }
                catch (Exception notifyEx)
                {
                    Console.WriteLine($"⚠️ ReBuy committed, but notification failed: {notifyEx.Message}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ ReBuy error: {ex.Message}\n{ex.StackTrace}");
                await Clients.Caller.SendAsync("ReBuyError", "Xəta baş verdi");
            }
        }
        private async Task AdvanceStreet(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                Console.WriteLine($"🎴 AdvanceStreet: Current street is '{room.CurrentStreet}'");

                switch (room.CurrentStreet)
                {
                    case "preflop":
                        room.CurrentStreet = "flop";
                        if (room.Deck.Count >= 3)
                        {
                            room.CommunityCards.AddRange(room.Deck.Take(3));
                            room.Deck.RemoveRange(0, 3);
                        }
                        room.CurrentPlayerIndex = (room.DealerIndex + 1) % room.Players.Count;
                        int attempts = 0;
                        while ((room.Players[room.CurrentPlayerIndex].HasFolded || !room.Players[room.CurrentPlayerIndex].IsInHand)
                               && attempts < room.Players.Count)
                        {
                            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % room.Players.Count;
                            attempts++;
                        }
                        room.FirstPlayerOfRound = room.CurrentPlayerIndex;
                        Console.WriteLine($"🎲 Flop - First to act: {room.Players[room.CurrentPlayerIndex].Name}");
                        break;

                    case "flop":
                        room.CurrentStreet = "turn";
                        if (room.Deck.Count >= 1)
                        {
                            room.CommunityCards.Add(room.Deck[0]);
                            room.Deck.RemoveAt(0);
                        }
                        room.CurrentPlayerIndex = (room.DealerIndex + 1) % room.Players.Count;
                        attempts = 0;
                        while ((room.Players[room.CurrentPlayerIndex].HasFolded || !room.Players[room.CurrentPlayerIndex].IsInHand)
                               && attempts < room.Players.Count)
                        {
                            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % room.Players.Count;
                            attempts++;
                        }
                        room.FirstPlayerOfRound = room.CurrentPlayerIndex;
                        Console.WriteLine($"🎲 Turn - First to act: {room.Players[room.CurrentPlayerIndex].Name}");
                        break;

                    case "turn":
                        room.CurrentStreet = "river";
                        if (room.Deck.Count >= 1)
                        {
                            room.CommunityCards.Add(room.Deck[0]);
                            room.Deck.RemoveAt(0);
                        }
                        room.CurrentPlayerIndex = (room.DealerIndex + 1) % room.Players.Count;
                        attempts = 0;
                        while ((room.Players[room.CurrentPlayerIndex].HasFolded || !room.Players[room.CurrentPlayerIndex].IsInHand)
                               && attempts < room.Players.Count)
                        {
                            room.CurrentPlayerIndex = (room.CurrentPlayerIndex + 1) % room.Players.Count;
                            attempts++;
                        }
                        room.FirstPlayerOfRound = room.CurrentPlayerIndex;
                        Console.WriteLine($"🎲 River - First to act: {room.Players[room.CurrentPlayerIndex].Name}");
                        break;

                    case "river":
                        Console.WriteLine("⚠️ Already on river!");
                        break;
                }

                room.HasAllInThisStreet = false;
                room.ResetBetsForNewStreet();
            }

            // 🔥 AŞAMA 1: Kartları göster
            await Clients.Group(roomId).SendAsync("StreetAdvanced", new
            {
                street = room.CurrentStreet,
                communityCards = room.CommunityCards
            });

            Console.WriteLine($"📤 StreetAdvanced event sent. Frontend animating cards for 2 seconds");

            // 🔥 AŞAMA 2: Kartlar 2 saniye gösterilir, düymeler deaktif
            await Task.Delay(2000);

            // 🔥 AŞAMA 3: Game state göndər
            await BroadcastGameState(roomId);
            Console.WriteLine($"📊 GameState broadcast sent");

            // 🔥 AŞAMA 4: Düymeler deaktif et (animasyon süresi)
            await Clients.Group(roomId).SendAsync("DisableActionsFor", 2);
            Console.WriteLine($"🚫 DisableActionsFor event sent - buttons disabled for 2 seconds");

            // 🔥 AŞAMA 5: 800ms daha bekle (animasyon bitsin)
            await Task.Delay(800);

            if (room.IsGameActive && room.IsAllInRunoutReady())
            {
                StopTurnTimer(roomId);
                await NotifyAllInRunoutStartedIfNeeded(roomId, room);
                Console.WriteLine($"🔥 All-in runout continues automatically on {room.CurrentStreet}");

                if (room.CurrentStreet == "river")
                {
                    await Task.Delay(500);
                    await DetermineWinner(roomId);
                }
                else
                {
                    await AdvanceStreet(roomId);
                }

                return;
            }

            RoomPlayers? nextActionPlayer = null;
            bool noActionReadyPlayer = false;

            lock (room.StateLock)
            {
                if (room.IsGameActive && room.CurrentPlayerIndex >= 0 && room.CurrentPlayerIndex < room.Players.Count)
                {
                    if (!room.CanPlayerAct(room.Players[room.CurrentPlayerIndex]))
                    {
                        room.MoveToNextActivePlayer();
                    }

                    if (room.CurrentPlayerIndex >= 0 &&
                        room.CurrentPlayerIndex < room.Players.Count &&
                        room.CanPlayerAct(room.Players[room.CurrentPlayerIndex]))
                    {
                        nextActionPlayer = room.Players[room.CurrentPlayerIndex];
                    }
                    else
                    {
                        noActionReadyPlayer = true;
                    }
                }
            }

            if (noActionReadyPlayer)
            {
                StopTurnTimer(roomId);
                await NotifyAllInRunoutStartedIfNeeded(roomId, room);
                Console.WriteLine($"⚠️ No action-ready player after {room.CurrentStreet} - continuing automatically");

                if (room.CurrentStreet == "river")
                {
                    await Task.Delay(500);
                    await DetermineWinner(roomId);
                }
                else
                {
                    await AdvanceStreet(roomId);
                }

                return;
            }

            // 🔥 AŞAMA 6: Timer başlat (düymeler henüz deaktif)
            if (nextActionPlayer != null)
            {
                StartTurnTimer(roomId, nextActionPlayer.UserId);
                Console.WriteLine($"⏱️ Turn timer started for {nextActionPlayer.Name}");
            }

            // ✅ ƏLAVƏ ET - Timer başladıqdan sonra TurnStartTime təyin edilib, indi broadcast et
            await Task.Delay(100);
            await BroadcastGameState(roomId);
            Console.WriteLine($"📊 GameState re-broadcast with timer");

            // 🔥 AŞAMA 7: Düymeler 1.2 saniye sonra aktif (toplam 2 saniye deaktif)
            await Task.Delay(1100); // 1200 - 100 = 1100 (toplam vaxt eyni qalır)
            Console.WriteLine($"✅ Actions should now be enabled by frontend");
            lock (room.StateLock)
            {
                var activePlayers = room.Players.Where(p => p.IsInHand && !p.HasFolded).ToList();

                if (activePlayers.Count == 1)
                {
                    StopTurnTimer(roomId);
                    Console.WriteLine($"🏆 Only 1 player left after advancing street!");

                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(1000);
                        await DetermineWinner(roomId);
                    });
                }
            }
        }
        public async Task SendQuickMessage(string messageType, string targetPlayerName = "")
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var userId = GetUserId();
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            await Clients.Group(roomId).SendAsync("QuickMessage", new
            {
                senderName = player.Name,
                messageType,
                targetPlayerName
            });
        }
        private async Task DetermineWinner(string roomId)
        {
            StopTurnTimer(roomId);

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (!room.IsGameActive)
                {
                    Console.WriteLine($"⚠️ Game already ended for room {roomId}");
                    return;
                }
                room.IsGameActive = false;
            }

            var trueActivePlayers = room.Players
                .Where(p => p.IsInHand && !p.HasFolded)
                .ToList();

            // ✅ FOLD YOLU
            if (trueActivePlayers.Count == 1)
            {
                var winner = trueActivePlayers[0];
                decimal commission = room.Pot * COMMISSION_RATE;
                decimal netPot = room.Pot - commission;

                Console.WriteLine($"🏆 {winner.Name} wins by default (fold). Net: {netPot}₼");

                winner.Chips += netPot;

                var updatedUser = await _db.Users.AsNoTracking()
                    .FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (updatedUser != null)
                    await _hubContext.Clients.Client(winner.ConnectionId)
                        .SendAsync("BalanceUpdated", updatedUser.Balance);

                try
                {
                    await _service.UpdateRankAfterGame(
                        winner.UserId, GameType.Poker, isWin: true, earnings: netPot);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }

                foreach (var loser in room.Players.Where(p =>
                             p.UserId != winner.UserId &&
                             p.IsInHand &&
                             p.ContributedToPot > 0))
                {
                    try
                    {
                        await _service.UpdateRankAfterGame(
                            loser.UserId, GameType.Poker,
                            isWin: false,
                            earnings: loser.ContributedToPot);
                    }
                    catch (Exception rankEx)
                    {
                        Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                    }
                }

                await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winner = winner.Name,
                    amount = netPot,
                    commission,
                    handName = "Qalib (fold)",
                    winnerCount = 1,
                    totalPot = room.Pot,
                    reason = "Others folded"
                });

                await Task.Delay(3000);
                await _hubContext.Clients.Group(roomId).SendAsync("HideShowdownCards");
                await ResetHand(roomId); // ← YALNIZ BİR DƏFƏ
                return;
            }

            // ✅ EDGE CASE
            if (trueActivePlayers.Count == 0)
            {
                Console.WriteLine($"❌ No active players - resetting");
                await ResetHand(roomId);
                return;
            }

            // ✅ SHOWDOWN YOLU
            var activePlayers = trueActivePlayers
                .Where(p => p.HoleCards.Count == 2)
                .ToList();

            if (activePlayers.Count == 0)
            {
                Console.WriteLine($"❌ No players with cards - resetting");
                await ResetHand(roomId);
                return;
            }

            try
            {
                decimal totalPot = room.Pot;
                decimal commission = totalPot * COMMISSION_RATE;
                decimal netPot = totalPot - commission;

                Console.WriteLine($"💰 WINNER DETERMINATION");
                Console.WriteLine($"   Total: {totalPot}₼ | Commission: {commission}₼ | Net: {netPot}₼");
                Console.WriteLine($"   Players: {activePlayers.Count}");

                var engine = new PokerHandEvaluator();
                var playerHands = new List<(RoomPlayers player, int rank, string handName, List<string> bestCards)>();
                var allPlayerHands = new List<object>();

                foreach (var player in activePlayers)
                {
                    var fullHand = player.HoleCards.Concat(room.CommunityCards).ToList();
                    if (fullHand.Count < 5) continue;

                    var evaluation = engine.Evaluate(fullHand);
                    playerHands.Add((player, evaluation.Rank, evaluation.HandName, evaluation.BestCards));
                    Console.WriteLine($"🎴 {player.Name}: {evaluation.HandName} (Rank: {evaluation.Rank})");
                }

                if (playerHands.Count == 0)
                {
                    Console.WriteLine($"⚠️ No valid hands - resetting");
                    await ResetHand(roomId);
                    return;
                }

                int showdownBestRank = playerHands.Max(ph => ph.rank);
                var showdownWinnerIds = playerHands
                    .Where(ph => ph.rank == showdownBestRank)
                    .Select(ph => ph.player.UserId)
                    .ToHashSet();

                foreach (var ph in playerHands)
                {
                    allPlayerHands.Add(new
                    {
                        playerName = ph.player.Name,
                        cards = ph.player.HoleCards,
                        handName = ph.handName,
                        handRank = ph.rank,
                        bestCards = ph.bestCards,
                        isWinner = showdownWinnerIds.Contains(ph.player.UserId),
                        isAllIn = ph.player.IsAllIn
                    });
                }

                await _hubContext.Clients.Group(roomId).SendAsync("ShowdownCards", allPlayerHands);
                await Task.Delay(1000);

                // ✅ Distribute metodları ResetHand ÇAĞIRMAMALIDIR
                if (HasAllInPlayers(activePlayers))
                    await DistributeAllInPots(roomId, room, playerHands, netPot);
                else if (activePlayers.Count > 1)
                    await DistributeSplitPot(roomId, room, playerHands, netPot);
                else
                    await DistributeSingleWinner(roomId, room, playerHands, netPot);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ DetermineWinner error: {ex.Message}");
                Console.WriteLine($"   Stack: {ex.StackTrace}");
                await _hubContext.Clients.Group(roomId)
                    .SendAsync("GameError", "Qalib müəyyən edilərkən xəta");
            }

            // ✅ SHOWDOWN YOLUNDA YALNIZ BİR DƏFƏ
            await Task.Delay(3000);
            await _hubContext.Clients.Group(roomId).SendAsync("HideShowdownCards");
            await ResetHand(roomId);
        }
        private bool HasAllInPlayers(List<RoomPlayers> activePlayers)
        {
            return activePlayers.Any(p => p.IsAllIn);
        }
        private async Task DistributeSingleWinner(string roomId, PokerRoom room,
        List<(RoomPlayers player, int rank, string handName, List<string> bestCards)> playerHands,
        decimal netPot)
        {
            int bestHandRank = playerHands.Max(ph => ph.rank);
            var winners = playerHands.Where(ph => ph.rank == bestHandRank).ToList();

            Console.WriteLine($"🏆 SINGLE WINNER: {winners[0].player.Name}");

            var winner = winners[0].player;
            winner.Chips += netPot;

            var updatedUser = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == winner.UserId);
            if (updatedUser != null)
            {
                // ✅ _hubContext
                await _hubContext.Clients.Client(winner.ConnectionId)
                    .SendAsync("BalanceUpdated", updatedUser.Balance);
            }

            try
            {
                await _service.UpdateRankAfterGame(
                    winner.UserId, GameType.Poker, isWin: true, earnings: netPot);
            }
            catch (Exception rankEx)
            {
                Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
            }

            var losers = room.Players
                .Where(p => p.UserId != winner.UserId && p.IsInHand && p.ContributedToPot > 0)
                .ToList();
            foreach (var loser in losers)
            {
                try
                {
                    // ✅ real itki məbləği
                    await _service.UpdateRankAfterGame(
                        loser.UserId, GameType.Poker,
                        isWin: false,
                        earnings: loser.ContributedToPot);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            // ✅ _hubContext
            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winner.Name,
                amount = netPot,
                commission = room.Pot * COMMISSION_RATE,
                handName = winners[0].handName,
                winnerCount = 1,
                totalPot = room.Pot
            });

        }
        private async Task DistributeSplitPot(string roomId, PokerRoom room,
    List<(RoomPlayers player, int rank, string handName, List<string> bestCards)> playerHands,
    decimal netPot)
        {
            int bestHandRank = playerHands.Max(ph => ph.rank);
            var winners = playerHands.Where(ph => ph.rank == bestHandRank).ToList();

            Console.WriteLine($"🏆 SPLIT POT: {winners.Count} winners");

            if (winners.Count == 1)
            {
                // Tek qalib (previous logic'e dönersin)
                await DistributeSingleWinner(roomId, room, playerHands, netPot);
                return;
            }

            // ✅ POT'U BÖLÜŞ (NET POT'U BÖL)
            decimal splitAmount = netPot / winners.Count;
            decimal commission = room.Pot * COMMISSION_RATE;

            Console.WriteLine($"💰 NET POT'U BÖLÜŞ: {netPot}₼ ÷ {winners.Count} = {splitAmount}₼ per winner");
            Console.WriteLine($"   Commission: {commission}₼");

            foreach (var winnerInfo in winners)
            {
                winnerInfo.player.Chips += splitAmount;
                Console.WriteLine($"✅ {winnerInfo.player.Name}: +{splitAmount}₼ table chips");
            }

            // Balance güncelle
            foreach (var winnerInfo in winners)
            {
                var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == winnerInfo.player.UserId);
                if (user != null)
                {
                    await Clients.Client(winnerInfo.player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                }
            }

            // Rank güncelle
            foreach (var winnerInfo in winners)
            {
                try
                {
                    await _service.UpdateRankAfterGame(winnerInfo.player.UserId, GameType.Poker, isWin: true, earnings: splitAmount);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            var losers = room.Players
                .Where(p => !winners.Any(w => w.player.UserId == p.UserId) &&
                            p.IsInHand &&
                            p.ContributedToPot > 0)
                .ToList();
            foreach (var loser in losers)
            {
                try
                {
                    await _service.UpdateRankAfterGame(
                        loser.UserId,
                        GameType.Poker,
                        isWin: false,
                        earnings: loser.ContributedToPot);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            string winnerNames = string.Join(", ", winners.Select(w => w.player.Name));
            await Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winnerNames,
                amount = splitAmount,
                commission = commission,
                handName = $"Bərabərlik - {winners[0].handName}",
                winnerCount = winners.Count,
                totalDistributed = netPot,
                totalPot = room.Pot
            });
        }
        private async Task DistributeAllInPots(string roomId, PokerRoom room,
    List<(RoomPlayers player, int rank, string handName, List<string> bestCards)> playerHands,
    decimal netPot)
        {
            Console.WriteLine($"🎲 ALL-IN POT DISTRIBUTION STARTED");

            var contributors = room.Players
                .Where(p => p.ContributedToPot > 0)
                .ToList();

            if (contributors.Count == 0)
            {
                await DistributeSplitPot(roomId, room, playerHands, netPot);
                return;
            }

            Dictionary<int, decimal> playerWinnings = new();
            foreach (var player in room.Players)
            {
                playerWinnings[player.UserId] = 0;
            }

            var contributionLevels = contributors
                .Select(p => p.ContributedToPot)
                .Distinct()
                .OrderBy(amount => amount)
                .ToList();

            decimal previousAmount = 0;

            foreach (var levelAmount in contributionLevels)
            {
                decimal grossPotForLevel = (levelAmount - previousAmount) *
                    contributors.Count(p => p.ContributedToPot >= levelAmount);

                if (grossPotForLevel <= 0)
                {
                    previousAmount = levelAmount;
                    continue;
                }

                decimal netPotForLevel = grossPotForLevel * (1 - COMMISSION_RATE);

                var eligibleHands = playerHands
                    .Where(ph => ph.player.ContributedToPot >= levelAmount &&
                                 ph.player.IsInHand &&
                                 !ph.player.HasFolded)
                    .ToList();

                Console.WriteLine($"\n🔸 SIDE POT LEVEL {levelAmount}₼");
                Console.WriteLine($"   Gross: {grossPotForLevel}₼ | Net: {netPotForLevel}₼ | Eligible: {eligibleHands.Count}");

                if (eligibleHands.Count == 0)
                {
                    previousAmount = levelAmount;
                    continue;
                }

                int bestRank = eligibleHands.Max(eh => eh.rank);
                var levelWinners = eligibleHands.Where(eh => eh.rank == bestRank).ToList();
                decimal winningShare = netPotForLevel / levelWinners.Count;

                foreach (var winner in levelWinners)
                {
                    playerWinnings[winner.player.UserId] += winningShare;
                    Console.WriteLine($"   ✅ {winner.player.Name}: +{winningShare}₼ table chips");
                }

                previousAmount = levelAmount;
            }

            foreach (var userId in playerWinnings.Keys)
            {
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) continue;

                var winnings = playerWinnings[userId];
                if (winnings > 0)
                {
                    player.Chips += winnings;
                }

                var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (user != null)
                {
                    await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                }
            }

            // Rank güncelle
            foreach (var userId in playerWinnings.Keys)
            {
                decimal winnings = playerWinnings[userId];
                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || (winnings <= 0 && player.ContributedToPot <= 0))
                    continue;

                try
                {
                    decimal rankAmount = winnings > 0
                        ? winnings
                        : player?.ContributedToPot ?? 0;

                    await _service.UpdateRankAfterGame(userId, GameType.Poker,
                        isWin: winnings > 0, earnings: rankAmount);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            var winningPlayers = playerWinnings
                .Where(kv => kv.Value > 0)
                .Select(kv =>
                {
                    var player = room.Players.FirstOrDefault(p => p.UserId == kv.Key);
                    return new
                    {
                        userId = kv.Key,
                        name = player?.Name ?? $"Player {kv.Key}",
                        amount = kv.Value
                    };
                })
                .ToList();

            await Clients.Group(roomId).SendAsync("GameOver", new
            {
                winner = winningPlayers.Count > 0
                    ? string.Join(", ", winningPlayers.Select(p => p.name))
                    : "Multiple (All-in pots)",
                amount = winningPlayers.Count == 1 ? winningPlayers[0].amount : netPot,
                commission = room.Pot * COMMISSION_RATE,
                handName = "All-in pot paylanması",
                potDistribution = winningPlayers,
                totalDistributed = netPot,
                totalPot = room.Pot
            });
        }
        private async Task DistributeMultiWayPots(string roomId, PokerRoom room,
         List<(RoomPlayers player, int rank, string handName, List<string> bestCards)> playerHands)
        {
            decimal totalPot = room.Pot;
            decimal commission = totalPot * COMMISSION_RATE;
            decimal netPot = totalPot - commission;

            Console.WriteLine($"🎲 Distributing {playerHands.Count} players across {room.Pots.Count} pot levels");

            Dictionary<int, decimal> playerWinnings = new();
            foreach (var player in room.Players)
            {
                playerWinnings[player.UserId] = 0;
            }

            // ✅ Hər pot level-i xüsusi şəkildə tut
            foreach (var pot in room.Pots)
            {
                Console.WriteLine($"💰 Processing pot with {pot.EligiblePlayerIds.Count} eligible players");

                // Bu pot-a hansı oyunçular qatıla bilər
                var eligibleForThisPot = playerHands.Where(ph => pot.EligiblePlayerIds.Contains(ph.player.UserId)).ToList();

                if (eligibleForThisPot.Count == 0) continue;

                // Bu pot-da ən yaxşı əli tapşır
                int bestHandRank = eligibleForThisPot.Max(ph => ph.rank);
                var winnersOfThisPot = eligibleForThisPot.Where(ph => ph.rank == bestHandRank).ToList();

                if (winnersOfThisPot.Count > 0)
                {
                    decimal potShare = pot.Amount / winnersOfThisPot.Count;

                    foreach (var winner in winnersOfThisPot)
                    {
                        playerWinnings[winner.player.UserId] += potShare;
                        Console.WriteLine($"✅ {winner.player.Name} wins {potShare} from side pot ({winner.handName})");
                    }
                }
            }

            // ✅ Main pot (bütün oyunçular qatıla biləcək)
            var mainPotWinners = playerHands.OrderByDescending(ph => ph.rank).Take(1).ToList();
            if (mainPotWinners.Count > 0)
            {
                // Bütün qatılmamış pot
                decimal mainPot = totalPot;
                foreach (var pot in room.Pots)
                {
                    mainPot -= pot.Amount;
                }

                if (mainPot > 0)
                {
                    var bestRank = mainPotWinners[0].rank;
                    var mainWinners = playerHands.Where(ph => ph.rank == bestRank).ToList();

                    foreach (var winner in mainWinners)
                    {
                        playerWinnings[winner.player.UserId] += mainPot / mainWinners.Count;
                    }
                }
            }

            // 🔥 ÖDƏMƏNI İCRA ET
            foreach (var userId in playerWinnings.Keys)
            {
                decimal winnings = playerWinnings[userId];
                if (winnings <= 0) continue;

                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) continue;

                player.Chips += winnings;

                var updatedUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId);
                if (updatedUser != null)
                {
                    await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", updatedUser.Balance);
                }

                try
                {
                    await _service.UpdateRankAfterGame(userId, GameType.Poker, isWin: true, earnings: winnings);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            var losers = room.Players
                .Where(p => playerWinnings.GetValueOrDefault(p.UserId, 0) == 0 &&
                            p.IsInHand &&
                            p.ContributedToPot > 0)
                .ToList();
            foreach (var loser in losers)
            {
                try
                {
                    await _service.UpdateRankAfterGame(
                        loser.UserId,
                        GameType.Poker,
                        isWin: false,
                        earnings: loser.ContributedToPot);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }
            }

            await Clients.Group(roomId).SendAsync("GameOver", new
            {
                message = "Multi-way pot distributed",
                winnings = playerWinnings,
                commission = commission
            });
        }

        private async Task DistributeSinglePot(string roomId, PokerRoom room,
    List<(RoomPlayers player, int rank, string handName, List<string> bestCards)> playerHands)
        {
            decimal totalPot = room.Pot;
            decimal commission = totalPot * COMMISSION_RATE;
            decimal netPot = totalPot - commission;

            int bestHandRank = playerHands.Max(ph => ph.rank);
            var winners = playerHands.Where(ph => ph.rank == bestHandRank).ToList();

            Console.WriteLine($"🏆 {winners.Count} winner(s) with best hand");

            if (winners.Count == 1)
            {
                var winner = winners[0].player;
                winner.Chips += netPot;

                var updatedUser = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (updatedUser != null)
                {
                    await Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", updatedUser.Balance);
                }

                try
                {
                    await _service.UpdateRankAfterGame(winner.UserId, GameType.Poker, isWin: true, earnings: netPot);
                }
                catch (Exception rankEx)
                {
                    Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                }

                var losers = room.Players
                    .Where(p => p.UserId != winner.UserId && p.IsInHand && p.ContributedToPot > 0)
                    .ToList();
                foreach (var loser in losers)
                {
                    try
                    {
                        await _service.UpdateRankAfterGame(
                            loser.UserId,
                            GameType.Poker,
                            isWin: false,
                            earnings: loser.ContributedToPot);
                    }
                    catch (Exception rankEx)
                    {
                        Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                    }
                }

                await Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winner = winner.Name,
                    amount = netPot,
                    commission = commission,
                    handName = winners[0].handName
                });
            }
            else
            {
                // ✅ Bərabərlik - pul bölünür
                decimal splitAmount = netPot / winners.Count;

                Console.WriteLine($"💰 Split pot: {splitAmount} AZN per winner");

                foreach (var winnerInfo in winners)
                {
                    winnerInfo.player.Chips += splitAmount;
                }

                foreach (var winnerInfo in winners)
                {
                    var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == winnerInfo.player.UserId);
                    if (user != null)
                    {
                        await Clients.Client(winnerInfo.player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                    }
                }

                foreach (var winnerInfo in winners)
                {
                    try
                    {
                        await _service.UpdateRankAfterGame(winnerInfo.player.UserId, GameType.Poker, isWin: true, earnings: splitAmount);
                    }
                    catch (Exception rankEx)
                    {
                        Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                    }
                }

                var losers = room.Players
                    .Where(p => !winners.Any(w => w.player.UserId == p.UserId) &&
                                p.IsInHand &&
                                p.ContributedToPot > 0)
                    .ToList();
                foreach (var loser in losers)
                {
                    try
                    {
                        await _service.UpdateRankAfterGame(
                            loser.UserId,
                            GameType.Poker,
                            isWin: false,
                            earnings: loser.ContributedToPot);
                    }
                    catch (Exception rankEx)
                    {
                        Console.WriteLine($"❌ Rank update error: {rankEx.Message}");
                    }
                }

                string winnerNames = string.Join(", ", winners.Select(w => w.player.Name));
                await Clients.Group(roomId).SendAsync("GameOver", new
                {
                    winner = winnerNames,
                    amount = splitAmount,
                    commission = commission,
                    handName = $"Bərabərlik - {winners[0].handName}"
                });
            }
        }
        private async Task ResetHand(string roomId)
        {
            StopTurnTimer(roomId);
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                var playersLeavingAfterHand = room.Players.Where(p => p.ShouldLeaveAfterHand).ToList();
                foreach (var leavingPlayer in playersLeavingAfterHand)
                {
                    room.Players.Remove(leavingPlayer);
                    _userRooms.TryRemove(leavingPlayer.ConnectionId, out _);
                    Console.WriteLine($"🧹 Removed after hand: {leavingPlayer.Name}");
                }
            }

            await AddWaitingPlayersToNextHand(roomId);

            List<RoomPlayers> playersWithNoChips;
            lock (room.StateLock)
            {
                playersWithNoChips = room.Players.Where(p => p.Chips <= 0).ToList();
                foreach (var player in playersWithNoChips)
                {
                    player.IsInHand = false;
                    player.HasFolded = true;
                    player.IsAllIn = false;
                    player.CurrentBet = 0;
                    player.HoleCards.Clear();
                }
            }

            if (playersWithNoChips.Any())
            {
                Console.WriteLine($"💰 {playersWithNoChips.Count} oyunçu re-buy gözləyir");

                foreach (var player in playersWithNoChips)
                {
                    var user = await _db.Users.AsNoTracking()
                        .FirstOrDefaultAsync(u => u.Id == player.UserId);
                    if (user != null)
                    {
                        // ✅ _hubContext istifadə et
                        await _hubContext.Clients.Client(player.ConnectionId)
                            .SendAsync("BalanceUpdated", user.Balance);
                        await _hubContext.Clients.Client(player.ConnectionId)
                            .SendAsync("ShowReBuyOption", REBUY_TIMEOUT_SECONDS);
                        Console.WriteLine($"💰 {player.Name} - Balance: {user.Balance} AZN");
                    }
                }

                // ✅ _hubContext istifadə et
                await _hubContext.Clients.Group(roomId).SendAsync("ReBuyCountdown", new
                {
                    duration = REBUY_TIMEOUT_SECONDS,
                    playersWaiting = playersWithNoChips.Select(p => p.Name).ToList()
                });

                await Task.Delay(TimeSpan.FromSeconds(REBUY_TIMEOUT_SECONDS));

                lock (room.StateLock)
                {
                    var stillNoChips = room.Players
                        .Where(p => p.Chips <= 0 && !p.IsReBuyPending)
                        .ToList();
                    foreach (var player in stillNoChips)
                    {
                        room.Players.Remove(player);
                        _userRooms.TryRemove(player.ConnectionId, out _);

                        // ✅ _hubContext istifadə et (lock içindən fire-and-forget)
                        _ = _hubContext.Clients.Client(player.ConnectionId)
                            .SendAsync("KickedFromRoom", "Çipləriniz bitdi və vaxtında re-buy etmədiniz");
                        _ = _hubContext.Clients.Group(roomId)
                            .SendAsync("PlayerKicked", player.Name);

                        Console.WriteLine($"❌ {player.Name} otaqdan çıxarıldı (no re-buy)");
                    }
                }
            }

            await StartHandPauseTimeout(roomId);
        }

        private async Task CompleteResetHandAfterPause(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (room.Players.Count == 0)
            {
                _roomManager.DeleteRoom(roomId);
                await _hubContext.Clients.All.SendAsync("RoomDeleted", roomId);
                return;
            }

            if (room.Players.Count(p => p.Chips > 0 && !p.IsPausedAfterHand) < 2)
            {
                await _hubContext.Clients.Group(roomId).SendAsync("WaitingForPlayers",
                    "Oyuna davam etmək üçün ən azı 2 aktiv oyunçu lazımdır");
                lock (room.StateLock)
                {
                    room.ResetForNewHand();
                }
                return;
            }

            lock (room.StateLock)
            {
                room.ResetForNewHand();
            }

            await _hubContext.Clients.Group(roomId).SendAsync("HandReset");
            await BroadcastGameStateBackground(roomId, _hubContext); // ← Background versiyonu
            await Task.Delay(3000);

            var currentRoom = _roomManager.GetRoom(roomId);
            if (currentRoom != null)
            {
                bool shouldStartNewGame = false;
                lock (currentRoom.StateLock)
                {
                    var playersWithChips = currentRoom.Players.Where(p => p.Chips > 0 && !p.IsPausedAfterHand).ToList();
                    shouldStartNewGame = playersWithChips.Count >= 2 && !currentRoom.IsGameActive;
                    Console.WriteLine($"🎮 Check auto-start: {playersWithChips.Count} players with chips");
                }

                if (shouldStartNewGame)
                {
                    await AutoStartGameBackground(roomId, _hubContext);
                }
                else
                {
                    // ✅ _hubContext istifadə et
                    await _hubContext.Clients.Group(roomId).SendAsync("WaitingForPlayers",
                        "Oyuna başlamaq üçün ən azı 2 oyunçuda çip olmalıdır");
                }
            }
        }

        private async Task StartHandPauseTimeout(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (!_handPauseActiveRooms.TryAdd(roomId, 1))
            {
                Console.WriteLine($"⏸️ Poker hand pause already active: {roomId}");
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");
            List<(int UserId, string ConnectionId, string Name)> targetPlayers;

            lock (room.StateLock)
            {
                room.TurnStartTime = null;
                room.IsGameActive = false;

                targetPlayers = room.Players
                    .Where(p => p.Chips > 0 && (!p.IsWaitingForNextHand || p.IsPausedAfterHand))
                    .Select(p => (p.UserId, p.ConnectionId, p.Name))
                    .ToList();

                foreach (var player in room.Players.Where(p => targetPlayers.Any(t => t.UserId == p.UserId)))
                {
                    player.HandPauseChoice = PokerHandPauseChoice.None;
                    player.HandPauseDecisionAt = null;
                }
            }

            if (targetPlayers.Count == 0)
            {
                _handPauseActiveRooms.TryRemove(roomId, out _);
                await CompleteResetHandAfterPause(roomId);
                return;
            }

            _handPauseResponses.TryRemove(roomId, out _);
            var responseMap = new ConcurrentDictionary<int, byte>();
            _handPauseResponses[roomId] = responseMap;
            _handPauseSessionIds[roomId] = sessionId;

            foreach (var targetPlayer in targetPlayers)
            {
                await _hubContext.Clients.Client(targetPlayer.ConnectionId).SendAsync("HandPausePrompt", new
                {
                    message = "Əl bitdi. Davam edəcəksiniz, yoxsa 1 əl timeout?",
                    timeoutSeconds = HAND_PAUSE_TIMEOUT_SECONDS,
                    sessionId
                });
            }

            for (int remaining = HAND_PAUSE_TIMEOUT_SECONDS; remaining > 0; remaining--)
            {
                if (!_handPauseActiveRooms.ContainsKey(roomId))
                {
                    return;
                }

                var waitingConnectionIds = targetPlayers
                    .Where(p => !_handPauseResponses.TryGetValue(roomId, out var responses) ||
                                !responses.ContainsKey(p.UserId))
                    .Select(p => p.ConnectionId)
                    .ToArray();

                if (waitingConnectionIds.Length > 0)
                {
                    await _hubContext.Clients.Clients(waitingConnectionIds).SendAsync("HandPauseTimer", new
                    {
                        remainingSeconds = remaining
                    });
                }

                await Task.Delay(1000);

                if (_handPauseResponses.TryGetValue(roomId, out var responses) &&
                    targetPlayers.All(p => responses.ContainsKey(p.UserId)))
                {
                    break;
                }
            }

            await FinalizeHandPauseTimeout(roomId, targetPlayers);
        }
        private async Task FinalizeHandPauseTimeout(string roomId, List<(int UserId, string ConnectionId, string Name)> targetPlayers)
        {
            if (!_handPauseActiveRooms.ContainsKey(roomId))
                return;

            await Task.Delay(HAND_PAUSE_FINALIZE_GRACE_MS);

            if (!_handPauseActiveRooms.TryRemove(roomId, out _))
                return;

            _handPauseResponses.TryRemove(roomId, out var responses);
            _handPauseSessionIds.TryRemove(roomId, out var activeSessionId);
            responses ??= new ConcurrentDictionary<int, byte>();

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<(int UserId, string ConnectionId, string Name, decimal Chips)> removedTimedOutPlayers;
            List<string> pausedPlayers;

            lock (room.StateLock)
            {
                removedTimedOutPlayers = new List<(int UserId, string ConnectionId, string Name, decimal Chips)>();

                foreach (var timedOutPlayer in targetPlayers.Where(p => !responses.ContainsKey(p.UserId)))
                {
                    var player = room.Players.FirstOrDefault(p => p.UserId == timedOutPlayer.UserId);
                    if (player == null || player.ConnectionId != timedOutPlayer.ConnectionId)
                        continue;

                    player.HasFolded = true;
                    player.IsInHand = false;
                    room.Players.Remove(player);
                    _userRooms.TryRemove(timedOutPlayer.ConnectionId, out _);
                    removedTimedOutPlayers.Add((timedOutPlayer.UserId, timedOutPlayer.ConnectionId, timedOutPlayer.Name, player.Chips));
                }

                pausedPlayers = room.Players
                    .Where(p => p.IsPausedAfterHand)
                    .Select(p => p.Name)
                    .ToList();
            }

            await _hubContext.Clients.Group(roomId).SendAsync("HandPauseExpired", new
            {
                timeoutSeconds = HAND_PAUSE_TIMEOUT_SECONDS,
                pausedPlayers,
                timedOutPlayers = removedTimedOutPlayers.Select(p => p.Name).ToArray()
            });

            // ✅ CreateExecutionStrategy ilə transaction
            foreach (var timedOutPlayer in removedTimedOutPlayers)
            {
                if (timedOutPlayer.Chips > 0)
                {
                    var chipsToReturn = timedOutPlayer.Chips;
                    var strategy = _db.Database.CreateExecutionStrategy();
                    await strategy.ExecuteAsync(async () =>
                    {
                        using var transaction = await _db.Database.BeginTransactionAsync();
                        try
                        {
                            var freshUser = await _db.Users.FirstOrDefaultAsync(u => u.Id == timedOutPlayer.UserId);
                            if (freshUser != null)
                            {
                                freshUser.Balance += chipsToReturn;
                                _db.Users.Update(freshUser);
                                await _db.SaveChangesAsync();
                                await transaction.CommitAsync();
                                await _hubContext.Clients.Client(timedOutPlayer.ConnectionId)
                                    .SendAsync("BalanceUpdated", freshUser.Balance);
                            }
                        }
                        catch
                        {
                            await transaction.RollbackAsync();
                            throw;
                        }
                    });
                }

                await _hubContext.Clients.Client(timedOutPlayer.ConnectionId).SendAsync("KickedFromRoom",
                    "15 saniyə ərzində seçim etmədiniz. Lobby-yə yönləndirilirsiniz...");
                await Groups.RemoveFromGroupAsync(timedOutPlayer.ConnectionId, roomId);
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", timedOutPlayer.Name);
            }

            await CompleteResetHandAfterPause(roomId);
        }
        public async Task HandPauseDecision(bool continuePlaying, string? sessionId = null)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var roomId = GetCurrentRoom();
            if (string.IsNullOrWhiteSpace(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Otaq tapılmadı");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (!_handPauseActiveRooms.ContainsKey(roomId))
            {
                await Clients.Caller.SendAsync("ActionError", "Pauza aktiv deyil");
                return;
            }

            if (_handPauseSessionIds.TryGetValue(roomId, out var activeSessionId) &&
                (string.IsNullOrWhiteSpace(sessionId) || !string.Equals(activeSessionId, sessionId, StringComparison.Ordinal)))
            {
                await Clients.Caller.SendAsync("ActionError", "Köhnə pauza pəncərəsi");
                return;
            }

            RoomPlayers? currentPlayer;
            lock (room.StateLock)
            {
                currentPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (currentPlayer != null && currentPlayer.ConnectionId != Context.ConnectionId)
                {
                    _userRooms.TryRemove(currentPlayer.ConnectionId, out _);
                    currentPlayer.ConnectionId = Context.ConnectionId;
                    _userRooms[Context.ConnectionId] = roomId;
                }
            }

            if (currentPlayer == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            var responses = _handPauseResponses.GetOrAdd(roomId, _ => new ConcurrentDictionary<int, byte>());
            responses[userId] = 1;

            if (continuePlaying)
            {
                lock (room.StateLock)
                {
                    currentPlayer.IsPausedAfterHand = false;
                    currentPlayer.IsWaitingForNextHand = false;
                    currentPlayer.HasFolded = false;
                    currentPlayer.HandPauseChoice = PokerHandPauseChoice.ContinuePlaying;
                    currentPlayer.HandPauseDecisionAt = DateTime.UtcNow;
                }

                await Clients.Caller.SendAsync("HandPauseDecisionAccepted", new { continuePlaying = true });
                return;
            }

            lock (room.StateLock)
            {
                currentPlayer.IsPausedAfterHand = true;
                currentPlayer.IsWaitingForNextHand = true;
                currentPlayer.HasFolded = true;
                currentPlayer.IsInHand = false;
                currentPlayer.IsAllIn = false;
                currentPlayer.CurrentBet = 0;
                currentPlayer.HoleCards.Clear();
                currentPlayer.HandPauseChoice = PokerHandPauseChoice.Timeout;
                currentPlayer.HandPauseDecisionAt = DateTime.UtcNow;
            }

            await Clients.Caller.SendAsync("StayedInRoomAsPaused", new
            {
                message = "Timeout seçdiniz. Otaqda qalacaqsınız və növbəti əli buraxacaqsınız."
            });

            await BroadcastGameState(roomId);
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
        public async Task<object> GetRoomInfo(string roomId)
        {
            try
            {
                var room = _roomManager.GetRoom(roomId);
                if (room == null)
                {
                    Console.WriteLine($"❌ GetRoomInfo: Room {roomId} not found");
                    await Clients.Caller.SendAsync("Error", "Room tapılmadı");
                    return null;
                }

                var roomInfo = new
                {
                    roomId = room.RoomId,
                    roomName = room.RoomName,
                    entryFee = room.BuyIn,
                    minBuyIn = room.BigBlind * 20,
                    maxBuyIn = room.BigBlind * 100,
                    smallBlind = room.SmallBlind,
                    bigBlind = room.BigBlind,
                    currentPlayers = room.Players.Count,
                    maxPlayers = room.MaxPlayers,
                    isGameActive = room.IsGameActive
                };

                Console.WriteLine($"✅ GetRoomInfo: {room.RoomName} - Entry Fee: {room.BuyIn}₼ (Max: {room.BigBlind * 100}₼)");

                return roomInfo;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ GetRoomInfo error: {ex.Message}\n{ex.StackTrace}");
                await Clients.Caller.SendAsync("Error", "Room məlumatı alına bilmədi");
                return null;
            }
        }
        private async Task BroadcastGameState(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            try
            {
                List<object> playersData;
                int dealerIdx, currentPlayerIdx;
                decimal pot, currentBet, bigBlind;
                List<string> communityCards;
                string currentStreet;
                int gameType;
                decimal minBuyIn, maxBuyIn;
                bool isGameActive;

                lock (room.StateLock)
                {
                    if (room.Players.Count == 0) return;

                    currentPlayerIdx = room.CurrentPlayerIndex;
                    if (currentPlayerIdx < 0 || currentPlayerIdx >= room.Players.Count)
                    {
                        room.CurrentPlayerIndex = 0;
                        currentPlayerIdx = 0;
                    }

                    var currentPlayer = room.Players[currentPlayerIdx];

                    // ✅ TURN TIME - yalnız bir dəfə hesabla
                    int turnTimeRemaining = 0;
                    if (room.TurnStartTime.HasValue && room.IsGameActive)
                    {
                        var elapsed = (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds;
                        turnTimeRemaining = Math.Max(0, (int)(PokerRoom.TURN_TIMEOUT_SECONDS - elapsed));
                        Console.WriteLine($"⏱️ OK: elapsed={elapsed:F1}s remaining={turnTimeRemaining}s player={currentPlayer.UserName}");
                    }
                    else
                    {
                        Console.WriteLine($"⚠️ SIFIR: TurnStartTime={room.TurnStartTime} | IsGameActive={room.IsGameActive} | Player={currentPlayer.UserName}");
                    }
                    Console.WriteLine($"⏱️ TurnTime: {turnTimeRemaining}s | Player: {currentPlayer.UserName} | Timeout: {PokerRoom.TURN_TIMEOUT_SECONDS}s");

                    playersData = room.Players.Select(p =>
                    {
                        var isCurrentTurn = room.IsGameActive &&
                                            p.UserId == currentPlayer.UserId &&
                                            p.IsInHand &&
                                            !p.HasFolded &&
                                            !p.IsAllIn &&
                                            !p.IsPausedAfterHand &&
                                            p.Chips > 0;

                        return (object)new
                        {
                            userId = p.UserId,
                            userName = p.UserName,
                            name = p.Name,
                            balance = p.Balance,
                            image = p.ProfileImage ?? "",
                            chips = p.Chips,
                            currentBet = p.CurrentBet,
                            isInHand = p.IsInHand,
                            hasFolded = p.HasFolded,
                            isWaitingForNextHand = p.IsWaitingForNextHand,
                            isPausedAfterHand = p.IsPausedAfterHand,
                            shouldLeaveAfterHand = p.ShouldLeaveAfterHand,
                            isActive = isCurrentTurn,
                            // ✅ Yalnız active oyunçuya turnTimeRemaining göndər
                            turnTimeRemaining = isCurrentTurn ? turnTimeRemaining : 0,
                            profileImage = p.ProfileImage ?? ""
                        };
                    }).ToList();

                    dealerIdx = room.DealerIndex;
                    pot = room.Pot;
                    currentBet = room.CurrentBet;
                    bigBlind = room.BigBlind;
                    minBuyIn = room.BigBlind * 20;
                    maxBuyIn = room.BigBlind * 100;
                    isGameActive = room.IsGameActive;
                    gameType = (int)room.GameType;
                    communityCards = new List<string>(room.CommunityCards);
                    currentStreet = room.CurrentStreet;
                }

                await Clients.Group(roomId).SendAsync("GameState", new
                {
                    players = playersData,
                    pot,
                    currentBet,
                    bigBlind,
                    minBuyIn,
                    maxBuyIn,
                    isGameActive,
                    gameType,
                    communityCards,
                    dealerIndex = dealerIdx,
                    currentStreet
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ BroadcastGameState error: {ex.Message}");
            }
        }
    }
}

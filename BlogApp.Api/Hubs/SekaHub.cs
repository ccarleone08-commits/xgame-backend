using BlogApp.Api.Hubs.Services;
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
    public class SekaHub : Hub
    {
        private readonly BlogAppDbContext _db;
        private readonly SekaRoomManager _roomManager;
        private readonly IHubContext<SekaHub> _hubContext;
        private readonly IRankService _rankService;
        private readonly IServiceScopeFactory _scopeFactory;
        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _autoStartTimers = new();
        private static readonly ConcurrentDictionary<string, string> _userRooms = new();
        private static readonly ConcurrentDictionary<int, string> _userRoomByUserId = new();
        private static readonly ConcurrentDictionary<string, System.Threading.Timer> _roomStartTimers = new();
        private static readonly ConcurrentDictionary<string, byte> _handPauseActiveRooms = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<int, byte>> _handPauseResponses = new();
        private static readonly ConcurrentDictionary<string, string> _handPauseSessionIds = new();
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _turnTimeoutTokens = new();

        private const decimal COMMISSION_RATE = 0.03m;
        private const int HAND_PAUSE_TIMEOUT_SECONDS = 15;
        private const int HAND_PAUSE_FINALIZE_GRACE_MS = 4000;

        // ✅ Constructor-a IHubContext əlavə et
        public SekaHub(BlogAppDbContext db, SekaRoomManager roomManager, IHubContext<SekaHub> hubContext, IRankService rankService, IServiceScopeFactory scopeFactory)
        {
            _db = db;
            _roomManager = roomManager;
            _hubContext = hubContext;
            _rankService = rankService;
            _scopeFactory = scopeFactory;
        }

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
                var user = await _db.Users
                    .Where(u => u.Id == userId)
                    .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance })
                    .FirstOrDefaultAsync();

                if (user == null)
                {
                    Console.WriteLine($"⚠️ User not found: {userId}");
                    Context.Abort();
                    return;
                }

                string fullName = $"{user.Name} {user.Surname}".Trim();
                if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

                // ✅ RANK MƏLUMATI ƏLAVƏ ET
                PlayerRankDetails? rankDetails = null;
                try
                {
                    rankDetails = await _rankService.GetPlayerRankDetails(userId, GameType.Seka);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"⚠️ Rank fetch error: {ex.Message}");
                }

                await Clients.Caller.SendAsync("UserData", new
                {
                    userId = user.Id,
                    username = user.UserName,
                    fullName = user.UserName,
                    balance = user.Balance,

                    // ✅ Rank məlumatları
                    rank = rankDetails?.CurrentRank ?? "Yeni Başlayan",
                    level = rankDetails?.RankLevel ?? 1,
                    xp = rankDetails?.ExperiencePoints ?? 0,
                    requiredXP = rankDetails?.RequiredXPForNextRank ?? 100,
                    progress = rankDetails?.ProgressPercentage ?? 0
                });

                Console.WriteLine($"✅ Connected: {fullName} | Balance: {user.Balance}₼ | Rank: {rankDetails?.CurrentRank ?? "N/A"}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ OnConnectedAsync error: {ex.Message}");
                Context.Abort();
            }

            await base.OnConnectedAsync();
        }
        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            string connId = Context.ConnectionId;
            var disconnectedUserId = GetUserId();
            Console.WriteLine($"🔴 DISCONNECT: {connId}");

            if (_userRooms.TryRemove(connId, out var roomId))
            {
                var room = _roomManager.GetRoom(roomId);
                if (room != null)
                {
                    SekaPlayer disconnectedPlayer = null;
                    bool needsGameUpdate = false;

                    lock (room.StateLock)
                    {
                        var player = room.Players.FirstOrDefault(p => p.ConnectionId == connId);
                        if (player != null)
                        {
                            disconnectedPlayer = player;
                            Console.WriteLine($"🚪 Oyuncu disconnect: {player.Name}");

                            if (!room.IsGameStarted || room.IsGameFinished)
                            {
                                var user = _db.Users.FirstOrDefault(u => u.Id == player.UserId);
                                if (user != null && room.EntryFee > 0)
                                {
                                    user.Balance += room.EntryFee;
                                    room.PotAmount -= room.EntryFee;
                                    _db.SaveChanges();
                                }
                                room.Players.Remove(player);
                                _userRoomByUserId.TryRemove(player.UserId, out _);
                                CancelAutoStart(roomId);
                                CancelRoomStartTimer(roomId);

                                // ✅ OTAQ BOŞALDISA SİL
                                if (room.Players.Count == 0 && room.CreatorUserId != 0)
                                {
                                    _roomManager.DeleteRoom(roomId);
                                    Console.WriteLine($"  🗑️ Boş otaq silindi: {room.RoomName}");
                                }
                            }
                            else if (room.IsGameStarted && !room.IsGameFinished)
                            {
                                if (room.CurrentTurnUserId == player.UserId)
                                {
                                    var nextPlayer = room.Players
                                        .Where(p => p.UserId != player.UserId && !p.HasFolded && p.IsActive)
                                        .FirstOrDefault();

                                    if (nextPlayer != null)
                                    {
                                        room.CurrentTurnUserId = nextPlayer.UserId;
                                        room.TurnStartTime = DateTime.UtcNow;
                                        Console.WriteLine($"  → Turn keçdi: {nextPlayer.Name}");
                                    }
                                }

                                room.Players.Remove(player);
                                _userRoomByUserId.TryRemove(player.UserId, out _);
                                needsGameUpdate = true;
                                Console.WriteLine($"  🗑️ {player.Name} oyundan çıkarıldı");
                            }
                        }
                    }

                    if (disconnectedPlayer != null)
                    {
                        // ✅ LOBBY-Ə YÖNLƏNDIR (bağlantı hələ tam bağlanmayıbsa çata bilər)
                        await _hubContext.Clients.Client(connId).SendAsync("RedirectToLobby", new
                        {
                            message = "Bağlantınız kəsildi. Lobby-ə yönləndirilirsiniz...",
                            reason = "disconnected"
                        });

                        await _hubContext.Clients.Group(roomId)
                            .SendAsync("PlayerLeft", disconnectedPlayer.Name);

                        await BroadcastRoomPlayers(roomId);

                        if (needsGameUpdate && room.IsGameStarted && !room.IsGameFinished)
                        {
                            await UpdateFoldRank(room, disconnectedPlayer, "disconnect");
                            await Task.Delay(100);

                            lock (room.StateLock)
                            {
                                var activePlayers = room.Players
                                    .Where(p => !p.HasFolded && p.IsActive)
                                    .ToList();

                                Console.WriteLine($"  📊 Aktif oyunçu: {activePlayers.Count}");
                            }

                            await CheckAllFolded(roomId);
                            await BroadcastGameStateWithContext(roomId);

                            // ✅ OYUN BİTDİKDƏN SONRA OTAQ BOŞALDISA SİL
                            var updatedRoom = _roomManager.GetRoom(roomId);
                            if (updatedRoom != null && updatedRoom.Players.Count == 0 && updatedRoom.CreatorUserId != 0)
                            {
                                _roomManager.DeleteRoom(roomId);
                                Console.WriteLine($"  🗑️ Boş otaq silindi: {updatedRoom.RoomName}");
                            }
                        }
                    }
                }
            }
            else if (disconnectedUserId != 0 &&
                     _userRoomByUserId.TryGetValue(disconnectedUserId, out var mappedRoomId))
            {
                var room = _roomManager.GetRoom(mappedRoomId);
                var currentConnectionStillActive = room?.Players.Any(p =>
                    p.UserId == disconnectedUserId && p.ConnectionId != connId) == true;

                if (!currentConnectionStillActive)
                {
                    _userRoomByUserId.TryRemove(disconnectedUserId, out _);
                }
            }

            await base.OnDisconnectedAsync(exception);
        }
        // ✅ UPDATED - Disconnect olunmayan oyuncuları göz ləyin
        private int GetNextTurnUserId(SekaRoom room, int excludeUserId = 0)
        {
            var activePlayers = room.Players
                .Where(p => !p.HasFolded && p.IsActive && p.UserId != excludeUserId)
                .ToList();

            if (activePlayers.Count == 0) return 0;

            // ✅ Cari oyuncu indexini tap
            int currentIndex = room.Players.FindIndex(p => p.UserId == room.CurrentTurnUserId);
            if (currentIndex == -1) currentIndex = 0;

            // ✅ Saat əqrəbi - sonrakı oyuncuya keç
            for (int i = 1; i <= room.Players.Count; i++)
            {
                int nextIndex = (currentIndex + i) % room.Players.Count;
                var nextPlayer = room.Players[nextIndex];

                if (!nextPlayer.HasFolded && nextPlayer.IsActive && nextPlayer.UserId != excludeUserId)
                {
                    Console.WriteLine($"  🔄 Turn keçidi: {room.Players[currentIndex].Name} → {nextPlayer.Name}");
                    return nextPlayer.UserId;
                }
            }

            return activePlayers.FirstOrDefault()?.UserId ?? 0;
        }

        private static decimal GetOutstandingCallAmount(SekaRoom room, SekaPlayer player)
        {
            if (room.CurrentBet <= player.CurrentBet)
            {
                return 0m;
            }

            return Math.Round(room.CurrentBet - player.CurrentBet, 2);
        }

        private static decimal GetActionCallAmount(SekaRoom room, SekaPlayer player)
        {
            var outstandingCall = GetOutstandingCallAmount(room, player);
            if (outstandingCall > 0)
            {
                return outstandingCall;
            }

            // ✅ Raise olubsa → son raise məbləği, olmayıbsa → EntryFee
            if (room.LastRaiseAmount > 0)
            {
                return Math.Round(room.LastRaiseAmount, 2);
            }

            return room.EntryFee;
        }
        private static decimal GetMinimumRaiseIncrement(SekaRoom room)
        {
            if (room.CurrentBet <= 0)
            {
                // ✅ Raise olubsa → LastRaiseAmount, olmayıbsa → EntryFee
                return room.LastRaiseAmount > 0
                    ? Math.Round(room.LastRaiseAmount, 2)
                    : room.EntryFee;
            }

            var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
            bool allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);

            if (allBetsEqual && activePlayers.Count > 0)
            {
                return Math.Round(room.LastRaiseAmount > 0 ? room.LastRaiseAmount : room.EntryFee, 2);
            }

            decimal minimumRaiseIncrement = room.LastRaiseAmount > 0
                ? room.LastRaiseAmount
                : room.CurrentBet;

            return Math.Round(minimumRaiseIncrement, 2);
        }

        //private static decimal GetMinimumRaiseIncrement(SekaRoom room)
        //{
        //    if (room.CurrentBet <= 0)
        //    {
        //        return room.EntryFee;
        //    }

        //    // Tüm aktif oyuncuların bet'i eşit olmuşsa (hamısı call etmişsə)
        //    var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
        //    bool allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);

        //    if (allBetsEqual && activePlayers.Count > 0)
        //    {
        //        return Math.Round(room.LastRaiseAmount > 0 ? room.LastRaiseAmount : room.EntryFee, 2);
        //    }

        //    decimal minimumRaiseIncrement = room.LastRaiseAmount > 0
        //        ? room.LastRaiseAmount
        //        : room.CurrentBet;

        //    return Math.Round(minimumRaiseIncrement, 2);
        //}

        private static decimal GetMinimumRaiseAmount(SekaRoom room, SekaPlayer player)
        {
            return GetMinimumRaiseIncrement(room);
        }

        private static decimal GetMaximumRaiseIncrement(SekaRoom room, SekaPlayer player)
        {
            decimal maxTotalBet = Math.Min(room.EntryFee * 100m, player.Balance + player.CurrentBet);
            decimal maxRaiseAmount = maxTotalBet - player.CurrentBet;
            return Math.Max(0m, Math.Round(maxRaiseAmount, 2));
        }

        private static bool IsRoundFinished(SekaRoom room, List<SekaPlayer> activePlayers)
        {
            if (activePlayers.Count < 2 || room.CurrentBet <= 0)
            {
                return false;
            }

            bool allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);
            int playersWithoutAction = activePlayers.Count(p => p.CurrentBet < room.CurrentBet && !p.IsAllIn);
            return allBetsEqual && playersWithoutAction == 0;
        }
        // ==================== ROOM OPERATIONS ====================

        public async Task<List<RoomTemplate>> GetRoomTemplates()
        {
            return _roomManager.GetRoomTemplates();
        }

        public async Task QuickJoin(decimal entryFee)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            if (user.Balance < entryFee)
            {
                await Clients.Caller.SendAsync("JoinError",
                    $"Kifayət qədər balans yoxdur (lazım: {entryFee}₼)");
                return;
            }

            var room = _roomManager.FindOrCreateSuitableRoom(entryFee, userId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinError", "Uyğun otaq tapılmadı");
                return;
            }

            await JoinRoom(room.RoomId, null);
        }

        public async Task JoinRoom(string roomId, string? password = null)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("JoinError", "Otaq tapılmadı");
                return;
            }

            string fullName = $"{user.Name} {user.Surname}".Trim();
            if (string.IsNullOrEmpty(fullName)) fullName = user.UserName;

            // ✅ ƏVVƏLCƏ MÖVCUd OYUNÇUNU YOXLA
            SekaPlayer? existingPlayer = null;
            lock (room.StateLock)
            {
                existingPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
            }

            if (existingPlayer != null)
            {
                Console.WriteLine($"🔄 Reconnecting: {fullName} → {room.RoomName}");
                var oldConnectionId = existingPlayer.ConnectionId;
                existingPlayer.ConnectionId = Context.ConnectionId;

                if (!string.IsNullOrWhiteSpace(oldConnectionId) && oldConnectionId != Context.ConnectionId)
                {
                    _userRooms.TryRemove(oldConnectionId, out _);
                    await Groups.RemoveFromGroupAsync(oldConnectionId, roomId);
                }

                await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
                _userRooms[Context.ConnectionId] = roomId;
                _userRoomByUserId[userId] = roomId;

                await Clients.Caller.SendAsync("JoinedRoom", new
                {
                    roomId,
                    roomName = room.RoomName,
                    hand = existingPlayer.Hand,
                    balance = user.Balance,
                    currentBet = existingPlayer.CurrentBet,
                    isGameStarted = room.IsGameStarted,
                    profileImage = user.Image
                });

                await BroadcastRoomPlayers(roomId);
                return;
            }

            bool isWaitingPlayer = false;
            if (room.IsGameStarted && !room.IsGameFinished)
            {
                if (room.Players.Count >= room.MaxPlayers)
                {
                    await Clients.Caller.SendAsync("JoinError", "Otaq doludur");
                    return;
                }
                isWaitingPlayer = true;
                Console.WriteLine($"⏳ Player joining as spectator: {fullName}");
            }

            // ✅ OTAQ DOLU YOXLAMASI
            if (room.Players.Count >= room.MaxPlayers)
            {
                if (room.CreatorUserId == 0)
                {
                    var alternativeRoom = _roomManager.FindOrCreateSuitableRoom(room.EntryFee, userId);
                    if (alternativeRoom != null && alternativeRoom.RoomId != roomId)
                    {
                        await Clients.Caller.SendAsync("RoomRedirect", new
                        {
                            message = $"'{room.RoomName}' doludur. '{alternativeRoom.RoomName}' otağına yönləndirilirik...",
                            newRoomId = alternativeRoom.RoomId
                        });

                        await Task.Delay(1000);
                        await JoinRoom(alternativeRoom.RoomId, null);
                        return;
                    }
                }

                await Clients.Caller.SendAsync("JoinError", "Otaq doludur");
                return;
            }

            // ✅ BALANS YOXLAMASI
            if (user.Balance < room.EntryFee)
            {
                await Clients.Caller.SendAsync("JoinError",
                    $"Kifayət qədər balans yoxdur (lazım: {room.EntryFee}₼)");
                return;
            }

            // ✅ 1. GİRİŞ HAQQINI DƏRHAL ÇIX
            user.Balance -= room.EntryFee;
            await _db.SaveChangesAsync();

            // ✅ 2. PLAYER OBYEKTI YARAT (yenilənmiş balansla)
            var player = new SekaPlayer
            {
                ConnectionId = Context.ConnectionId,
                UserId = user.Id,
                Name = fullName,
                Balance = user.Balance, // ✅ YENİLƏNMİŞ BALANS
                Hand = new List<SekaCard>(),
                CurrentBet = 0,
                TotalBet = 0,
                HasFolded = isWaitingPlayer, // ✅ Gözləyənsə fold sayılır
                IsActive = true,
                IsWaitingForNextRound = isWaitingPlayer, // ✅ YENİ
                HasPaidEntryFee = true,
                ProfileImage = user.Image  // ✅ AVATAR ƏLAVƏ ET


            };

            // ✅ 3. OTAĞA ƏLAVƏ ET
            if (!_roomManager.AddPlayerToRoom(roomId, player, password))
            {
                // ✅ UĞURSUZ OLSA PULUNU GERİ VER
                user.Balance += room.EntryFee;
                await _db.SaveChangesAsync();
                await Clients.Caller.SendAsync("JoinError", "Otağa qoşulmaq alınmadı");
                return;
            }

            // ✅ 4. POTU ARTIR
            room.PotAmount += room.EntryFee;

            await Groups.AddToGroupAsync(Context.ConnectionId, roomId);
            _userRooms[Context.ConnectionId] = roomId;
            _userRoomByUserId[userId] = roomId;

            await CollectMissingEntryFees(roomId, room, _db);

            // ✅ 5. YENİLƏNMİŞ BALANSI GÖNDƏR
            await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);

            await Clients.Caller.SendAsync("JoinedRoom", new
            {
                roomId,
                roomName = room.RoomName,
                balance = user.Balance,
                isGameStarted = false,
                isWaiting = isWaitingPlayer // ✅ YENİ

            });
            if (isWaitingPlayer)
            {
                await Clients.Caller.SendAsync("WaitingForNextRound",
                    "Oyun davam edir. Növbəti raund başlayana qədər gözləyin...");
            }

            await Clients.Group(roomId).SendAsync("PlayerJoined", fullName);
            await BroadcastRoomPlayers(roomId);
            await BroadcastPotAmount(roomId);

            Console.WriteLine($"✅ {fullName} → {room.RoomName} | Fee: {room.EntryFee}₼ | New Balance: {user.Balance}₼ | Pot: {room.PotAmount}₼");

            int activePlayers = room.Players.Count(p => !p.IsWaitingForNextRound);

            if (!room.IsGameStarted && activePlayers == 2)
            {
                StartRoomStartTimer(roomId);
            }
            else if (!room.IsGameStarted && activePlayers >= room.MaxPlayers)
            {
                CancelRoomStartTimer(roomId);
                await Clients.Group(roomId).SendAsync("RoomStarting", "✅ Otaq doldu! Oyun başlayır...");
                await Task.Delay(2000);
                await AutoStartGameWithContext(roomId);
            }

            _roomManager.CheckAndCreateNewRoomIfNeeded(roomId);

            // ✅ OTAQ DOLARSA OYUNU BAŞLAT
            if (room.Players.Count == room.MaxPlayers && !room.IsGameStarted)
            {
                await Clients.Group(roomId).SendAsync("RoomFull",
                    "✅ Otaq doldu! 2 saniyə sonra oyun başlayır...");
                StartAutoStartTimer(roomId);
            }
        }

        private void StartTurnTimer(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            CancelTurnTimer(roomId);

            var turnTimeoutCts = new CancellationTokenSource();
            _turnTimeoutTokens[roomId] = turnTimeoutCts;

            lock (room.StateLock)
            {
                room.TurnStartTime = DateTime.UtcNow;
            }

            // ✅ TURN TIMEOUT KONTROLÜ (10 saniye)
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(SekaRoom.TURN_TIMEOUT_SECONDS), turnTimeoutCts.Token);
                    if (!turnTimeoutCts.IsCancellationRequested)
                    {
                        await CheckTurnTimeout(roomId, userId);
                    }
                }
                catch (TaskCanceledException)
                {
                    // İptal edilmiş turn timer
                }
            });

            Console.WriteLine($"⏱️ TURN TİMER BAŞLADI: {roomId} | UserId: {userId} | Zaman: {SekaRoom.TURN_TIMEOUT_SECONDS}s");
        }

        private void CancelTurnTimer(string roomId)
        {
            if (_turnTimeoutTokens.TryRemove(roomId, out var cts))
            {
                try
                {
                    cts.Cancel();
                }
                catch { }
                cts.Dispose();
            }
        }

        private async Task CheckTurnTimeout(string roomId, int userId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            bool shouldAutoFold = false;
            SekaPlayer? player = null;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsGameFinished)
                {
                    return;
                }

                // ✅ HƏMİN OYUNÇUNUN NÖBƏSIDIRSE VƏ VAXT BİTİBSƏ
                if (room.CurrentTurnUserId == userId &&
                    room.TurnStartTime.HasValue &&
                    (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds >= SekaRoom.TURN_TIMEOUT_SECONDS)
                {
                    player = room.Players.FirstOrDefault(p => p.UserId == userId);
                    if (player != null && !player.HasFolded)
                    {
                        shouldAutoFold = true;
                        player.HasFolded = true;
                        player.IsActive = false;
                        Console.WriteLine($"⏰ AUTO-FOLD: {player.Name} (vaxt bitdi)");
                    }
                }
            }

            if (shouldAutoFold && player != null)
            {
                // ✅ TÜM OYUNCULARA BİLDİR
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerFolded", player.Name);
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerTimeout", new
                {
                    playerName = player.Name,
                    message = $"{player.Name} vaxt bitdiyi üçün avtomatik fold oldu"
                });

                await UpdateFoldRank(room, player, "auto-fold");

                // ✅ KONTROL ET
                await CheckAllFolded(roomId);

                // ✅ OYUN HALA DEVAM EDİYORSA
                var room2 = _roomManager.GetRoom(roomId);
                if (room2 != null && room2.IsGameStarted && !room2.IsGameFinished)
                {
                    await NextTurn(roomId);
                }
            }
        }

        private static decimal GetFoldLossAmount(SekaRoom room, SekaPlayer player)
        {
            var entryLoss = player.HasPaidEntryFee ? room.EntryFee : 0m;
            var actionLoss = Math.Max(player.TotalBet, player.CurrentBet);
            return Math.Round(entryLoss + actionLoss, 2);
        }

        private async Task UpdateFoldRank(SekaRoom room, SekaPlayer player, string reason)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var rankService = scope.ServiceProvider.GetRequiredService<IRankService>();
                var lossAmount = GetFoldLossAmount(room, player);

                await rankService.UpdateRankAfterGame(
                    player.UserId,
                    GameType.Seka,
                    isWin: false,
                    earnings: lossAmount);

                var rankDetails = await rankService.GetPlayerRankDetails(player.UserId, GameType.Seka);

                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("RankUpdated", new
                {
                    rank = rankDetails.CurrentRank,
                    level = rankDetails.RankLevel,
                    xp = rankDetails.ExperiencePoints,
                    requiredXP = rankDetails.RequiredXPForNextRank,
                    progress = rankDetails.ProgressPercentage
                });

                Console.WriteLine($"📊 Fold rank updated: {player.Name} | Reason: {reason} | Loss: {lossAmount}₼");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Fold rank update error: {ex.Message}");
            }
        }

        private async Task BroadcastGameState(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
            bool allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);
            int playersWithoutAction = activePlayers.Count(p => p.CurrentBet < room.CurrentBet && !p.IsAllIn);
            bool roundFinished = IsRoundFinished(room, activePlayers);

            Console.WriteLine($"📊 BroadcastGameState START");
            Console.WriteLine($"  CurrentBet: {room.CurrentBet}");
            Console.WriteLine($"  LastCallerId: {room.LastCallerId}");
            Console.WriteLine($"  RoundFinished: {roundFinished}");
            Console.WriteLine($"  ActivePlayers: {activePlayers.Count}");

            foreach (var player in room.Players)
            {
                decimal myCurrentBet = player.CurrentBet;
                int playerIndex = room.Players.IndexOf(player);
                bool isDealer = (playerIndex == room.DealerIndex);
                bool isTurn = (room.CurrentTurnUserId == player.UserId);

                // Timer
                int remainingSeconds = 0;
                if (room.TurnStartTime.HasValue && isTurn)
                {
                    var elapsed = (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds;
                    remainingSeconds = Math.Max(0, (int)(SekaRoom.TURN_TIMEOUT_SECONDS - elapsed));
                }

                // Hand score
                int myHandScore = 0;
                if (player.Hand != null && player.Hand.Count > 0)
                {
                    myHandScore = SekaHandEvaluator.CalculateHandScore(player.Hand);
                }
                // ✅ DÜYMƏLƏR - BAŞLANĞIC
                bool canFold = false;
                bool canCall = false;
                bool canRaise = false;
                bool canShowdownCall = false;
                decimal callAmountDisplay = 0;

                // ✅ YALNIZ NÖVBƏSI OLANLAR ÜÇÜN
                if (isTurn && !player.HasFolded && player.IsActive)
                {
                    var outstandingCall = GetOutstandingCallAmount(room, player);
                    callAmountDisplay = GetActionCallAmount(room, player);

                    canFold = true;
                    canCall = callAmountDisplay > 0;
                    canRaise = true;
                    canShowdownCall = CanPlayerShowdownCall(room, player.UserId);
                    Console.WriteLine($"📞 CALL DISPLAY: {player.Name} üçün lazım={callAmountDisplay}₼");

                    // ✅ ƏSAS LOQİKA
                    if (outstandingCall > 0 || (room.CurrentBet == 0 && player.CurrentBet == 0))
                    {
                        Console.WriteLine($"🎮 OYUNÇU: {player.Name} | ShowdownCall={(canShowdownCall ? "✅" : "❌")} | Flag={room.ShowdownCallActivated}");
                    }
                    else
                    {
                        Console.WriteLine($"⏭️ NÖVBƏ: {player.Name} | ShowdownCall={(canShowdownCall ? "✅" : "❌")} | Flag={room.ShowdownCallActivated}");
                    }

                    Console.WriteLine($"🔢 ActivePlayers count: {activePlayers.Count}");
                    foreach (var ap in activePlayers)
                        Console.WriteLine($"  → {ap.Name} | Folded:{ap.HasFolded} | Active:{ap.IsActive}");
                }
                Console.WriteLine(canFold);
                Console.WriteLine(canCall);
                Console.WriteLine(canRaise);

                // Min/Max raise
                decimal minRaise = GetMinimumRaiseAmount(room, player);
                decimal maxRaise = GetMaximumRaiseIncrement(room, player);
                if (minRaise > maxRaise) minRaise = maxRaise;

                // ✅ Oyunçuya göndər
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("GameState", new
                {
                    currentTurnUserId = room.CurrentTurnUserId,
                    currentBet = room.CurrentBet,
                    myCurrentBet = myCurrentBet,
                    potAmount = room.PotAmount,
                    round = room.CurrentRound,
                    limitType = room.LimitType.ToString(),

                    // Düymələr
                    canFold = canFold,
                    canCall = canCall,
                    canRaise = canRaise,
                    canShowdownCall = canShowdownCall,

                    // Məbləğlər
                    callAmount = callAmountDisplay,
                    showdownCallAmount = callAmountDisplay,
                    minRaise = minRaise,
                    maxRaise = maxRaise,
                    entryFee = room.EntryFee,

                    // Məlumat
                    turnTimeRemaining = remainingSeconds,
                    myHandScore = myHandScore,
                    dealerIndex = room.DealerIndex,
                    isDealer = isDealer,
                    dealerName = room.Players[room.DealerIndex].Name,
                    isTurn = isTurn,

                    // Debug
                    raiseCount = room.RaiseCount,
                    lastRaiserId = room.LastRaiserId,
                    lastCallerId = room.LastCallerId,
                    roundFinished = roundFinished,
                    allBetsEqual = allBetsEqual
                });
            }

            Console.WriteLine($"📊 BroadcastGameState END\n");
        }
        private async Task NextTurn(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            int loopCounter = 0;
            const int MAX_LOOPS = 100;

            while (true)
            {
                loopCounter++;
                if (loopCounter > MAX_LOOPS)
                {
                    Console.WriteLine($"❌ NextTurn MAX LOOP EXCEEDED!");
                    break;
                }

                var activePlayers = new List<SekaPlayer>();

                lock (room.StateLock)
                {
                    activePlayers = room.Players
                        .Where(p => !p.HasFolded && p.IsActive)
                        .ToList();

                    // ❌ Tək oyunçu qaldısa
                    if (activePlayers.Count <= 1)
                    {
                        if (activePlayers.Count == 1)
                        {
                            Console.WriteLine($"❌ Tək oyunçu qaldı: KAZANDI!");
                            var winner = activePlayers[0];
                            _ = Task.Run(() => AwardWinner(roomId, winner, "Digər oyunçular fold etdi", null));
                        }
                        return;
                    }

                    // ✅ NORMAL TURN KEÇİ
                    int currentIdx = room.Players.FindIndex(p => p.UserId == room.CurrentTurnUserId);
                    if (currentIdx == -1) currentIdx = 0;

                    int nextIdx = (currentIdx + 1) % room.Players.Count;
                    int skipped = 0;

                    while ((room.Players[nextIdx].HasFolded || !room.Players[nextIdx].IsActive) &&
                           skipped < room.Players.Count)
                    {
                        nextIdx = (nextIdx + 1) % room.Players.Count;
                        skipped++;
                    }

                    if (skipped >= room.Players.Count)
                    {
                        Console.WriteLine($"❌ Aktif oyuncu yok!");
                        return;
                    }

                    room.CurrentTurnUserId = room.Players[nextIdx].UserId;
                    room.TurnStartTime = DateTime.UtcNow;

                    Console.WriteLine($"⏭️ Turn keçdi → {room.Players[nextIdx].Name}");

                    StartTurnTimer(roomId, room.CurrentTurnUserId);
                } // ✅ LOCK AZAD

                // ✅ BROADCAST
                await BroadcastGameState(roomId);

                break;
            }

            Console.WriteLine($"✅ NextTurn tamamlandı (Loop: {loopCounter})");
        }
        public async Task LeaveRoom()
        {
            var connId = Context.ConnectionId;
            if (!_userRooms.TryGetValue(connId, out var roomId))
                return;

            var userId = GetUserId();
            if (userId == 0) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            SekaPlayer? player = null;

            lock (room.StateLock)
            {
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;
            }

            // ✅ DURUM 1: OYUN DAVAM EDİYORSA
            if (room.IsGameStarted && !room.IsGameFinished)
            {
                Console.WriteLine($"🚪 LeaveRoom sırasında oyun davam ediyor: {player.Name}");

                lock (room.StateLock)
                {
                    player.HasFolded = true;
                    player.IsActive = false;
                    // ✅ DƏRHAL SİL - əks halda BroadcastRoomPlayers göndərəcək
                    room.Players.Remove(player);
                    Console.WriteLine($"  📛 {player.Name} fold oldu və siyahıdan silindi");
                }

                // ✅ DİGƏR OYUNCULARA BİLDİR
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerFolded", player.Name);
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", player.Name);

                // ✅ CONNECTION KALDIR
                await Groups.RemoveFromGroupAsync(connId, roomId);
                _userRooms.TryRemove(connId, out _);
                _userRoomByUserId.TryRemove(player.UserId, out _);

                // ✅ LOBBY-Ə YÖNLƏNDIR
                await Clients.Caller.SendAsync("RedirectToLobby", new
                {
                    message = "Otaqdan çıxdınız. Lobby-ə yönləndirilirsiniz...",
                    reason = "left"
                });

                // ✅ KAZANAN VAR MI? (artıq 1 oyunçu qalıbsa)
                await CheckAllFolded(roomId);

                // ✅ OYUN DAVAM EDİYORSA NÖVBƏTİ OYUNCUYA KEÇ
                var room2 = _roomManager.GetRoom(roomId);
                if (room2 != null && room2.IsGameStarted && !room2.IsGameFinished)
                {
                    var activePlayers = room2.Players
                        .Where(p => !p.HasFolded && p.IsActive)
                        .ToList();

                    if (activePlayers.Count > 1)
                    {
                        Console.WriteLine($"  → Aktif oyuncu: {activePlayers.Count}, NextTurn çağrılıyor");
                        await NextTurn(roomId);
                    }
                }

                // ✅ BROADCAST - artıq silinmiş oyunçu olmadan göndəriləcək
                await BroadcastRoomPlayers(roomId);
                await BroadcastPotAmount(roomId);

                // ✅ OTAQ BOŞALDISA SİL
                var updatedRoom = _roomManager.GetRoom(roomId);
                if (updatedRoom != null && updatedRoom.Players.Count == 0 && updatedRoom.CreatorUserId != 0)
                {
                    _roomManager.DeleteRoom(roomId);
                    Console.WriteLine($"  🗑️ Boş otaq silindi: {updatedRoom.RoomName}");
                }

                return;
            }

            // ✅ DURUM 2: OYUN BAŞLANMAMIŞ/BİTMİŞSE - NORMAL ÇIKIŞ
            Console.WriteLine($"🚪 LeaveRoom normal çıkış: {player.Name}");

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user != null && room.EntryFee > 0)
            {
                user.Balance += room.EntryFee;
                room.PotAmount -= room.EntryFee;
                await _db.SaveChangesAsync();

                await Clients.Caller.SendAsync("BalanceUpdated", user.Balance);
                Console.WriteLine($"  💰 Refund: {player.Name} → {room.EntryFee}₼");
            }

            lock (room.StateLock)
            {
                room.Players.Remove(player);
            }

            await Groups.RemoveFromGroupAsync(connId, roomId);
            _userRooms.TryRemove(connId, out _);
            _userRoomByUserId.TryRemove(player.UserId, out _);

            CancelAutoStart(roomId);
            CancelRoomStartTimer(roomId);

            await Clients.Caller.SendAsync("LeftRoom");

            await Clients.Caller.SendAsync("RedirectToLobby", new
            {
                message = "Otaqdan uğurla çıxdınız.",
                reason = "left"
            });

            await Clients.Group(roomId).SendAsync("PlayerLeft", user?.Name ?? player.Name);
            await BroadcastRoomPlayers(roomId);
            await BroadcastPotAmount(roomId);

            if (room.Players.Count == 0 && room.CreatorUserId != 0)
            {
                _roomManager.DeleteRoom(roomId);
                Console.WriteLine($"  🗑️ Boş otaq silindi: {room.RoomName}");
            }

            Console.WriteLine($"  ✅ {player.Name} otaqtan çıktı");
        }

        public async Task<List<object>> GetRoomList()
        {
            var rooms = _roomManager.GetAllRooms();
            return rooms.Select(r => new
            {
                roomId = r.RoomId,
                roomName = r.RoomName,
                creatorName = r.CreatorName,
                playerCount = r.Players.Count,
                maxPlayers = r.MaxPlayers,
                entryFee = r.EntryFee,
                isPrivate = r.IsPrivate,
                isGameStarted = r.IsGameStarted,
                isDefault = r.CreatorUserId == 0,
                isFull = r.Players.Count >= r.MaxPlayers,
                limitType = r.LimitType.ToString()
            }).ToList<object>();
        }

        public async Task SendEmoji(string emoji)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var userId = GetUserId();
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var player = room.Players.FirstOrDefault(p => p.UserId == userId);
            if (player == null) return;

            await Clients.Group(roomId).SendAsync("PlayerEmoji", new
            {
                playerName = player.Name,
                userId = player.UserId,
                emoji = emoji
            });

            Console.WriteLine($"😊 {player.Name} sent: {emoji}");
        }

        // ==================== AUTO-START TIMER (FIXED) ====================

        private void StartAutoStartTimer(string roomId)
        {
            CancelAutoStart(roomId);

            var timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    var room = _roomManager.GetRoom(roomId);
                    if (room != null && !room.IsGameStarted && room.Players.Count >= 2)
                    {
                        Console.WriteLine($"⏰ Auto-starting: {room.RoomName}");

                        // ✅ Hub-dan kənarda olduğumuz üçün _hubContext istifadə edirik
                        await AutoStartGameWithFreshScope(roomId);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Auto-start error: {ex.Message}");
                }
                finally
                {
                    _autoStartTimers.TryRemove(roomId, out var t);
                    t?.Dispose();
                }
            }, null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);

            _autoStartTimers[roomId] = timer;
        }

        private void CancelAutoStart(string roomId)
        {
            if (_autoStartTimers.TryRemove(roomId, out var timer))
            {
                timer?.Dispose();
                Console.WriteLine($"⏹️ Auto-start cancelled: {roomId}");
            }
        }

        private async Task AutoStartGameWithFreshScope(string roomId)
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<BlogAppDbContext>();
            await AutoStartGameWithContext(roomId, scopedDb);
        }

        private async Task CollectMissingEntryFees(string roomId, SekaRoom room, BlogAppDbContext db)
        {
            List<SekaPlayer> playersNeedingEntryFee;

            lock (room.StateLock)
            {
                playersNeedingEntryFee = room.Players
                    .Where(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand && !p.HasPaidEntryFee)
                    .ToList();
            }

            foreach (var player in playersNeedingEntryFee)
            {
                var user = await db.Users.FirstOrDefaultAsync(u => u.Id == player.UserId);
                if (user == null || user.Balance < room.EntryFee)
                {
                    lock (room.StateLock)
                    {
                        room.Players.RemoveAll(p => p.UserId == player.UserId);
                    }

                    _userRooms.TryRemove(player.ConnectionId, out _);
                    _userRoomByUserId.TryRemove(player.UserId, out _);

                    await _hubContext.Clients.Client(player.ConnectionId).SendAsync("JoinError",
                        $"Kifayət qədər balans yoxdur. Minimum {room.EntryFee}₼ lazımdır.");
                    await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", player.Name);
                    continue;
                }

                bool charged = false;
                lock (room.StateLock)
                {
                    var currentPlayer = room.Players.FirstOrDefault(p => p.UserId == player.UserId);
                    if (currentPlayer != null && !currentPlayer.HasPaidEntryFee)
                    {
                        user.Balance -= room.EntryFee;
                        currentPlayer.Balance = user.Balance;
                        currentPlayer.HasPaidEntryFee = true;
                        room.PotAmount += room.EntryFee;
                        charged = true;
                    }
                }

                if (!charged)
                {
                    continue;
                }

                await db.SaveChangesAsync();
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                Console.WriteLine($"💰 Missing entry fee collected: {player.Name} → {room.EntryFee}₼ | POT: {room.PotAmount}₼");
            }
        }

        // ✅ YENİ METOD: IHubContext istifadə edən versiya
        private async Task AutoStartGameWithContext(string roomId, BlogAppDbContext? dbOverride = null)
        {
            var db = dbOverride ?? _db;
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            await CollectMissingEntryFees(roomId, room, db);

            lock (room.StateLock)
            {
                var roundPlayers = room.Players
                    .Where(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand)
                    .ToList();

                if (room.IsGameStarted || roundPlayers.Count < 2)
                {
                    Console.WriteLine($"⚠️ Oyun başlatılamadı: {roomId} - IsGameStarted: {room.IsGameStarted}, Players: {roundPlayers.Count}");
                    return;
                }

                // ✅ OYUN DURUMUNU AYARLA
                room.IsGameStarted = true;
                room.IsGameFinished = false;
                room.CurrentRound = 0;
                room.CurrentBet = 0;
                room.RaiseCount = 0;
                room.LastRaiserId = 0;
                room.LastCallerId = 0;

                room.ShowdownCallActivated = false;

                // ✅ DEALER INDEX
                if (room.DealerIndex == -1 || room.DealerIndex >= room.Players.Count)
                {
                    var dealerPlayer = roundPlayers[new Random().Next(0, roundPlayers.Count)];
                    room.DealerIndex = room.Players.IndexOf(dealerPlayer);
                }
                else
                {
                    for (int i = 1; i <= room.Players.Count; i++)
                    {
                        int nextDealerIndex = (room.DealerIndex + i) % room.Players.Count;
                        var nextDealer = room.Players[nextDealerIndex];
                        if (!nextDealer.IsWaitingForNextRound && !nextDealer.IsPausedAfterHand)
                        {
                            room.DealerIndex = nextDealerIndex;
                            break;
                        }
                    }
                }

                room.TurnStartTime = DateTime.UtcNow;
                room.Deck = SekaCardDeck.CreateShuffledDeck();

                // ✅ KARTLARI DAĞ IT
                foreach (var player in room.Players)
                {
                    if (player.IsWaitingForNextRound || player.IsPausedAfterHand)
                    {
                        player.Hand.Clear();
                        player.HasFolded = true;
                        player.IsActive = false;
                        player.CurrentBet = 0;
                        player.TotalBet = 0;
                        continue;
                    }

                    player.Hand = room.Deck.Take(3).ToList();
                    room.Deck.RemoveRange(0, 3);
                    player.HasFolded = false;
                    player.IsActive = true;
                    player.CurrentBet = 0;
                    player.TotalBet = 0;
                }

                // ✅ DEALER-DƏN SONRAKISI BAŞLA
                room.CurrentPlayerIndex = room.DealerIndex;
                for (int i = 1; i <= room.Players.Count; i++)
                {
                    int nextIndex = (room.DealerIndex + i) % room.Players.Count;
                    var nextPlayer = room.Players[nextIndex];
                    if (!nextPlayer.IsWaitingForNextRound && !nextPlayer.IsPausedAfterHand)
                    {
                        room.CurrentPlayerIndex = nextIndex;
                        break;
                    }
                }
                room.CurrentTurnUserId = room.Players[room.CurrentPlayerIndex].UserId;

                Console.WriteLine($"🎮 OYUN BAŞLADI: {room.RoomName}");
                Console.WriteLine($"  🃏 Dealer: {room.DealerIndex} ({room.Players[room.DealerIndex].Name})");
                Console.WriteLine($"  👤 Başlayan: {room.CurrentPlayerIndex} ({room.Players[room.CurrentPlayerIndex].Name})");
                Console.WriteLine($"  💰 POT: {room.PotAmount}₼ (Entry fee × {roundPlayers.Count})");
            }

            // ✅ OYUNCULARA BİLDİRİM GÖNDER
            await _hubContext.Clients.Group(roomId).SendAsync("GameStarted", new
            {
                message = "🎮 Oyun başladı!",
                potAmount = room.PotAmount,
                playerCount = room.Players.Count(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand),
                entryFee = room.EntryFee
            });

            // ✅ KARTLARI GÖNDER
            foreach (var player in room.Players)
            {
                if (player.IsWaitingForNextRound || player.IsPausedAfterHand)
                {
                    continue;
                }

                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CardsDealt", new
                {
                    hand = player.Hand.Select(c => new { suit = c.Suit, rank = c.Rank })
                });
            }
            room.NextCallAmount = 0;
            // ✅ POT BROADCAST - OYUN BAŞLANGICINDA
            await BroadcastPotAmount(roomId);

            // ✅ TİMER BAŞLAT VE GAME STATE GÖNDER
            StartTurnTimer(roomId, room.CurrentTurnUserId);
            await BroadcastGameStateWithContext(roomId);
        }
        public async Task PlaceBet(decimal amount)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            SekaPlayer? player = null;
            string? errorMessage = null;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsGameFinished)
                {
                    errorMessage = "Oyun davam etmir";
                    goto SendError;
                }

                if (room.CurrentTurnUserId != userId)
                {
                    errorMessage = "Sizin növbəniz deyil";
                    goto SendError;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.HasFolded)
                {
                    errorMessage = "Oyunçu tapılmadı";
                    goto SendError;
                }

                decimal callAmount = room.CurrentBet - player.CurrentBet;
                bool isCall = (callAmount > 0 && amount == room.CurrentBet);
                bool isBet = (room.CurrentBet == 0 && amount > 0);
                bool isRaise = (amount > room.CurrentBet);

                if (isBet)
                {
                    if (amount < room.EntryFee)
                    {
                        errorMessage = $"Minimum bet: {room.EntryFee}₼";
                        goto SendError;
                    }

                    decimal maxBet = room.PotAmount;
                    if (amount > maxBet)
                    {
                        errorMessage = $"Maksimum bet: {maxBet}₼ (Pot Limit)";
                        goto SendError;
                    }
                }
                else if (isRaise)
                {
                    decimal minRaise = room.CurrentBet > 0
                        ? Math.Round(room.CurrentBet + GetMinimumRaiseIncrement(room), 2)
                        : room.EntryFee;
                    if (amount < minRaise)
                    {
                        errorMessage = $"Minimum raise: {minRaise}₼";
                        goto SendError;
                    }

                    decimal maxRaise = Math.Min(room.EntryFee * 100m, player.Balance + player.CurrentBet);
                    if (amount > maxRaise)
                    {
                        errorMessage = $"Maksimum raise: {maxRaise}₼";
                        goto SendError;
                    }
                }
                else if (!isCall)
                {
                    errorMessage = $"Minimum mərc: {room.CurrentBet}₼";
                    goto SendError;
                }

                goto Success;

            SendError:
                _ = Clients.Caller.SendAsync("BetError", errorMessage);
                return;

            Success:
                ;
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("BetError", "İstifadəçi tapılmadı");
                return;
            }

            decimal requiredAmount = amount - player.CurrentBet;
            if (user.Balance < requiredAmount)
            {
                await Clients.Caller.SendAsync("BetError",
                    $"Kifayət qədər balans yoxdur. Lazım: {requiredAmount}₼, Mövcud: {user.Balance}₼");
                return;
            }

            lock (room.StateLock)
            {
                decimal previousTableBet = room.CurrentBet;

                user.Balance -= requiredAmount;
                player.Balance = user.Balance;
                player.CurrentBet = amount;
                player.TotalBet += requiredAmount;

                // ✅ DƏRHAL POTA ƏLAVƏ ET
                room.PotAmount += requiredAmount;

                if (amount > room.CurrentBet)
                {
                    room.CurrentBet = amount;
                    room.LastRaiseAmount = Math.Round(amount - previousTableBet, 2);
                    room.RaiseCount++;
                    room.LastRaiserId = userId;
                    room.LastCallerId = 0;

                    Console.WriteLine($"📈 RAISE: {player.Name} | {requiredAmount}₼ → Total: {amount}₼ | POT: {room.PotAmount}₼");
                }
                else if (amount == room.CurrentBet && room.CurrentBet > 0)
                {
                    room.LastCallerId = userId;
                    Console.WriteLine($"📞 CALL: {player.Name} | {requiredAmount}₼ | POT: {room.PotAmount}₼");
                }
                else
                {
                    Console.WriteLine($"💰 BET: {player.Name} | {amount}₼ | POT: {room.PotAmount}₼");
                }
            }

            await _db.SaveChangesAsync();
            await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

            await Clients.Group(roomId).SendAsync("PlayerBet", new
            {
                playerName = player.Name,
                amount = requiredAmount,
                totalBet = amount,
                isRaise = amount > room.CurrentBet
            });

            await BroadcastRoomPlayers(roomId);

            // ✅ POT BROADCAST - HEMEN
            await BroadcastPotAmount(roomId);

            await CheckForceShowdown(roomId, userId, amount);
            await NextTurn(roomId);
        }
        private async Task CheckForceShowdown(string roomId, int bettingUserId, decimal betAmount)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<SekaPlayer> activePlayers;
            lock (room.StateLock)
            {
                activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
            }

            // ✅ YALNIZ 2 OYUNÇU QALIBSA
            if (activePlayers.Count != 2) return;

            var bettingPlayer = activePlayers.FirstOrDefault(p => p.UserId == bettingUserId);
            var opponent = activePlayers.FirstOrDefault(p => p.UserId != bettingUserId);

            if (bettingPlayer == null || opponent == null) return;

            // ✅ RƏQİBİN BALANSI MƏRC-DƏN AZDIR?
            decimal requiredAmount = room.CurrentBet - opponent.CurrentBet;

            if (opponent.Balance < requiredAmount)
            {
                // ✅ RƏQİB QARŞILAYA BİLMİR - AVTOMATIK SHOWDOWN
                await Clients.Group(roomId).SendAsync("ForceShowdown", new
                {
                    message = $"{opponent.Name} mərc etməyə kifayət pulu yoxdur. Kartlar açılır!",
                    bettingPlayer = bettingPlayer.Name,
                    opponent = opponent.Name,
                    betAmount = betAmount,
                    opponentBalance = opponent.Balance
                });

                await Task.Delay(2000);
                await DetermineWinner(roomId);
            }
        }
        public async Task Fold()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            SekaPlayer? player = null;

            lock (room.StateLock)
            {
                if (room.CurrentTurnUserId != userId)
                {
                    _ = Clients.Caller.SendAsync("FoldError", "Sizin növbəniz deyil");
                    return;
                }
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;
                player.HasFolded = true;
                player.IsActive = false;

                //room.LastCallerId = 0;
                room.LastFolderId = userId;
                room.LastRaiserId = 0;
            }

            await Clients.Group(roomId).SendAsync("PlayerFolded", player.Name);
            await UpdateFoldRank(room, player, "manual-fold");
            await CheckAllFolded(roomId);
            await NextTurn(roomId);
        }

        // ==================== CALL FUNKSİYASI ====================
        public async Task Call()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;
            var userId = GetUserId();
            if (userId == 0) return;

            SekaPlayer? player = null;
            decimal callAmount = 0;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsGameFinished)
                { _ = Clients.Caller.SendAsync("ActionError", "Oyun davam etmir"); return; }

                if (room.CurrentTurnUserId != userId)
                { _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil"); return; }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.HasFolded)
                { _ = Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı"); return; }

                // ✅ Call = room.CurrentBet - player.CurrentBet (fərq)
                // Əgər heç kim raise etməyibsə → EntryFee
                callAmount = GetActionCallAmount(room, player);
                if (callAmount <= 0)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Bu anda call etmək mümkün deyil");
                    return;
                }
                Console.WriteLine($"📞 CALL: {player.Name} | room.CurrentBet={room.CurrentBet}₼ | player.CurrentBet={player.CurrentBet}₼ | Ödəyəcək={callAmount}₼");
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            { await Clients.Caller.SendAsync("ActionError", "İstifadəçi tapılmadı"); return; }

            if (user.Balance < callAmount)
            { await Clients.Caller.SendAsync("ActionError", $"Kifayət qədər balans yoxdur. Lazım: {callAmount}₼, Mövcud: {user.Balance}₼"); return; }

            lock (room.StateLock)
            {
                decimal previousTableBet = room.CurrentBet;

                user.Balance -= callAmount;
                player.Balance = user.Balance;
                player.CurrentBet += callAmount;
                player.TotalBet += callAmount;
                room.PotAmount += callAmount;

                // Açılış call-u masadakı cari total bet-i formalaşdırır.
                if (player.CurrentBet > room.CurrentBet)
                {
                    room.CurrentBet = player.CurrentBet;
                    room.LastRaiseAmount = Math.Round(room.CurrentBet - previousTableBet, 2);
                }

                room.LastCallerId = userId;

                Console.WriteLine($"✅ CALL: {player.Name} | Ödədi={callAmount}₼ | PlayerBet={player.CurrentBet}₼ | POT={room.PotAmount}₼");
            }

            await _db.SaveChangesAsync();
            await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
            await Clients.Group(roomId).SendAsync("PlayerBet", new
            {
                playerName = player.Name,
                amount = callAmount,
                totalBet = player.CurrentBet,
                isCall = true
            });
            await BroadcastRoomPlayers(roomId);
            await BroadcastPotAmount(roomId);
            await NextTurn(roomId);
        }

        // ==================== SHOWDOWN CALL METODU ====================
        public async Task ShowdownCall()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;
            var userId = GetUserId();
            if (userId == 0) return;

            SekaPlayer? player = null;
            decimal callAmount = 0;

            lock (room.StateLock)
            {
                if (room.CurrentTurnUserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
                player = activePlayers.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                    return;
                }

                if (!CanPlayerShowdownCall(room, userId))
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Hazırda Showdown Call mümkün deyil");
                    return;
                }

                // ShowdownCall bu turn üçün Call ilə eyni məbləği ödəyir.
                callAmount = GetActionCallAmount(room, player);

                room.CurrentPhase = GamePhase.Showdown;
                room.LastCallerId = 0;

                Console.WriteLine($"⭐ SHOWDOWN CALL: {player.Name} | CurrentBet={room.CurrentBet}₼ | MyBet={player.CurrentBet}₼ | Ödəyəcəyi={callAmount}₼");
            }

            if (callAmount > 0)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
                if (user == null)
                {
                    await Clients.Caller.SendAsync("ActionError", "İstifadəçi tapılmadı");
                    return;
                }
                if (user.Balance < callAmount)
                {
                    await Clients.Caller.SendAsync("ActionError", $"Kifayət qədər balans yoxdur. Lazım: {callAmount}₼");
                    return;
                }

                lock (room.StateLock)
                {
                    decimal previousTableBet = room.CurrentBet;

                    user.Balance -= callAmount;
                    player.Balance = user.Balance;
                    player.CurrentBet += callAmount;
                    player.TotalBet += callAmount;
                    room.PotAmount += callAmount;

                    // ✅ CurrentBet yalnız bu call nəticəsində artırsa yenilə
                    if (player.CurrentBet > room.CurrentBet)
                    {
                        room.CurrentBet = player.CurrentBet;
                        room.LastRaiseAmount = Math.Round(room.CurrentBet - previousTableBet, 2);
                    }

                    Console.WriteLine($"✅ SHOWDOWN CALL TƏSDİQ: {player.Name} | Ödədi={callAmount}₼ | PlayerBet={player.CurrentBet}₼ | POT={room.PotAmount}₼");
                }

                await _db.SaveChangesAsync();
                await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

                await Clients.Group(roomId).SendAsync("PlayerBet", new
                {
                    playerName = player.Name,
                    amount = callAmount,
                    totalBet = player.CurrentBet,
                    potAmount = room.PotAmount,
                    isShowdownCall = true,
                });

                await BroadcastRoomPlayers(roomId);
                await BroadcastPotAmount(roomId);
            }

            await Clients.Group(roomId).SendAsync("ShowdownCallMade", new
            {
                playerName = player.Name,
                message = "Kartlar açılır! 🎴"
            });

            CancelTurnTimer(roomId);
            lock (room.StateLock)
            {
                room.TurnStartTime = null;
                room.CurrentTurnUserId = 0;
            }
            await BroadcastGameStateWithContext(roomId);
            await Task.Delay(100);
            await DetermineWinner(roomId);
        }

        public async Task Raise(decimal raiseAmount)
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;
            var userId = GetUserId();
            if (userId == 0) return;
            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);

            SekaPlayer? player = null;
            string? errorMessage = null;
            decimal requiredAmount = 0;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsGameFinished)
                { errorMessage = "Oyun davam etmir"; goto SendError; }

                if (room.CurrentTurnUserId != userId)
                { errorMessage = "Sizin növbəniz deyil"; goto SendError; }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.HasFolded)
                { errorMessage = "Oyunçu tapılmadı"; goto SendError; }

                if (user == null)
                { errorMessage = "İstifadəçi tapılmadı"; goto SendError; }

                // ✅ Max: EntryFee * 100, balansla məhdudlaşır
                decimal sekaMinRaise = GetMinimumRaiseAmount(room, player);
                decimal sekaMaxRaise = GetMaximumRaiseIncrement(room, player);
                if (sekaMinRaise > sekaMaxRaise) sekaMinRaise = sekaMaxRaise;

                if (raiseAmount <= 0)
                {
                    errorMessage = "Raise məbləği 0-dan böyük olmalıdır";
                    goto SendError;
                }

                if (raiseAmount < sekaMinRaise)
                { errorMessage = $"Minimum raise: {sekaMinRaise}₼"; goto SendError; }

                if (raiseAmount > sekaMaxRaise)
                { errorMessage = $"Maksimum raise: {sekaMaxRaise}₼"; goto SendError; }

                requiredAmount = Math.Round(raiseAmount, 2);

                if (user.Balance < requiredAmount)
                { errorMessage = $"Balans kifayət deyil. Lazım: {requiredAmount}₼"; goto SendError; }

                decimal targetTotalBet = Math.Round(player.CurrentBet + requiredAmount, 2);
                user.Balance -= requiredAmount;
                player.Balance = user.Balance;
                player.CurrentBet = targetTotalBet;
                player.TotalBet += requiredAmount;
                room.CurrentBet = targetTotalBet;
                room.LastRaiseAmount = Math.Round(raiseAmount, 2);
                room.PotAmount += requiredAmount;
                room.RaiseCount++;
                room.LastRaiserId = userId;
                room.LastCallerId = 0;

                Console.WriteLine($"✅ RAISE: {player.Name} | RaiseMeblegi={raiseAmount}₼ | Ödədi={requiredAmount}₼ | PlayerBet={player.CurrentBet}₼ | room.CurrentBet={room.CurrentBet}₼ | POT={room.PotAmount}₼");

                goto Success;

            SendError:
                _ = Clients.Caller.SendAsync("RaiseError", errorMessage);
                return;
            Success:;
            }

            if (player != null && user != null)
            {
                await _db.SaveChangesAsync();
                await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                await Clients.Group(roomId).SendAsync("PlayerBet", new
                {
                    playerName = player.Name,
                    amount = requiredAmount,
                    paidAmount = requiredAmount,
                    raiseIncrement = room.LastRaiseAmount,
                    totalBet = player.CurrentBet,
                    isRaise = true
                });
                await BroadcastRoomPlayers(roomId);
                await BroadcastPotAmount(roomId);
                await NextTurn(roomId);
            }
        }
        public async Task AllIn()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null) return;

            SekaPlayer? player = null;

            lock (room.StateLock)
            {
                if (room.CurrentTurnUserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null) return;

                player.IsAllIn = true;

                // ✅ DOĞRU - CurrentBet kullan
                decimal currentBet = room.CurrentBet;
                decimal playerBetAmount = player.CurrentBet;  // ← CurrentBet!
                decimal remainingToCall = currentBet - playerBetAmount;

                if (remainingToCall < 0m)
                {
                    remainingToCall = 0m;
                }

                if (user.Balance >= remainingToCall)
                {
                    decimal allInAmount = remainingToCall;

                    Console.WriteLine($"💰 {player.Name} All-In: {allInAmount} AZN (Full call)");

                    _ = PlaceBet(allInAmount);

                    _ = Clients.Group(roomId).SendAsync("PlayerAllIn", new
                    {
                        playerName = player.Name,
                        allInAmount = allInAmount,
                        playerBalance = user.Balance - allInAmount,
                        isFullCall = true
                    });
                }
                else
                {
                    decimal allInAmount = user.Balance;
                    decimal shortfall = remainingToCall - allInAmount;

                    Console.WriteLine($"💰 {player.Name} All-In: {allInAmount} AZN (Insufficient balance, shortfall: {shortfall})");

                    _ = PlaceBet(allInAmount);

                    _ = Clients.Group(roomId).SendAsync("PlayerAllIn", new
                    {
                        playerName = player.Name,
                        allInAmount = allInAmount,
                        playerBalance = 0m,
                        isFullCall = false,
                        shortfall = shortfall
                    });
                }
            }

            await CheckForceShowdown(roomId, userId, user.Balance);
        }
        public async Task RequestShowdown()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            bool canShowdown = false;
            lock (room.StateLock)
            {
                var activePlayers = room.Players.Where(p => !p.HasFolded).ToList();

                if (activePlayers.Count >= 2)
                {
                    var allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);
                    if (allBetsEqual)
                    {
                        canShowdown = true;
                    }
                }
            }

            if (canShowdown)
            {
                await Clients.Group(roomId).SendAsync("ShowdownRequested");
                await Task.Delay(1000);
                await DetermineWinner(roomId);
            }
            else
            {
                await Clients.Caller.SendAsync("ActionError", "Showdown üçün hamı eyni məbləğ qoymalıdır");
            }
        }

        private async Task DetermineWinner(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<SekaPlayer> activePlayers;
            lock (room.StateLock)
            {
                // ✅ Svara durumunda yalnız tied players
                if (room.CurrentPhase == GamePhase.Svara)
                {
                    activePlayers = room.Players
                        .Where(p => room.SvaraParticipants.Contains(p.UserId))
                        .ToList();
                }
                else
                {
                    activePlayers = room.Players.Where(p => !p.HasFolded).ToList();
                }
            }

            // ✅ Tək oyunçu qalıbsa (fold durumu)
            if (activePlayers.Count == 1)
            {
                var winner = activePlayers[0];
                await AwardWinner(roomId, winner, "Bütün oyunçular fold etdi", null);
                return;
            }

            // ✅ Kartları masada göstər
            var playersWithHands = activePlayers.Select(p => new
            {
                userId = p.UserId,
                name = p.Name,
                hand = p.Hand.Select(c => new { suit = c.Suit, rank = c.Rank }).ToList(),
                handValue = SekaHandEvaluator.EvaluateHand(p.Hand),
                handScore = SekaHandEvaluator.CalculateHandScore(p.Hand)
            }).ToList();

            await _hubContext.Clients.Group(roomId).SendAsync("ShowCardsOnTable", playersWithHands);
            await _hubContext.Clients.Group(roomId).SendAsync("ShowdownStart", playersWithHands);
            await Task.Delay(3000);

            // ✅ Sıralama
            var rankedPlayers = activePlayers
                .Select(p => new PlayerHandRank
                {
                    Player = p,
                    HandValue = SekaHandEvaluator.EvaluateHand(p.Hand),
                    HandScore = SekaHandEvaluator.CalculateHandScore(p.Hand),
                    TieBreakScore = GetDeterministicTieBreakScore(p.Hand)
                })
                .OrderByDescending(x => x.HandScore)
                .ThenByDescending(x => x.TieBreakScore)
                .ToList();

            var topScore = rankedPlayers[0].HandScore;

            var tiedWinners = rankedPlayers
                .Where(x => x.HandScore == topScore)
                .ToList();

            var winners = tiedWinners
                .Take(2)
                .Select(x => x.Player)
                .ToList();

            Console.WriteLine($"🎯 KAZANANLAR KONTROLÜ:");
            Console.WriteLine($"  Top Score: {topScore}");
            Console.WriteLine($"  Tied Count: {tiedWinners.Count}");
            Console.WriteLine($"  Payout Count: {winners.Count}");
            foreach (var w in winners)
            {
                Console.WriteLine($"    ✅ {w.Name}");
            }

            if (tiedWinners.Count > 2)
            {
                Console.WriteLine($"⚠️ 2-dən çox eyni xal tapıldı. Payout yalnız ilk 2 oyunçu arasında bölünəcək.");
            }

            // ✅ TƏK QALIB
            if (winners.Count == 1)
            {
                var winnerData = rankedPlayers[0];
                var handName = SekaHandEvaluator.GetHandName(winnerData.HandValue);
                await AwardWinner(roomId, winners[0], $"Qalib: {handName}", rankedPlayers);
            }
            // ✅ BERABƏRLİK → POT BÖLÜNSÜNÜprofessional
            else
            {
                Console.WriteLine($"🤝 TIE! {winners.Count} oyunçu aynı skora sahip | Pot bölünür");
                await SplitPotEqually(roomId, winners, rankedPlayers, tiedWinners.Count);
            }
        }
        private async Task SplitPotEqually(string roomId, List<SekaPlayer> winners, List<PlayerHandRank> rankedPlayers, int tiedCount)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            decimal totalPot = room.PotAmount;

            if (room.CurrentPhase == GamePhase.Svara && room.FrozenPot > 0)
            {
                totalPot += room.FrozenPot;
            }

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ SplitPotEqually skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                room.CurrentPhase = GamePhase.Finished;
            }

            decimal commission = totalPot * COMMISSION_RATE;
            decimal netPot = totalPot - commission;
            decimal splitAmount = netPot / winners.Count;

            Console.WriteLine($"💰 POT BÖLME: Total={totalPot}₼, Commission={commission}₼, Each={splitAmount}₼");

            // ✅ KAZANANLARA PARA VER
            foreach (var winner in winners)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (user != null)
                {
                    user.Balance += splitAmount;
                    await _db.SaveChangesAsync();

                    try
                    {
                        await _rankService.UpdateRankAfterGame(winner.UserId, GameType.Seka, isWin: true, earnings: splitAmount);
                        var rankDetails = await _rankService.GetPlayerRankDetails(winner.UserId, GameType.Seka);

                        await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = rankDetails.CurrentRank,
                            level = rankDetails.RankLevel,
                            xp = rankDetails.ExperiencePoints,
                            requiredXP = rankDetails.RequiredXPForNextRank,
                            progress = rankDetails.ProgressPercentage
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Winner rank error: {ex.Message}");
                    }

                    await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                    Console.WriteLine($"✅ {winner.Name} kazandı: {splitAmount}₼");
                }
            }

            // ✅ KAYBEDENLERİN RANK'İNİ GÜNCELLE
            if (rankedPlayers != null)
            {
                var losers = rankedPlayers
                    .Where(p => !winners.Any(w => w.UserId == p.Player.UserId))
                    .Select(p => p.Player)
                    .ToList();

                foreach (var loser in losers)
                {
                    try
                    {
                        // ✅ KAYBEDENLER - Entry fee kadarını loss olarak kaydet
                        await _rankService.UpdateRankAfterGame(
                            loser.UserId,
                            GameType.Seka,
                            isWin: false,
                            earnings: loser.TotalBet
                        );

                        var loserRank = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Seka);

                        await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = loserRank.CurrentRank,
                            level = loserRank.RankLevel,
                            xp = loserRank.ExperiencePoints,
                            requiredXP = loserRank.RequiredXPForNextRank,
                            progress = loserRank.ProgressPercentage
                        });

                        Console.WriteLine($"❌ {loser.Name} kaybetti: {room.EntryFee}₼");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Loser rank error: {ex.Message}");
                    }
                }
            }

            // ✅ SONUÇLARI GÖNDER
            object? results = null;
            if (rankedPlayers != null)
            {
                results = rankedPlayers.Select((p, index) => new
                {
                    rank = index + 1,
                    name = p.Player.Name,
                    handName = SekaHandEvaluator.GetHandName(p.HandValue),
                    hand = p.Player.Hand.Select(c => new { suit = c.Suit, rank = c.Rank }).ToList(),
                    isWinner = winners.Any(w => w.UserId == p.Player.UserId),
                    earnings = winners.Any(w => w.UserId == p.Player.UserId) ? splitAmount : 0
                }).ToList();
            }

            // ✅ OYUNCULARA SONUÇ GÖNDER
            await _hubContext.Clients.Group(roomId).SendAsync("TieResult", new
            {
                winMode = "split",
                tiedCount,
                payoutCount = winners.Count,
                message = tiedCount > 2
                    ? $"🤝 {tiedCount} oyuncu eyni xal aldı, amma payout 2 nəfər arasında bölündü."
                    : $"🤝 {winners.Count} oyuncu aynı skora sahip! Pot eşit bölündü.",
                winners = winners.Select(w => w.Name).ToArray(),
                splitAmount = splitAmount,
                totalPot = totalPot,
                commission = commission,
                commissionRate = COMMISSION_RATE,
                results = results
            });

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                winMode = "split",
                tiedCount,
                payoutCount = winners.Count,
                winners = winners.Select(w => w.Name).ToArray(),
                amount = splitAmount,
                totalPot = totalPot,
                commission = commission,
                commissionRate = COMMISSION_RATE,
                reason = tiedCount > 2
                    ? "Berabərlik - payout 2 nəfər arasında bölündü"
                    : "Berabərlik - pot bölündü",
                results = results
            });

            Console.WriteLine($"🎉 Berabere bitdi: {string.Join(", ", winners.Select(w => w.Name))}");

            lock (room.StateLock)
            {
                room.PotAmount = 0;
                room.FrozenPot = 0;
                room.CurrentPhase = GamePhase.Finished;
                room.IsGameFinished = true;
                room.TurnStartTime = null;
                room.CurrentTurnUserId = 0;
            }

            await StartHandPauseTimeout(roomId);
        }
        private async Task HandleTieWithSvara(string roomId, List<SekaPlayer> tiedPlayers, List<PlayerHandRank> rankedPlayers)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                // ✅ İLK SVARA - POTU DONDUR
                if (room.CurrentPhase != GamePhase.Svara)
                {
                    room.FrozenPot = room.PotAmount; // ✅ Əvvəlki potu dondur
                    room.CurrentPhase = GamePhase.Svara;
                    room.SvaraRound = 1;
                    room.PotAmount = 0; // ✅ Yeni mərc potu sıfırla

                    Console.WriteLine($"🔥 SVARA BAŞLADI | Frozen: {room.FrozenPot}₼");
                }
                else
                {
                    room.SvaraRound++;
                    Console.WriteLine($"🔥 SVARA RAUND {room.SvaraRound} | Frozen: {room.FrozenPot}₼ | Current: {room.PotAmount}₼");
                }

                room.SvaraParticipants = tiedPlayers.Select(p => p.UserId).ToList();
            }

            var tiedPlayerNames = string.Join(", ", tiedPlayers.Select(p => p.Name));

            await _hubContext.Clients.Group(roomId).SendAsync("SvaraAnnounced", new
            {
                message = $"🔥 BERABƏRLIK! Avtomatik SVARA başlayır!",
                tiedPlayers = tiedPlayerNames,
                svaraRound = room.SvaraRound,
                requiredBet = room.EntryFee,
                frozenPot = room.FrozenPot,
                currentPot = room.PotAmount
            });

            await Task.Delay(3000);

            // ✅ SVARA MƏRCLƏRI
            var foldedPlayers = new List<string>();

            foreach (var player in tiedPlayers)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == player.UserId);
                if (user == null || user.Balance < room.EntryFee)
                {
                    player.HasFolded = true;
                    player.IsActive = false;
                    foldedPlayers.Add(player.Name);

                    await _hubContext.Clients.Group(roomId).SendAsync("PlayerForcedFold", new
                    {
                        playerName = player.Name,
                        reason = "Svara üçün kifayət pul yoxdur"
                    });

                    Console.WriteLine($"❌ {player.Name} fold oldu (pul yoxdur)");
                    continue;
                }

                // ✅ MƏRC ET
                user.Balance -= room.EntryFee;
                player.Balance = user.Balance;
                player.CurrentBet = room.EntryFee; // ✅ Svara mərcini təyin et
                room.PotAmount += room.EntryFee; // ✅ Yeni pota əlavə et

                await _db.SaveChangesAsync();
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

                Console.WriteLine($"✅ {player.Name} svara mərcini etdi: {room.EntryFee}₼");
            }

            // ✅ YALNIZ 1 NƏFƏR QALDISA
            var remainingPlayers = tiedPlayers.Where(p => !p.HasFolded).ToList();
            if (remainingPlayers.Count == 1)
            {
                var winner = remainingPlayers[0];

                // ✅ HƏM FROZEN HƏM CURRENT POTU BİRLƏŞDİR
                lock (room.StateLock)
                {
                    room.PotAmount += room.FrozenPot;
                    room.FrozenPot = 0;
                    room.CurrentPhase = GamePhase.Finished;
                }

                await _hubContext.Clients.Group(roomId).SendAsync("SvaraResult", new
                {
                    message = $"{winner.Name} qalib oldu! (Digərləri fold etdi)",
                    totalPot = room.PotAmount,
                    foldedPlayers = foldedPlayers
                });

                await Task.Delay(2000);
                await AwardWinner(roomId, winner, "Digərləri svara mərcini edə bilmədi", null);
                return;
            }

            await BroadcastPotAmount(roomId);

            // ✅ YENİ KARTLAR VER
            lock (room.StateLock)
            {
                room.Deck = SekaCardDeck.CreateShuffledDeck();

                foreach (var player in remainingPlayers)
                {
                    player.Hand = room.Deck.Take(3).ToList();
                    room.Deck.RemoveRange(0, 3);
                    player.CurrentBet = 0; // ✅ Kart paylandıqdan sonra mərc reset
                }

                room.CurrentBet = 0; // ✅ Otaq mərcini də reset et
            }

            await _hubContext.Clients.Group(roomId).SendAsync("SvaraCardsDealt", new
            {
                message = $"🃏 Svara kartları paylandı! (Raund {room.SvaraRound})",
                totalPot = room.PotAmount,
                frozenPot = room.FrozenPot
            });

            foreach (var player in remainingPlayers)
            {
                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("CardsDealt", new
                {
                    hand = player.Hand.Select(c => new { suit = c.Suit, rank = c.Rank })
                });
            }

            await Task.Delay(2000);

            // ✅ YENİDƏN QALİB TƏYİN ET
            await DetermineWinner(roomId);
        }
        private class PlayerHandRank
        {
            public SekaPlayer Player { get; set; }
            public SekaHandValue HandValue { get; set; }
            public int HandScore { get; set; }
            public int TieBreakScore { get; set; }
        }

        private static int GetDeterministicTieBreakScore(List<SekaCard> hand)
        {
            unchecked
            {
                int hash = 17;
                foreach (var card in hand
                    .OrderBy(c => c.Suit)
                    .ThenBy(c => c.Rank))
                {
                    hash = hash * 31 + card.Suit.GetHashCode(StringComparison.Ordinal);
                    hash = hash * 31 + card.Rank.GetHashCode(StringComparison.Ordinal);
                }

                return Math.Abs(hash);
            }
        }

        public async Task ReBuy()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId))
            {
                await Clients.Caller.SendAsync("ReBuyError", "Otaq bulunamadı");
                return;
            }

            var room = _roomManager.GetRoom(roomId);
            if (room == null)
            {
                await Clients.Caller.SendAsync("ReBuyError", "Otaq bulunamadı");
                return;
            }

            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("ReBuyError", "Kullanıcı bulunamadı");
                return;
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("ReBuyError", "Kullanıcı veritabanında bulunamadı");
                return;
            }

            SekaPlayer? player = null;
            string? errorMessage = null;

            lock (room.StateLock)
            {
                // ✅ OYUNCU OTAQTA VAR MI?
                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null)
                {
                    errorMessage = "Otaqta oyuncu bulunamadı";
                    goto SendError;
                }

                // ✅ OYUN DAVAM EDIYORSA REBUY YAPILAMAZ
                if (room.IsGameStarted && !room.IsGameFinished)
                {
                    errorMessage = "Oyun davam ederken rebuy yapılamaz";
                    goto SendError;
                }

                // ✅ OYUNCU ZATEN PARA VARSA REBUY YAPAMAZ
                if (player.Balance > 0)
                {
                    errorMessage = "Zaten paranız var, rebuy gerekmez";
                    goto SendError;
                }
            }

            // ✅ REBUY TUTARINI KONTROL ET (ENTRY FEE TUTARINDA)
            decimal rebuyAmount = room.EntryFee;

            if (user.Balance < rebuyAmount)
            {
                await Clients.Caller.SendAsync("ReBuyError",
                    $"Rebuy için yeterli balansınız yok. Gerekli: {rebuyAmount}₼, Mevcut: {user.Balance}₼");
                return;
            }

            // ✅ REBUY TUTARINI ÇIK
            user.Balance -= rebuyAmount;

            lock (room.StateLock)
            {
                player.Balance = user.Balance;
                room.PotAmount += rebuyAmount;

                Console.WriteLine($"🔄 REBUY: {player.Name} | Tutar: {rebuyAmount}₼ | Yeni Balans: {user.Balance}₼");
            }

            await _db.SaveChangesAsync();

            // ✅ OYUNCUYA BİLDİR
            await Clients.Client(player.ConnectionId).SendAsync("ReBuySuccess", new
            {
                newBalance = user.Balance,
                rebuyAmount = rebuyAmount,
                message = $"✅ Rebuy başarılı! {rebuyAmount}₼ düşüldü"
            });

            await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

            // ✅ OTAQ OYUNCULARINA BİLDİR
            await Clients.Group(roomId).SendAsync("PlayerReBuy", new
            {
                playerName = player.Name,
                newBalance = user.Balance,
                rebuyAmount = rebuyAmount
            });

            await BroadcastRoomPlayers(roomId);
            await BroadcastPotAmount(roomId);

            return;

        SendError:
            await Clients.Caller.SendAsync("ReBuyError", errorMessage);
            Console.WriteLine($"❌ ReBuy hatası: {errorMessage}");
        }
        public async Task AllowReBuyAfterGame(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            // ✅ OYUN BITMIŞ MI?
            if (!room.IsGameFinished)
            {
                return;
            }

            lock (room.StateLock)
            {
                // ✅ PARA YOKSUN OYUNCULARI İŞARETLE
                foreach (var player in room.Players.Where(p => p.Balance == 0))
                {
                    player.CanBeBuy = true; // ✅ YENI PROPERTY
                    Console.WriteLine($"✅ {player.Name} rebuy yapabilir");
                }
            }

            // ✅ TÜM OYUNCULARA ReBuy ŞANSI GÖNDER
            await _hubContext.Clients.Group(roomId).SendAsync("ReBuyAvailable", new
            {
                message = "Rebuy şansınız var! 30 saniye içinde karar verin.",
                timeLimit = 30
            });

            // ✅ 30 SANİYE BEKLE, SONRA RESET YAP
            await Task.Delay(30000);

            lock (room.StateLock)
            {
                room.CanBeBuy = false; // ✅ ReBuy SURELİ YAPILABİLİR
            }

            // ✅ RESET BAŞLA
            await ResetGame(roomId);
        }
        private async Task AwardWinner(string roomId, SekaPlayer winner, string reason, List<PlayerHandRank>? rankedPlayers)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ AwardWinner skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                room.CurrentPhase = GamePhase.Finished;
            }

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
            if (user != null)
            {
                // ✅ KAZANAN - Komisyon hesapla
                decimal totalPot = room.PotAmount;

                if (room.CurrentPhase == GamePhase.Svara && room.FrozenPot > 0)
                {
                    totalPot += room.FrozenPot;
                }

                decimal commission = totalPot * COMMISSION_RATE;
                decimal winAmount = totalPot - commission;

                user.Balance += winAmount;
                await _db.SaveChangesAsync();

                // ✅ KAZANANI RANK'İ GÜNCELLE
                try
                {
                    await _rankService.UpdateRankAfterGame(winner.UserId, GameType.Seka, isWin: true, earnings: winAmount);
                    var rankDetails = await _rankService.GetPlayerRankDetails(winner.UserId, GameType.Seka);

                    await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                    {
                        rank = rankDetails.CurrentRank,
                        level = rankDetails.RankLevel,
                        xp = rankDetails.ExperiencePoints,
                        requiredXP = rankDetails.RequiredXPForNextRank,
                        progress = rankDetails.ProgressPercentage
                    });
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"❌ Winner rank update error: {ex.Message}");
                }

                await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                Console.WriteLine($"💰 Qalib: {winner.Name} | Total Pot: {totalPot}₼ | Kazanılan: {winAmount}₼ | Komisyon: {commission}₼");
            }

            // ✅ KAYBEDENLERİN RANK'İNİ GÜNCELLE
            if (rankedPlayers != null)
            {
                var losers = rankedPlayers.Where(p => p.Player.UserId != winner.UserId).ToList();

                foreach (var loserRank in losers)
                {
                    var loser = loserRank.Player;

                    try
                    {
                        // ✅ KAYBEDENLER - Entry fee kadarını loss olarak kaydet
                        await _rankService.UpdateRankAfterGame(
                            loser.UserId,
                            GameType.Seka,
                            isWin: false,
                            earnings: loser.TotalBet  // ✅ Entry fee = ziyar
                        );

                        var loserRankDetails = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Seka);

                        await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = loserRankDetails.CurrentRank,
                            level = loserRankDetails.RankLevel,
                            xp = loserRankDetails.ExperiencePoints,
                            requiredXP = loserRankDetails.RequiredXPForNextRank,
                            progress = loserRankDetails.ProgressPercentage
                        });

                        Console.WriteLine($"❌ Kaybeden: {loser.Name} | Loss: {room.EntryFee}₼");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Loser rank update error: {ex.Message}");
                    }
                }
            }

            // ✅ KAZANAN DUYURUSU
            decimal displayAmount = (room.PotAmount + room.FrozenPot) - ((room.PotAmount + room.FrozenPot) * COMMISSION_RATE);

            await _hubContext.Clients.Group(roomId).SendAsync("WinnerAnnounced", new
            {
                winMode = "single",
                winnerName = _db.Users

               .Where(u => u.Id == winner.UserId)

               .Select(u => u.UserName)
               .FirstOrDefault(),
                winnerUserId = winner.UserId,
                amount = displayAmount,
                commission = (room.PotAmount + room.FrozenPot) * COMMISSION_RATE,
                commissionRate = COMMISSION_RATE,
                reason = reason,
                wasSvara = room.CurrentPhase == GamePhase.Svara
            });

            Console.WriteLine($"🎉 Kazanan duyuruldu: {winner.Name}");

            lock (room.StateLock)
            {
                room.PotAmount = 0;
                room.FrozenPot = 0;
                room.TurnStartTime = null;
                room.CurrentTurnUserId = 0;
            }

            CancelTurnTimer(roomId);

            await StartHandPauseTimeout(roomId);
        }
        private async Task SendRankUpdate(int userId, string connectionId, GameType gameType)
        {
            try
            {
                var rankDetails = await _rankService.GetPlayerRankDetails(userId, gameType);

                await _hubContext.Clients.Client(connectionId).SendAsync("RankUpdated", new
                {
                    rank = rankDetails.CurrentRank,
                    level = rankDetails.RankLevel,
                    xp = rankDetails.ExperiencePoints,
                    requiredXP = rankDetails.RequiredXPForNextRank,
                    progress = rankDetails.ProgressPercentage
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Rank update error: {ex.Message}");
            }
        }

        private async Task SplitPot(string roomId, List<SekaPlayer> winners, List<PlayerHandRank>? rankedPlayers)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            decimal totalPot = room.PotAmount;
            lock (room.StateLock)
            {
                if (room.IsGameFinished)
                {
                    Console.WriteLine($"⚠️ SplitPot skipped: {room.RoomName} already finished");
                    return;
                }

                room.IsGameFinished = true;
                room.CurrentPhase = GamePhase.Finished;
            }

            decimal commission = totalPot * COMMISSION_RATE;
            decimal netPot = totalPot - commission;
            decimal splitAmount = netPot / winners.Count;

            foreach (var winner in winners)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == winner.UserId);
                if (user != null)
                {
                    user.Balance += splitAmount;
                    await _db.SaveChangesAsync();

                    // ✅ RANK YENİLƏ
                    try
                    {
                        await _rankService.UpdateRankAfterGame(winner.UserId, GameType.Seka, isWin: true, earnings: splitAmount);
                        var rankDetails = await _rankService.GetPlayerRankDetails(winner.UserId, GameType.Seka);

                        await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = rankDetails.CurrentRank,
                            level = rankDetails.RankLevel,
                            xp = rankDetails.ExperiencePoints,
                            requiredXP = rankDetails.RequiredXPForNextRank,
                            progress = rankDetails.ProgressPercentage
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Winner rank update error: {ex.Message}");
                    }

                    await _hubContext.Clients.Client(winner.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                }
            }

            // ✅ İTİRƏNLƏRİN RANK-INI YENİLƏ
            if (rankedPlayers != null)
            {
                var loserIds = rankedPlayers
                    .Where(p => !winners.Any(w => w.UserId == p.Player.UserId))
                    .Select(p => p.Player)
                    .ToList();

                foreach (var loser in loserIds)
                {
                    try
                    {
                        await _rankService.UpdateRankAfterGame(loser.UserId, GameType.Seka, isWin: false, earnings: loser.TotalBet);
                        var loserRank = await _rankService.GetPlayerRankDetails(loser.UserId, GameType.Seka);

                        await _hubContext.Clients.Client(loser.ConnectionId).SendAsync("RankUpdated", new
                        {
                            rank = loserRank.CurrentRank,
                            level = loserRank.RankLevel,
                            xp = loserRank.ExperiencePoints,
                            requiredXP = loserRank.RequiredXPForNextRank,
                            progress = loserRank.ProgressPercentage
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Loser rank update error: {ex.Message}");
                    }
                }
            }

            Console.WriteLine($"💰 Split: {winners.Count} winners | Each: {splitAmount}₼ | Commission: {commission}₼");

            object? results = null;
            if (rankedPlayers != null)
            {
                results = rankedPlayers.Select((p, index) => new
                {
                    rank = index + 1,
                    name = p.Player.Name,
                    handName = SekaHandEvaluator.GetHandName(p.HandValue),
                    hand = p.Player.Hand.Select(c => new { suit = c.Suit, rank = c.Rank }).ToList(),
                    isWinner = winners.Any(w => w.UserId == p.Player.UserId)
                }).ToList();
            }

            await _hubContext.Clients.Group(roomId).SendAsync("GameOver", new
            {
                winMode = "split",
                winners = winners.Select(w => w.Name).ToArray(),
                amount = splitAmount,
                commission = commission,
                commissionRate = COMMISSION_RATE,
                reason = "Berabərlik - pot bölündü",
                results = results
            });

            // ✅ POT-U SIFIRLA
            room.PotAmount = 0;

            lock (room.StateLock)
            {
                room.IsGameFinished = true;
                room.TurnStartTime = null;
                room.CurrentTurnUserId = 0;
            }

            CancelTurnTimer(roomId);

            await StartHandPauseTimeout(roomId);
        }
        public async Task DealerCall()
        {
            var roomId = GetCurrentRoom();
            if (string.IsNullOrEmpty(roomId)) return;

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var userId = GetUserId();
            if (userId == 0) return;

            SekaPlayer? player = null;

            lock (room.StateLock)
            {
                if (!room.IsGameStarted || room.IsGameFinished)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyun davam etmir");
                    return;
                }

                if (room.CurrentTurnUserId != userId)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Sizin növbəniz deyil");
                    return;
                }

                player = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (player == null || player.HasFolded)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                    return;
                }

                // ✅ DEALER YOXLAMASI
                int playerIndex = room.Players.IndexOf(player);
                if (playerIndex != room.DealerIndex)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Yalnız dealer call edə bilər");
                    return;
                }

                // ✅ Artıq call edibsə
                if (player.CurrentBet >= room.EntryFee)
                {
                    _ = Clients.Caller.SendAsync("ActionError", "Artıq call etmisiniz");
                    return;
                }
            }

            // ✅ DEALER CALL - EntryFee məbləğində
            decimal dealerCallAmount = room.EntryFee;

            var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
            if (user == null)
            {
                await Clients.Caller.SendAsync("ActionError", "İstifadəçi tapılmadı");
                return;
            }

            decimal requiredAmount = dealerCallAmount - player.CurrentBet;

            if (user.Balance < requiredAmount)
            {
                await Clients.Caller.SendAsync("ActionError",
                    $"Kifayət qədər balans yoxdur. Lazım: {requiredAmount}₼");
                return;
            }

            lock (room.StateLock)
            {
                user.Balance -= requiredAmount;
                player.Balance = user.Balance;
                player.CurrentBet = dealerCallAmount;
                player.TotalBet += requiredAmount;
                room.PotAmount += requiredAmount;

                if (room.CurrentBet < dealerCallAmount)
                {
                    room.LastRaiseAmount = Math.Round(dealerCallAmount - room.CurrentBet, 2);
                    room.CurrentBet = dealerCallAmount;
                }

                // ✅ ÖNEMLİ: LastCallerId yeniləmə
                room.LastCallerId = userId;

                Console.WriteLine($"🎯 DEALER CALL: {player.Name} | {requiredAmount}₼ → Total: {dealerCallAmount}₼ | LastCallerId: {room.LastCallerId}");
            }

            await _db.SaveChangesAsync();
            await Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

            await Clients.Group(roomId).SendAsync("PlayerBet", new
            {
                playerName = player.Name,
                amount = requiredAmount,
                totalBet = dealerCallAmount,
                isDealerCall = true
            });

            await BroadcastRoomPlayers(roomId);
            await BroadcastPotAmount(roomId);
            await NextTurn(roomId);
        }
        private async Task ResetGame(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            CancelTurnTimer(roomId);

            Console.WriteLine($"🔄 ResetGame başladı: {room.RoomName}");
            Console.WriteLine($"  Önceki POT: {room.PotAmount}₼");

            lock (room.StateLock)
            {
                foreach (var player in room.Players.Where(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand))
                {
                    player.HasPaidEntryFee = false;
                }
            }

            // ✅ 1. WAITING OYUNÇULARINI AKTIVE ET
            var waitingPlayers = room.Players.Where(p => p.IsWaitingForNextRound && !p.IsPausedAfterHand).ToList();
            foreach (var player in waitingPlayers)
            {
                player.IsWaitingForNextRound = false;
                player.HasFolded = false;
                player.IsActive = true;
                player.ShowdownCall = false;

                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("JoinedGame",
                    "✅ Növbəti raunda qoşuldunuz!");
                Console.WriteLine($"✅ Waiting player activated: {player.Name}");
            }

            // ✅ 2. POT'U SIFIRLA (ÖNEMLİ!)
            lock (room.StateLock)
            {
                room.PotAmount = 0;
                room.FrozenPot = 0;
                room.NextCallAmount = 0;
                room.LastRaiseAmount = 0;
                room.CurrentBet = 0;
                Console.WriteLine($"  ✅ POT sıfırlandı: {room.PotAmount}₼");
            }

            // ✅ 3. BALANSI DOLDUR - YENI ROUND FEE ÇIK VE POT'A EKLE
            var activePlayers = room.Players.Where(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand).ToList();
            var playersToRemove = new List<SekaPlayer>();

            foreach (var player in activePlayers)
            {
                var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == player.UserId);
                if (user != null)
                {
                    if (player.HasPaidEntryFee)
                    {
                        player.Balance = user.Balance;
                        Console.WriteLine($"💰 {player.Name}: entry fee artıq ödənib | Balans: {user.Balance}₼ | POT: {room.PotAmount}₼");
                        continue;
                    }

                    // ✅ BALANS YOXLAMASI
                    if (user.Balance >= room.EntryFee)
                    {
                        // ✅ BALANSDAN ÇIX
                        user.Balance -= room.EntryFee;
                        player.Balance = user.Balance;
                        player.HasPaidEntryFee = true;

                        // ✅ POT'A EKLE
                        lock (room.StateLock)
                        {
                            room.PotAmount += room.EntryFee;
                        }

                        await _db.SaveChangesAsync();
                        await _hubContext.Clients.Client(player.ConnectionId).SendAsync("BalanceUpdated", user.Balance);

                        Console.WriteLine($"💰 {player.Name}: {room.EntryFee}₼ çıkıldı | Balans: {user.Balance}₼ | POT: {room.PotAmount}₼");
                    }
                    else
                    {
                        // ❌ PARA YOKSUNSA ÇIKAR
                        playersToRemove.Add(player);
                        await _hubContext.Clients.Client(player.ConnectionId).SendAsync("JoinError",
                            $"Kifayət qədər balans yoxdur. Minimum {room.EntryFee}₼ lazımdır.");
                        await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", player.Name);
                        Console.WriteLine($"❌ {player.Name} yeterli balansa sahip değil, odadan çıkarıldı");
                    }
                }
            }

            // ✅ 4. OTAQ DURUMUNU SIFIRLA
            lock (room.StateLock)
            {
                // Eksik oyunçuları kaldır
                foreach (var player in playersToRemove)
                {
                    room.Players.Remove(player);
                    _userRooms.TryRemove(player.ConnectionId, out _);
                }

                // Tüm oyuncu verilerini temizle
                foreach (var player in room.Players)
                {
                    player.Hand.Clear();
                    player.CurrentBet = 0;
                    player.TotalBet = 0;
                    if (player.IsPausedAfterHand)
                    {
                        player.HasFolded = true;
                        player.IsActive = false;
                        continue;
                    }

                    player.HasFolded = false;
                    player.IsActive = true;
                    player.HasChecked = false;
                    player.IsAllIn = false;
                    player.ShowdownCall = false;
                }

                // Otaq verilerini sıfırla
                room.IsGameStarted = false;
                room.IsGameFinished = false;
                room.CurrentBet = 0;
                room.CurrentRound = 0;
                room.CurrentPlayerIndex = 0;
                room.CurrentTurnUserId = 0;
                room.Deck.Clear();
                room.RaiseCount = 0;
                room.LastRaiserId = 0;
                room.LastCallerId = 0;
                room.LastFolderId = 0;

                room.TurnStartTime = null;
                room.CurrentPhase = GamePhase.Normal;
                room.CanBeBuy = false;
                room.ShowdownCallActivated = false;
                room.NextCallAmount = 0;

                Console.WriteLine($"✅ {room.RoomName} temizlendi | Oyuncu: {room.Players.Count} | POT: {room.PotAmount}₼");
            }

            // ✅ 5. YETERLI OYUNCU VARSA DEVAM ET
            if (room.Players.Count(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand) < 2)
            {
                // ❌ Eksik oyuncu varsa, son oyuncunun parasını geri ver
                var remainingRoundPlayers = room.Players.Where(p => !p.IsWaitingForNextRound && !p.IsPausedAfterHand).ToList();
                if (remainingRoundPlayers.Count == 1)
                {
                    var lastPlayer = remainingRoundPlayers[0];
                    var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == lastPlayer.UserId);
                    if (user != null)
                    {
                        user.Balance += room.EntryFee;
                        room.PotAmount = 0;
                        lastPlayer.HasPaidEntryFee = false;
                        await _db.SaveChangesAsync();
                        await _hubContext.Clients.Client(lastPlayer.ConnectionId).SendAsync("BalanceUpdated", user.Balance);
                    }
                }

                await _hubContext.Clients.Group(roomId).SendAsync("GameReset");
                Console.WriteLine($"⚠️ {room.RoomName} - yeterli oynayan oyuncu yok (En az 2 gerekli)");
                return;
            }

            // ✅ 6. OYUNCULARA BILDIRIM GÖNDER
            await _hubContext.Clients.Group(roomId).SendAsync("GameReset");
            await BroadcastRoomPlayers(roomId);

            // ✅ POT BROADCAST - HEM ENTRY FEE ALINIP HEM POT GUNCELLENDI
            await BroadcastPotAmount(roomId);

            Console.WriteLine($"🔄 {room.RoomName} resetlendi | Oyuncu: {room.Players.Count} | YENİ POT: {room.PotAmount}₼");

            // ✅ 7. OTOMATIK OYUN BAŞLAT (500ms gecikme)
            await Task.Delay(500);
            await AutoStartGameWithContext(roomId);
        }
        private async Task CheckAllFolded(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<SekaPlayer> activePlayers;
            lock (room.StateLock)
            {
                activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
                Console.WriteLine($"🔍 CheckAllFolded: {roomId} | Aktif oyuncu: {activePlayers.Count}");
            }

            // ✅ TAM 1 OYUNCU QALIBSA
            if (activePlayers.Count == 1)
            {
                var winner = activePlayers[0];
                Console.WriteLine($"  ✅ KAZANAN BULUNDU: {winner.Name}");
                await AwardWinner(roomId, winner, "Tüm oyunçular fold/disconnect etdi", null);
            }
            // ✅ 0 OYUNCU KALIBSA (ACIP OLMAYAN DURUM)
            else if (activePlayers.Count == 0)
            {
                Console.WriteLine($"  ❌ HATA: Hiç oyuncu kalmadı! Oyun reset edilecek");
                await ResetGame(roomId);
            }
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

        private string? ResolveCurrentRoomForUser(int userId)
        {
            var roomId = GetCurrentRoom();
            if (!string.IsNullOrWhiteSpace(roomId))
            {
                return roomId;
            }

            if (_userRoomByUserId.TryGetValue(userId, out var mappedRoomId) &&
                _roomManager.GetRoom(mappedRoomId)?.Players.Any(p => p.UserId == userId) == true)
            {
                _userRooms[Context.ConnectionId] = mappedRoomId;
                return mappedRoomId;
            }

            var activeRoom = _roomManager.GetAllRooms()
                .FirstOrDefault(r => _handPauseActiveRooms.ContainsKey(r.RoomId) &&
                                     r.Players.Any(p => p.UserId == userId));

            if (activeRoom != null)
            {
                _userRooms[Context.ConnectionId] = activeRoom.RoomId;
                _userRoomByUserId[userId] = activeRoom.RoomId;
                return activeRoom.RoomId;
            }

            return null;
        }

        private async Task BroadcastRoomPlayers(string roomId)
        {
            var userId = GetUserId();
            if (userId == 0)
            {
                await Clients.Caller.SendAsync("JoinError", "İstifadəçi tapılmadı");
                return;
            }

            var user = await _db.Users
                   .Where(u => u.Id == userId)
                   .Select(u => new { u.Id, u.UserName, u.Name, u.Surname, u.Balance })
                   .FirstOrDefaultAsync();

            if (user == null)
            {
                Console.WriteLine($"⚠️ User not found: {userId}");
                Context.Abort();
                return;
            }
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var playersList = room.Players.Select(p => new
            {
                userId = p.UserId,
                userName = _db.Users
                 .Where(u => u.Id == p.UserId)
                 .Select(u => u.UserName)
                 .FirstOrDefault(),

                balance = p.Balance,
                isActive = p.IsActive,
                hasFolded = p.HasFolded,
                isWaitingForNextRound = p.IsWaitingForNextRound,
                isPausedAfterHand = p.IsPausedAfterHand,
                currentBet = p.CurrentBet,
                totalBet = p.TotalBet,
                potContribution = (p.HasPaidEntryFee ? room.EntryFee : 0m) + p.TotalBet,
                profileImage = p.ProfileImage ?? "/assets/characters/default.png"
            }).ToList();

            await _hubContext.Clients.Group(roomId).SendAsync("PlayersList", playersList);
        }
        private async Task BroadcastPotAmount(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            decimal displayPot = 0;
            lock (room.StateLock)
            {
                displayPot = room.PotAmount;

                // ✅ SVARA VARSA FROZEN POT DA EKLENİYOR
                if (room.CurrentPhase == GamePhase.Svara && room.FrozenPot > 0)
                {
                    displayPot += room.FrozenPot;
                }
            }

            Console.WriteLine($"💰 POT BROADCAST: {roomId} | Display: {displayPot}₼ | Current: {room.PotAmount}₼");

            await _hubContext.Clients.Group(roomId).SendAsync("PotUpdated", new
            {
                potAmount = displayPot,
                currentPot = room.PotAmount,
                frozenPot = room.FrozenPot,
                totalPot = displayPot,
                timestamp = DateTime.UtcNow
            });
        }

        private async Task StartHandPauseTimeout(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            if (!_handPauseActiveRooms.TryAdd(roomId, 1))
            {
                Console.WriteLine($"⏸️ Hand pause already active, duplicate ignored before state reset: {roomId}");
                return;
            }

            var sessionId = Guid.NewGuid().ToString("N");

            List<(int UserId, string ConnectionId, string Name)> targetPlayers;
            lock (room.StateLock)
            {
                room.TurnStartTime = null;
                room.CurrentTurnUserId = 0;
                room.IsGameFinished = true;
                targetPlayers = room.Players
                    .Where(p => !p.IsWaitingForNextRound || p.IsPausedAfterHand)
                    .Select(p => (p.UserId, p.ConnectionId, p.Name))
                    .ToList();

                Console.WriteLine($"⏸️ StartHandPauseTimeout: room={roomId} session={sessionId} targets={string.Join(", ", targetPlayers.Select(p => $"{p.Name}#{p.UserId}"))}");

                foreach (var player in room.Players.Where(p => targetPlayers.Any(t => t.UserId == p.UserId)))
                {
                    player.HandPauseChoice = HandPauseChoice.None;
                    player.HandPauseDecisionAt = null;
                }
            }

            if (targetPlayers.Count == 0)
            {
                _handPauseActiveRooms.TryRemove(roomId, out _);
                await ResetGame(roomId);
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
                    message = "Əl bitdi. Davam edəcəksiniz, yoxsa 1 raund timeout?",
                    timeoutSeconds = HAND_PAUSE_TIMEOUT_SECONDS,
                    sessionId = sessionId
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

                await _hubContext.Clients.Clients(waitingConnectionIds).SendAsync("HandPauseTimer", new
                {
                    remainingSeconds = remaining
                });

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
            {
                return;
            }

            // Give in-flight button clicks a practical window to reach the hub before final removal.
            await Task.Delay(HAND_PAUSE_FINALIZE_GRACE_MS);

            if (!_handPauseActiveRooms.TryRemove(roomId, out _))
            {
                return;
            }

            _handPauseResponses.TryRemove(roomId, out var responses);
            _handPauseSessionIds.TryRemove(roomId, out var activeSessionId);
            responses ??= new ConcurrentDictionary<int, byte>();

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            List<(int UserId, string ConnectionId, string Name)> timedOutPlayers;
            List<(int UserId, string ConnectionId, string Name)> removedTimedOutPlayers;
            List<string> pausedPlayers;

            lock (room.StateLock)
            {
                timedOutPlayers = targetPlayers
                    .Where(p =>
                    {
                        var player = room.Players.FirstOrDefault(x => x.UserId == p.UserId);
                        var hasDecision = responses.ContainsKey(p.UserId) ||
                                          (player != null && player.HandPauseChoice != HandPauseChoice.None);

                        Console.WriteLine($"⏸️ HandPauseFinalizeCheck: room={roomId} user={p.UserId} hasDecision={hasDecision} playerState={(player == null ? "null" : $"{player.HandPauseChoice}/{player.IsPausedAfterHand}/{player.IsWaitingForNextRound}")}");

                        return !hasDecision;
                    })
                    .ToList();
                removedTimedOutPlayers = new List<(int UserId, string ConnectionId, string Name)>();

                pausedPlayers = room.Players
                    .Where(p => p.IsPausedAfterHand)
                    .Select(p => p.Name)
                    .ToList();

                foreach (var timedOutPlayer in timedOutPlayers)
                {
                    var player = room.Players.FirstOrDefault(p => p.UserId == timedOutPlayer.UserId);
                    if (player == null || player.ConnectionId != timedOutPlayer.ConnectionId)
                    {
                        continue;
                    }

                    player.HasFolded = true;
                    player.IsActive = false;
                    room.Players.Remove(player);
                    _userRooms.TryRemove(timedOutPlayer.ConnectionId, out _);
                    _userRoomByUserId.TryRemove(timedOutPlayer.UserId, out _);
                    removedTimedOutPlayers.Add(timedOutPlayer);
                }
            }

            await _hubContext.Clients.Group(roomId).SendAsync("HandPauseExpired", new
            {
                timeoutSeconds = HAND_PAUSE_TIMEOUT_SECONDS,
                pausedPlayers = pausedPlayers,
                timedOutPlayers = removedTimedOutPlayers.Select(p => p.Name).ToArray()
            });

            foreach (var timedOutPlayer in removedTimedOutPlayers)
            {
                await _hubContext.Clients.Client(timedOutPlayer.ConnectionId).SendAsync("RedirectToLobby", new
                {
                    message = "15 saniyə ərzində seçim etmədiniz. Lobby-yə yönləndirilirsiniz...",
                    reason = "hand_pause_timeout",
                    sessionId = activeSessionId
                });

                await Groups.RemoveFromGroupAsync(timedOutPlayer.ConnectionId, roomId);
                await _hubContext.Clients.Group(roomId).SendAsync("PlayerLeft", timedOutPlayer.Name);
            }

            await BroadcastRoomPlayers(roomId);

            if (room.Players.Count == 0 && room.CreatorUserId != 0)
            {
                _roomManager.DeleteRoom(roomId);
                Console.WriteLine($"  🗑️ Boş otaq silindi: {room.RoomName}");
                return;
            }

            await ResetGame(roomId);
        }

        public async Task HandPauseDecision(bool continuePlaying, string? sessionId = null)
        {
            var userId = GetUserId();
            if (userId == 0) return;

            var roomId = ResolveCurrentRoomForUser(userId);
            if (string.IsNullOrEmpty(roomId))
            {
                Console.WriteLine($"⏸️ HandPauseDecision ignored: room not found for conn={Context.ConnectionId} user={userId}");
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

            if (_handPauseSessionIds.TryGetValue(roomId, out var activeSessionId))
            {
                if (string.IsNullOrWhiteSpace(sessionId) || !string.Equals(activeSessionId, sessionId, StringComparison.Ordinal))
                {
                    await Clients.Caller.SendAsync("ActionError", "Köhnə pauza pəncərəsi");
                    return;
                }
            }

            Console.WriteLine($"⏸️ HandPauseDecision: room={roomId} user={userId} continue={continuePlaying} session={sessionId}");

            SekaPlayer? currentPlayer;
            lock (room.StateLock)
            {
                currentPlayer = room.Players.FirstOrDefault(p => p.UserId == userId);
                if (currentPlayer != null && currentPlayer.ConnectionId != Context.ConnectionId)
                {
                    _userRooms.TryRemove(currentPlayer.ConnectionId, out _);
                    currentPlayer.ConnectionId = Context.ConnectionId;
                    _userRooms[Context.ConnectionId] = roomId;
                    _userRoomByUserId[userId] = roomId;
                }
            }

            if (currentPlayer == null)
            {
                await Clients.Caller.SendAsync("ActionError", "Oyunçu tapılmadı");
                return;
            }

            var responses = _handPauseResponses.GetOrAdd(roomId, _ => new ConcurrentDictionary<int, byte>());

            if (continuePlaying)
            {
                responses[userId] = 1;
                lock (room.StateLock)
                {
                    currentPlayer.IsPausedAfterHand = false;
                    currentPlayer.IsWaitingForNextRound = false;
                    currentPlayer.HasFolded = false;
                    currentPlayer.IsActive = true;
                    currentPlayer.HandPauseChoice = HandPauseChoice.ContinuePlaying;
                    currentPlayer.HandPauseDecisionAt = DateTime.UtcNow;
                }

                await Clients.Caller.SendAsync("HandPauseDecisionAccepted", new
                {
                    continuePlaying = true
                });

                return;
            }

            lock (room.StateLock)
            {
                currentPlayer.IsPausedAfterHand = true;
                currentPlayer.IsWaitingForNextRound = true;
                currentPlayer.HasFolded = true;
                currentPlayer.IsActive = false;
                currentPlayer.HasPaidEntryFee = false;
                currentPlayer.CurrentBet = 0;
                currentPlayer.TotalBet = 0;
                currentPlayer.Hand.Clear();
                currentPlayer.HandPauseChoice = HandPauseChoice.Timeout;
                currentPlayer.HandPauseDecisionAt = DateTime.UtcNow;
            }

            responses[userId] = 1;

            await Clients.Caller.SendAsync("StayedInRoomAsPaused", new
            {
                message = "Timeout seçdiniz. Otaqda qalacaqsınız və yalnız növbəti raundu buraxacaqsınız."
            });

            await BroadcastRoomPlayers(roomId);
        }

        private bool CanPlayerShowdownCall(SekaRoom room, int userId)
        {
            var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();

            if (activePlayers.Count < 2)
                return false;

            var currentPlayer = activePlayers.FirstOrDefault(p => p.UserId == userId);
            if (currentPlayer == null || room.CurrentTurnUserId != userId)
                return false;

            if (!room.IsGameStarted || room.IsGameFinished)
                return false;

            if (room.CurrentBet <= 0m)
                return false;

            var showdownCallAmount = GetActionCallAmount(room, currentPlayer);
            return showdownCallAmount > 0;
        }


        // ✅ Context versiyası
        private async Task BroadcastPendingBets(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var pendingBets = room.Players
                .Select(p => new
                {
                    userId = p.UserId,
                    name = p.Name,
                })
                .ToList();

            await _hubContext.Clients.Group(roomId).SendAsync("PendingBetsUpdate", pendingBets);
        }

        private async Task BroadcastGameStateWithContext(string roomId)
        {
            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            var activePlayers = room.Players.Where(p => !p.HasFolded && p.IsActive).ToList();
            bool allBetsEqual = activePlayers.All(p => p.CurrentBet == room.CurrentBet || p.IsAllIn);
            int playersWithoutAction = activePlayers.Count(p => p.CurrentBet < room.CurrentBet && !p.IsAllIn);
            bool roundFinished = IsRoundFinished(room, activePlayers);
            // ✅ DealerIndex yoxla
            if (room.DealerIndex < 0 || room.DealerIndex >= room.Players.Count)
            {
                room.DealerIndex = 0;
            }

            int broadcastTurnUserId = room.CurrentTurnUserId;

            // ✅ CurrentTurnUserId 0 olarsa, sonrakı oyuncuya keç
            if (broadcastTurnUserId == 0 && activePlayers.Count > 0)
            {
                broadcastTurnUserId = activePlayers[0].UserId;
            }
            // ✅ Ən sadə həll
            foreach (var player in room.Players)
            {
                int playerIndex = room.Players.IndexOf(player);
                bool isDealer = (playerIndex == room.DealerIndex);
                bool isTurn = (broadcastTurnUserId == player.UserId);

                int remainingSeconds = 0;
                if (room.TurnStartTime.HasValue && isTurn && room.CurrentTurnUserId != 0)
                {
                    var elapsed = (DateTime.UtcNow - room.TurnStartTime.Value).TotalSeconds;
                    remainingSeconds = Math.Max(0, (int)(SekaRoom.TURN_TIMEOUT_SECONDS - elapsed));
                }

                int myHandScore = 0;
                if (player.Hand != null && player.Hand.Count > 0)
                {
                    myHandScore = SekaHandEvaluator.CalculateHandScore(player.Hand);
                }

                bool canFold = false;
                bool canCall = false;
                bool canRaise = false;
                decimal callAmountDisplay = 0;


                bool canShowdownCall = false;
                decimal showdownCallAmount = 0;
                if (isTurn && !player.HasFolded && player.IsActive && room.IsGameStarted && !room.IsGameFinished)
                {
                    callAmountDisplay = GetActionCallAmount(room, player);
                    canFold = true;
                    canCall = callAmountDisplay > 0;
                    canRaise = true;
                    showdownCallAmount = callAmountDisplay;
                }

                canShowdownCall = CanPlayerShowdownCall(room, player.UserId);

                decimal minRaise = GetMinimumRaiseAmount(room, player);

                decimal maxRaise = GetMaximumRaiseIncrement(room, player);
                if (minRaise > maxRaise) minRaise = maxRaise;

                // ✅ CurrentTurnUserName HƏMIŞƏ dəyər olacaq
                var currentTurnUser = room.Players.FirstOrDefault(p => p.UserId == broadcastTurnUserId);
                string currentTurnUserName = currentTurnUser?.Name ?? "System";

                string dealerName = room.Players[room.DealerIndex].Name;

                await _hubContext.Clients.Client(player.ConnectionId).SendAsync("GameState", new
                {
                    currentTurnUserId = broadcastTurnUserId,
                    currentTurnUserName = currentTurnUserName, // ✅ HƏMIŞƏ dəyər
                    currentBet = room.CurrentBet,
                    myCurrentBet = player.CurrentBet,
                    potAmount = room.PotAmount,
                    round = room.CurrentRound,
                    limitType = "PotLimit",
                    canFold = canFold,
                    canCall = canCall,
                    canRaise = canRaise,
                    nextCallAmount = callAmountDisplay,
                    canShowdownCall = canShowdownCall,
                    turnTimeRemaining = remainingSeconds,
                    myHandScore = myHandScore,
                    callAmount = callAmountDisplay,
                    showdownCallAmount = showdownCallAmount,
                    entryFee = room.EntryFee,
                    minRaise = minRaise,
                    maxRaise = maxRaise,
                    dealerIndex = room.DealerIndex,
                    isDealer = isDealer,
                    dealerName = dealerName,
                    roundFinished = roundFinished,
                    isTurn = isTurn,
                    raiseCount = room.RaiseCount,
                    lastRaiserId = room.LastRaiserId,
                    lastCallerId = room.LastCallerId,
                    allBetsEqual = allBetsEqual,
                });
            }
        }
        private void StartRoomStartTimer(string roomId)
        {
            CancelRoomStartTimer(roomId);

            var room = _roomManager.GetRoom(roomId);
            if (room == null) return;

            lock (room.StateLock)
            {
                room.RoomCreatedTime = DateTime.UtcNow;
            }

            Console.WriteLine($"⏰ Room start timer başladı: {roomId}");

            var timer = new System.Threading.Timer(async _ =>
            {
                try
                {
                    var currentRoom = _roomManager.GetRoom(roomId);
                    if (currentRoom == null || currentRoom.IsGameStarted)
                    {
                        CancelRoomStartTimer(roomId);
                        return;
                    }

                    // ✅ 2 və ya daha çox oyunçu varsa oyunu başlat
                    if (currentRoom.Players.Count >= 2)
                    {
                        Console.WriteLine($"⏰ 2 dəqiqə bitdi! Oyunçular: {currentRoom.Players.Count}");

                        await _hubContext.Clients.Group(roomId).SendAsync("RoomStartTimeout", "⏰ 2 dəqiqə bitdi! Oyun başlayır...");
                        await Task.Delay(2000);
                        await AutoStartGameWithFreshScope(roomId);
                    }
                    else
                    {
                        // ✅ Hələ 1 nəfərdirsə bildiriş göndər
                        await _hubContext.Clients.Group(roomId).SendAsync("RoomTimerExpired", "⏰ Vaxt bitdi! Başqa oyunçu gözlənilir...");
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
            }, null, TimeSpan.FromSeconds(SekaRoom.ROOM_START_TIMEOUT_SECONDS), Timeout.InfiniteTimeSpan);

            _roomStartTimers[roomId] = timer;

            // ✅ Hər saniyə frontend-ə göndər
            _ = Task.Run(async () =>
            {
                int remainingSeconds = SekaRoom.ROOM_START_TIMEOUT_SECONDS;

                while (remainingSeconds > 0)
                {
                    await Task.Delay(1000);
                    remainingSeconds--;

                    var currentRoom = _roomManager.GetRoom(roomId);
                    if (currentRoom == null || currentRoom.IsGameStarted || currentRoom.Players.Count >= 2)
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
    }
}




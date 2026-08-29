//using BlogApp.Core.Entities;
//using System.Collections.Concurrent;

//namespace BlogApp.Api.Hubs
//{
//    public class OkeyRoomManager
//    {
//        private readonly ConcurrentDictionary<string, OkeyRoom> _rooms = new();

//        public OkeyRoom? CreateRoom(string roomName, string creatorName, int creatorId,
//            decimal entryFee, int maxPlayers, bool isPrivate, string? password)
//        {
//            var room = new OkeyRoom
//            {
//                RoomName = roomName,
//                CreatorName = creatorName,
//                CreatorId = creatorId,
//                EntryFee = entryFee,
//                MaxPlayers = maxPlayers,
//                IsPrivate = isPrivate,
//                Password = password
//            };

//            return _rooms.TryAdd(room.RoomId, room) ? room : null;
//        }

//        public List<RoomListItem> GetAvailableRooms()
//        {
//            return _rooms.Values
//                .Where(r => !r.IsGameStarted && r.Players.Count < r.MaxPlayers)
//                .Select(r => new RoomListItem
//                {
//                    RoomId = r.RoomId,
//                    RoomName = r.RoomName,
//                    CreatorName = r.CreatorName,
//                    PlayerCount = r.Players.Count,
//                    MaxPlayers = r.MaxPlayers,
//                    EntryFee = r.EntryFee,
//                    IsPrivate = r.IsPrivate
//                })
//                .ToList();
//        }

//        public OkeyRoom? GetRoom(string roomId)
//        {
//            _rooms.TryGetValue(roomId, out var room);
//            return room;
//        }

//        public bool AddPlayerToRoom(string roomId, OkeyPlayer player, string? password)
//        {
//            var room = GetRoom(roomId);
//            if (room == null) return false;

//            lock (room.StateLock)
//            {
//                if (room.Players.Count >= room.MaxPlayers) return false;
//                if (room.IsPrivate && room.Password != password) return false;
//                if (room.IsGameStarted) return false;

//                room.Players.Add(player);
//                return true;
//            }
//        }

//        public void RemovePlayerFromRoom(string roomId, int userId)
//        {
//            var room = GetRoom(roomId);
//            if (room == null) return;

//            lock (room.StateLock)
//            {
//                var player = room.Players.FirstOrDefault(p => p.UserId == userId);
//                if (player != null)
//                {
//                    room.Players.Remove(player);
//                }
//            }

//            if (room.Players.Count == 0)
//            {
//                DeleteRoom(roomId);
//            }
//        }

//        public void DeleteRoom(string roomId)
//        {
//            _rooms.TryRemove(roomId, out _);
//        }
//    }

//    public class RoomListItem
//    {
//        public string RoomId { get; set; }
//        public string RoomName { get; set; }
//        public string CreatorName { get; set; }
//        public int PlayerCount { get; set; }
//        public int MaxPlayers { get; set; }
//        public decimal EntryFee { get; set; }
//        public bool IsPrivate { get; set; }
//        public bool IsGameStarted { get; set; }
//    }
//}


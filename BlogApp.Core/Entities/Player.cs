namespace BlogApp.Core.Entities
{
    public class Player
    {
        public string ConnectionId { get; set; } = "";
        public int UserId { get; set; } = 0;
        public string Name { get; set; } = "";
        public decimal Balance { get; set; } = 0;
        public int?[][] Card { get; set; } = null!;
        public HashSet<int> CompletedRows { get; set; } = new();
    }
}

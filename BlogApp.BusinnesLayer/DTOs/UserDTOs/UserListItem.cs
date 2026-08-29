namespace BlogApp.BusinnesLayer.DTOs.UserDTOs
{
    public class UserListItem
    {
        public int Id { get; set; }
        public string UserName { get; set; }
        public string Name { get; set; }
        public string Surname { get; set; }
        public string Email { get; set; }
        public bool IsMale { get; set; }
        public decimal Balance { get; set; }
        public int Role { get; set; }
        public DateTime? BanDeadline { get; set; }

    }
}

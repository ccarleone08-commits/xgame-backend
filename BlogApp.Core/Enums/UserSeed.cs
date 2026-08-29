namespace BlogApp.Core.Enums
{
    public class UserSeed
    {
        public string UserName { get; set; }
        public string Email { get; set; }
        public string Password { get; set; }
        public int Role { get; set; } =1;
        public decimal Balance { get; set; } = 0;
    }

}

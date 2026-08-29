namespace BlogApp.BusinnesLayer.DTOs.DepositDTOs
{
    public class DepositUserSeedOptions
    {
        public string UserName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Password { get; set; } = "Deposit123!";
        public string Name { get; set; } = string.Empty;
        public string Surname { get; set; } = string.Empty;
        public bool IsMale { get; set; } = true;
        public int Balance { get; set; } = 0;
        public int Role { get; set; }
    }
}

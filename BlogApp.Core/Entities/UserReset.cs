namespace BlogApp.Core.Entities
{
    public class UserReset
    {
        public string? PasswordResetToken { get; set; }
        public DateTime? TokenExpiry { get; set; }

    }
}

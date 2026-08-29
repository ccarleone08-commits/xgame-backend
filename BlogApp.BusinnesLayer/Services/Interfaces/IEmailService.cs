namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface IEmailService
{
    Task SendEmailAsync();
    Task AccountVerify(string token);
    Task SendPasswordResetEmail(string email, string resetLink);

}

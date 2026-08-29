using BlogApp.BusinnesLayer.Services.Interfaces;
namespace BlogApp.BusinnesLayer.Services.Implements
{
    public class EmailService : IEmailService
    {
        public async Task SendPasswordResetEmail(string email, string resetLink)
        {
            var smtpUser = Environment.GetEnvironmentVariable("SMTP_USER")
                ?? throw new InvalidOperationException("SMTP_USER is not configured.");
            var smtpPass = Environment.GetEnvironmentVariable("SMTP_PASS")
                ?? throw new InvalidOperationException("SMTP_PASS is not configured.");
            var senderAddress = Environment.GetEnvironmentVariable("SMTP_FROM_ADDRESS") ?? smtpUser;
            var senderName = Environment.GetEnvironmentVariable("SMTP_FROM_NAME") ?? "Application Support";

            var message = new MimeKit.MimeMessage();
            message.From.Add(new MimeKit.MailboxAddress(senderName, senderAddress));
            message.To.Add(MimeKit.MailboxAddress.Parse(email));
            message.Subject = "Şifrə sıfırlama linki";
            message.Body = new MimeKit.TextPart("plain")
            {
                Text = $"Şifrəni sıfırlamaq üçün link: {resetLink}"
            };

            using var client = new MailKit.Net.Smtp.SmtpClient();
            await client.ConnectAsync("smtp.gmail.com", 587, MailKit.Security.SecureSocketOptions.StartTls);
            await client.AuthenticateAsync(smtpUser, smtpPass);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

        }

        public Task AccountVerify(string token)
        {
            throw new NotImplementedException();
        }

        public Task SendEmailAsync()
        {
            throw new NotImplementedException();
        }
    }

}

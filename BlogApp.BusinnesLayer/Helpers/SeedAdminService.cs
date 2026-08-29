using BlogApp.BusinnesLayer.Helpers;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.Extensions.Options;

public class SeedAdminService
{
    private readonly BlogAppDbContext _db;
    private readonly AdminSeedOptions _options;

    public SeedAdminService(
        BlogAppDbContext db,
        IOptions<AdminSeedOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task SeedAdminAsync()
    {
        // Admin artıq varsa → çıx
        if (_db.Users.Any(u => u.Role == 1))
            return;

        // Eyni email varsa admin et
        var user = _db.Users.FirstOrDefault(u => u.Email == _options.Email);
        if (user != null)
        {
            user.Role = 1;
            await _db.SaveChangesAsync();
            return;
        }

        var admin = new User
        {
            UserName = _options.UserName,
            Email = _options.Email,
            Image = "default_admin.png",
            Name = _options.Name,
            Surname = _options.Surname,
            IsMale = _options.IsMale,
            Role = 1,
            PasswordHash = HashHelper.HashPassword(_options.Password),
            Balance = 1000
        };
        Console.WriteLine("Seeding admin...");
        Console.WriteLine("Email: " + _options.Email);
        Console.WriteLine("DB Users count before: " + _db.Users.Count());

        await _db.Users.AddAsync(admin);
        await _db.SaveChangesAsync();
    }
}
public class AdminSeedOptions
{
    public string UserName { get; set; }
    public string Email { get; set; }
    public string Password { get; set; } = "Admin123!";
    public string Name { get; set; } = "System";
    public string Surname { get; set; } = "SBZ234";
    public int Role { get; set; } = 1;
    public bool IsMale { get; set; } = true;
}

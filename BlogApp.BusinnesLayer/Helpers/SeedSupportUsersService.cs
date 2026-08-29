using BlogApp.BusinnesLayer.Helpers;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.Extensions.Options;

public class SeedSupportUsersService
{
    private readonly BlogAppDbContext _db;
    private readonly List<SupportUserSeedOptions> _options;

    public SeedSupportUsersService(
        BlogAppDbContext db,
        IOptions<List<SupportUserSeedOptions>> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task SeedAsync()
    {
        foreach (var opt in _options)
        {
            // Bu rol artıq varsa → keç
            if (_db.Users.Any(u => u.Role == opt.Role))
                continue;

            // Eyni email varsa rolu yenilə
            var existing = _db.Users.FirstOrDefault(u => u.Email == opt.Email);
            if (existing != null)
            {
                existing.Role = opt.Role;
                await _db.SaveChangesAsync();
                continue;
            }

            var user = new User
            {
                UserName = opt.UserName,
                Email = opt.Email,
                Image = "default_admin.png",
                Name = opt.Name,
                Surname = opt.Surname,
                IsMale = opt.IsMale,
                Role = opt.Role,
                PasswordHash = HashHelper.HashPassword(opt.Password),
                Balance = opt.Balance
            };

            await _db.Users.AddAsync(user);
            Console.WriteLine($"Seeding role {opt.Role} user: {opt.Email}");
        }

        await _db.SaveChangesAsync();
    }
}

public class SupportUserSeedOptions
{
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = "Support123!";
    public string Name { get; set; } = string.Empty;
    public string Surname { get; set; } = string.Empty;
    public bool IsMale { get; set; } = true;
    public int Balance { get; set; } = 0;
    public int Role { get; set; }
}
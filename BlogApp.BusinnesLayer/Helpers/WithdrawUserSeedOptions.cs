using BlogApp.BusinnesLayer.Helpers;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.Extensions.Options;

public class WithdrawUserSeedOptions
{
    public string UserName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public int Role { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Surname { get; set; } = string.Empty;
    public bool IsMale { get; set; }
    public decimal Balance { get; set; }
}

public class SeedWithdrawUsersService
{
    private readonly BlogAppDbContext _db;
    private readonly List<WithdrawUserSeedOptions> _options;

    public SeedWithdrawUsersService(
        BlogAppDbContext db,
        IOptions<List<WithdrawUserSeedOptions>> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task SeedAsync()
    {
        foreach (var opt in _options)
        {
            if (_db.Users.Any(u => u.Role == opt.Role))
                continue;

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
            Console.WriteLine($"Seeding withdraw role {opt.Role} user: {opt.Email}");
        }
        await _db.SaveChangesAsync();
    }
}
// BlogApp.BusinnesLayer/Helpers/SeedDepositUsersService.cs
using BlogApp.BusinnesLayer.DTOs.DepositDTOs;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.Core.Entities;
using BlogApp.DAL.DALs;
using Microsoft.Extensions.Options;

public class SeedDepositUsersService
{
    private readonly BlogAppDbContext _db;
    private readonly List<DepositUserSeedOptions> _options;

    public SeedDepositUsersService(
        BlogAppDbContext db,
        IOptions<List<DepositUserSeedOptions>> options)
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
            Console.WriteLine($"Seeding deposit role {opt.Role} user: {opt.Email}");
        }

        await _db.SaveChangesAsync();
    }
}
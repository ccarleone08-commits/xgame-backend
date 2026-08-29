using AutoMapper;
using BlogApp.BusinnesLayer.DTOs.UserDTOs;
using BlogApp.BusinnesLayer.Exceptions.Common;
using BlogApp.BusinnesLayer.Exceptions.UserExceptions;
using BlogApp.BusinnesLayer.ExternalServices.Interfaces;
using BlogApp.BusinnesLayer.Helpers;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Enums;
using BlogApp.Core.Repositories;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace BlogApp.BusinnesLayer.Services.Implements;

public class AuthService(
    IUserRepository _repo,
    IMapper _mapper,
    IJwtTokenHandler _tokenHandler,
    ISessionService _sessionService,
    IWebHostEnvironment _env,
    IConfiguration _configuration) : IAuthService
{
    public async Task BanUserAsync(string username, DateTime deadline)
    {
        var user = await _repo.GetAll().FirstOrDefaultAsync(x => x.UserName == username);
        if (user is null)
            throw new NotFoundException<User>();

        user.BanDeadline = deadline;
        user.Role = Roles.Viewer.GetHashCode();
        await _repo.SaveAsync();
    }

    public async Task UnbanUserAsync(string username)
    {
        var user = await _repo.GetAll().FirstOrDefaultAsync(x => x.UserName == username);
        if (user is null)
            throw new NotFoundException<User>();

        user.BanDeadline = null;
        user.Role = Roles.User.GetHashCode();
        await _repo.SaveAsync();
    }
    public async Task UpdateUserAsync(User user)
    {
        _repo.Update(user);
        await _repo.SaveChangesAsync();
    }

    public async Task<UserDto> GetByUserNameAsync(string username)
    {
        var user = await _repo.GetAll().Where(x => x.UserName == username).FirstOrDefaultAsync();
        if (user is null)
            throw new NotFoundException<User>();

        return _mapper.Map<UserDto>(user);
    }

    public async Task<User> GetByUserIdAsync(int id)
    {
        var user = await _repo.GetAll().Where(x => x.Id == id).FirstOrDefaultAsync();
        if (user is null)
            throw new NotFoundException<User>();
        return _mapper.Map<User>(user);
    }

    public async Task<string> LoginAsync(LoginDto dto)
    {
        User? user = null;

        if (dto.UserNameOrEmail.Contains('@'))
            user = await _repo.GetAll().Where(x => x.Email == dto.UserNameOrEmail).FirstOrDefaultAsync();
        else
            user = await _repo.GetAll().Where(x => x.UserName == dto.UserNameOrEmail).FirstOrDefaultAsync();

        if (user == null)
            throw new NotFoundException<User>();

        if (user.BanDeadline is not null && user.BanDeadline > DateTime.UtcNow)
            throw new Exception($"User is banned until {user.BanDeadline}");

        if (!HashHelper.VerifyHashedPassword(user.PasswordHash, dto.Password))
            throw new IncorrectPasswordException("Password is wrong, please check again");

        // 🔥 Token yaradırıq
        var token = _tokenHandler.CreateToken(user, 1440);

        // ✅ Eyni user üçün köhnə tokeni əvəz edirik (tək giriş)

        //_sessionService.StoreUserToken(user.UserName, token);

        return token;
    }


    public async Task RegisterAsync(RegisterCreateDto dto)
    {
        var existing = await _repo.GetAll().Where(x => x.UserName == dto.Username || x.Email == dto.Email).FirstOrDefaultAsync();
        if (existing != null)
        {
            if (existing.Email == dto.Email)
                throw new ExistsException<User>("Email already in use.");
            if (existing.UserName == dto.Username)
                throw new ExistsException<User>("Username already in use.");
        }

        var user = _mapper.Map<User>(dto);
        if (dto.Image != null)
        {
            var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" };
            var extension = Path.GetExtension(dto.Image.FileName).ToLowerInvariant();
            if (!allowedExtensions.Contains(extension))
                throw new ArgumentException("Profile image type is not supported.", nameof(dto.Image));

            if (dto.Image.Length > 5 * 1024 * 1024)
                throw new ArgumentException("Profile image size cannot exceed 5MB.", nameof(dto.Image));

            var fileName = FileStoragePathHelper.BuildSafeFileName(dto.Image.FileName);
            var folder = Path.Combine(FileStoragePathHelper.GetUploadsRoot(_env, _configuration), "characters");
            Directory.CreateDirectory(folder);

            var fullPath = Path.Combine(folder, fileName);
            using var stream = new FileStream(fullPath, FileMode.Create);
            await dto.Image.CopyToAsync(stream);

            user.Image = $"/uploads/characters/{fileName}";
        }
        else
        {
            user.Image = "/assets/characters/default.png"; // optional
        }
        await _repo.AddAsync(user);
        await _repo.SaveAsync();
    }

    public async Task UpdateBalance(int id, decimal amout)
    {
        var user = await _repo.GetByIdAsync(id);
        if (user.Id == null || user.Id == 0)
            throw new Exception("Bu Id de user tapilmadi");
        user.Balance += amout;
        if (user.Balance < 0)
            user.Balance = 0;
        await _repo.SaveAsync();
    }

    public async Task<List<UserListItem>> GetAllAsync()
    {
        var data = await _repo.GetAll().Select(x => new UserListItem
        {
            Id = x.Id,
            UserName = x.UserName,
            Email = x.Email,
            Name = x.Name,
            Surname = x.Surname,
            Balance = x.Balance,
            IsMale = x.IsMale,
            Role = x.Role,
            BanDeadline = x.BanDeadline,

        }).ToListAsync();
        return data;
    }

    public async Task<User?> GetByEmailAsync(string email)
    {
        return await _repo.GetByEmailAsync(email);
    }

    public async Task<bool> ResetPasswordAsync(string email, string token, string newPassword)
    {
        var user = await _repo.GetByEmailAsync(email);
        if (user == null) return false;

        // Token yoxla
        if (user.PasswordResetToken != token || user.TokenExpiry < DateTime.UtcNow)
            return false;

        // Şifrəni hash et
        user.PasswordHash = HashHelper.HashPassword(newPassword);
        user.PasswordResetToken = null;
        user.TokenExpiry = null;

        _repo.Update(user);
        await _repo.SaveChangesAsync();

        return true;
    }
}

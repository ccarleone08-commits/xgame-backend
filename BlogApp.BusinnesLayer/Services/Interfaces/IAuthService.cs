using BlogApp.BusinnesLayer.DTOs.UserDTOs;
using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface IAuthService
{
    Task RegisterAsync(RegisterCreateDto dto);
    Task<string> LoginAsync(LoginDto dto);
    Task<List<UserListItem>> GetAllAsync();
    Task<UserDto> GetByUserNameAsync(string username);
    Task<bool> ResetPasswordAsync(string email, string token, string newPassword);
    Task<User?> GetByEmailAsync(string email);
    Task<User> GetByUserIdAsync(int Id);
    Task UpdateUserAsync(User user);
    Task BanUserAsync(string username, DateTime deadline);
    Task UnbanUserAsync(string username);
    Task UpdateBalance(int Id, decimal amout);
}

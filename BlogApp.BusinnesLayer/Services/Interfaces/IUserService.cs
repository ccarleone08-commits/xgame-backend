using BlogApp.Core.Entities;
using System.Security.Claims;

namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface IUserService
{
    Task<User> GetCurrentUserAsync(ClaimsPrincipal principal);
    Task<string> CreateAsync();
    Task UserDeleteAsync(string username);
    Task Update(User user);
    Task SaveChangesAsync();
}

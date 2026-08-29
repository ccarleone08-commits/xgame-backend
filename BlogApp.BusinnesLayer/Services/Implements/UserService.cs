using BlogApp.BusinnesLayer.Exceptions.Common;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace BlogApp.BusinnesLayer.Services.Implements;

public class UserService(IUserRepository _repo) : IUserService
{
    public Task<string> CreateAsync()
    {
        throw new NotImplementedException();
    }

    public Task<User> GetCurrentUserAsync(ClaimsPrincipal principal)
    {
        throw new NotImplementedException();
    }

    public async Task SaveChangesAsync()
    {
        await _repo.SaveChangesAsync();
    }

    public async Task Update(User user)
    {
        _repo.Update(user);
        await _repo.SaveChangesAsync();
    }

    public async Task UserDeleteAsync(string username)
    {
        var user = await _repo.GetAll().Where(x => x.UserName == username).FirstOrDefaultAsync();
        if (user is null)
            throw new NotFoundException<User>();
        await _repo.DeleteAsync(user.Id);
    }


}

using BlogApp.Core.Entities;

namespace BlogApp.Core.Repositories;

public interface IUserRepository : IGenericRepository<User>
{
    User GetCurrentUser();
    int GetCurrentUserId();
    Task<User> GetByUserName(string userName);
    Task<User?> GetByEmailAsync(string email);
    void Update(User user);
    Task SaveChangesAsync(); // <- bunu əlavə et


}

using BlogApp.Core.Entities;

namespace BlogApp.BusinnesLayer.ExternalServices.Interfaces;

public interface IJwtTokenHandler
{
    string CreateToken(User user, int minutes);
}

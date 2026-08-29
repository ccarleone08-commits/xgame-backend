namespace BlogApp.BusinnesLayer.Services.Interfaces;

public interface ISessionService
{
    void StoreUserToken(string username, string token);
    string? GetUserToken(string username);
}

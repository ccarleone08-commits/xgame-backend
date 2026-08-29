using BlogApp.BusinnesLayer.Services.Interfaces;

namespace BlogApp.BusinnesLayer.Services.Implements
{
    public class SessionService : ISessionService
    {
        private static Dictionary<string, string> _userTokens = new();

        public void StoreUserToken(string username, string token)
        {
            _userTokens[username] = token;
        }

        public string? GetUserToken(string username)
        {
            return _userTokens.ContainsKey(username) ? _userTokens[username] : null;
        }

    }
}

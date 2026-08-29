namespace BlogApp.BusinnesLayer.Helpers
{
    using System.IdentityModel.Tokens.Jwt;
    using System.Linq;
    using System.Security.Claims;

    public static class JwtHelper
    {
        public static string GetUserNameFromToken(string token)
        {
            var handler = new JwtSecurityTokenHandler();
            var jwt = handler.ReadJwtToken(token);

            var username = jwt.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value;

            return username ?? throw new Exception("Username claim not found in token");
        }
    }

}

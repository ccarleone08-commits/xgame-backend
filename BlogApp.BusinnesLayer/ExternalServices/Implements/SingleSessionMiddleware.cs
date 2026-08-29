//using BlogApp.BusinnesLayer.Helpers;
//using BlogApp.BusinnesLayer.Services.Interfaces;
//using Microsoft.AspNetCore.Http;

//namespace BlogApp.BusinnesLayer.ExternalServices.Implements
//{
//    public class SingleSessionMiddleware
//    {
//        private readonly RequestDelegate _next;

//        public SingleSessionMiddleware(RequestDelegate next) => _next = next;

//        public async Task Invoke(HttpContext context, ISessionService sessionService)
//        {
//            var path = context.Request.Path.Value?.ToLower();

//            // ✅ Login, Register, Refresh üçün keçid ver
//            if (path.Contains("/api/auths/login") ||
//                path.Contains("/api/auths/register") ||
//                path.Contains("/api/auths/refresh"))
//            {
//                await _next(context);
//                return;
//            }

//            // 1️⃣ Header-dən token oxu
//            var token = context.Request.Headers.ContainsKey("Authorization")
//                ? context.Request.Headers["Authorization"].ToString().Replace("Bearer ", "")
//                : string.Empty;

//            // 2️⃣ Əgər header boşdursa → cookie-dən oxu
//            if (string.IsNullOrEmpty(token) && context.Request.Cookies.ContainsKey("AuthToken"))
//            {
//                token = context.Request.Cookies["AuthToken"];
//            }

//            // 3️⃣ Token hələ də boşdursa → user hələ login olmayıb → pass
//            if (string.IsNullOrEmpty(token))
//            {
//                await _next(context);
//                return;
//            }

//            // 4️⃣ Tokendən username çıxart
//            var username = JwtHelper.GetUserNameFromToken(token);

//            // 5️⃣ Session token-i yoxla
//            var storedToken = sessionService.GetUserToken(username);

//            if (storedToken != token)
//            {
//                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
//                await context.Response.WriteAsync("❌ Bu istifadəçi artıq başqa cihazdan daxil olub.");
//                return;
//            }

//            // 6️⃣ Hər şey OK → növbəti middleware
//            await _next(context);
//        }
//    }
//}

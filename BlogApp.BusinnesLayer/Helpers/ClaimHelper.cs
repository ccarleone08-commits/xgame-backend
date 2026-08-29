using BlogApp.Core.Enums;
using System.Security.Claims;

namespace BlogApp.BusinnesLayer.Helpers
{
    public static class ClaimHelper
    {
        public static int GetUserId(ClaimsPrincipal user)
        {
            var val = user.FindFirstValue(ClaimTypes.NameIdentifier);
            return int.TryParse(val, out var id) ? id : 0;
        }

        public static string GetUsername(ClaimsPrincipal user)
            => user.FindFirstValue(ClaimTypes.Name) ?? string.Empty;

        public static int GetRole(ClaimsPrincipal user)
        {
            var val = user.FindFirstValue(ClaimTypes.Role)
                   ?? user.FindFirstValue("http://schemas.microsoft.com/ws/2008/06/identity/claims/role");
            return int.TryParse(val, out var r) ? r : 0;
        }

        public static bool IsSupportAdmin(ClaimsPrincipal user)
            => GetRole(user) == 64;

        public static bool IsSupportWorkerOrAdmin(ClaimsPrincipal user)
            => GetRole(user) == 64 || GetRole(user) == 128;
        public static bool IsUser(ClaimsPrincipal user)
           => GetRole(user) == 8;

        public static bool IsDepositWorkerOrAdmin(ClaimsPrincipal user)
            => GetRole(user) == 256 || GetRole(user) == 512;

        public static bool IsBankOrAdmin(ClaimsPrincipal user)
            => GetRole(user) == 1024 || GetRole(user) == 512;

        public static bool IsWithdrawWorkerOrAdmin(ClaimsPrincipal user)
        => GetRole(user) == (int)Roles.WithdrawWorker
        || GetRole(user) == (int)Roles.WithdrawAdmin;

        public static bool IsWithdrawAdmin(ClaimsPrincipal user)
            => GetRole(user) == (int)Roles.WithdrawAdmin;

        public static bool IsWithdrawBank(ClaimsPrincipal user)
            => GetRole(user) == (int)Roles.WithdrawBank
            || GetRole(user) == (int)Roles.WithdrawAdmin;
    }
}

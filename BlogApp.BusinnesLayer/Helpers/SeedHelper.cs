//using BlogApp.Core.Entities;
//using BlogApp.Core.Enums;
//using Microsoft.AspNetCore.Builder;
//using Microsoft.AspNetCore.Identity;
//using Microsoft.Extensions.DependencyInjection;

//namespace BlogApp.BusinnesLayer.Helpers
//{
//    public static class SeedHelper
//    {
//        public static async Task SeedRolesAndAdminAsync(WebApplication app)
//        {
//            using var scope = app.Services.CreateScope();

//            var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
//            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();

//            // 1️⃣ Rolları yoxla və yarat
//            string[] roles = { "Admin", "User", "Manager" };

//            foreach (var role in roles)
//            {
//                if (!await roleManager.RoleExistsAsync(role))
//                {
//                    await roleManager.CreateAsync(new IdentityRole(role));
//                }
//            }

//            // 2️⃣ Admin istifadəçini yoxla və yarat
//            var adminUser = await userManager.FindByEmailAsync(adminEmail);

//            if (adminUser == null)
//            {
//                adminUser = new User
//                {
//                    UserName = "admin",
//                    Email = adminEmail,
//                    //EmailConfirmed = true,
//                    Balance = 0 // default balans
//                };

//                var result = await userManager.CreateAsync(adminUser, "Admin123!");
//                if (!result.Succeeded)
//                {
//                    throw new Exception("Admin user yaratmaq mümkün olmadı: " + string.Join(", ", result.Errors));
//                }

//                await userManager.AddToRoleAsync(adminUser, "Admin");
//            }
//            else
//            {
//                // Əgər artıq varsa, rol yoxdursa əlavə et
//                var rolesForUser = await userManager.GetRolesAsync(adminUser);
//                if (!rolesForUser.Contains("Admin"))
//                {
//                    await userManager.AddToRoleAsync(adminUser, "Admin");
//                }
//            }
//        }
//    }
//}

using BlogApp.BusinnesLayer.DTOs.Options;
using BlogApp.BusinnesLayer.Services.Implements;
using BlogApp.BusinnesLayer.Services.Interfaces;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace BlogApp.Api;

public static class ServiceRegistiration
{
    public static IServiceCollection AddJwtOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Jwt));
        services.AddScoped<ISessionService, SessionService>();

        return services;
    }
    public static IServiceCollection AddAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        JwtOptions jwtOption = new JwtOptions();
        jwtOption.Issuer = configuration.GetRequiredSection("JwtOptions")["Issuer"]!.ToString();
        jwtOption.Audience = configuration.GetRequiredSection("JwtOptions")["Audience"]!.ToString();
        jwtOption.SecretKey = configuration.GetRequiredSection("JwtOptions")["SecretKey"]!.ToString();
        if (string.IsNullOrWhiteSpace(jwtOption.Issuer) ||
            string.IsNullOrWhiteSpace(jwtOption.Audience) ||
            string.IsNullOrWhiteSpace(jwtOption.SecretKey))
        {
            throw new InvalidOperationException("JWT configuration is missing.");
        }

        var signInKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOption.SecretKey));
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(opt =>
            {
                opt.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,

                    IssuerSigningKey = signInKey,
                    ValidAudience = jwtOption.Audience,
                    ValidIssuer = jwtOption.Issuer,
                    ClockSkew = TimeSpan.Zero
                };

                opt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var accessToken = context.Request.Query["access_token"];
                        var path = context.HttpContext.Request.Path;
                        if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/adminChatHub"))
                        {
                            context.Token = accessToken;
                        }
                        return Task.CompletedTask;
                    }
                };
            });

        return services;
    }
}

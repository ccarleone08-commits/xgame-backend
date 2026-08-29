using BlogApp.Api;
using BlogApp.Api.Hubs;
using BlogApp.Api.Hubs.Services;
using BlogApp.Api.Hubs.Services.BlogApp.Api.Hubs.Services;
using BlogApp.API.Hubs;
using BlogApp.BusinnesLayer;
using BlogApp.BusinnesLayer.DTOs.DepositDTOs;
using BlogApp.BusinnesLayer.DTOs.Options;
using BlogApp.BusinnesLayer.ExternalServices.Implements;
using BlogApp.BusinnesLayer.ExternalServices.Interfaces;
using BlogApp.BusinnesLayer.Services.Abstracts;
using BlogApp.BusinnesLayer.Services.Implements;
using BlogApp.BusinnesLayer.Services.Interfaces;
using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using BlogApp.DAL.DALs;
using BlogApp.DAL.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.OpenApi.Models;
using System.Security.Claims;
using static OkeyRoomInitializer;

var builder = WebApplication.CreateBuilder(args);

ValidateConfiguration(builder.Configuration, builder.Environment);

var allowedOrigins = GetAllowedOrigins(builder.Configuration, builder.Environment);
var authCookieName = builder.Configuration["AuthCookie:Name"] ?? "AuthToken";

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

// Controllers & Swagger
builder.Services.AddControllers();
builder.Services.AddMemoryCache();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "My API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter 'Bearer' [space] and then your valid token."
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor |
        ForwardedHeaders.XForwardedProto |
        ForwardedHeaders.XForwardedHost;

    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// DB Context
builder.Services.AddDbContextFactory<BlogAppDbContext>(option =>
{
    option.UseSqlServer(GetConnectionString(builder.Configuration, builder.Environment),
        sql => sql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null));
});

// SignalR
builder.Services.AddSignalR(options =>
{
    options.EnableDetailedErrors = true;
    options.KeepAliveInterval = TimeSpan.FromSeconds(15);
    options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    options.HandshakeTimeout = TimeSpan.FromSeconds(30);
    options.MaximumReceiveMessageSize = 10 * 1024 * 1024; // 10 MB
    options.MaximumParallelInvocationsPerClient = 4;
});

// Authorization
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
       policy.RequireClaim(ClaimTypes.Role, "1"));
});

// Authentication & JWT
builder.Services.AddAuthentication(builder.Configuration);
builder.Services.AddJwtOptions(builder.Configuration);

builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
{
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;

            // Query string yoxlaması (SignalR üçün)
            if (!string.IsNullOrEmpty(accessToken) &&
                (path.StartsWithSegments("/adminChatHub") ||
                 path.StartsWithSegments("/lotoHub") ||
                 path.StartsWithSegments("/dominoHub") ||
                 path.StartsWithSegments("/okeyHub") ||
                 path.StartsWithSegments("/backgammonHub") ||
                 path.StartsWithSegments("/sekaHub") ||
                 path.StartsWithSegments("/pokerHub") ||
                 path.StartsWithSegments("/durakHub") ||
                 path.StartsWithSegments("/hubs/support")))

            {
                context.Token = accessToken;
            }

            // 🔹 Cookie yoxlaması (normal API çağırışları üçün)
            if (string.IsNullOrEmpty(context.Token))
            {
                context.Token = context.HttpContext.Request.Cookies[authCookieName];
            }

            return Task.CompletedTask;
        }
    };
});

// Services

builder.Services.AddScoped<ICategoryRepository, CategoryRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
builder.Services.AddScoped<ISupportTicketMessageRepository, SupportTicketMessageRepository>();


builder.Services.AddScoped<SeedWithdrawUsersService>();
builder.Services.AddScoped<SeedDepositUsersService>();

builder.Services.AddScoped<IDepositService, DepositService>();
builder.Services.AddScoped<IWithdrawService, WithdrawService>();
builder.Services.Configure<NowPaymentsOptions>(builder.Configuration.GetSection("NowPayments"));
builder.Services.PostConfigure<NowPaymentsOptions>(options =>
{
    var publicBaseUrl = GetBaseUrl(builder.Configuration, "App:PublicBaseUrl");
    var frontendBaseUrl = GetBaseUrl(builder.Configuration, "App:FrontendBaseUrl") ?? publicBaseUrl;

    if (!string.IsNullOrWhiteSpace(publicBaseUrl))
    {
        options.IpnCallbackUrl = string.IsNullOrWhiteSpace(options.IpnCallbackUrl)
            ? $"{publicBaseUrl}/api/payments/nowpayments/ipn"
            : options.IpnCallbackUrl;
    }

    if (!string.IsNullOrWhiteSpace(frontendBaseUrl))
    {
        var paymentResultPath = builder.Configuration["App:PaymentResultPath"]
            ?? "/FrontEnd/ChatSystemFront/crypto-deposit.html";
        options.SuccessUrl = string.IsNullOrWhiteSpace(options.SuccessUrl)
            ? $"{frontendBaseUrl}{paymentResultPath}?status=success"
            : options.SuccessUrl;
        options.CancelUrl = string.IsNullOrWhiteSpace(options.CancelUrl)
            ? $"{frontendBaseUrl}{paymentResultPath}?status=cancel"
            : options.CancelUrl;
    }
});
builder.Services.AddHttpClient<INowPaymentsClient, NowPaymentsClient>((sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NowPaymentsOptions>>().Value;
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(options.BaseUrl)
        ? "https://api.nowpayments.io/v1/"
        : options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddScoped<IPaymentService, PaymentService>();
builder.Services.AddScoped<IWalletService, WalletService>();

builder.Services.AddScoped<ISupportTicketService, SupportTicketService>();
builder.Services.AddScoped<ICategoryService, CategoryService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<IJwtTokenHandler, JwtTokenHandler>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ILotoGameService, LotoGameService>();

builder.Services.AddSingleton<BotManager>();
builder.Services.AddSingleton<BotBudgetService>();

builder.Services.AddHostedService<AutoLotoService>();
builder.Services.AddSingleton<LotoRoomManager>();
builder.Services.AddSingleton<DominoRoomManager>(sp =>
{
    var dbContextFactory = sp.GetRequiredService<IDbContextFactory<BlogAppDbContext>>();
    return new DominoRoomManager(dbContextFactory);
});
builder.Services.AddSingleton<OkeyRoomManager>();
builder.Services.AddHostedService<OkeyRoomInitializer>();
builder.Services.AddSingleton<BackgammonRoomManager>();
builder.Services.AddSingleton<SekaRoomManager>();
builder.Services.AddSingleton<PokerRoomManager>();
builder.Services.AddSingleton<DurakRoomManager>();
builder.Services.AddScoped<IRankService, RankService>();

builder.Services.AddHttpContextAccessor();
builder.Services.AddFluentValidation();
builder.Services.AddAutoMapper();

builder.Services.AddHostedService<OkeyRoomCleanupService>();
builder.Services.Configure<AdminSeedOptions>(
    builder.Configuration.GetSection("SeedUsers")
);

builder.Services.Configure<List<SupportUserSeedOptions>>(
    builder.Configuration.GetSection("SeedSupportUsers"));

builder.Services.Configure<List<DepositUserSeedOptions>>(
    builder.Configuration.GetSection("DepositSeedUsers")
);

builder.Services.Configure<List<WithdrawUserSeedOptions>>(
    builder.Configuration.GetSection("WithdrawSeedUsers"));

builder.Services.AddScoped<SeedAdminService>();
builder.Services.AddScoped<SeedSupportUsersService>();

// Health Checks  
builder.Services.AddHealthChecks()
    .AddDbContextCheck<BlogAppDbContext>(
        name: "Database",
        tags: new[] { "database" }
    )
    .AddCheck("API", () => HealthCheckResult.Healthy("API is running"),
        tags: new[] { "api" }
    )
    .AddCheck("SignalR", () => HealthCheckResult.Healthy("SignalR is connected"),
        tags: new[] { "signalr" }
    );

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;

    if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
    {
        var dbContext = services.GetRequiredService<BlogAppDbContext>();
        await dbContext.Database.MigrateAsync();
    }

    if (app.Configuration.GetValue<bool>("Seed:Enabled"))
    {
        var budgetService = services.GetRequiredService<BotBudgetService>();
        await budgetService.InitializeBotBudgetAccount();

        var adminSeeder = scope.ServiceProvider.GetRequiredService<SeedAdminService>();
        var supportSeeder = scope.ServiceProvider.GetRequiredService<SeedSupportUsersService>();

        await adminSeeder.SeedAdminAsync();
        await supportSeeder.SeedAsync();

        var withdrawSeed = scope.ServiceProvider.GetRequiredService<SeedWithdrawUsersService>();
        await withdrawSeed.SeedAsync();

        var depositSeeder = scope.ServiceProvider.GetRequiredService<SeedDepositUsersService>();
        await depositSeeder.SeedAsync();
        Console.WriteLine("System seed completed successfully");
    }
}


if (app.Configuration.GetValue<bool>("ForwardedHeaders:Enabled", true))
{
    app.UseForwardedHeaders();
}

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(x => x.EnablePersistAuthorization());
}

// ✅ Middleware Sırası
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.UseRouting();

app.UseCors("AllowFrontend");     // CORS authentication-dan əvvəl olmalıdır

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseAuthentication();          // Token yoxlaması
app.UseAuthorization();           // Policy yoxlaması

//app.UseMiddleware<SingleSessionMiddleware>();


// Static files
var uploadsPath = GetUploadsPath(app.Configuration, app.Environment);
Directory.CreateDirectory(uploadsPath);
var chatUploadsPath = Path.Combine(uploadsPath, "chat");
Directory.CreateDirectory(chatUploadsPath);
Directory.CreateDirectory(Path.Combine(uploadsPath, "characters"));
Directory.CreateDirectory(Path.Combine(uploadsPath, "receipts"));
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsPath),
    RequestPath = "/uploads",
    OnPrepareResponse = ctx =>
        ctx.Context.Response.Headers.Append("Cache-Control", "public,max-age=604800")
});
// Endpoints
app.UseEndpoints(endpoints =>
{
    endpoints.MapControllers();

    // Health Checks - JSON formatında cavab
    endpoints.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
    {
        ResponseWriter = async (context, report) =>
        {
            context.Response.ContentType = "application/json";
            var response = new
            {
                status = report.Status.ToString(),
                timestamp = DateTime.UtcNow,
                checks = report.Entries.ToDictionary(
                    x => x.Key,
                    x => new
                    {
                        status = x.Value.Status.ToString(),
                        description = x.Value.Description,
                        duration = x.Value.Duration.TotalMilliseconds
                    }
                ),
                overall = new
                {
                    database = report.Entries.ContainsKey("Database") ? report.Entries["Database"].Status.ToString() : "Unknown",
                    api = report.Entries.ContainsKey("API") ? report.Entries["API"].Status.ToString() : "Unknown",
                    signalr = report.Entries.ContainsKey("SignalR") ? report.Entries["SignalR"].Status.ToString() : "Unknown"
                }
            };
            await context.Response.WriteAsJsonAsync(response);
        }
    });

    endpoints.MapHub<SupportHub>("/hubs/support");
    endpoints.MapHub<AdminChatHub>("/adminChatHub");
    endpoints.MapHub<LotoHub>("/lotoHub");
    endpoints.MapHub<DominoHub>("/dominoHub");
    endpoints.MapHub<OkeyHub>("/okeyHub", options =>
    {
        options.Transports =
        HttpTransportType.WebSockets
        |
        HttpTransportType.LongPolling;
    });

    endpoints.MapHub<BackgammonHub>("/backgammonhub", options =>
    {
        options.Transports =
        HttpTransportType.WebSockets
        |
        HttpTransportType.LongPolling;
    });
    endpoints.MapHub<SekaHub>("/sekaHub");
    endpoints.MapHub<PokerHub>("/pokerHub");
    endpoints.MapHub<DurakHub>("/durakHub");
});

app.Run();

static string GetConnectionString(IConfiguration configuration, IWebHostEnvironment environment)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection");

    if (string.IsNullOrWhiteSpace(connectionString))
    {
        connectionString = environment.IsDevelopment()
            ? configuration.GetConnectionString("MYSqlHome")
            : configuration.GetConnectionString("MYSqlDeploy");
    }

    if (string.IsNullOrWhiteSpace(connectionString) && environment.IsDevelopment())
    {
        connectionString = configuration.GetConnectionString("MYSqlDeploy");
    }

    return string.IsNullOrWhiteSpace(connectionString)
        ? throw new InvalidOperationException("Database connection string is not configured.")
        : connectionString;
}

static string[] GetAllowedOrigins(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configuredOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (!environment.IsDevelopment())
    {
        return configuredOrigins
            .Where(origin => !string.IsNullOrWhiteSpace(origin))
            .Select(origin => origin.Trim().TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var developmentOrigins = new[]
    {
        "http://localhost:3000",
        "http://127.0.0.1:3000",
        "http://localhost:5173",
        "http://127.0.0.1:5173",
        "http://localhost:5063",
        "https://localhost:7046"
    };

    return configuredOrigins
        .Concat(developmentOrigins)
        .Where(origin => !string.IsNullOrWhiteSpace(origin))
        .Select(origin => origin.Trim().TrimEnd('/'))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

static string GetUploadsPath(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configuredPath = configuration["App:UploadsPath"];
    if (!string.IsNullOrWhiteSpace(configuredPath))
    {
        return Path.GetFullPath(Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.Combine(environment.ContentRootPath, configuredPath));
    }

    var webRoot = environment.WebRootPath ?? Path.Combine(environment.ContentRootPath, "wwwroot");
    return Path.Combine(webRoot, "uploads");
}

static string? GetBaseUrl(IConfiguration configuration, string key)
{
    var value = configuration[key];
    return string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimEnd('/');
}

static void ValidateConfiguration(IConfiguration configuration, IWebHostEnvironment environment)
{
    var errors = new List<string>();

    if (string.IsNullOrWhiteSpace(configuration.GetConnectionString("DefaultConnection")) &&
        string.IsNullOrWhiteSpace(configuration.GetConnectionString(environment.IsDevelopment() ? "MYSqlHome" : "MYSqlDeploy")))
    {
        errors.Add("ConnectionStrings:DefaultConnection is required.");
    }

    if (environment.IsDevelopment())
    {
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
        }

        return;
    }

    Require("JwtOptions:Issuer");
    Require("JwtOptions:Audience");
    Require("JwtOptions:SecretKey");
    Require("App:PublicBaseUrl");

    var jwtSecret = configuration["JwtOptions:SecretKey"];
    if (!string.IsNullOrWhiteSpace(jwtSecret) && jwtSecret.Length < 32)
    {
        errors.Add("JwtOptions:SecretKey must be at least 32 characters in production.");
    }

    ValidateHttpsUrl("App:PublicBaseUrl");
    ValidateHttpsUrl("App:FrontendBaseUrl", required: false);

    var allowedOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
    if (allowedOrigins.Length == 0)
    {
        errors.Add("Cors:AllowedOrigins must contain at least one production origin.");
    }

    foreach (var origin in allowedOrigins)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
        {
            errors.Add($"Cors origin must be an absolute URL: {origin}");
            continue;
        }

        var isHttpsOrigin = uri.Scheme == Uri.UriSchemeHttps;

        var isApprovedMobileOrigin =
            string.Equals(origin, "capacitor://localhost", StringComparison.OrdinalIgnoreCase);

        if (!isHttpsOrigin && !isApprovedMobileOrigin)
        {
            errors.Add($"Cors origin must be HTTPS or an approved mobile app origin: {origin}");
        }
    }

    if (configuration.GetValue("NowPayments:Enabled", true))
    {
        Require("NowPayments:ApiKey");
        Require("NowPayments:IpnSecret");
    }

    if (configuration.GetValue("Seed:Enabled", false))
    {
        RejectDefaultPassword("SeedUsers:Password", "Admin123!");
        RejectDefaultSeedPasswords("SeedSupportUsers", "Support123!");
        RejectDefaultSeedPasswords("DepositSeedUsers", "Deposit123!");
        RejectDefaultSeedPasswords("WithdrawSeedUsers", "Withdraw123!");
    }

    if (configuration.GetValue("AuthCookie:Secure", true) == false)
    {
        errors.Add("AuthCookie:Secure must be true in production.");
    }

    if (errors.Count > 0)
    {
        throw new InvalidOperationException(
            "Production configuration is invalid:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, errors.Select(error => $"- {error}")));
    }

    void Require(string key)
    {
        if (string.IsNullOrWhiteSpace(configuration[key]))
        {
            errors.Add($"{key} is required.");
        }
    }

    void ValidateHttpsUrl(string key, bool required = true)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            if (required)
            {
                errors.Add($"{key} is required.");
            }

            return;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add($"{key} must be an absolute HTTPS URL.");
        }
    }

    void RejectDefaultPassword(string key, string defaultValue)
    {
        if (string.Equals(configuration[key], defaultValue, StringComparison.Ordinal))
        {
            errors.Add($"{key} still uses the development default password.");
        }
    }

    void RejectDefaultSeedPasswords(string sectionName, string defaultValue)
    {
        foreach (var item in configuration.GetSection(sectionName).GetChildren())
        {
            if (string.Equals(item["Password"], defaultValue, StringComparison.Ordinal))
            {
                errors.Add($"{sectionName}:{item.Key}:Password still uses the development default password.");
            }
        }
    }
}




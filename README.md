# XGame API

XGame API is a real-time multiplayer gaming backend built with ASP.NET Core 8. It provides authentication, wallets, payments, player rankings, support workflows, and SignalR-powered game rooms for several classic games.

## Features

- JWT authentication with cookie and bearer-token support
- Real-time multiplayer rooms powered by SignalR
- Loto, Domino, Okey, Backgammon, Seka, Poker, and Durak game services
- Wallet balances, coin ledgers, deposits, and withdrawals
- NOWPayments cryptocurrency payment integration and IPN handling
- Player leaderboards and game statistics
- Support tickets and real-time support chat
- Role-based admin, support, cashier, and bank workflows
- Swagger/OpenAPI documentation and JSON health checks
- SQL Server persistence through Entity Framework Core
- Static frontend assets and runtime upload storage

## Architecture

| Project | Responsibility |
| --- | --- |
| `BlogApp.Api` | HTTP API, SignalR hubs, middleware, static files, and application startup |
| `BlogApp.BusinnesLayer` | Services, DTOs, validation, mapping, integrations, and business rules |
| `BlogApp.Core` | Domain entities, enums, and repository contracts |
| `BlogApp.DAL` | Entity Framework Core context, repositories, and migrations |
| `ConsumeWebAPI` | Example API consumer |
| `ConsumeWebMVC` | Example MVC consumer |

## Technology Stack

- .NET 8 and ASP.NET Core
- SignalR
- Entity Framework Core 8 with SQL Server
- JWT bearer authentication
- Swagger / OpenAPI
- FluentValidation and AutoMapper
- MailKit and MimeKit
- Docker

## Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- SQL Server or SQL Server Express
- Docker, if you prefer a containerized deployment

## Local Setup

1. Restore the dependencies:

   ```bash
   dotnet restore BlogApp.sln
   ```

2. Configure a local SQL Server connection:

   ```bash
   dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=BlogAppData;Trusted_Connection=true;TrustServerCertificate=true" --project BlogApp.Api
   ```

   If your SQL Server requires a username and password, use SQL authentication in the connection string instead. Never save the real password in `appsettings.Development.json`.

3. Apply the database migrations:

   ```bash
   dotnet ef database update --project BlogApp.DAL --startup-project BlogApp.Api
   ```

   If the `dotnet ef` command is unavailable, install the .NET 8 EF Core CLI first:

   ```bash
   dotnet tool install --global dotnet-ef --version 8.*
   ```

4. Start the API:

   ```bash
   dotnet run --project BlogApp.Api
   ```

In Development, Swagger is available at `/swagger` and the health endpoint is available at `/health`. The default local launch URLs are defined in `BlogApp.Api/Properties/launchSettings.json`.

## Configuration

ASP.NET Core configuration supports `appsettings` files, user secrets, and environment variables. Keep credentials outside version-controlled files. For environment variables, replace each `:` in a configuration key with `__`.

Copy [.env.example](.env.example) to `.env` when running with Docker:

```bash
cp .env.example .env
```

Replace every `CHANGE_ME` value before starting the application. The `.env` file is ignored by Git, while `.env.example` contains only safe placeholders and should remain committed.

> ASP.NET Core does not load `.env` files automatically during `dotnet run`. Use .NET Secret Manager for local development, export the variables in your shell, or start the Docker container with `--env-file .env`.

Important settings include:

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__DefaultConnection` | SQL Server connection string |
| `JwtOptions__Issuer` | JWT token issuer |
| `JwtOptions__Audience` | JWT token audience |
| `JwtOptions__SecretKey` | JWT signing key; use at least 32 characters in production |
| `App__PublicBaseUrl` | Public API base URL |
| `App__FrontendBaseUrl` | Frontend base URL used in redirects |
| `Cors__AllowedOrigins__0` | First allowed frontend origin; increment the final index for more origins |
| `NowPayments__ApiKey` | NOWPayments API key |
| `NowPayments__IpnSecret` | NOWPayments IPN signature secret |
| `NowPayments__AuthEmail` / `NowPayments__AuthPassword` | NOWPayments account credentials used when invoice lookup requires an auth token |
| `SMTP_USER` / `SMTP_PASS` | SMTP credentials for password-reset email |
| `SMTP_FROM_ADDRESS` | Optional sender address; defaults to `SMTP_USER` |
| `SMTP_FROM_NAME` | Optional display name for outgoing email |

For local development, secrets can also be stored with the Secret Manager:

```bash
dotnet user-secrets set "JwtOptions:SecretKey" "replace-with-a-long-random-development-key" --project BlogApp.Api
dotnet user-secrets set "NowPayments:ApiKey" "your-api-key" --project BlogApp.Api
dotnet user-secrets set "NowPayments:IpnSecret" "your-ipn-secret" --project BlogApp.Api
dotnet user-secrets set "NowPayments:AuthEmail" "your-provider-account-email" --project BlogApp.Api
dotnet user-secrets set "NowPayments:AuthPassword" "your-provider-account-password" --project BlogApp.Api
```

SMTP values are read directly from environment variables rather than ASP.NET Core configuration. Set them in the shell that starts the API:

```bash
export SMTP_USER="your-smtp-account"
export SMTP_PASS="your-smtp-password"
export SMTP_FROM_ADDRESS="no-reply@example.com"
export SMTP_FROM_NAME="XGame Support"
```

Development seed users are configured in `BlogApp.Api/appsettings.Development.json`. They are intended only for local use. Change their passwords before enabling seeding in any shared environment.

## Real-Time Hubs

| Hub | Path |
| --- | --- |
| Support | `/hubs/support` |
| Admin chat | `/adminChatHub` |
| Loto | `/lotoHub` |
| Domino | `/dominoHub` |
| Okey | `/okeyHub` |
| Backgammon | `/backgammonhub` |
| Seka | `/sekaHub` |
| Poker | `/pokerHub` |
| Durak | `/durakHub` |

Authenticated SignalR clients may provide the JWT as the `access_token` query parameter.

## Docker

Create the local environment file, replace its placeholders, then build and run the production image:

```bash
cp .env.example .env
docker build -t xgame-api .
docker run --rm --env-file .env -p 8080:8080 -v xgame-uploads:/app/uploads xgame-api
```

The container listens on port `8080` and stores uploads in the `/app/uploads` volume.

## Security

- Never commit API keys, passwords, JWT secrets, private connection strings, or personal contact details.
- Do not deploy the placeholder values from `.env.example`.
- Use user secrets for development and environment variables or a managed secret store in production.
- Rotate any credential that has previously been committed, even after removing it from the current files.
- Production startup validates required configuration, HTTPS origins, secure cookies, non-default seed passwords, and JWT key length.

## License

No license file is currently included. Add a license before distributing the project publicly.

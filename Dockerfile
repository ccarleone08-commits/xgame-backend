FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY BlogApp.sln ./
COPY BlogApp.Api/BlogApp.Api.csproj BlogApp.Api/
COPY BlogApp.BusinnesLayer/BlogApp.BusinnesLayer.csproj BlogApp.BusinnesLayer/
COPY BlogApp.Core/BlogApp.Core.csproj BlogApp.Core/
COPY BlogApp.DAL/BlogApp.DAL.csproj BlogApp.DAL/
COPY ConsumeWebAPI/ConsumeWebAPI.csproj ConsumeWebAPI/
COPY ConsumeWebMVC/ConsumeWebMVC.csproj ConsumeWebMVC/

RUN dotnet restore BlogApp.Api/BlogApp.Api.csproj

COPY . .
RUN dotnet publish BlogApp.Api/BlogApp.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

ENV ASPNETCORE_ENVIRONMENT=Production
ENV ASPNETCORE_URLS=http://+:8080
ENV App__UploadsPath=/app/uploads

COPY --from=build /app/publish .

RUN mkdir -p /app/uploads && chown -R $APP_UID:$APP_UID /app/uploads

VOLUME ["/app/uploads"]
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "BlogApp.Api.dll"]

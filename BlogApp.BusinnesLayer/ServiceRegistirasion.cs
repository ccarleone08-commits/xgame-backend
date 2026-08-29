using BlogApp.BusinnesLayer.Services.Abstracts;
using BlogApp.BusinnesLayer.Services.Implements;
using BlogApp.BusinnesLayer.Services.Interfaces;
using FluentValidation;
using FluentValidation.AspNetCore;
using Microsoft.Extensions.DependencyInjection;

namespace BlogApp.BusinnesLayer;

public static class ServiceRegistirasion
{
    public static IServiceCollection AddRepositories(this IServiceCollection services)
    {
        services.AddScoped<ICategoryService, CategoryService>();
        services.AddScoped<ISupportTicketService, SupportTicketService>();
        return services;
    }
    public static IServiceCollection AddFluentValidation(this IServiceCollection services)
    {
        services.AddFluentValidationAutoValidation();
        services.AddValidatorsFromAssemblyContaining(typeof(ServiceRegistirasion));
        return services;
    }
    public static IServiceCollection AddAutoMapper(this IServiceCollection services)
    {
        services.AddAutoMapper(_ => { }, typeof(ServiceRegistirasion).Assembly);
        return services;
    }
}

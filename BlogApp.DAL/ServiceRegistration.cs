using BlogApp.Core.Entities;
using BlogApp.Core.Repositories;
using BlogApp.DAL.Repositories;
using Microsoft.Extensions.DependencyInjection;

namespace BlogApp.DAL;

public static class ServiceRegistration
{
    public static IServiceCollection AddService(this IServiceCollection services)
    {
        services.AddScoped<IGenericRepository<Category>, GenericRepository<Category>>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<ISupportTicketRepository, SupportTicketRepository>();
        services.AddScoped<ISupportTicketMessageRepository, SupportTicketMessageRepository>();
        return services;
    }
}

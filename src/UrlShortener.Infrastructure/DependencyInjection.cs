using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Links;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Repositories;
using UrlShortener.Infrastructure.Repositories;

namespace UrlShortener.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddUrlShortenerInfrastructure(this IServiceCollection services, string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        services.AddDbContext<AppDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IShortLinkRepository, ShortLinkRepository>();
        services.AddScoped<IClickEventRepository, ClickEventRepository>();
        services.AddScoped<ILinkCreationRepository, LinkCreationRepository>();
        services.AddScoped<ILinkStatsRepository, LinkStatsRepository>();
        services.AddSingleton<RedirectCache>();
        services.AddScoped<IRedirectStore, CachedRedirectStore>();
        return services;
    }
}

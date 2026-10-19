using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UrlShortener.Infrastructure;

namespace UrlShortener.IntegrationTests;

public sealed class ManualClock : TimeProvider
{
    private long ticks = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc).Ticks;
    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
    public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
}

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public string DatabasePath { get; init; } = Path.Combine(Path.GetTempPath(), $"shortener-api-{Guid.NewGuid():N}.db");
    public ManualClock Clock { get; } = new();
    public bool CleanupDatabase { get; init; } = true;
    public int PermitLimit { get; init; } = 500;
    public string EnvironmentName { get; init; } = "Development";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(EnvironmentName);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Shortener"] = $"Data Source={DatabasePath};Pooling=False",
            ["RateLimiting:PermitLimit"] = PermitLimit.ToString(),
            ["RateLimiting:WindowSeconds"] = "60",
            ["PublicBaseUrl"] = "http://localhost"
        }));
        builder.ConfigureServices(services =>
        {
            services.AddTransient<IStartupFilter, TestClientIpFilter>();
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={DatabasePath};Pooling=False"));
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
        });
    }

    public HttpClient Client() => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!CleanupDatabase) return;
        foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(DatabasePath + suffix);
    }
}

// Test-only remote-address injection. Production never trusts this header.
public sealed class TestClientIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, continuePipeline) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Client-IP"].FirstOrDefault() ?? "127.0.0.1");
            await continuePipeline();
        });
        next(app);
    };
}

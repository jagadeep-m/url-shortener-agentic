using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using UrlShortener.Infrastructure;

namespace UrlShortener.Api;

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.ShortLinks.AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQLite is unavailable or its schema is not initialized.");
        }
    }
}

public static class HealthResponses
{
    public static Task WriteAsync(HttpContext context, HealthReport report) =>
        (report.Status == HealthStatus.Healthy ? Results.Ok(new { status = "Healthy" })
            : Results.Problem(statusCode: 503, title: "Service unavailable", detail: "Database readiness check failed."))
        .ExecuteAsync(context);
}

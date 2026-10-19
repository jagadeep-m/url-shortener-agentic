using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace UrlShortener.Infrastructure;

public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var connectionString = args.Length > 0 ? args[0] : "Data Source=urlshortener.db";
        return new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(connectionString).Options);
    }
}

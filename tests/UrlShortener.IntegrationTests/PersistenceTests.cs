using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;
using UrlShortener.Infrastructure;
using UrlShortener.Infrastructure.Repositories;
using Xunit;

namespace UrlShortener.IntegrationTests;

public sealed class PersistenceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);
    private readonly SqliteConnection connection = new("Data Source=:memory:");

    private AppDbContext CreateContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseSqlite(connection).Options);

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => connection.DisposeAsync().AsTask();

    [Fact]
    public async Task MigrationCreatesExpectedIndexesAndMatchesModel()
    {
        await using var db = CreateContext();
        // Every migration in the assembly is applied (not a hard-coded count, so new migrations do not break this test).
        Assert.Equal(db.Database.GetMigrations(), await db.Database.GetAppliedMigrationsAsync());
        Assert.False(db.Database.HasPendingModelChanges());
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA index_info('IX_ClickEvents_ShortLinkId_ClickedAtUtc')";
        await using (var reader = await command.ExecuteReaderAsync())
        {
            var columns = new List<string>();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(2));
            Assert.Equal(new[] { "ShortLinkId", "ClickedAtUtc" }, columns);
        }
        command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'IX_ShortLinks_Code'";
        Assert.Contains("UNIQUE INDEX", (string)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task MigrationCanBeRevertedAndAppliedAgain()
    {
        await using var db = CreateContext();
        await db.GetService<IMigrator>().MigrateAsync("0");
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name IN ('ShortLinks', 'ClickEvents')";
        Assert.Equal(0L, await command.ExecuteScalarAsync());
        await db.Database.MigrateAsync();
        Assert.Empty(await db.ShortLinks.ToListAsync());
    }

    [Fact]
    public async Task LinkRoundTripsUtcAndQueriesExpiryInSqlite()
    {
        await using var db = CreateContext();
        var repository = new ShortLinkRepository(db);
        var link = new ShortLink("abc1234", "https://example.com", Now, Now.AddHours(1));
        Assert.True(await repository.TryAddAsync(link));
        Assert.True(link.Id > 0);
        var stored = await repository.FindByCodeAsync(link.Code);
        Assert.NotNull(stored);
        Assert.NotSame(link, stored);
        Assert.Equal(DateTimeKind.Utc, stored.CreatedAtUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, stored.ExpiresAtUtc!.Value.Kind);
        Assert.Equal(Now.AddHours(1), stored.ExpiresAtUtc);
        Assert.Single(await db.ShortLinks.Where(x => x.ExpiresAtUtc <= Now.AddHours(2)).ToListAsync());
        Assert.Null(await repository.FindByCodeAsync("missing"));
    }

    [Fact]
    public async Task DuplicateCodeIsRejectedWithoutPoisoningNextInsert()
    {
        await using var db = CreateContext();
        var repository = new ShortLinkRepository(db);
        Assert.True(await repository.TryAddAsync(new ShortLink("abc", "https://example.com", Now)));
        Assert.False(await repository.TryAddAsync(new ShortLink("abc", "https://other.example", Now)));
        Assert.True(await repository.TryAddAsync(new ShortLink("ABC", "https://example.com", Now)));
        Assert.Equal(2, await db.ShortLinks.CountAsync());
        Assert.Null((await repository.FindByCodeAsync("abc"))!.ExpiresAtUtc);
    }

    [Fact]
    public async Task DisableIsPersistentIdempotentAndDoesNotDeleteAnalytics()
    {
        await using var db = CreateContext();
        var links = new ShortLinkRepository(db);
        var clicks = new ClickEventRepository(db);
        var link = new ShortLink("abc", "https://example.com", Now);
        await links.TryAddAsync(link);
        await clicks.RecordAsync(new ClickEvent(link.Id, Now));
        Assert.True(await links.DisableAsync(link.Code));
        Assert.True(await links.DisableAsync(link.Code));
        Assert.False(await links.DisableAsync("missing"));
        Assert.False((await links.FindByCodeAsync(link.Code))!.IsActive);
        Assert.Single(await clicks.GetByShortLinkIdAsync(link.Id));
    }

    [Fact]
    public async Task ClicksRoundTripUtcAreOrderedAndIncrementTotal()
    {
        await using var db = CreateContext();
        var links = new ShortLinkRepository(db);
        var clicks = new ClickEventRepository(db);
        var link = new ShortLink("abc", "https://example.com", Now);
        await links.TryAddAsync(link);
        Assert.True(await clicks.RecordAsync(new ClickEvent(link.Id, Now.AddMinutes(1), new string('a', 64))));
        Assert.True(await clicks.RecordAsync(new ClickEvent(link.Id, Now)));
        var events = await clicks.GetByShortLinkIdAsync(link.Id);
        Assert.Equal(new[] { Now, Now.AddMinutes(1) }, events.Select(x => x.ClickedAtUtc));
        Assert.All(events, x => Assert.Equal(DateTimeKind.Utc, x.ClickedAtUtc.Kind));
        Assert.Equal(new string('a', 64), events[1].IpHash);
        Assert.Equal(2L, (await links.FindByCodeAsync(link.Code))!.ClickCount);
        Assert.Empty(await clicks.GetByShortLinkIdAsync(long.MaxValue));
    }

    [Fact]
    public async Task MissingLinkDoesNotRecordClick()
    {
        await using var db = CreateContext();
        Assert.False(await new ClickEventRepository(db).RecordAsync(new ClickEvent(123, Now)));
        Assert.Empty(await db.ClickEvents.ToListAsync());
    }

    [Fact]
    public async Task FailedEventInsertRollsBackCounterAndAllowsRecovery()
    {
        await using var db = CreateContext();
        var links = new ShortLinkRepository(db);
        var clicks = new ClickEventRepository(db);
        var link = new ShortLink("abc", "https://example.com", Now);
        await links.TryAddAsync(link);
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectClick BEFORE INSERT ON ClickEvents BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => clicks.RecordAsync(new ClickEvent(link.Id, Now)));
        Assert.Equal(0L, (await links.FindByCodeAsync(link.Code))!.ClickCount);
        Assert.Empty(await db.ClickEvents.ToListAsync());
        await db.Database.ExecuteSqlRawAsync("DROP TRIGGER RejectClick;");
        Assert.True(await clicks.RecordAsync(new ClickEvent(link.Id, Now)));
        Assert.Equal(1L, (await links.FindByCodeAsync(link.Code))!.ClickCount);
    }

    [Fact]
    public async Task DatabaseRejectsOrphanClick()
    {
        await using var db = CreateContext();
        db.ClickEvents.Add(new ClickEvent(123, Now));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task CancellationPreventsInsert()
    {
        await using var db = CreateContext();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ShortLinkRepository(db).TryAddAsync(new ShortLink("abc", "https://example.com", Now), cancellation.Token));
        Assert.Empty(await db.ShortLinks.ToListAsync());
    }

    [Fact]
    public void InfrastructureRegistrationAndDesignTimeFactoryUseSqlite()
    {
        var services = new ServiceCollection().AddUrlShortenerInfrastructure("Data Source=:memory:");
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.IsType<ShortLinkRepository>(scope.ServiceProvider.GetRequiredService<IShortLinkRepository>());
        Assert.IsType<ClickEventRepository>(scope.ServiceProvider.GetRequiredService<IClickEventRepository>());
        Assert.Equal("Microsoft.EntityFrameworkCore.Sqlite", scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ProviderName);
        using var designContext = new AppDbContextFactory().CreateDbContext(["Data Source=:memory:"]);
        Assert.Equal("Data Source=:memory:", designContext.Database.GetConnectionString());
    }

    [Fact]
    public async Task ConcurrentWritersDoNotLoseClickCounts()
    {
        var file = Path.Combine(Path.GetTempPath(), $"shortener-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={file};Pooling=False;Default Timeout=30").Options;
        try
        {
            long linkId;
            await using (var setup = new AppDbContext(options))
            {
                await setup.Database.MigrateAsync();
                var link = new ShortLink("abc", "https://example.com", Now);
                await new ShortLinkRepository(setup).TryAddAsync(link);
                linkId = link.Id;
            }
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(async () =>
            {
                await using var writer = new AppDbContext(options);
                Assert.True(await new ClickEventRepository(writer).RecordAsync(new ClickEvent(linkId, Now)));
            })));
            await using var verification = new AppDbContext(options);
            Assert.Equal(20L, (await verification.ShortLinks.SingleAsync()).ClickCount);
            Assert.Equal(20, await verification.ClickEvents.CountAsync());
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" }) File.Delete(file + suffix);
        }
    }
}

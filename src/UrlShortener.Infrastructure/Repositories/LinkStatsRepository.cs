using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Links;

namespace UrlShortener.Infrastructure.Repositories;

public sealed class LinkStatsRepository(AppDbContext db) : ILinkStatsRepository
{
    public async Task<LinkStats> GetAsync(long linkId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var query = db.ClickEvents.AsNoTracking().Where(x => x.ShortLinkId == linkId);
        var total = await query.LongCountAsync(ct);
        var days = await query.GroupBy(x => x.ClickedAtUtc.Date).OrderBy(x => x.Key)
            .Select(x => new DailyClicks(x.Key, x.LongCount())).ToListAsync(ct);
        var referrers = await query.Where(x => x.Referrer != null && x.Referrer != "")
            .GroupBy(x => x.Referrer!).OrderByDescending(x => x.LongCount()).ThenBy(x => x.Key)
            .Take(10).Select(x => new ReferrerClicks(x.Key, x.LongCount())).ToListAsync(ct);
        await transaction.CommitAsync(ct);
        return new(total, days.Select(day => day with { Date = DateTime.SpecifyKind(day.Date, DateTimeKind.Utc) }).ToArray(), referrers);
    }
}

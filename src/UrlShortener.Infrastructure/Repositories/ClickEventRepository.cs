using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Repositories;

public sealed class ClickEventRepository(AppDbContext db) : IClickEventRepository
{
    public async Task<bool> RecordAsync(ClickEvent clickEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(clickEvent);
        if (clickEvent.Id != 0)
        {
            throw new ArgumentException("Only new click events may be recorded.", nameof(clickEvent));
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var affected = await db.ShortLinks.Where(link => link.Id == clickEvent.ShortLinkId)
            .ExecuteUpdateAsync(update => update.SetProperty(link => link.ClickCount, link => link.ClickCount + 1), cancellationToken);
        if (affected == 0)
        {
            return false;
        }

        db.ClickEvents.Add(clickEvent);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch
        {
            db.Entry(clickEvent).State = EntityState.Detached;
            throw; // Disposing the uncommitted transaction rolls back the counter as well.
        }
    }

    public async Task<IReadOnlyList<ClickEvent>> GetByShortLinkIdAsync(long shortLinkId,
        CancellationToken cancellationToken = default) =>
        await db.ClickEvents.AsNoTracking().Where(click => click.ShortLinkId == shortLinkId)
            .OrderBy(click => click.ClickedAtUtc).ThenBy(click => click.Id)
            .ToListAsync(cancellationToken);
}

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Repositories;

public sealed class ShortLinkRepository(AppDbContext db) : IShortLinkRepository
{
    public Task<ShortLink?> FindByCodeAsync(string code, CancellationToken cancellationToken = default) =>
        db.ShortLinks.AsNoTracking().SingleOrDefaultAsync(link => link.Code == code, cancellationToken);

    public async Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        if (link.Id != 0)
        {
            throw new ArgumentException("Only new links may be added.", nameof(link));
        }

        db.ShortLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (exception.InnerException is SqliteException { SqliteExtendedErrorCode: 2067 })
        {
            // Code is the only unique non-primary-key constraint in this table.
            db.Entry(link).State = EntityState.Detached;
            return false;
        }
        catch
        {
            db.Entry(link).State = EntityState.Detached;
            throw;
        }
    }

    public async Task<bool> DisableAsync(string code, CancellationToken cancellationToken = default) =>
        await db.ShortLinks.Where(link => link.Code == code)
            .ExecuteUpdateAsync(update => update.SetProperty(link => link.IsActive, false), cancellationToken) > 0;
}

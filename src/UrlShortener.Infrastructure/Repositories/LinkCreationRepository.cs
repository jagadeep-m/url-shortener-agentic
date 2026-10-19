using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using UrlShortener.Application.Links;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure.Repositories;

public sealed class LinkCreationRepository(AppDbContext db) : ILinkCreationRepository
{
    public async Task<IdempotentLink?> FindByKeyAsync(string key, CancellationToken ct)
    {
        var record = await db.Set<IdempotencyRecord>().AsNoTracking().Include(x => x.Link)
            .SingleOrDefaultAsync(x => x.Key == key, ct);
        return record is null ? null : new IdempotentLink(record.RequestHash, record.Link);
    }

    public async Task<CreationResult> TryCreateAsync(ShortLink link, string? key, string requestHash, CancellationToken ct)
    {
        db.ShortLinks.Add(link);
        var record = key is null ? null : new IdempotencyRecord { Key = key, RequestHash = requestHash, Link = link };
        if (record is not null) db.Add(record);
        try
        {
            // SaveChanges wraps both inserts in a transaction: no orphan link on key conflict.
            await db.SaveChangesAsync(ct);
            return new(CreationStatus.Created, link);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19, SqliteExtendedErrorCode: 1555 or 2067 })
        {
            db.Entry(link).State = EntityState.Detached;
            if (record is not null) db.Entry(record).State = EntityState.Detached;
            if (key is not null && await FindByKeyAsync(key, ct) is { } previous)
                return previous.RequestHash == requestHash
                    ? new(CreationStatus.Replayed, previous.Link) : new(CreationStatus.KeyConflict);
            return new(CreationStatus.CodeConflict);
        }
    }
}

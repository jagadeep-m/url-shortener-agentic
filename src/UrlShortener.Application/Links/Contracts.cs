using UrlShortener.Domain;

namespace UrlShortener.Application.Links;

public sealed record CreateLinkRequest(string LongUrl, string? CustomAlias = null, DateTime? ExpiresAt = null);
public sealed record LinkResponse(string Code, string ShortUrl, string LongUrl, DateTime CreatedAt, DateTime? ExpiresAt);
public sealed record LinkDetails(string Code, string ShortUrl, string LongUrl, DateTime CreatedAt, DateTime? ExpiresAt, bool IsActive, long ClickCount);
public sealed record DailyClicks(DateTime Date, long Clicks);
public sealed record ReferrerClicks(string Referrer, long Clicks);
public sealed record LinkStats(long TotalClicks, IReadOnlyList<DailyClicks> ClicksPerDay, IReadOnlyList<ReferrerClicks> TopReferrers);
public sealed record IdempotentLink(string RequestHash, ShortLink Link);
public enum CreationStatus { Created, CodeConflict, Replayed, KeyConflict }
public sealed record CreationResult(CreationStatus Status, ShortLink? Link = null);

public interface ILinkCreationRepository
{
    Task<IdempotentLink?> FindByKeyAsync(string key, CancellationToken ct);
    Task<CreationResult> TryCreateAsync(ShortLink link, string? key, string requestHash, CancellationToken ct);
}

public interface IRedirectStore
{
    Task<ShortLink?> FindAsync(string code, CancellationToken ct);
    Task<bool> DisableAsync(string code, CancellationToken ct);
}

public interface ILinkStatsRepository
{
    Task<LinkStats> GetAsync(long linkId, CancellationToken ct);
}

public sealed class LinkOperationException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

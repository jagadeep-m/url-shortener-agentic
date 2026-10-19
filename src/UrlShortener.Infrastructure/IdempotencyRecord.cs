using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

public sealed class IdempotencyRecord
{
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public long ShortLinkId { get; set; }
    public ShortLink Link { get; set; } = null!;
}

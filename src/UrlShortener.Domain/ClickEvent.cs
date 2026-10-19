namespace UrlShortener.Domain;

public sealed class ClickEvent
{
    private ClickEvent() { }

    public ClickEvent(long shortLinkId, DateTime clickedAtUtc, string? ipHash = null,
        string? referrer = null, string? userAgent = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(shortLinkId);
        UtcTimestamp.RequireUtc(clickedAtUtc, nameof(clickedAtUtc));
        if (ipHash is not null && (ipHash.Length != 64 || ipHash.Any(c => !char.IsAsciiHexDigit(c))))
        {
            throw new ArgumentException("IP hash must be a 64-character SHA-256 hexadecimal digest.", nameof(ipHash));
        }

        ShortLinkId = shortLinkId;
        ClickedAtUtc = clickedAtUtc;
        IpHash = ipHash?.ToLowerInvariant();
        Referrer = referrer;
        UserAgent = userAgent;
    }

    public long Id { get; private set; }
    public long ShortLinkId { get; private set; }
    public DateTime ClickedAtUtc { get; private set; }
    public string? Referrer { get; private set; }
    public string? UserAgent { get; private set; }
    public string? IpHash { get; private set; }
}

namespace UrlShortener.Domain;

public sealed class ShortLink
{
    private ShortLink() { }

    public ShortLink(string code, string longUrl, DateTime createdAtUtc, DateTime? expiresAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length is < 3 or > 30 || code.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        {
            throw new ArgumentException("Code must contain 3–30 ASCII letters, digits, underscores or hyphens.", nameof(code));
        }

        if (new[] { "api", "health", "swagger" }.Contains(code, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Code is reserved.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(longUrl);
        if (longUrl.Length > 2048 || longUrl != longUrl.Trim() || longUrl.Any(char.IsControl) || !Uri.TryCreate(longUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new ArgumentException("Target must be an absolute HTTP(S) URL of at most 2,048 characters.", nameof(longUrl));
        }

        UtcTimestamp.RequireUtc(createdAtUtc, nameof(createdAtUtc));
        if (expiresAtUtc is { } expiry)
        {
            UtcTimestamp.RequireUtc(expiry, nameof(expiresAtUtc));
            if (expiry <= createdAtUtc)
            {
                throw new ArgumentOutOfRangeException(nameof(expiresAtUtc), "Expiry must be later than creation.");
            }
        }

        Code = code;
        LongUrl = longUrl;
        CreatedAtUtc = createdAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public long Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string LongUrl { get; private set; } = string.Empty;
    public DateTime CreatedAtUtc { get; private set; }
    public DateTime? ExpiresAtUtc { get; private set; }
    public bool IsActive { get; private set; } = true;
    public long ClickCount { get; private set; }

    public void Disable() => IsActive = false;

    public bool IsAvailableAt(DateTime atUtc)
    {
        UtcTimestamp.RequireUtc(atUtc, nameof(atUtc));
        return IsActive && (ExpiresAtUtc is null || atUtc < ExpiresAtUtc);
    }
}

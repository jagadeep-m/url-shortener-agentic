using System.Security.Cryptography;
using System.Net;
using System.Text;
using System.Text.Json;
using FluentValidation;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;

namespace UrlShortener.Application.Links;

public sealed class LinkService(IValidator<CreateLinkRequest> validator, ILinkCreationRepository creation,
    IShortLinkRepository links, IRedirectStore redirects, ILinkStatsRepository stats,
    ICodeGenerator generator, TimeProvider clock)
{
    public async Task<ShortLink> CreateAsync(CreateLinkRequest request, string? key,
        IReadOnlyCollection<string> ownHosts, CancellationToken ct = default)
    {
        await validator.ValidateAndThrowAsync(request, ct);
        if (key is not null && (string.IsNullOrWhiteSpace(key) || key.Length > 128 || key.Any(char.IsControl)))
            throw new LinkOperationException(400, "Idempotency-Key must contain 1–128 non-control characters.");
        var target = new Uri(request.LongUrl);
        if (ownHosts.Any(host => string.Equals(host.TrimEnd('.'), target.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase)
            || (IsLoopbackHost(host) && target.IsLoopback)))
            throw new LinkOperationException(400, "The target URL cannot point to this service.");

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        if (key is not null && await creation.FindByKeyAsync(key, ct) is { } previous)
        {
            if (previous.RequestHash != hash) throw new LinkOperationException(409, "Idempotency-Key was used with a different request.");
            return previous.Link;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (request.ExpiresAt <= now) throw new LinkOperationException(400, "Expiry must be in the future.");
        // An initial attempt plus at most three collision retries.
        for (var attempt = 0; attempt <= 3; attempt++)
        {
            var code = request.CustomAlias ?? generator.Generate();
            if (request.CustomAlias is null && new[] { "api", "health", "swagger" }.Contains(code, StringComparer.OrdinalIgnoreCase)) continue;
            var result = await creation.TryCreateAsync(new ShortLink(code, request.LongUrl, now, request.ExpiresAt), key, hash, ct);
            if (result.Status is CreationStatus.Created or CreationStatus.Replayed) return result.Link!;
            if (result.Status == CreationStatus.KeyConflict) throw new LinkOperationException(409, "Idempotency-Key was used with a different request.");
            if (request.CustomAlias is not null) throw new LinkOperationException(409, "The custom alias is already taken.");
        }
        throw new LinkOperationException(503, "Unable to allocate a unique code. Please retry.");
    }

    private static bool IsLoopbackHost(string host) => host.TrimEnd('.').Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address));

    public async Task<ShortLink> GetAsync(string code, CancellationToken ct = default) =>
        await links.FindByCodeAsync(code, ct) ?? throw new LinkOperationException(404, "Link not found.");

    public async Task<ShortLink> ResolveAsync(string code, CancellationToken ct = default)
    {
        var link = await redirects.FindAsync(code, ct) ?? throw new LinkOperationException(404, "Link not found.");
        if (!link.IsAvailableAt(clock.GetUtcNow().UtcDateTime)) throw new LinkOperationException(410, "Link has expired or is disabled.");
        return link;
    }

    public async Task DisableAsync(string code, CancellationToken ct = default)
    {
        if (!await redirects.DisableAsync(code, ct)) throw new LinkOperationException(404, "Link not found.");
    }

    public async Task<LinkStats> GetStatsAsync(string code, CancellationToken ct = default)
    {
        var link = await GetAsync(code, ct);
        return await stats.GetAsync(link.Id, ct);
    }
}

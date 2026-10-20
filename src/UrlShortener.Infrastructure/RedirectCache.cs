using Microsoft.Extensions.Caching.Memory;
using UrlShortener.Application.Links;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;

namespace UrlShortener.Infrastructure;

// A bounded cache and fixed lock stripes prevent unbounded memory growth from arbitrary codes.
public sealed class RedirectCache : IDisposable
{
    internal IMemoryCache Cache { get; } = new MemoryCache(new MemoryCacheOptions { SizeLimit = 10_000 });
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    internal SemaphoreSlim Gate(string code) => gates[(uint)StringComparer.Ordinal.GetHashCode(code) % gates.Length];
    public void Dispose()
    {
        Cache.Dispose();
        foreach (var gate in gates) gate.Dispose();
    }
}

public sealed class CachedRedirectStore(IShortLinkRepository links, RedirectCache shared) : IRedirectStore
{
    public async Task<ShortLink?> FindAsync(string code, CancellationToken ct)
    {
        var gate = shared.Gate(code);
        await gate.WaitAsync(ct);
        try
        {
            if (shared.Cache.TryGetValue(code, out ShortLink? cached)) return cached;
            var link = await links.FindByCodeAsync(code, ct);
            if (link is not null) shared.Cache.Set(code, link, new MemoryCacheEntryOptions
            { AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1), Size = 1 });
            return link;
        }
        finally { gate.Release(); }
    }

    public async Task<bool> DisableAsync(string code, CancellationToken ct)
    {
        var gate = shared.Gate(code);
        await gate.WaitAsync(ct);
        try
        {
            var found = await links.DisableAsync(code, ct);
            shared.Cache.Remove(code);
            return found;
        }
        finally { gate.Release(); }
    }
}

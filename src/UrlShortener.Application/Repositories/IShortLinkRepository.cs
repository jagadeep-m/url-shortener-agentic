using UrlShortener.Domain;

namespace UrlShortener.Application.Repositories;

public interface IShortLinkRepository
{
    Task<ShortLink?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);
    // Saves immediately; false means the code already exists. Retry policy belongs to the service.
    Task<bool> TryAddAsync(ShortLink link, CancellationToken cancellationToken = default);
    // Returns true for an existing link, including one already disabled.
    Task<bool> DisableAsync(string code, CancellationToken cancellationToken = default);
}

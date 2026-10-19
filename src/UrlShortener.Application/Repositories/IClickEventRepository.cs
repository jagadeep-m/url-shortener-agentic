using UrlShortener.Domain;

namespace UrlShortener.Application.Repositories;

public interface IClickEventRepository
{
    // Atomically persists the event and increments the total. False means the link no longer exists.
    Task<bool> RecordAsync(ClickEvent clickEvent, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ClickEvent>> GetByShortLinkIdAsync(long shortLinkId,
        CancellationToken cancellationToken = default);
}

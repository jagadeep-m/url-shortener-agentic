using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UrlShortener.Api;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;
using Xunit;

namespace UrlShortener.IntegrationTests;

public sealed class ClickRecorderTests
{
    private sealed class Repository : IClickEventRepository
    {
        public int Calls { get; private set; }
        public List<long> Recorded { get; } = [];
        public Task<bool> RecordAsync(ClickEvent click, CancellationToken ct = default)
        {
            Calls++;
            if (click.ShortLinkId == 1) throw new InvalidOperationException("Injected storage failure");
            Recorded.Add(click.ShortLinkId);
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<ClickEvent>> GetByShortLinkIdAsync(long id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    [Fact]
    public void QueueIsBoundedAndRejectsAfterCompletion()
    {
        var queue = new ClickQueue();
        for (var i = 0; i < 1024; i++) Assert.True(queue.TryWrite(new ClickEvent(1, DateTime.UtcNow)));
        Assert.False(queue.TryWrite(new ClickEvent(1, DateTime.UtcNow)));
        queue.Complete();
        Assert.False(queue.TryWrite(new ClickEvent(1, DateTime.UtcNow)));
    }

    [Fact]
    public async Task WorkerBoundsFailureRetriesContinuesAndDrainsOnStop()
    {
        var repository = new Repository();
        using var provider = new ServiceCollection().AddSingleton<IClickEventRepository>(repository).BuildServiceProvider();
        var queue = new ClickQueue();
        using var worker = new ClickRecorder(queue, provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<ClickRecorder>.Instance);
        await worker.StartAsync(CancellationToken.None);
        Assert.True(queue.TryWrite(new ClickEvent(1, DateTime.UtcNow)));
        Assert.True(queue.TryWrite(new ClickEvent(2, DateTime.UtcNow)));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await worker.StopAsync(timeout.Token);
        Assert.Equal(4, repository.Calls);
        Assert.Equal(2L, Assert.Single(repository.Recorded));
    }
}

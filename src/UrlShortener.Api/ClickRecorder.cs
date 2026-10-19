using System.Threading.Channels;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;

namespace UrlShortener.Api;

public sealed class ClickQueue
{
    private readonly Channel<ClickEvent> channel = Channel.CreateBounded<ClickEvent>(new BoundedChannelOptions(1024)
        { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    public bool TryWrite(ClickEvent click) => channel.Writer.TryWrite(click);
    public ChannelReader<ClickEvent> Reader => channel.Reader;
    public void Complete() => channel.Writer.TryComplete();
}

public sealed class ClickRecorder(ClickQueue queue, IServiceScopeFactory scopes, ILogger<ClickRecorder> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var click in queue.Reader.ReadAllAsync(stoppingToken))
        {
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var recorded = await scope.ServiceProvider.GetRequiredService<IClickEventRepository>().RecordAsync(click, stoppingToken);
                    if (!recorded) logger.LogWarning("Click target {LinkId} no longer exists", click.ShortLinkId);
                    break;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Click recording failed for link {LinkId}, attempt {Attempt}", click.ShortLinkId, attempt);
                    if (attempt < 3) await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt), stoppingToken);
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();
        try
        {
            if (ExecuteTask is not null) await ExecuteTask.WaitAsync(cancellationToken);
        }
        finally { await base.StopAsync(cancellationToken); }
    }
}

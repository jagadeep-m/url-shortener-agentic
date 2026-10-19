using FluentValidation;
using UrlShortener.Application.Links;
using UrlShortener.Application.Repositories;
using UrlShortener.Domain;
using Xunit;

namespace UrlShortener.UnitTests;

public sealed class LinkServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class Generator(params string[] codes) : ICodeGenerator
    {
        public int Calls { get; private set; }
        public string Generate() => codes[Math.Min(Calls++, codes.Length - 1)];
    }

    private sealed class Store : ILinkCreationRepository, IShortLinkRepository, IRedirectStore, ILinkStatsRepository
    {
        public int Failures { get; init; }
        public int Calls { get; private set; }
        public Task<IdempotentLink?> FindByKeyAsync(string key, CancellationToken ct) => Task.FromResult<IdempotentLink?>(null);
        public Task<CreationResult> TryCreateAsync(ShortLink link, string? key, string requestHash, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(Calls <= Failures ? new CreationResult(CreationStatus.CodeConflict) : new CreationResult(CreationStatus.Created, link));
        }
        public Task<ShortLink?> FindByCodeAsync(string code, CancellationToken ct = default) => Task.FromResult<ShortLink?>(null);
        public Task<ShortLink?> FindAsync(string code, CancellationToken ct) => Task.FromResult<ShortLink?>(null);
        public Task<bool> TryAddAsync(ShortLink link, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DisableAsync(string code, CancellationToken ct = default) => Task.FromResult(false);
        public Task<LinkStats> GetAsync(long linkId, CancellationToken ct) => throw new NotSupportedException();
    }

    private static LinkService Service(Store store, Generator generator) =>
        new(new CreateLinkValidator(), store, store, store, store, generator, new Clock());

    [Fact]
    public async Task RetriesCollisionsAtMostThreeTimes()
    {
        var store = new Store { Failures = int.MaxValue };
        var generator = new Generator("Abc1234");
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, generator).CreateAsync(new("https://example.com"), null, ["localhost"]));
        Assert.Equal(503, exception.StatusCode);
        Assert.Equal(4, store.Calls);
        Assert.Equal(4, generator.Calls);
    }

    [Fact]
    public async Task ThirdRetryCanSucceed()
    {
        var store = new Store { Failures = 3 };
        var generator = new Generator("Abc1234", "Bbc1234", "Cbc1234", "Dbc1234");
        var result = await Service(store, generator).CreateAsync(new("https://example.com"), null, ["localhost"]);
        Assert.Equal("Dbc1234", result.Code);
        Assert.Equal(4, store.Calls);
    }

    [Fact]
    public async Task CustomAliasConflictIsNotRetried()
    {
        var store = new Store { Failures = 100 };
        var generator = new Generator("Abc1234");
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, generator).CreateAsync(new("https://example.com", "custom"), null, ["localhost"]));
        Assert.Equal(409, exception.StatusCode);
        Assert.Equal(1, store.Calls);
        Assert.Equal(0, generator.Calls);
    }

    [Fact]
    public async Task GeneratedReservedCodeIsSkipped()
    {
        var store = new Store();
        var generator = new Generator("swagger", "Abc1234");
        var link = await Service(store, generator).CreateAsync(new("https://example.com"), null, ["localhost"]);
        Assert.Equal("Abc1234", link.Code);
        Assert.Equal(1, store.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("bad\nkey")]
    public async Task InvalidIdempotencyKeyDoesNotWrite(string key)
    {
        var store = new Store();
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, new("Abc1234")).CreateAsync(new("https://example.com"), key, ["localhost"]));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task OverlongIdempotencyKeyDoesNotWrite()
    {
        var store = new Store();
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, new("Abc1234")).CreateAsync(new("https://example.com"), new string('a', 129), ["localhost"]));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("https://sho.rt/path")]
    [InlineData("http://SHO.RT.:8080/path")]
    public async Task ConfiguredOwnHostIsRejected(string url)
    {
        var store = new Store();
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, new("Abc1234")).CreateAsync(new(url), null, ["localhost", "sho.rt"]));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task PastExpiryDoesNotWrite()
    {
        var store = new Store();
        var exception = await Assert.ThrowsAsync<LinkOperationException>(() =>
            Service(store, new("Abc1234")).CreateAsync(new("https://example.com", ExpiresAt: new Clock().GetUtcNow().UtcDateTime), null, ["localhost"]));
        Assert.Equal(400, exception.StatusCode);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task MissingUrlProducesValidationFailure()
    {
        var store = new Store();
        await Assert.ThrowsAsync<ValidationException>(() => Service(store, new("Abc1234")).CreateAsync(new(null!), null, ["localhost"]));
        Assert.Equal(0, store.Calls);
    }
}

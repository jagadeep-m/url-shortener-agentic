using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using UrlShortener.Application.Links;
using UrlShortener.Infrastructure;
using Xunit;

namespace UrlShortener.IntegrationTests;

public sealed class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private async Task<LinkResponse> CreateAsync(HttpClient client, string? alias = null, DateTime? expiry = null)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com/docs", alias, expiry));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var link = (await response.Content.ReadFromJsonAsync<LinkResponse>())!;
        Assert.Equal($"/api/v1/links/{link.Code}", response.Headers.Location!.OriginalString);
        return link;
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)status, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task CreateDetailsAndRedirect()
    {
        using var client = factory.Client();
        var link = await CreateAsync(client);
        Assert.Equal(7, link.Code.Length);
        Assert.Equal($"http://localhost/{link.Code}", link.ShortUrl);
        var details = await client.GetFromJsonAsync<LinkDetails>($"/api/v1/links/{link.Code}");
        Assert.Equal(link.LongUrl, details!.LongUrl);
        Assert.True(details.IsActive);
        using var redirect = await client.GetAsync($"/{link.Code}");
        Assert.Equal(HttpStatusCode.Found, redirect.StatusCode);
        Assert.Equal(link.LongUrl, redirect.Headers.Location!.AbsoluteUri);
        Assert.True(redirect.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public async Task DuplicateAliasReturnsConflict()
    {
        using var client = factory.Client();
        var alias = "alias-" + Guid.NewGuid().ToString("N")[..12];
        await CreateAsync(client, alias);
        using var duplicate = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com", alias));
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,test")]
    [InlineData("http://localhost/self")]
    [InlineData("http://LOCALHOST./self")]
    [InlineData("/relative")]
    [InlineData("http://127.0.0.1/self")]
    [InlineData("http://[::1]/self")]
    [InlineData("https://example.com/\r\nInjected: header")]
    public async Task InvalidUrlsReturnBadRequest(string url)
    {
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest(url));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task LongUrlAndMalformedJsonReturnBadRequest()
    {
        using var client = factory.Client();
        using var tooLong = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com/" + new string('x', 2048)));
        await AssertProblemAsync(tooLong, HttpStatusCode.BadRequest);
        using var malformed = await client.PostAsync("/api/v1/links", new StringContent("{ broken", Encoding.UTF8, "application/json"));
        await AssertProblemAsync(malformed, HttpStatusCode.BadRequest);
    }

    [Theory]
    [InlineData("api")]
    [InlineData("HEALTH")]
    [InlineData("swagger")]
    [InlineData("ab")]
    [InlineData("bad alias")]
    [InlineData("abc\n")]
    public async Task InvalidAliasesReturnBadRequest(string alias)
    {
        using var client = factory.Client();
        using var response = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com", alias));
        await AssertProblemAsync(response, HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UnknownRoutesReturnProblemDetails()
    {
        using var client = factory.Client();
        foreach (var path in new[] { "/not-found", "/api/v1/links/not-found", "/api/v1/links/not-found/stats", "/api/unknown/path" })
        {
            using var response = await client.GetAsync(path);
            await AssertProblemAsync(response, HttpStatusCode.NotFound);
        }
        using var deleted = await client.DeleteAsync("/api/v1/links/not-found");
        await AssertProblemAsync(deleted, HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CachedLinkExpiresAtBoundary()
    {
        using var client = factory.Client();
        var link = await CreateAsync(client, expiry: factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(1));
        using var first = await client.GetAsync($"/{link.Code}");
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        using var expired = await client.GetAsync($"/{link.Code}");
        await AssertProblemAsync(expired, HttpStatusCode.Gone);
    }

    [Fact]
    public async Task DeleteInvalidatesWarmCache()
    {
        using var client = factory.Client();
        var link = await CreateAsync(client);
        using var warm = await client.GetAsync($"/{link.Code}");
        Assert.Equal(HttpStatusCode.Found, warm.StatusCode);
        using var deleted = await client.DeleteAsync($"/api/v1/links/{link.Code}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var redirect = await client.GetAsync($"/{link.Code}");
        await AssertProblemAsync(redirect, HttpStatusCode.Gone);
        Assert.False((await client.GetFromJsonAsync<LinkDetails>($"/api/v1/links/{link.Code}"))!.IsActive);
    }

    [Fact]
    public async Task StatsShowThreeBackgroundClicks()
    {
        using var client = factory.Client();
        var link = await CreateAsync(client);
        client.DefaultRequestHeaders.Referrer = new Uri("https://referrer.example/campaign");
        for (var i = 0; i < 3; i++)
        {
            using var response = await client.GetAsync($"/{link.Code}");
            Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        }
        var stopwatch = Stopwatch.StartNew();
        LinkStats? stats;
        do
        {
            stats = await client.GetFromJsonAsync<LinkStats>($"/api/v1/links/{link.Code}/stats");
            if (stats!.TotalClicks == 3) break;
            await Task.Delay(25);
        } while (stopwatch.Elapsed < TimeSpan.FromSeconds(10));
        Assert.Equal(3, stats!.TotalClicks);
        Assert.Equal(3, Assert.Single(stats.ClicksPerDay).Clicks);
        Assert.Equal(DateTimeKind.Utc, stats.ClicksPerDay[0].Date.Kind);
        Assert.Equal(new ReferrerClicks("https://referrer.example/campaign", 3), Assert.Single(stats.TopReferrers));
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var linkId = await db.ShortLinks.Where(l => l.Code == link.Code).Select(l => l.Id).SingleAsync();
        var savedClicks = await db.ClickEvents.Where(x => x.ShortLinkId == linkId).ToListAsync();
        var expectedHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("127.0.0.1"))).ToLowerInvariant();
        Assert.All(savedClicks, click => Assert.Equal(expectedHash, click.IpHash));
        Assert.Equal(3, (await client.GetFromJsonAsync<LinkDetails>($"/api/v1/links/{link.Code}"))!.ClickCount);
    }

    [Fact]
    public async Task ConcurrentIdempotentPostsCreateOneLinkAndRejectChangedPayload()
    {
        using var client = factory.Client();
        var key = Guid.NewGuid().ToString("N");
        client.DefaultRequestHeaders.Add("Idempotency-Key", key);
        var request = new CreateLinkRequest("https://example.com/idempotent");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.PostAsJsonAsync("/api/v1/links", request)));
        try
        {
            Assert.All(responses, response => Assert.Equal(HttpStatusCode.Created, response.StatusCode));
            var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
            Assert.Single(bodies.Distinct());
            using var changed = await client.PostAsJsonAsync("/api/v1/links", request with { LongUrl = "https://example.com/changed" });
            await AssertProblemAsync(changed, HttpStatusCode.Conflict);
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(1, await db.ShortLinks.CountAsync(x => x.LongUrl == request.LongUrl));
            Assert.Equal(1, await db.Set<IdempotencyRecord>().CountAsync(x => x.Key == key));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task IdempotentReplayStillWorksAfterExpiry()
    {
        using var client = factory.Client();
        client.DefaultRequestHeaders.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        var request = new CreateLinkRequest("https://example.com/replay", ExpiresAt: factory.Clock.GetUtcNow().UtcDateTime.AddMinutes(1));
        using var first = await client.PostAsJsonAsync("/api/v1/links", request);
        factory.Clock.Advance(TimeSpan.FromMinutes(2));
        using var replay = await client.PostAsJsonAsync("/api/v1/links", request);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task SwaggerAndHealthAreAvailable()
    {
        using var client = factory.Client();
        Assert.Contains("Swagger UI", await client.GetStringAsync("/swagger/index.html"));
        var document = await client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        Assert.True(document.GetProperty("paths").TryGetProperty("/api/v1/links", out _));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/ready")).StatusCode);
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Contains(factory.DatabasePath, scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.GetConnectionString());
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public async Task ThirdPostReturns429ButReadsRemainAvailable()
    {
        using var factory = new ApiFactory { PermitLimit = 2 };
        using var client = factory.Client();
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com"))).StatusCode);
        using var rejected = await client.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.Equal("application/problem+json", rejected.Content.Headers.ContentType!.MediaType);
        Assert.NotNull(rejected.Headers.RetryAfter);
        using var otherIp = factory.Client();
        otherIp.DefaultRequestHeaders.Add("X-Test-Client-IP", "192.0.2.2");
        Assert.Equal(HttpStatusCode.Created, (await otherIp.PostAsJsonAsync("/api/v1/links", new CreateLinkRequest("https://example.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task IdempotencySurvivesApplicationRestart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"shortener-restart-{Guid.NewGuid():N}.db");
        var request = new CreateLinkRequest("https://example.com/restart");
        string original;
        using (var first = new ApiFactory { DatabasePath = file, CleanupDatabase = false })
        using (var client = first.Client())
        {
            client.DefaultRequestHeaders.Add("Idempotency-Key", "persistent-key");
            using var response = await client.PostAsJsonAsync("/api/v1/links", request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            original = await response.Content.ReadAsStringAsync();
        }
        using var restarted = new ApiFactory { DatabasePath = file };
        using var replayClient = restarted.Client();
        replayClient.DefaultRequestHeaders.Add("Idempotency-Key", "persistent-key");
        using var replay = await replayClient.PostAsJsonAsync("/api/v1/links", request);
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(original, await replay.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ProductionDoesNotMigrateAndReadinessFailsForUninitializedDatabase()
    {
        using var factory = new ApiFactory { EnvironmentName = "Production" };
        using var client = factory.Client();
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
        using var readiness = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, readiness.StatusCode);
        Assert.Equal("application/problem+json", readiness.Content.Headers.ContentType!.MediaType);
    }
}

using UrlShortener.Domain;
using Xunit;

namespace UrlShortener.UnitTests;

public sealed class DomainTests
{
    private static readonly DateTime Now = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GeneratorProducesTenThousandDistinctSevenCharacterBase62Codes()
    {
        var generator = new Base62CodeGenerator();
        var codes = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 10_000; i++)
        {
            var code = generator.Generate();
            Assert.Equal(7, code.Length);
            Assert.All(code, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
            Assert.True(codes.Add(code), $"Duplicate code: {code}");
        }
    }

    [Fact]
    public void LinkStartsActiveWithNoClicksAndNoExpiry()
    {
        var link = new ShortLink("abc_123-", "https://example.com/page", Now);
        Assert.Equal("abc_123-", link.Code);
        Assert.Equal("https://example.com/page", link.LongUrl);
        Assert.Equal(Now, link.CreatedAtUtc);
        Assert.Null(link.ExpiresAtUtc);
        Assert.Equal(0, link.ClickCount);
        Assert.True(link.IsAvailableAt(Now.AddYears(10)));
    }

    [Fact]
    public void ExpiryBoundaryIsExclusive()
    {
        var expiry = Now.AddHours(1);
        var link = new ShortLink("abc", "https://example.com", Now, expiry);
        Assert.True(link.IsAvailableAt(expiry.AddTicks(-1)));
        Assert.False(link.IsAvailableAt(expiry));
        Assert.False(link.IsAvailableAt(expiry.AddTicks(1)));
    }

    [Fact]
    public void DisableIsIdempotent()
    {
        var link = new ShortLink("abc", "https://example.com", Now);
        link.Disable();
        link.Disable();
        Assert.False(link.IsActive);
        Assert.False(link.IsAvailableAt(Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ab")]
    [InlineData("a b")]
    [InlineData("éabc")]
    [InlineData("abc/def")]
    [InlineData("API")]
    [InlineData("health")]
    [InlineData("Swagger")]
    public void RejectsInvalidCodes(string code) =>
        Assert.ThrowsAny<ArgumentException>(() => new ShortLink(code, "https://example.com", Now));

    [Fact]
    public void CodeLengthBoundariesAreEnforced()
    {
        _ = new ShortLink(new string('a', 30), "https://example.com", Now);
        Assert.Throws<ArgumentException>(() => new ShortLink(new string('a', 31), "https://example.com", Now));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/plain,test")]
    [InlineData("ftp://example.com")]
    public void RejectsInvalidUrls(string url) =>
        Assert.ThrowsAny<ArgumentException>(() => new ShortLink("abc", url, Now));

    [Fact]
    public void UrlLengthBoundaryIsEnforced()
    {
        const string prefix = "https://example.com/";
        var url = prefix + new string('a', 2048 - prefix.Length);
        _ = new ShortLink("abc", url, Now);
        Assert.Throws<ArgumentException>(() => new ShortLink("abc", url + "a", Now));
    }

    [Theory]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public void RejectsNonUtcTimestamps(DateTimeKind kind)
    {
        var invalid = DateTime.SpecifyKind(Now, kind);
        Assert.Throws<ArgumentException>(() => new ShortLink("abc", "https://example.com", invalid));
        Assert.Throws<ArgumentException>(() => new ShortLink("abc", "https://example.com", Now, invalid.AddHours(1)));
        Assert.Throws<ArgumentException>(() => new ClickEvent(1, invalid));
        var link = new ShortLink("abc", "https://example.com", Now);
        Assert.Throws<ArgumentException>(() => link.IsAvailableAt(invalid));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ExpiryMustFollowCreation(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShortLink("abc", "https://example.com", Now, Now.AddSeconds(seconds)));

    [Fact]
    public void ClickStoresOnlyHashAndPreservesMetadata()
    {
        var click = new ClickEvent(42, Now, new string('A', 64), "https://referrer.test", "Test browser");
        Assert.Equal(42, click.ShortLinkId);
        Assert.Equal(Now, click.ClickedAtUtc);
        Assert.Equal(new string('a', 64), click.IpHash);
        Assert.Equal("https://referrer.test", click.Referrer);
        Assert.Equal("Test browser", click.UserAgent);
        Assert.Null(new ClickEvent(42, Now).IpHash);
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("")]
    [InlineData("not-a-hash")]
    public void RejectsRawIpOrInvalidHash(string hash) =>
        Assert.Throws<ArgumentException>(() => new ClickEvent(1, Now, hash));

    [Fact]
    public void HashMustBeHexadecimal() =>
        Assert.Throws<ArgumentException>(() => new ClickEvent(1, Now, new string('z', 64)));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ClickRequiresPersistedLink(long id) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClickEvent(id, Now));
}

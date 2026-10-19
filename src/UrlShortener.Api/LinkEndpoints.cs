using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using UrlShortener.Application.Links;
using UrlShortener.Domain;

namespace UrlShortener.Api;

public static class LinkEndpoints
{
    public static void MapLinkEndpoints(this WebApplication app)
    {
        var links = app.MapGroup("/api/v1/links").WithTags("Links");
        links.MapPost("", async (CreateLinkRequest request, LinkService service, HttpContext context,
            IConfiguration configuration, [FromHeader(Name = "Idempotency-Key")] string? key, CancellationToken ct) =>
        {
            if (context.Request.Headers["Idempotency-Key"].Count > 1)
                throw new LinkOperationException(400, "Supply one Idempotency-Key value.");
            var baseUrl = BaseUrl(context, configuration);
            var hosts = new[] { context.Request.Host.Host, new Uri(baseUrl).IdnHost };
            var link = await service.CreateAsync(request, key, hosts, ct);
            return Results.Created($"/api/v1/links/{link.Code}", Response(link, baseUrl));
        }).RequireRateLimiting("create").WithName("CreateLink").WithOpenApi()
            .Produces<LinkResponse>(201).ProducesValidationProblem().ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        links.MapGet("/{code}", async (string code, LinkService service, HttpContext context, IConfiguration configuration, CancellationToken ct) =>
        {
            var link = await service.GetAsync(code, ct);
            return Results.Ok(new LinkDetails(link.Code, $"{BaseUrl(context, configuration)}/{link.Code}", link.LongUrl,
                link.CreatedAtUtc, link.ExpiresAtUtc, link.IsActive, link.ClickCount));
        }).WithName("GetLink").WithOpenApi().Produces<LinkDetails>().ProducesProblem(404);

        links.MapGet("/{code}/stats", async (string code, LinkService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatsAsync(code, ct)))
            .WithName("GetLinkStats").WithOpenApi().Produces<LinkStats>().ProducesProblem(404);

        links.MapDelete("/{code}", async (string code, LinkService service, CancellationToken ct) =>
        {
            await service.DisableAsync(code, ct);
            return Results.NoContent();
        }).WithName("DisableLink").WithOpenApi().Produces(204).ProducesProblem(404);

        app.MapGet("/{code}", async (string code, LinkService service, ClickQueue queue, TimeProvider clock, HttpContext context, CancellationToken ct) =>
        {
            var link = await service.ResolveAsync(code, ct);
            var address = context.Connection.RemoteIpAddress;
            var hash = address is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(address.ToString())));
            var click = new ClickEvent(link.Id, clock.GetUtcNow().UtcDateTime, hash,
                context.Request.Headers.Referer.ToString(), context.Request.Headers.UserAgent.ToString());
            if (!queue.TryWrite(click)) throw new LinkOperationException(503, "Click recording is busy. Please retry.");
            // Prevent browser caching from bypassing subsequent expiry/disable checks and analytics.
            context.Response.Headers.CacheControl = "no-store";
            return Results.Redirect(link.LongUrl);
        }).WithTags("Redirect").WithName("RedirectLink").WithOpenApi().Produces(302).ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
    }

    private static string BaseUrl(HttpContext context, IConfiguration configuration) =>
        (configuration["PublicBaseUrl"] ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}").TrimEnd('/');

    private static LinkResponse Response(ShortLink link, string baseUrl) =>
        new(link.Code, $"{baseUrl}/{link.Code}", link.LongUrl, link.CreatedAtUtc, link.ExpiresAtUtc);
}

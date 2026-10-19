using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.OpenApi.Models;
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
        }).RequireRateLimiting("create").WithName("CreateLink")
            .WithSummary("Create a short link")
            .WithDescription("Creates a short link to an absolute http(s) URL of at most 2,048 characters (javascript:, data: and this service's own host are rejected). "
                + "customAlias is optional: 3-30 characters from A-Z, a-z, 0-9, '_' and '-', and not 'api', 'health' or 'swagger'; without it a random 7-character Base62 code is generated. "
                + "expiresAt is optional and must be a future UTC timestamp ending in 'Z'. Creation is rate limited per client IP.")
            .WithOpenApi(operation =>
            {
                var header = operation.Parameters.FirstOrDefault(p => p.Name == "Idempotency-Key");
                if (header is not null)
                    header.Description = "Optional, at most 128 characters. Retrying with the same key and body returns the original link; the same key with a different body returns 409.";
                return operation;
            })
            .Produces<LinkResponse>(201).ProducesValidationProblem().ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);

        links.MapGet("/{code}", async (string code, LinkService service, HttpContext context, IConfiguration configuration, CancellationToken ct) =>
        {
            var link = await service.GetAsync(code, ct);
            return Results.Ok(new LinkDetails(link.Code, $"{BaseUrl(context, configuration)}/{link.Code}", link.LongUrl,
                link.CreatedAtUtc, link.ExpiresAtUtc, link.IsActive, link.ClickCount));
        }).WithName("GetLink").WithSummary("Get link details")
            .WithDescription("Returns the target URL, creation and expiry times (UTC), whether the link is active, and its recorded click count.")
            .WithOpenApi().Produces<LinkDetails>().ProducesProblem(404);

        links.MapGet("/{code}/stats", async (string code, LinkService service, CancellationToken ct) =>
            Results.Ok(await service.GetStatsAsync(code, ct)))
            .WithName("GetLinkStats").WithSummary("Get click statistics")
            .WithDescription("Total clicks, clicks per UTC day and the top 10 referrers. Clicks are recorded in the background, so totals can lag a redirect briefly.")
            .WithOpenApi().Produces<LinkStats>().ProducesProblem(404);

        links.MapDelete("/{code}", async (string code, LinkService service, CancellationToken ct) =>
        {
            await service.DisableAsync(code, ct);
            return Results.NoContent();
        }).WithName("DisableLink").WithSummary("Disable a link")
            .WithDescription("Soft delete: the link and its analytics are kept, and redirects return 410 from now on.")
            .WithOpenApi().Produces(204).ProducesProblem(404);

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
        }).WithTags("Redirect").WithName("RedirectLink").WithSummary("Follow a short link")
            .WithDescription("Redirects with 302 to the target URL and records the click in the background (the client IP is stored only as a SHA-256 hash). "
                + "Returns 404 for an unknown code and 410 when the link has expired or been disabled. "
                + "Swagger's 'Try it out' cannot show this redirect, because the browser follows it to another origin; open the short URL directly instead.")
            .WithOpenApi(operation =>
            {
                if (!operation.Responses.TryGetValue("302", out var found)) operation.Responses["302"] = found = new OpenApiResponse();
                found.Description = "Found: the client is redirected to the target URL.";
                found.Headers["Location"] = new OpenApiHeader
                {
                    Description = "The link's target URL.",
                    Schema = new OpenApiSchema { Type = "string", Format = "uri" }
                };
                return operation;
            }).Produces(302).ProducesProblem(404).ProducesProblem(410).ProducesProblem(503);
    }

    private static string BaseUrl(HttpContext context, IConfiguration configuration) =>
        (configuration["PublicBaseUrl"] ?? $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}").TrimEnd('/');

    private static LinkResponse Response(ShortLink link, string baseUrl) =>
        new(link.Code, $"{baseUrl}/{link.Code}", link.LongUrl, link.CreatedAtUtc, link.ExpiresAtUtc);
}

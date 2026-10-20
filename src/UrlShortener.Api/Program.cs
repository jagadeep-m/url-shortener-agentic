using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Serilog;
using UrlShortener.Api;
using UrlShortener.Application.Links;
using UrlShortener.Domain;
using UrlShortener.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog((_, configuration) => configuration.MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).WriteTo.Console());
builder.Services.AddUrlShortenerInfrastructure(builder.Configuration.GetConnectionString("Shortener") ?? "Data Source=urlshortener.db");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ICodeGenerator, Base62CodeGenerator>();
builder.Services.AddScoped<IValidator<CreateLinkRequest>, CreateLinkValidator>();
builder.Services.AddScoped<LinkService>();
builder.Services.AddSingleton<ClickQueue>();
builder.Services.AddHostedService<ClickRecorder>();
builder.Services.AddProblemDetails();
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new()
    {
        Title = "URL Shortener",
        Version = "v1",
        Description = "Create short links, follow them, and read click analytics. Errors use RFC 7807 ProblemDetails."
    });
    // Read C# nullable annotations, so non-nullable members are documented as required and not nullable.
    options.SupportNonNullableReferenceTypes();
    options.NonNullableReferenceTypesAsRequired();
});
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("sqlite", tags: ["ready"]);
builder.Services.AddRateLimiter(options =>
{
    options.AddPolicy("create", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = Math.Max(1, builder.Configuration.GetValue("RateLimiting:PermitLimit", 30)),
            Window = TimeSpan.FromSeconds(Math.Max(1, builder.Configuration.GetValue("RateLimiting:WindowSeconds", 60))),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
    options.OnRejected = async (context, ct) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await Results.Problem(statusCode: 429, title: "Too many requests", detail: "The create-link rate limit was exceeded.")
            .ExecuteAsync(context.HttpContext);
    };
});

var app = builder.Build();
if (app.Environment.IsDevelopment())
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseSerilogRequestLogging();
app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
{
    var exception = context.Features.Get<IExceptionHandlerFeature>()?.Error;
    IResult result = exception switch
    {
        ValidationException validation => Results.ValidationProblem(validation.Errors.GroupBy(x => x.PropertyName)
            .ToDictionary(x => x.Key, x => x.Select(e => e.ErrorMessage).ToArray())),
        LinkOperationException operation => Results.Problem(statusCode: operation.StatusCode, detail: operation.Message),
        BadHttpRequestException bad => Results.Problem(statusCode: bad.StatusCode, detail: "The request body or parameters are invalid."),
        _ => Results.Problem(statusCode: 500, detail: "An unexpected server error occurred.")
    };
    await result.ExecuteAsync(context);
}));
app.UseStatusCodePages(async status => await Results.Problem(statusCode: status.HttpContext.Response.StatusCode)
    .ExecuteAsync(status.HttpContext));
app.UseSwagger();
app.UseSwaggerUI();
app.UseRateLimiter();
app.MapLinkEndpoints();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false, ResponseWriter = HealthResponses.WriteAsync });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready"), ResponseWriter = HealthResponses.WriteAsync });
app.Run();

public partial class Program { }

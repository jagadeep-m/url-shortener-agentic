using Orchestrator.Agents;
using Xunit;

namespace Orchestrator.Tests;

public sealed class CodebaseIndexTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "index-" + Guid.NewGuid().ToString("N"));

    private void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
        File.WriteAllText(Path.Combine(root, path), text);
    }

    [Fact]
    public void Syntax_trees_yield_endpoints_with_group_prefixes_tables_and_referencing_tests()
    {
        Write("src/Shop.Domain/Order.cs", "namespace Shop; public sealed class Order { public int Total { get; set; } public bool IsPaid() => true; }");
        Write("src/Shop.Domain/Unrelated.cs", "namespace Shop; public sealed class Unrelated { }");
        Write("src/Shop.Data/Db.cs", """
            class Db { void OnModelCreating(ModelBuilder modelBuilder) {
                var orders = modelBuilder.Entity<Order>();
                orders.ToTable("Orders");
                modelBuilder.Entity<Unrelated>().ToTable("Other");
            } }
            """);
        Write("src/Shop.Api/Endpoints.cs", """
            static class Endpoints { static void Map(WebApplication app) {
                var group = app.MapGroup("/api/orders");
                group.MapPost("", (OrderService s) => s.Create());
                group.MapGet("/{id}", (int id, OrderService s) => s.Find(id).IsPaid());
                app.MapGet("/health", () => "ok");
            } }
            class OrderService { public Order Create() => new(); public Order Find(int id) => new(); }
            """);
        Write("tests/Shop.Tests/OrderTests.cs", "class OrderTests { void Paid() => new Order().IsPaid(); }");
        Write("tests/Shop.Tests/HealthTests.cs", "class HealthTests { }");
        Write("src/Shop.Api/obj/Generated.cs", "class Order { }"); // build output is ignored

        var index = CodebaseIndex.Build(root, ["src/Shop.*", "tests/Shop.*"]);
        Assert.Equal(new[] { "POST /api/orders", "GET /api/orders/{id}", "GET /health" }, index.Endpoints.Select(e => $"{e.Method} {e.Route}"));
        Assert.Equal("Orders", index.Tables["Order"]);

        var report = index.Analyze(["Order.IsPaid", "OrderService.Create", "Invoice"]);
        Assert.Equal(new[] { "src/Shop.Api/Endpoints.cs", "src/Shop.Domain/Order.cs" }, report.Files.Select(f => f.Name).Order());
        Assert.Equal(new[] { "POST /api/orders", "GET /api/orders/{id}" }, report.Endpoints.Select(e => e.Name));
        Assert.Equal("Orders", Assert.Single(report.Tables).Name);
        Assert.Equal("tests/Shop.Tests/OrderTests.cs", Assert.Single(report.Tests).Name);
        Assert.Equal("Invoice", Assert.Single(report.UnknownSeeds));
        Assert.Contains("## Affected endpoints (2)", report.ToMarkdown());
    }

    [Fact]
    public void Unknown_members_of_known_types_are_reported()
    {
        Write("src/Shop.Domain/Order.cs", "class Order { void Pay() { } }");
        var report = CodebaseIndex.Build(root, ["src/Shop.*"]).Analyze(["Order.Refund", "Order.Pay"]);
        Assert.Equal("Order.Refund", Assert.Single(report.UnknownSeeds));
    }

    [Fact]
    public void The_real_solution_is_indexed()
    {
        var index = CodebaseIndex.Build(ScenarioReplayTests.RepositoryRoot(), ["src/UrlShortener.*", "tests/UrlShortener.*"]);
        Assert.Contains(index.Endpoints, e => e is { Method: "DELETE", Route: "/api/v1/links/{code}" });
        Assert.Contains(index.Endpoints, e => e is { Method: "GET", Route: "/api/v1/links/{code}/stats" });
        Assert.Equal("ClickEvents", index.Tables["ClickEvent"]);
        Assert.DoesNotContain(index.FilePaths, p => p.Contains("/obj/"));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

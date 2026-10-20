using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Orchestrator.Agents;
using Orchestrator.Cli;
using Orchestrator.Core;
using Xunit;

namespace Orchestrator.Tests;

// Live-mode support: plain-English requirements, read-only code context for agents, and the provider client.
// Everything here runs against fakes; no API key or network is needed.
public sealed class LiveModeTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "live-" + Guid.NewGuid().ToString("N"));

    public LiveModeTests()
    {
        Write("src/UrlShortener.Domain/ShortLink.cs", "namespace UrlShortener.Domain; public sealed class ShortLink { public string Code { get; set; } = \"\"; public bool IsAvailableAt() => true; }");
        Write("src/UrlShortener.Api/LinkEndpoints.cs", "static class LinkEndpoints { static void Map(WebApplication app) { var links = app.MapGroup(\"/api/v1/links\"); links.MapPost(\"\", () => 1); } }");
        Write("tests/UrlShortener.IntegrationTests/ApiFactory.cs", "public sealed class ApiFactory { }");
        Write("src/UrlShortener.Api/obj/Generated.cs", "class Generated { }");
    }

    private void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, path))!);
        File.WriteAllText(Path.Combine(root, path), content);
    }

    // ---- Plain-English requirement on the command line

    [Fact]
    public void Requirement_option_is_parsed_and_validated()
    {
        var command = CommandLine.Parse(["run", "scenarios/feature.yaml", "--requirement", "  Add QR codes for short links  "]);
        Assert.Equal(("run", "scenarios/feature.yaml", "Add QR codes for short links", false), (command.Command, command.Target, command.Requirement, command.Replay));
        Assert.True(CommandLine.Parse(["report", Guid.NewGuid().ToString(), "--lineage"]).Lineage);

        Assert.Contains("needs a plain-English requirement", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "s.yaml", "--requirement"])).Message);
        Assert.Contains("needs a plain-English requirement", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "s.yaml", "--requirement", "--replay"])).Message);
        Assert.Contains("needs a real model", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "s.yaml", "--replay", "--requirement", "Add QR codes"])).Message);
        Assert.Contains("only once", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "s.yaml", "--requirement", "a", "--requirement", "b"])).Message);
        Assert.Contains("Unknown option '--lineage'", Assert.Throws<ArgumentException>(() => CommandLine.Parse(["run", "s.yaml", "--lineage"])).Message);
        Assert.Throws<ArgumentException>(() => CommandLine.Parse(["deploy", "x"]));
    }

    [Fact]
    public async Task Feature_scenario_has_no_built_in_requirement_and_a_valid_brownfield_style_graph()
    {
        var scenario = await new ScenarioLoader().LoadAsync(Path.Combine(AppContext.BaseDirectory, "scenarios", "feature.yaml"));
        Assert.Equal("", scenario.Requirement);
        Assert.Equal(new[] { "requirements", "impact", "design", "implement", "docs", "tests", "validate", "release" }, DagBuilder.Build(scenario).Select(s => s.Id));
        Assert.Equal(["implement"], scenario.Stages.Single(s => s.Id == "tests").DependsOn);
        Assert.Equal(400_000, scenario.Budget.MaxTokens);
    }

    // ---- Read-only code context

    [Fact]
    public void Relevant_files_are_the_existing_product_files_named_in_the_run()
    {
        var code = new CodeContext(root);
        var files = code.RelevantFiles(
            ["Impact: `src/UrlShortener.Domain/ShortLink.cs` and a new `src/UrlShortener.Domain/QrCode.cs`.",
             "Never: src/UrlShortener.Domain/../../secrets.cs, src/UrlShortener.Api/obj/Generated.cs, src/Orchestrator.Core/RunEngine.cs"],
            ["tests/UrlShortener.IntegrationTests/ApiFactory.cs"]);

        // Role defaults come first, then files named by path.
        Assert.Equal(new[] { "tests/UrlShortener.IntegrationTests/ApiFactory.cs", "src/UrlShortener.Domain/ShortLink.cs" }, files.Select(f => f.Path));
        Assert.Contains("IsAvailableAt", files[1].Content);
    }

    [Fact]
    public void Large_files_and_budget_overflow_are_omitted_and_absolute_paths_are_redacted()
    {
        Write("src/UrlShortener.Domain/Big.cs", new string('x', 500));
        Write("src/UrlShortener.Domain/Paths.cs", $"// built in {Path.GetFullPath(root)}/src");
        // Budget fits Paths.cs (read first, alphabetically) but not ShortLink.cs after it.
        var pathsLength = File.ReadAllText(Path.Combine(root, "src/UrlShortener.Domain/Paths.cs")).Length;
        var code = new CodeContext(root, maxFileChars: 400, maxTotalChars: pathsLength + 10);
        var files = code.RelevantFiles(["src/UrlShortener.Domain/Big.cs src/UrlShortener.Domain/Paths.cs src/UrlShortener.Domain/ShortLink.cs"]).ToDictionary(f => f.Path, f => f.Content);

        Assert.StartsWith("(omitted: 500 characters exceeds", files["src/UrlShortener.Domain/Big.cs"]);
        Assert.Equal("// built in <workspace>/src", files["src/UrlShortener.Domain/Paths.cs"]);
        Assert.Equal("(omitted: context budget reached)", files["src/UrlShortener.Domain/ShortLink.cs"]);
        Assert.Equal("failed in <workspace>/x.cs", code.Redact($"failed in {Path.GetFullPath(root)}/x.cs"));
    }

    [Fact]
    public void Outline_lists_types_members_endpoints_and_test_files_but_not_build_output()
    {
        var outline = new CodeContext(root).Outline();
        Assert.Contains("src/UrlShortener.Domain/ShortLink.cs: ShortLink { Code, IsAvailableAt }", outline);
        Assert.Contains("- POST /api/v1/links (src/UrlShortener.Api/LinkEndpoints.cs)", outline);
        Assert.Contains("tests/UrlShortener.IntegrationTests/ApiFactory.cs: ApiFactory", outline);
        Assert.DoesNotContain("Generated", outline);
    }

    // ---- What each role is shown

    private sealed class CapturingClient(string reply) : BufferedChatClient
    {
        public readonly List<string> System = [];
        public readonly List<JsonElement> User = [];
        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            System.Add(list[0].Text);
            User.Add(JsonDocument.Parse(list[1].Text).RootElement.Clone());
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { Usage = new() { TotalTokenCount = 10 } });
        }
    }

    private RunContext Context(string stage, string agent, string failure = "")
    {
        var ctx = new RunContext { Requirement = "Add QR codes for short links", Scenario = new() { Stages = [new() { Id = stage, Agent = agent }] } };
        ctx.Artifacts["impact"] = new Artifact("impact.json", "Affected: `src/UrlShortener.Domain/ShortLink.cs`");
        ctx.StageStatuses[stage] = new() { LastFailure = failure };
        return ctx;
    }

    private const string Document = """{"summary":"s","rationale":"r","content":"c","files":[]}""";
    private const string CoderDocument = """{"summary":"s","rationale":"r","content":"c","files":[{"path":"src/UrlShortener.Domain/X.cs","content":"class X { }"}]}""";
    private const string TesterDocument = """{"summary":"s","rationale":"r","content":"c","files":[{"path":"tests/UrlShortener.UnitTests/XTests.cs","content":"class XTests { }"}]}""";

    [Fact]
    public async Task Coder_sees_current_files_and_never_the_absolute_path()
    {
        var client = new CapturingClient("```json\n" + CoderDocument + "\n```"); // fenced JSON is accepted
        var coder = new CoderAgent(client) { Code = new CodeContext(root) };
        var result = await coder.RunAsync(new("implement", $"Fix this failure: error in {Path.GetFullPath(root)}/src/a.cs", []), Context("implement", "Coder"), default);

        Assert.True(result.Success);
        var files = client.User[0].GetProperty("currentFiles").EnumerateArray().ToArray();
        // The named file first, then the rest of the product source (a design may name no files at all).
        Assert.Equal(new[] { "src/UrlShortener.Domain/ShortLink.cs", "src/UrlShortener.Api/LinkEndpoints.cs" }, files.Select(f => f.GetProperty("path").GetString()));
        Assert.Contains("complete new content", client.System[0]);
        Assert.Equal(12288, new ArchitectAgent(client).Boundary.MaxTokens); // 6,144 was cut off in the first live run
        Assert.Equal(8192, new ReviewerAgent(client).Boundary.MaxTokens);   // 4,096 was cut off once reviews listed every gap
        Assert.DoesNotContain(Path.GetFullPath(root), client.User[0].GetRawText());
        Assert.Contains("<workspace>/src/a.cs", client.User[0].GetProperty("prompt").GetString());
        Assert.Contains("ShortLink { Code, IsAvailableAt }", client.User[0].GetProperty("codebaseOutline").GetString()); // the Coder sees the whole map too
    }

    [Fact]
    public void Files_are_found_by_type_name_when_no_path_is_given()
    {
        // A live design said "change LinkEndpoints and ShortLink" without any path; the Coder then saw no code at all.
        var files = new CodeContext(root).RelevantFiles(["Update LinkEndpoints to call the normalizer, and keep ShortLink unchanged. ShortLinkX is not a type."]);
        Assert.Equal(new[] { "src/UrlShortener.Api/LinkEndpoints.cs", "src/UrlShortener.Domain/ShortLink.cs" }, files.Select(f => f.Path));
    }

    [Fact]
    public async Task A_code_stage_that_returns_no_files_fails_with_the_models_explanation()
    {
        // A live Coder answered "Unable to implement: no source files were provided" with files=[], and the gate passed it.
        var client = new CapturingClient("""{"summary":"Unable to implement: no source files were provided.","rationale":"r","content":"c","files":[]}""");
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => new CoderAgent(client) { Code = new CodeContext(root) }
            .RunAsync(new("implement", "", []), Context("implement", "Coder"), default));
        Assert.Equal("The Coder returned no file changes, but this stage must change code. Its explanation: Unable to implement: no source files were provided.", error.Message);
    }

    [Fact]
    public async Task Planning_roles_get_the_outline_tester_always_gets_the_api_factory_and_requirements_get_no_code()
    {
        var impact = new CapturingClient("""{"summary":"s","rationale":"r","seeds":["ShortLink"],"risks":[]}""");
        await new ImpactAnalystAgent(impact, root) { Code = new CodeContext(root) }.RunAsync(new("impact", "", []), Context("impact", "ImpactAnalyst"), default);
        Assert.Contains("ShortLink { Code, IsAvailableAt }", impact.User[0].GetProperty("codebaseOutline").GetString());
        Assert.False(impact.User[0].TryGetProperty("currentFiles", out _));

        var tester = new CapturingClient(TesterDocument);
        await new TesterAgent(tester) { Code = new CodeContext(root) }.RunAsync(new("tests", "", []), Context("tests", "Tester"), default);
        var testerFiles = tester.User[0].GetProperty("currentFiles").EnumerateArray().Select(f => f.GetProperty("path").GetString()).ToArray();
        Assert.Contains("tests/UrlShortener.IntegrationTests/ApiFactory.cs", testerFiles);
        Assert.Contains("src/UrlShortener.Api/LinkEndpoints.cs", testerFiles); // the API contract, so HTTP tests send the real request shape

        var requirements = new CapturingClient(Document);
        await new RequirementsAgent(requirements) { Code = new CodeContext(root) }.RunAsync(new("requirements", "", []), Context("requirements", "Requirements"), default);
        Assert.False(requirements.User[0].TryGetProperty("currentFiles", out _));
        Assert.False(requirements.User[0].TryGetProperty("codebaseOutline", out _));
        Assert.Equal("Add QR codes for short links", requirements.User[0].GetProperty("requirement").GetString());
    }

    // ---- The provider client

    private sealed class FakeProvider(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? RequestBody;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static async Task<(ChatResponse Response, FakeProvider Provider)> Call(HttpStatusCode status, string body)
    {
        var provider = new FakeProvider(status, body);
        using var client = new OpenAiCompatibleChatClient("sk-test", "gpt-test", "https://api.example.test/v1/chat/completions", provider);
        var response = await client.GetResponseAsync([new(ChatRole.System, "sys"), new(ChatRole.User, "hi")], new ChatOptions { MaxOutputTokens = 1234 });
        return (response, provider);
    }

    [Fact]
    public async Task Successful_reply_returns_text_usage_and_the_served_model()
    {
        var (response, provider) = await Call(HttpStatusCode.OK,
            """{"model":"gpt-test-2026","choices":[{"finish_reason":"stop","message":{"content":"{\"ok\":true}"}}],"usage":{"prompt_tokens":7,"completion_tokens":3,"total_tokens":10}}""");

        Assert.Equal("{\"ok\":true}", response.Text);
        Assert.Equal("gpt-test-2026", response.ModelId);
        Assert.Equal(10, response.Usage!.TotalTokenCount);
        Assert.Equal("Bearer sk-test", provider.Request!.Headers.Authorization!.ToString());
        var sent = JsonDocument.Parse(provider.RequestBody!).RootElement;
        Assert.Equal(("gpt-test", 1234, "json_object"), (sent.GetProperty("model").GetString(), sent.GetProperty("max_completion_tokens").GetInt32(),
            sent.GetProperty("response_format").GetProperty("type").GetString()));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "The API key was rejected. Check LLM:ApiKey.")]
    [InlineData(HttpStatusCode.NotFound, "Model 'gpt-test' or the endpoint was not found.")]
    [InlineData(HttpStatusCode.TooManyRequests, "Rate limited or out of credit.")]
    public async Task Setup_errors_are_explained_with_the_providers_message(HttpStatusCode status, string hint)
    {
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => Call(status, """{"error":{"message":"Provider detail here."}}"""));
        Assert.Contains($"HTTP {(int)status}. {hint}", error.Message);
        Assert.EndsWith("Provider says: Provider detail here.", error.Message);
        Assert.DoesNotContain("sk-test", error.Message);
    }

    [Fact]
    public async Task Cut_off_answers_are_reported_as_such()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Call(HttpStatusCode.OK,
            """{"choices":[{"finish_reason":"length","message":{"content":"{\"partial\":"}}]}"""));
        Assert.Contains("cut off at the 1234-token output limit", error.Message);
    }

    [Fact]
    public void Markdown_fences_around_json_are_removed()
    {
        Assert.Equal("{\"a\":1}", AgentDocument.Unwrap("```json\n{\"a\":1}\n```"));
        Assert.Equal("{\"a\":1}", AgentDocument.Unwrap("```\n{\"a\":1}\n```\n"));
        Assert.Equal("{\"a\":1}", AgentDocument.Unwrap("{\"a\":1}"));
    }

    [Fact]
    public async Task Report_names_the_live_model()
    {
        var runs = Path.Combine(root, "runs");
        var context = new RunContext { Scenario = new() { Name = "feature", Model = "gpt-6-luna", Stages = [] } };
        Directory.CreateDirectory(Path.Combine(runs, context.RunId.ToString("D")));
        await File.WriteAllTextAsync(Path.Combine(runs, context.RunId.ToString("D"), "audit.jsonl"), "");
        Assert.Contains("Mode: Live (model gpt-6-luna)", await File.ReadAllTextAsync(await RunReport.WriteAsync(context, runs)));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}

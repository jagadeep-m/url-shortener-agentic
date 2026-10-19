using Orchestrator.Core;
using Orchestrator.Policies;
using Xunit;

namespace Orchestrator.Tests;

public sealed class PolicyTests
{
    private static readonly RunContext Ctx = new();
    private static readonly string FakeOpenAiKey = "sk-" + new string('x', 32);

    private static ProposedChange Change(params (string Path, string? Old, string New)[] files) => Change([], files);
    private static ProposedChange Change(string[] packages, params (string Path, string? Old, string New)[] files) =>
        new(files.Select(f => f.Path).ToArray(), string.Concat(files.Select(f => UnifiedDiff.Create(f.Path, f.Old, f.New).Text)), packages, false);

    private static PolicyVerdict Verdict(Policy policy, ProposedChange change) => policy.Check(change, Ctx).Verdict;

    [Fact]
    public void PathAllowlist_allows_roots_requires_approval_for_build_config_and_denies_everything_else()
    {
        var policy = new PathAllowlistPolicy(DefaultPolicies.AgentRoots);
        Assert.Equal(PolicyVerdict.Allow, Verdict(policy, Change(("src/UrlShortener.Api/Program.cs", null, "x"), ("docs/a.md", null, "x"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(("src/UrlShortener.Api/UrlShortener.Api.csproj", null, "<Project />"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(("src/UrlShortener.Api/appsettings.json", null, "{}"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change((".github/workflows/ci.yml", null, "x"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(("src/Orchestrator.Core/RunEngine.cs", null, "x"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(("docs/.env", null, "KEY=1"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(("src/UrlShortener.Api/appsettings.Production.local.json", null, "{}"))));
    }

    [Theory]
    [InlineData("var key = \"" + "AKIA" + "ABCDEFGHIJKLMNOP\";")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----")]
    [InlineData("\"Shortener\": \"Data Source=db;User Id=sa;Password=hunter2;\"")]
    [InlineData("token: ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789")]
    public void SecretScan_denies_definite_credentials(string line) =>
        Assert.Equal(PolicyVerdict.Deny, Verdict(new SecretScanPolicy(), Change(("docs/setup.md", null, line + "\n"))));

    [Fact]
    public void SecretScan_allows_clean_code_requires_approval_for_suspicious_values_and_never_logs_them()
    {
        var policy = new SecretScanPolicy();
        Assert.Equal(PolicyVerdict.Allow, Verdict(policy, Change(("src/UrlShortener.Api/A.cs", null, "var password = GetPassword();\nconst string Header = \"Idempotency-Key\";\n"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(("tests/UrlShortener.UnitTests/A.cs", null, "var apiKey = \"test-value-123\";\n"))));
        var denied = policy.Check(Change(("docs/setup.md", "intro\n", "intro\nexport KEY=" + FakeOpenAiKey + "\n")), Ctx);
        Assert.Equal(PolicyVerdict.Deny, denied.Verdict);
        Assert.Contains("docs/setup.md:2", denied.Reason);
        Assert.DoesNotContain(FakeOpenAiKey, denied.Reason);
    }

    [Fact]
    public void SecretScan_only_inspects_added_lines()
    {
        var existing = "-----BEGIN RSA PRIVATE KEY-----\n";
        Assert.Equal(PolicyVerdict.Allow, Verdict(new SecretScanPolicy(), Change(("docs/a.md", existing, existing + "new line\n"))));
    }

    [Fact]
    public void NuGetAllowlist_allows_no_packages_requires_approval_for_allowlisted_and_denies_others()
    {
        var policy = new NuGetAllowlistPolicy(DefaultPolicies.AllowedPackages);
        Assert.Equal(PolicyVerdict.Allow, Verdict(policy, Change(("docs/a.md", null, "x"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(["FluentValidation.AspNetCore@11.3.0"], ("src/UrlShortener.Api/A.csproj", null, "x"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(["Newtonsoft.Json@13.0.3"], ("src/UrlShortener.Api/A.csproj", null, "x"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(["Polly.Core@8.0.0\"/><Evil"], ("src/UrlShortener.Api/A.csproj", null, "x"))));
    }

    [Fact]
    public void DiffSize_allows_small_requires_approval_above_400_and_denies_above_the_hard_limit()
    {
        var policy = new DiffSizePolicy(approvalLimit: 400, hardLimit: 2000);
        string Lines(int n) => string.Concat(Enumerable.Range(0, n).Select(i => $"line {i}\n"));
        Assert.Equal(PolicyVerdict.Allow, Verdict(policy, Change(("docs/a.md", null, Lines(400)))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(("docs/a.md", null, Lines(401)))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change(("docs/a.md", null, Lines(2001)))));
        // Rewriting 250 existing lines changes 500 (250 removed + 250 added).
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(("docs/a.md", Lines(250), Lines(250).Replace("line", "row")))));
    }

    [Fact]
    public void Migration_allows_none_requires_approval_for_new_and_denies_editing_existing_ones()
    {
        var policy = new MigrationPolicy();
        const string folder = "src/UrlShortener.Infrastructure/Migrations/";
        Assert.Equal(PolicyVerdict.Allow, Verdict(policy, Change(("src/UrlShortener.Api/A.cs", null, "x"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(policy, Change(
            (folder + "20261018_AddExpiry.cs", null, "migration"), (folder + "AppDbContextModelSnapshot.cs", "old", "new"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(policy, Change((folder + "20261017_InitialCreate.cs", "old", "rewritten"))));
    }

    [Fact]
    public void VulnerablePackages_allows_clean_requires_approval_when_unknown_and_denies_vulnerable()
    {
        var packages = new[] { "Serilog@4.0.0" };
        var none = new FakeScanner();
        Assert.Equal(PolicyVerdict.Allow, Verdict(new VulnerablePackagePolicy(none), Change(("docs/a.md", null, "x"))));
        Assert.Equal(0, none.Calls); // nothing new to scan, so no process runs
        Assert.Equal(PolicyVerdict.Allow, Verdict(new VulnerablePackagePolicy(new FakeScanner()), Change(packages, ("src/UrlShortener.Api/A.csproj", null, "x"))));
        Assert.Equal(PolicyVerdict.RequireApproval, Verdict(new VulnerablePackagePolicy(new FakeScanner(VulnerabilityStatus.Unknown)), Change(packages, ("src/UrlShortener.Api/A.csproj", null, "x"))));
        Assert.Equal(PolicyVerdict.Deny, Verdict(new VulnerablePackagePolicy(new FakeScanner(VulnerabilityStatus.Vulnerable)), Change(packages, ("src/UrlShortener.Api/A.csproj", null, "x"))));
    }

    [Fact]
    public void Vulnerability_report_json_is_parsed()
    {
        const string vulnerable = """
            {"version":1,"projects":[{"path":"probe.csproj","frameworks":[{"framework":"net8.0",
              "topLevelPackages":[{"id":"Newtonsoft.Json","requestedVersion":"12.0.1","resolvedVersion":"12.0.1",
                "vulnerabilities":[{"severity":"High","advisoryurl":"https://github.com/advisories/GHSA-5crp-9r3c-p9vr"}]}]}]}]}
            """;
        var report = DotnetVulnerabilityScanner.Parse(vulnerable);
        Assert.Equal(VulnerabilityStatus.Vulnerable, report.Status);
        Assert.Equal("Newtonsoft.Json@12.0.1 (High)", report.Detail);
        Assert.Equal(VulnerabilityStatus.Clean, DotnetVulnerabilityScanner.Parse("""{"version":1,"projects":[{"path":"probe.csproj"}]}""").Status);
        Assert.Equal(VulnerabilityStatus.Unknown, DotnetVulnerabilityScanner.Parse("""{"version":1,"problems":[{"level":"error","text":"offline"}]}""").Status);
    }

    [Fact]
    public void Unified_diff_has_headers_hunks_and_line_numbers()
    {
        var (text, added, removed) = UnifiedDiff.Create("docs/a.md", "one\ntwo\nthree\n", "one\nTWO\nthree\nfour\n");
        Assert.Equal("--- a/docs/a.md\n+++ b/docs/a.md\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n", text);
        Assert.Equal((2, 1), (added, removed));
        Assert.Equal(new[] { 2, 4 }, UnifiedDiff.AddedLines(text).Select(l => l.LineNumber));
        Assert.StartsWith("--- /dev/null\n+++ b/docs/b.md\n@@ -0,0 +1,1 @@\n+new", UnifiedDiff.Create("docs/b.md", null, "new").Text);
        Assert.Equal("", UnifiedDiff.Create("docs/a.md", "same\n", "same\n").Text);
    }

    [Fact]
    public void Unified_diff_splits_distant_changes_into_separate_hunks()
    {
        var old = string.Concat(Enumerable.Range(1, 20).Select(i => $"{i}\n"));
        var changed = string.Concat(Enumerable.Range(1, 20).Select(i => i switch { 2 => "two\n", 19 => "nineteen\n", _ => $"{i}\n" }));
        var text = UnifiedDiff.Create("docs/a.md", old, changed).Text;
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("@@")));
        Assert.Equal(new[] { 2, 19 }, UnifiedDiff.AddedLines(text).Select(l => l.LineNumber));
    }
}

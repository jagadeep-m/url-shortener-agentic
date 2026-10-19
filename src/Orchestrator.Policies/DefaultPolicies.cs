using Orchestrator.Core;

namespace Orchestrator.Policies;

public static class DefaultPolicies
{
    public static readonly string[] AgentRoots = ["src/UrlShortener.*", "tests/UrlShortener.*", "docs"];

    // The agreed tech stack plus what the solution already uses.
    public static readonly string[] AllowedPackages =
    [
        "Microsoft.EntityFrameworkCore*", "Microsoft.Data.Sqlite*", "Microsoft.AspNetCore.*", "Microsoft.Extensions.*",
        "FluentValidation*", "Polly.Core", "LibGit2Sharp", "Serilog*", "OpenTelemetry*", "Spectre.Console",
        "Swashbuckle.AspNetCore*", "YamlDotNet", "xunit*", "Microsoft.NET.Test.Sdk", "coverlet.collector"
    ];

    public static IReadOnlyList<IPolicy> Create(IVulnerabilityScanner scanner) =>
    [
        new PathAllowlistPolicy(AgentRoots), new SecretScanPolicy(), new NuGetAllowlistPolicy(AllowedPackages),
        new DiffSizePolicy(), new MigrationPolicy(), new VulnerablePackagePolicy(scanner)
    ];
}

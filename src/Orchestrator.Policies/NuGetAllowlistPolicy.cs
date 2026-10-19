using System.Text.RegularExpressions;
using Orchestrator.Core;

namespace Orchestrator.Policies;

// New or changed package references: allowlisted packages need approval, anything else is denied.
public sealed class NuGetAllowlistPolicy(IReadOnlyList<string> allowedPackages) : Policy
{
    private static readonly Regex Reference = new(@"\A(?<id>[A-Za-z0-9_.-]+)@(?<version>[A-Za-z0-9.*+-]+)\z", RegexOptions.CultureInvariant);

    public override string Name => "NuGetAllowlist";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        if (change.NewPackages.Count == 0) return Allow("no new packages");
        var malformed = change.NewPackages.Where(p => !Reference.IsMatch(p)).ToArray();
        if (malformed.Length > 0) return Deny("malformed package reference: " + string.Join(", ", malformed));
        var blocked = change.NewPackages.Where(p => !IsAllowed(Reference.Match(p).Groups["id"].Value)).ToArray();
        if (blocked.Length > 0) return Deny("packages outside the allowlist: " + string.Join(", ", blocked));
        return Approve("new allowlisted packages need approval: " + string.Join(", ", change.NewPackages));
    }

    // Entries ending in '*' match a prefix, e.g. "Microsoft.EntityFrameworkCore*".
    private bool IsAllowed(string id) => allowedPackages.Any(a => a.EndsWith('*')
        ? id.StartsWith(a[..^1], StringComparison.OrdinalIgnoreCase)
        : id.Equals(a, StringComparison.OrdinalIgnoreCase));
}

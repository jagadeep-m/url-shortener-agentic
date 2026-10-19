using Orchestrator.Core;

namespace Orchestrator.Policies;

// A new EF Core migration is a schema change and needs approval. Editing an existing migration
// rewrites history that databases may already have applied, so it is denied (the model snapshot may change).
public sealed class MigrationPolicy : Policy
{
    public override string Name => "MigrationApproval";

    public override PolicyResult Check(ProposedChange change, RunContext ctx)
    {
        var rewritten = UnifiedDiff.ModifiedExistingFiles(change.Diff)
            .Where(p => IsMigration(p) && !p.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal)).ToArray();
        if (rewritten.Length > 0) return Deny("existing migrations must not be edited; add a new migration instead: " + string.Join(", ", rewritten));
        if (change.IncludesMigration || change.Paths.Any(IsMigration))
            return Approve("EF Core migration: " + string.Join(", ", change.Paths.Where(IsMigration)));
        return Allow("no migrations");
    }

    private static bool IsMigration(string path) => path.Contains("/Migrations/", StringComparison.OrdinalIgnoreCase) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}

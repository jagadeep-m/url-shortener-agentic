using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orchestrator.Core;

namespace Orchestrator.Agents;

public sealed record EndpointInfo(string Method, string Route, string File, IReadOnlySet<string> Uses);
public sealed record ImpactItem(string Name, string Reason);
public sealed record ImpactReport(IReadOnlyList<string> Seeds, IReadOnlyList<ImpactItem> Files, IReadOnlyList<ImpactItem> Endpoints,
    IReadOnlyList<ImpactItem> Tables, IReadOnlyList<ImpactItem> Tests, IReadOnlyList<string> UnknownSeeds)
{
    public string ToMarkdown()
    {
        var text = new StringBuilder("# Impact analysis\n\nComputed by the host from Roslyn syntax trees of the current code; the agent only proposed the seeds.\n\n");
        text.AppendLine("Seeds: " + string.Join(", ", Seeds.Select(s => $"`{s}`")) + "\n");
        Section("Affected source files", Files);
        Section("Affected endpoints", Endpoints);
        Section("Affected database tables", Tables);
        Section("Affected tests", Tests);
        if (UnknownSeeds.Count > 0) text.AppendLine("## Not found in the codebase (new, or a wrong guess)\n\n" + string.Join("\n", UnknownSeeds.Select(s => $"- `{s}`")) + "\n");
        return text.ToString().TrimEnd() + "\n";

        void Section(string title, IReadOnlyList<ImpactItem> items) =>
            text.AppendLine($"## {title} ({items.Count})\n\n" + (items.Count == 0 ? "- none" : string.Join("\n", items.Select(i => $"- `{i.Name}`: {i.Reason}"))) + "\n");
    }
}

// A syntax-only index of the product code: declared types and members, minimal API endpoints
// (Map* calls with MapGroup prefixes), EF Core table mappings and identifier references.
public sealed class CodebaseIndex
{
    private static readonly HashSet<string> MapMethods = ["MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch"];
    private readonly List<SourceFile> files = [];
    private readonly List<EndpointInfo> endpoints = [];
    private readonly Dictionary<string, string> tables = new(StringComparer.Ordinal); // entity type -> table

    private sealed record SourceFile(string Path, bool IsTest, IReadOnlyDictionary<string, IReadOnlySet<string>> Types, IReadOnlySet<string> References);

    public IReadOnlyList<EndpointInfo> Endpoints => endpoints;
    public IReadOnlyDictionary<string, string> Tables => tables;
    public IEnumerable<string> FilePaths => files.Select(f => f.Path);

    public static CodebaseIndex Build(string workspace, IReadOnlyList<string> roots)
    {
        var index = new CodebaseIndex();
        var root = Path.GetFullPath(workspace);
        var paths = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(root, p).Replace('\\', '/'))
            .Where(p => PathPatterns.IsUnderAny(p, roots) && !p.Split('/').Any(s => s is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
        foreach (var path in paths) index.Add(path, File.ReadAllText(Path.Combine(root, path)));
        return index;
    }

    private void Add(string path, string text)
    {
        var syntax = CSharpSyntaxTree.ParseText(text, path: path).GetCompilationUnitRoot();
        var types = syntax.DescendantNodes().OfType<BaseTypeDeclarationSyntax>().GroupBy(t => t.Identifier.ValueText)
            .ToDictionary(g => g.Key, g => (IReadOnlySet<string>)g.SelectMany(Members).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        files.Add(new(path, path.StartsWith("tests/", StringComparison.Ordinal), types, Identifiers(syntax)));
        IndexEndpoints(path, syntax);
        IndexTables(syntax);
    }

    private static IEnumerable<string> Members(BaseTypeDeclarationSyntax type) => type switch
    {
        TypeDeclarationSyntax t => t.Members.Select(m => m switch
        {
            MethodDeclarationSyntax method => method.Identifier.ValueText,
            PropertyDeclarationSyntax property => property.Identifier.ValueText,
            _ => null
        }).OfType<string>()
            .Concat(t is RecordDeclarationSyntax { ParameterList: { } parameters } ? parameters.Parameters.Select(p => p.Identifier.ValueText) : [])
            .Concat(t.ParameterList?.Parameters.Select(p => p.Identifier.ValueText) ?? []),
        EnumDeclarationSyntax e => e.Members.Select(m => m.Identifier.ValueText),
        _ => []
    };

    private static HashSet<string> Identifiers(SyntaxNode node) => node.DescendantNodesAndSelf().OfType<SimpleNameSyntax>()
        .Select(n => n.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);

    private void IndexEndpoints(string path, CompilationUnitSyntax syntax)
    {
        // var links = app.MapGroup("/api/v1/links").WithTags("Links"); (the group call may sit anywhere in the chain)
        var groups = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var variable in syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>())
        {
            var group = variable.Initializer?.Value.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>()
                .FirstOrDefault(i => i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "MapGroup" });
            if (group is not null) groups[variable.Identifier.ValueText] = Literal(group.ArgumentList.Arguments.FirstOrDefault()) ?? "";
        }
        foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax access || !MapMethods.Contains(access.Name.Identifier.ValueText)) continue;
            var route = Literal(call.ArgumentList.Arguments.FirstOrDefault());
            if (route is null) continue;
            var prefix = access.Expression is IdentifierNameSyntax receiver && groups.TryGetValue(receiver.Identifier.ValueText, out var group) ? group : "";
            var full = (prefix.TrimEnd('/') + (route.Length > 0 && !route.StartsWith('/') ? "/" : "") + route) is { Length: > 0 } r ? r : "/";
            var handler = call.ArgumentList.Arguments.Skip(1).FirstOrDefault()?.Expression;
            endpoints.Add(new(access.Name.Identifier.ValueText[3..].ToUpperInvariant(), full, path, handler is null ? new HashSet<string>() : Identifiers(handler)));
        }
    }

    private void IndexTables(CompilationUnitSyntax syntax)
    {
        // var links = modelBuilder.Entity<ShortLink>(); links.ToTable("ShortLinks", ...);
        var entities = syntax.DescendantNodes().OfType<VariableDeclaratorSyntax>()
            .Select(v => (Name: v.Identifier.ValueText, Entity: EntityType(v.Initializer?.Value)))
            .Where(x => x.Entity is not null).ToDictionary(x => x.Name, x => x.Entity!, StringComparer.Ordinal);
        foreach (var call in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "ToTable" } access) continue;
            var entity = access.Expression is IdentifierNameSyntax variable && entities.TryGetValue(variable.Identifier.ValueText, out var e) ? e : EntityType(access.Expression);
            if (entity is not null && Literal(call.ArgumentList.Arguments.FirstOrDefault()) is { } table) tables[entity] = table;
        }
    }

    private static string? EntityType(ExpressionSyntax? expression) =>
        expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "Entity" } generic } }
            ? generic.TypeArgumentList.Arguments.FirstOrDefault()?.ToString() : null;

    private static string? Literal(ArgumentSyntax? argument) =>
        argument?.Expression is LiteralExpressionSyntax literal && literal.IsKind(SyntaxKind.StringLiteralExpression) ? literal.Token.ValueText : null;

    // Seeds are "Type" or "Type.Member". Types widen the impact to every user of the type; members
    // narrow endpoint and test impact to code that calls that member.
    public ImpactReport Analyze(IReadOnlyList<string> seeds)
    {
        var parsed = seeds.Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.Ordinal)
            .Select(s => (Seed: s, Type: s.Split('.')[0], Member: s.Contains('.') ? s[(s.IndexOf('.') + 1)..] : null)).ToArray();
        var declared = files.SelectMany(f => f.Types).GroupBy(t => t.Key).ToDictionary(g => g.Key, g => g.SelectMany(x => x.Value).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var unknown = parsed.Where(p => !declared.TryGetValue(p.Type, out var members) || (p.Member is not null && !members.Contains(p.Member))).Select(p => p.Seed).ToArray();
        var known = parsed.Where(p => !unknown.Contains(p.Seed)).ToArray();
        var typeSeeds = known.Where(p => p.Member is null).Select(p => p.Type).ToHashSet(StringComparer.Ordinal);
        var memberSeeds = known.Where(p => p.Member is not null).Select(p => p.Member!).ToHashSet(StringComparer.Ordinal);
        var owners = known.Select(p => p.Type).ToHashSet(StringComparer.Ordinal);

        string? Why(SourceFile file)
        {
            var declares = file.Types.Keys.Where(owners.Contains).ToArray();
            if (declares.Length > 0) return "declares " + string.Join(", ", declares);
            var uses = typeSeeds.Where(file.References.Contains).Concat(memberSeeds.Where(file.References.Contains)).ToArray();
            return uses.Length > 0 ? "uses " + string.Join(", ", uses) : null;
        }

        var affected = files.Where(f => !f.IsTest).Select(f => (f.Path, Reason: Why(f))).Where(x => x.Reason is not null).Select(x => new ImpactItem(x.Path, x.Reason!)).ToArray();
        var tests = files.Where(f => f.IsTest).Select(f => (f.Path, Reason: Why(f))).Where(x => x.Reason is not null).Select(x => new ImpactItem(x.Path, x.Reason!)).ToArray();
        var routes = endpoints.Select(e => (e, Hits: typeSeeds.Concat(memberSeeds).Where(e.Uses.Contains).ToArray())).Where(x => x.Hits.Length > 0)
            .Select(x => new ImpactItem($"{x.e.Method} {x.e.Route}", $"handler in {x.e.File} uses {string.Join(", ", x.Hits)}")).ToArray();
        var mapped = tables.Where(t => owners.Contains(t.Key)).Select(t => new ImpactItem(t.Value, $"maps entity {t.Key}")).ToArray();
        return new(parsed.Select(p => p.Seed).ToArray(), affected, routes, mapped, tests, unknown);
    }
}

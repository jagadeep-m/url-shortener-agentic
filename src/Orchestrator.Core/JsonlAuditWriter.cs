using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Orchestrator.Core;

public sealed record AuditEvent(Guid EventId, Guid RunId, DateTime TimestampUtc, string TraceId,
    string? Stage, string? Agent, string Event, string? PromptHash, string? OutputHash,
    string? Verdict, string? Approver, string? Message);

public sealed class JsonlAuditWriter : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider clock;
    public string RunsDirectory { get; }

    public JsonlAuditWriter(string runsDirectory = "runs", TimeProvider? clock = null)
    {
        RunsDirectory = Path.GetFullPath(runsDirectory);
        this.clock = clock ?? TimeProvider.System;
    }

    public string GetPath(Guid runId) => Path.Combine(RunsDirectory, runId.ToString("D"), "audit.jsonl");

    public Task WriteAsync(Guid runId, string eventName, StageDefinition? stage = null,
        string? outputHash = null, string? verdict = null, string? approver = null, string? message = null,
        CancellationToken ct = default, string? promptHash = null)
    {
        var activity = Activity.Current ?? throw new InvalidOperationException("Audit events require an active ActivitySource span.");
        return AppendAsync(new(Guid.NewGuid(), runId, clock.GetUtcNow().UtcDateTime, activity.TraceId.ToHexString(),
            stage?.Id, stage?.Agent, eventName, promptHash, outputHash, verdict, approver, message), ct);
    }

    public async Task AppendAsync(AuditEvent entry, CancellationToken ct = default)
    {
        if (entry.RunId == Guid.Empty || entry.EventId == Guid.Empty || entry.TimestampUtc.Kind != DateTimeKind.Utc
            || string.IsNullOrWhiteSpace(entry.Event) || entry.TraceId.Length != 32
            || entry.TraceId.Any(c => !char.IsAsciiHexDigit(c)) || entry.TraceId.All(c => c == '0'))
            throw new ArgumentException("Audit event requires IDs, a UTC timestamp, an event name and a nonzero W3C trace ID.", nameof(entry));
        var line = JsonSerializer.Serialize(entry, RunJson.Options) + "\n";
        await gate.WaitAsync(ct);
        try
        {
            var path = GetPath(entry.RunId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(line), ct);
            await stream.FlushAsync(ct);
        }
        finally { gate.Release(); }
    }

    public void Dispose() => gate.Dispose();
}

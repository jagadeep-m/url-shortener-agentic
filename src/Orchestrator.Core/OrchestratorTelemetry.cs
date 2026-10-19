using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Orchestrator.Core;

public static class OrchestratorTelemetry
{
    public const string SourceName = "UrlShortenerAgentic.Orchestrator";
    public static ActivitySource Source { get; } = new(SourceName);
    // Live reliability metrics; any OpenTelemetry exporter can subscribe to this meter by name.
    // Tags stay low-cardinality (scenario, stage, status), never run IDs.
    public static Meter Meter { get; } = new(SourceName);
    internal static readonly Counter<long> RunsStarted = Meter.CreateCounter<long>("orchestrator.runs.started");
    internal static readonly Counter<long> RunsFinished = Meter.CreateCounter<long>("orchestrator.runs.finished");
    internal static readonly Counter<long> StageAttempts = Meter.CreateCounter<long>("orchestrator.stage.attempts");
    internal static readonly Counter<long> Retries = Meter.CreateCounter<long>("orchestrator.stage.retries");
    internal static readonly Counter<long> Rollbacks = Meter.CreateCounter<long>("orchestrator.rollbacks");
    internal static readonly Counter<long> Replans = Meter.CreateCounter<long>("orchestrator.replans");
    internal static readonly Histogram<double> StageDuration = Meter.CreateHistogram<double>("orchestrator.stage.duration", "s");
    internal static readonly Histogram<double> ApprovalWait = Meter.CreateHistogram<double>("orchestrator.approval.wait", "s");
    internal static readonly Histogram<double> RunDuration = Meter.CreateHistogram<double>("orchestrator.run.duration", "s");

    internal static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
    private static readonly ActivityListener AuditListener;

    static OrchestratorTelemetry()
    {
        // Keep correlation available even when no tracing exporter has been configured.
        AuditListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(AuditListener);
    }

    internal static Activity StartRun(RunContext context, bool resume)
    {
        var parent = context.TraceId is not null && context.SpanId is not null
            ? new ActivityContext(ActivityTraceId.CreateFromString(context.TraceId.AsSpan()),
                ActivitySpanId.CreateFromString(context.SpanId.AsSpan()), ActivityTraceFlags.Recorded)
            : default;
        var activity = Source.StartActivity(resume ? "run.resume" : "run.start", ActivityKind.Internal, parent)!;
        activity.SetTag("run.id", context.RunId);
        context.TraceId = activity.TraceId.ToHexString();
        context.SpanId = activity.SpanId.ToHexString();
        return activity;
    }
}

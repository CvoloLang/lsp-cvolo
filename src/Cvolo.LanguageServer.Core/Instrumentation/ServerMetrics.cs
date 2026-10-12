using System.Diagnostics;
using System.Text;

namespace Cvolo.LanguageServer.Core.Instrumentation;

/// <summary>
/// Thread-safe counters for the document input / analysis pipeline (INC
/// "Incremental Semantic Snapshots", migration stage S0). This is measurement
/// only: recording a metric never changes scheduling, publication or freshness
/// behaviour. The counters let the P0 baseline (didChange publication rate,
/// snapshot advances, ingress lock hold, interactive query churn) be compared
/// across later correctness-first stages.
/// </summary>
internal sealed class ServerMetrics
{
    private long _didOpen;
    private long _didChange;
    private long _didClose;
    private long _snapshotAdvances;
    private long _interactiveQueriesRun;
    private long _interactiveQueriesSkippedStale;
    private long _diagnosticCommits;
    private long _diagnosticCommitsDiscarded;
    private long _publicationGateHolds;
    private long _publicationGateHoldTicks;
    private long _publicationGateHoldMaxTicks;
    private long _analysisRunsStarted;
    private long _analysisRunsCompleted;
    private long _analysisRunsFailed;
    private long _interactiveQueriesOnPendingGeneration;

    public long DidOpen => Interlocked.Read(ref _didOpen);

    public long DidChange => Interlocked.Read(ref _didChange);

    public long DidClose => Interlocked.Read(ref _didClose);

    /// <summary>Backend snapshot publications (one per accepted text/config advance).</summary>
    public long SnapshotAdvances => Interlocked.Read(ref _snapshotAdvances);

    public long InteractiveQueriesRun => Interlocked.Read(ref _interactiveQueriesRun);

    public long InteractiveQueriesSkippedStale => Interlocked.Read(ref _interactiveQueriesSkippedStale);

    public long DiagnosticCommits => Interlocked.Read(ref _diagnosticCommits);

    public long DiagnosticCommitsDiscarded => Interlocked.Read(ref _diagnosticCommitsDiscarded);

    public long PublicationGateHolds => Interlocked.Read(ref _publicationGateHolds);

    public double PublicationGateHoldTotalMs => TicksToMs(Interlocked.Read(ref _publicationGateHoldTicks));

    public double PublicationGateHoldMaxMs => TicksToMs(Interlocked.Read(ref _publicationGateHoldMaxTicks));

    public void RecordDidOpen() => Interlocked.Increment(ref _didOpen);

    public void RecordDidChange() => Interlocked.Increment(ref _didChange);

    public void RecordDidClose() => Interlocked.Increment(ref _didClose);

    public void RecordSnapshotAdvance() => Interlocked.Increment(ref _snapshotAdvances);

    public void RecordInteractiveQueryRun() => Interlocked.Increment(ref _interactiveQueriesRun);

    public void RecordInteractiveQuerySkippedStale() => Interlocked.Increment(ref _interactiveQueriesSkippedStale);

    public void RecordDiagnosticCommit() => Interlocked.Increment(ref _diagnosticCommits);

    public void RecordDiagnosticCommitDiscarded() => Interlocked.Increment(ref _diagnosticCommitsDiscarded);

    /// <summary>Background generation analyses started (migration stage S4).</summary>
    public long AnalysisRunsStarted => Interlocked.Read(ref _analysisRunsStarted);

    public long AnalysisRunsCompleted => Interlocked.Read(ref _analysisRunsCompleted);

    public long AnalysisRunsFailed => Interlocked.Read(ref _analysisRunsFailed);

    /// <summary>Interactive queries that ran before the generation's full analysis was ready.</summary>
    public long InteractiveQueriesOnPendingGeneration => Interlocked.Read(ref _interactiveQueriesOnPendingGeneration);

    public void RecordAnalysisRunStarted() => Interlocked.Increment(ref _analysisRunsStarted);

    public void RecordAnalysisRunCompleted() => Interlocked.Increment(ref _analysisRunsCompleted);

    public void RecordAnalysisRunFailed() => Interlocked.Increment(ref _analysisRunsFailed);

    public void RecordInteractiveQueryOnPendingGeneration() => Interlocked.Increment(ref _interactiveQueriesOnPendingGeneration);

    /// <summary>Records one acquisition of the document publication gate and how long it was held.</summary>
    public void RecordPublicationGateHold(long elapsedStopwatchTicks)
    {
        Interlocked.Increment(ref _publicationGateHolds);
        Interlocked.Add(ref _publicationGateHoldTicks, elapsedStopwatchTicks);
        UpdateMax(ref _publicationGateHoldMaxTicks, elapsedStopwatchTicks);
    }

    public ServerMetricsSnapshot Capture() => new(
        DidOpen,
        DidChange,
        DidClose,
        SnapshotAdvances,
        InteractiveQueriesRun,
        InteractiveQueriesSkippedStale,
        DiagnosticCommits,
        DiagnosticCommitsDiscarded,
        PublicationGateHolds,
        PublicationGateHoldTotalMs,
        PublicationGateHoldMaxMs,
        AnalysisRunsStarted,
        AnalysisRunsCompleted,
        AnalysisRunsFailed,
        InteractiveQueriesOnPendingGeneration);

    /// <summary>Renders a single-line, log-friendly report of the current counters.</summary>
    public string FormatReport()
    {
        ServerMetricsSnapshot s = Capture();
        var builder = new StringBuilder("[metrics] ");
        builder.Append("didOpen=").Append(s.DidOpen)
            .Append(" didChange=").Append(s.DidChange)
            .Append(" didClose=").Append(s.DidClose)
            .Append(" snapshotAdvances=").Append(s.SnapshotAdvances)
            .Append(" interactiveQueriesRun=").Append(s.InteractiveQueriesRun)
            .Append(" interactiveQueriesSkippedStale=").Append(s.InteractiveQueriesSkippedStale)
            .Append(" interactiveQueriesOnPendingGeneration=").Append(s.InteractiveQueriesOnPendingGeneration)
            .Append(" analysisRunsStarted=").Append(s.AnalysisRunsStarted)
            .Append(" analysisRunsCompleted=").Append(s.AnalysisRunsCompleted)
            .Append(" analysisRunsFailed=").Append(s.AnalysisRunsFailed)
            .Append(" diagnosticCommits=").Append(s.DiagnosticCommits)
            .Append(" diagnosticCommitsDiscarded=").Append(s.DiagnosticCommitsDiscarded)
            .Append(" publicationGateHolds=").Append(s.PublicationGateHolds)
            .Append(" publicationGateHoldTotalMs=").Append(s.PublicationGateHoldTotalMs.ToString("F3"))
            .Append(" publicationGateHoldMaxMs=").Append(s.PublicationGateHoldMaxMs.ToString("F3"));
        return builder.ToString();
    }

    private static void UpdateMax(ref long target, long value)
    {
        long current = Interlocked.Read(ref target);
        while (value > current)
        {
            long previous = Interlocked.CompareExchange(ref target, value, current);
            if (previous == current)
            {
                return;
            }

            current = previous;
        }
    }

    private static double TicksToMs(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}

/// <summary>Immutable point-in-time copy of <see cref="ServerMetrics"/>.</summary>
internal readonly record struct ServerMetricsSnapshot(
    long DidOpen,
    long DidChange,
    long DidClose,
    long SnapshotAdvances,
    long InteractiveQueriesRun,
    long InteractiveQueriesSkippedStale,
    long DiagnosticCommits,
    long DiagnosticCommitsDiscarded,
    long PublicationGateHolds,
    double PublicationGateHoldTotalMs,
    double PublicationGateHoldMaxMs,
    long AnalysisRunsStarted,
    long AnalysisRunsCompleted,
    long AnalysisRunsFailed,
    long InteractiveQueriesOnPendingGeneration);

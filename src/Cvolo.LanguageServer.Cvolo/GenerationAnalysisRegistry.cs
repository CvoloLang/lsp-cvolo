using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Core.Instrumentation;

namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Migration stage S4 (INC "Incremental Semantic Snapshots"): memoizes exactly
/// one background full analysis per project generation and collapses bursts of
/// edits onto the newest generation.
///
/// Rules enforced here:
/// <list type="bullet">
/// <item>At most one background analysis worker runs per session at a time; a
/// running worker observes the newest requested generation when it finishes, so
/// superseded generations are dropped rather than queued.</item>
/// <item>A quiet period coalesces rapid keystrokes so a single typed burst does
/// not bind once per character.</item>
/// <item>Analysis is never shared across generations: every generation is its
/// own immutable <see cref="ProjectSnapshot"/> with its own lazy analysis, and
/// this registry only records which generations have completed.</item>
/// <item>Readiness is bounded: only the most recent generations are retained so
/// the ready set cannot grow without limit.</item>
/// </list>
/// </summary>
internal sealed class GenerationAnalysisRegistry(ServerMetrics? metrics)
{
    private const long RetainedGenerations = 8;

    private readonly object _sync = new();
    private readonly ServerMetrics? _metrics = metrics;
    private readonly HashSet<long> _ready = new();

    private (ProjectSnapshot Snapshot, long Generation)? _latest;
    private Task? _worker;
    private long _lastReadyGeneration = -1;

    /// <summary>
    /// Set by tests to remove the coalescing delay. Production keeps a short
    /// quiet period so a keystroke burst collapses onto one analysis.
    /// </summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromMilliseconds(350);

    public long LastReadyGeneration
    {
        get
        {
            lock (_sync)
            {
                return _lastReadyGeneration;
            }
        }
    }

    /// <summary>Requests a background analysis for the newest generation.</summary>
    public void Request(ProjectSnapshot snapshot, long generation)
    {
        lock (_sync)
        {
            if (_latest is { } pending && pending.Generation >= generation)
            {
                return;
            }

            _latest = (snapshot, generation);

            if (_worker is { IsCompleted: false })
            {
                return;
            }

            _worker = Task.Run(RunAsync);
        }
    }

    /// <summary>True when the generation's full analysis has completed.</summary>
    public bool IsReady(long generation)
    {
        lock (_sync)
        {
            return _ready.Contains(generation);
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            (ProjectSnapshot Snapshot, long Generation) work;
            lock (_sync)
            {
                if (_latest is not { } pending)
                {
                    _worker = null;
                    return;
                }

                work = pending;
            }

            // Debounce: restart the wait whenever a newer generation arrives so a
            // burst of edits produces one analysis of the newest generation.
            TimeSpan quiet = QuietPeriod;
            if (quiet > TimeSpan.Zero)
            {
                await Task.Delay(quiet).ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (_latest is { } newer && newer.Generation > work.Generation)
                {
                    continue;
                }

                _latest = null;
            }

            _metrics?.RecordAnalysisRunStarted();
            try
            {
                await Task.Run(() => PrepareAnalysisIfAvailable(work.Snapshot)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                _metrics?.RecordAnalysisRunFailed();
                continue;
            }

            lock (_sync)
            {
                _ready.Add(work.Generation);
                if (work.Generation > _lastReadyGeneration)
                {
                    _lastReadyGeneration = work.Generation;
                }

                long floor = work.Generation - RetainedGenerations;
                _ready.RemoveWhere(generation => generation < floor);
            }

            _metrics?.RecordAnalysisRunCompleted();
        }
    }

    private static void PrepareAnalysisIfAvailable(ProjectSnapshot snapshot)
    {
        var method = typeof(ProjectSnapshot).GetMethod("PrepareAnalysisV1", Type.EmptyTypes);
        method?.Invoke(snapshot, null);
    }
}

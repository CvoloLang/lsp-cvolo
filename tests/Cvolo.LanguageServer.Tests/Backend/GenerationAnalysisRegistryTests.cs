using Cvolo.Compiler.Tooling;
using Cvolo.LanguageServer.Cvolo;
using Cvolo.LanguageServer.Tests.TestSupport;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Backend;

/// <summary>
/// Migration stage S4 (INC "Incremental Semantic Snapshots"): one single-flight
/// background analysis per project generation, newest-generation-wins
/// coalescing, and bounded readiness retention.
/// </summary>
public class GenerationAnalysisRegistryTests : IDisposable
{
    private readonly TestWorkspace _workspace;

    public GenerationAnalysisRegistryTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"], _ => "int Main() { return 0; }");
    }

    public void Dispose()
    {
        _workspace.Dispose();
    }

    private ProjectSnapshot OpenSnapshot()
    {
        CvoloWorkspace workspace = CvoloWorkspace.Create();
        return workspace.OpenProject(_workspace.ProjectFilePath).InitialSnapshot;
    }

    [Fact]
    public async Task Request_CompletesAnalysisForTheGeneration()
    {
        var registry = new GenerationAnalysisRegistry(null) { QuietPeriod = TimeSpan.Zero };
        ProjectSnapshot snapshot = OpenSnapshot();

        registry.Request(snapshot, 1);

        await WaitUntilAsync(() => registry.IsReady(1)).ConfigureAwait(false);
        Assert.True(registry.IsReady(1));
        Assert.Equal(1, registry.LastReadyGeneration);
    }

    [Fact]
    public async Task OlderGeneration_IsRequestedWithoutAnalysisAsyncBlockingCapture()
    {
        var registry = new GenerationAnalysisRegistry(null) { QuietPeriod = TimeSpan.FromMilliseconds(30) };
        ProjectSnapshot snapshot = OpenSnapshot();

        // A burst of edits collapses onto the newest generation: only the final
        // request is analyzed, and intermediate generations never become ready.
        registry.Request(snapshot, 1);
        registry.Request(snapshot, 2);
        registry.Request(snapshot, 3);

        await WaitUntilAsync(() => registry.IsReady(3)).ConfigureAwait(false);
        Assert.True(registry.IsReady(3));
        Assert.False(registry.IsReady(1));
        Assert.False(registry.IsReady(2));
    }

    [Fact]
    public async Task ReadyGenerations_AreRetainedForABoundedWindow()
    {
        var registry = new GenerationAnalysisRegistry(null) { QuietPeriod = TimeSpan.Zero };
        ProjectSnapshot snapshot = OpenSnapshot();

        for (long generation = 1; generation <= 20; generation++)
        {
            registry.Request(snapshot, generation);
            await WaitUntilAsync(() => registry.IsReady(generation)).ConfigureAwait(false);
        }

        Assert.True(registry.IsReady(20));
        Assert.True(registry.IsReady(12));
        Assert.False(registry.IsReady(1));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 4000 && !condition(); attempt++)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }

        Assert.True(condition(), "condition was not met within the timeout");
    }
}

using System.Reflection;
using Xunit;

namespace Cvolo.LanguageServer.Tests;

/// <summary>
/// Enforces the Inc 3.3 architecture rather than the pre-observability assumption
/// that the only Cvolo dependency of every LSP assembly is Compiler.Tooling.
/// The precise allowlists are intentional: an unexpected compiler, protocol,
/// runtime or binder dependency still fails the tests.
/// </summary>
public class ToolingBoundaryTests
{
    private const string Tooling = "Cvolo.Compiler.Tooling";
    private const string Contracts = "Cvolo.Observability.Abstractions";
    private const string Runtime = "Cvolo.Observability";

    private static bool ManagedToolingAvailable =>
        File.Exists(Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll"));

    [Fact]
    public void CompilerToolingAssembly_LoadsFromDefaultAlc_AndTypeResolves()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll");
        if (!File.Exists(path))
        {
            return;
        }

        Assert.True(File.Exists(path),
            $"Expected tooling assembly at {path} (run build/fetch-tooling before tests).");

        var loaded = Assembly.LoadFrom(path);
        Type? workspace = loaded.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace", throwOnError: false);
        Assert.NotNull(workspace);

        var expected = Type.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace, Cvolo.Compiler.Tooling", throwOnError: false);
        Assert.NotNull(expected);
        Assert.Same(expected, workspace);
    }

    [Fact]
    public void Server_ReferencesOnlyToolingContractsAndRuntime_AmongCompilerFamily()
    {
        // §13, §29.2–29.3: the LSP host may load the compiler-facing Tooling API,
        // observability contracts and the recording runtime. It must not directly
        // bind to compiler Core, Analysis, Parser, code generation or Binder.
        var server = typeof(LanguageServer.Protocol.LanguageServer).Assembly;
        string[] expected = ManagedToolingAvailable
            ? [Tooling, Runtime, Contracts]
            : [Runtime, Contracts];

        Assert.Equal(Ordered(expected), DirectCompilerFamilyReferences(server));
    }

    [Fact]
    public void Core_ReferencesOnlyObservabilityContracts_AmongCompilerFamily()
    {
        // §29.2: Core can emit typed events through Abstractions. It cannot own
        // the recording implementation, compiler backend, protocol or parser.
        var names = DirectReferenceNames(typeof(Core.DocumentStore).Assembly);

        Assert.Equal(new[] { Contracts }, DirectCompilerFamilyReferences(typeof(Core.DocumentStore).Assembly));
        Assert.DoesNotContain(names, n => n.Contains("Antlr4.Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.StartsWith("StreamJsonRpc", StringComparison.Ordinal));
    }

    [Fact]
    public void CvoloAdapter_ReferencesOnlyToolingAndContracts_AmongCompilerFamily()
    {
        // §29.2: the adapter translates between Compiler.Tooling and the LSP
        // model; it may forward events, but may not own a writer/runtime.
        var adapter = typeof(Cvolo.CvoloLanguageBackend).Assembly;
        string[] expected = ManagedToolingAvailable ? [Tooling, Contracts] : [Contracts];

        Assert.Equal(Ordered(expected), DirectCompilerFamilyReferences(adapter));
        var names = DirectReferenceNames(adapter);
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ObservabilityContracts_HaveNoCompilerOrLspDependencies()
    {
        // The shared contract must be usable by the compiler, CLI, standalone
        // Tooling and LSP without loading host or compiler implementation assemblies.
        var contracts = typeof(global::Cvolo.Observability.Abstractions.ICompilerObserver).Assembly;
        Assert.Empty(DirectCompilerFamilyReferences(contracts));
        var names = DirectReferenceNames(contracts);
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.StartsWith("StreamJsonRpc", StringComparison.Ordinal));
    }

    [Fact]
    public void ObservabilityRuntime_DependsOnlyOnContracts_AmongCompilerFamily()
    {
        // §29.2: the runtime records events, but must not depend on Tooling,
        // Compiler Core, LSP, protocol handlers or source-analysis internals.
        var runtime = typeof(global::Cvolo.Observability.CompilerObserver).Assembly;
        Assert.Equal(new[] { Contracts }, DirectCompilerFamilyReferences(runtime));
        var names = DirectReferenceNames(runtime);
        Assert.DoesNotContain(names, n => n.StartsWith("Microsoft.VisualStudio.LanguageServer", StringComparison.Ordinal));
        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.StartsWith("StreamJsonRpc", StringComparison.Ordinal));
    }

    [Fact]
    public void Server_ReferencesNoAntlrLlmOrCodegenAssemblies()
    {
        var names = DirectReferenceNames(typeof(LanguageServer.Protocol.LanguageServer).Assembly);
        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("LLVM", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, n => n.Contains("CodeGen", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CvoloAdapter_ReferencesNoAntlrAssembly()
    {
        var names = DirectReferenceNames(typeof(Cvolo.CvoloLanguageBackend).Assembly);
        Assert.DoesNotContain(names, n => n.Contains("Antlr", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] DirectCompilerFamilyReferences(Assembly assembly) =>
        Ordered(DirectReferenceNames(assembly)
            .Where(name => name.StartsWith("Cvolo.", StringComparison.Ordinal)
                && !name.StartsWith("Cvolo.LanguageServer.", StringComparison.Ordinal)));

    private static string[] Ordered(IEnumerable<string> names) =>
        names.OrderBy(name => name, StringComparer.Ordinal).ToArray();

    private static string[] DirectReferenceNames(Assembly assembly) =>
        assembly.GetReferencedAssemblies()
            .Select(assemblyName => assemblyName.Name!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}

#if CVOLO_MANAGED_TOOLING
using Cvolo.Compiler.Tooling;
#endif
using Cvolo.LanguageServer.Core.Scheduling;
using Cvolo.LanguageServer.Diagnostics;
using Cvolo.LanguageServer.Logging;
using Cvolo.LanguageServer.Observability;
using Cvolo.LanguageServer.Protocol;
using Cvolo.Observability.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using System.Reflection;
using System.Runtime.Loader;

namespace Cvolo.LanguageServer.Cli;

/// <summary>
/// Starts and runs the stdio JSON-RPC session. All diagnostics go to stderr or
/// the log file; stdout carries only JSON-RPC frames while the transport lives.
/// </summary>
internal static class ServerPipeline
{
    public static async Task<int> RunAsync(string? logPath, bool verbose, string? diagnosticLogDirectory = null)
    {
        using LspLogger logger = new(verbose, logPath, diagnosticLogDirectory);

        Console.Error.WriteLine($"{ServerMetadata.ServerName} {ServerMetadata.ServerVersion}");
        Console.Error.WriteLine($"tooling {ServerMetadata.ToolingVersion}");
        Console.Error.WriteLine($"compiler-line {ServerMetadata.CompilerCompatibilityLine}");
        logger.Info($"{ServerMetadata.ServerName} {ServerMetadata.ServerVersion} starting");
        logger.Info($"[provenance] commit={ServerMetadata.LanguageServerCommit} tooling={ServerMetadata.ToolingVersion} toolingCommit={ServerMetadata.ToolingCommit} compilerLine={ServerMetadata.CompilerCompatibilityLine} rid={ServerMetadata.RuntimeIdentifier}");
        logger.Info($"[debug] structuredLog={(logPath is null ? "off" : "enabled")} rawProtocol={(verbose ? "enabled" : "disabled")}");

        if (!TryLoadTooling(logger))
        {
            return 1;
        }

        TerminationRequest termination = new();
        using IClientProcessWatcher clientWatcher = ClientProcessWatcherFactory.Create();
        using LspObservabilitySession observability = LspObservabilitySession.Start();
        using Protocol.LanguageServer server = new(termination, logger, clientWatcher, observer: observability.Observer, recordingSession: observability);
        observability.Emit(new CompilerEvent
        {
            Event = "server.start",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Outcome = "started",
        });

        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        formatter.JsonSerializer.Converters.Add(new UriJsonConverter());
        Stream stdout = Console.OpenStandardOutput();
        Stream stdin = Console.OpenStandardInput();
        if (verbose && logger.RawSink is { } rawSink)
        {
            stdout = new TeeStream(stdout, rawSink);
            stdin = new TeeStream(stdin, rawSink);
        }

        using RpcObservability rpcObservability = new(observability.Observer);
        using ObservedHeaderDelimitedHandler handler = new(stdout, stdin, formatter, rpcObservability);
        observability.Emit(new CompilerEvent
        {
            Event = "transport.ready",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Outcome = "ready",
        });
        IngressPipeline ingress = new();
        server.Ingress = ingress;
        JsonRpc rpc = new(handler);
        rpcObservability.Attach(rpc);
        rpc.SynchronizationContext = new IngressSynchronizationContext(ingress);
        server.Diagnostics.Attach(rpc);
        server.Refresh.Attach(rpc);
        server.EditorIntelligenceRefresh.Attach(rpc);
        rpc.AddLocalRpcTarget(server, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Sync, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Completion, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.SignatureHelp, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Hover, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Definition, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.TypeDefinition, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Implementation, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.PrepareTypeHierarchy, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.TypeHierarchySupertypes, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.TypeHierarchySubtypes, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.DocumentSymbols, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.SemanticTokens, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.References, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Rename, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.CodeActions, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.CodeLenses, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.InlayHints, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.DocumentHighlights, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.FoldingRanges, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.SelectionRanges, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.TypeLayout, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.Configuration, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.AddLocalRpcTarget(server.AnchoredLens, new JsonRpcTargetOptions
        {
            MethodNameTransform = m => m.Length == 0 ? m : char.ToLowerInvariant(m[0]) + m.Substring(1),
            UseSingleObjectParameterDeserialization = true,
        });
        rpc.StartListening();

        await Task.WhenAny(rpc.Completion, termination.Task).ConfigureAwait(false);

        if (!termination.IsRequested)
        {
            termination.TryRequestAbnormal();
        }

        rpc.Dispose();
        server.Dispose();

        observability.Emit(new CompilerEvent
        {
            Event = "server.stop",
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P0,
            Outcome = termination.NormalRequested ? "stopped" : "abnormal",
        });

        return termination.NormalRequested ? 0 : 1;
    }

    public static void PrintVersion()
    {
        Console.WriteLine($"{ServerMetadata.ProductName} {ServerMetadata.ServerVersion}");
        Console.WriteLine($"Commit: {ServerMetadata.LanguageServerCommit}");
        Console.WriteLine($"Tooling: {ServerMetadata.ToolingVersion ?? ServerMetadata.Unknown}");
        Console.WriteLine($"Compiler compatibility: {ServerMetadata.CompilerCompatibilityLine ?? ServerMetadata.Unknown}");
    }

    /// <summary>
    /// Prints the same provenance as <see cref="PrintVersion"/> as a single JSON object
    /// for scripts and by the release pipeline. The field names match
    /// <c>bundle-manifest.json</c> exactly so the two can be compared field by field.
    /// </summary>
    public static void PrintVersionJson()
    {
        var identity = new
        {
            languageServerVersion = ServerMetadata.ServerVersion,
            languageServerCommit = ServerMetadata.LanguageServerCommit,
            toolingVersion = ServerMetadata.ToolingVersion,
            toolingCommit = ServerMetadata.ToolingCommit,
            compilerCompatibilityLine = ServerMetadata.CompilerCompatibilityLine,
            rid = ServerMetadata.RuntimeIdentifier,
            targetFramework = ServerMetadata.TargetFramework,
            runtimeVersion = ServerMetadata.RuntimeVersion,
        };

        Console.WriteLine(JsonConvert.SerializeObject(identity, Formatting.None, new JsonSerializerSettings
        {
            NullValueHandling = NullValueHandling.Include,
            ContractResolver = new DefaultContractResolver(),
        }));
    }

    private static bool TryLoadTooling(ILspLogger logger)
    {
#if CVOLO_MANAGED_TOOLING
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Cvolo.Compiler.Tooling.dll");
            if (!File.Exists(path))
            {
                logger.Error($"Cvolo.Compiler.Tooling.dll was not found at {path}.");
                return false;
            }

            Assembly assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
            Type? workspaceType = assembly.GetType("Cvolo.Compiler.Tooling.CvoloWorkspace", throwOnError: false);
            if (workspaceType is null)
            {
                logger.Error("Cvolo.Compiler.Tooling loaded but the CvoloWorkspace type was not found.");
                return false;
            }

            if (!ReferenceEquals(workspaceType, typeof(CvoloWorkspace)))
            {
                logger.Error("CvoloWorkspace type identity mismatch; tooling is incompatible with this server.");
                return false;
            }

            logger.Info($"Cvolo.Compiler.Tooling {assembly.GetName().Version} loaded successfully.");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to load Cvolo.Compiler.Tooling: {ex.Message}");
            return false;
        }
#else
        logger.Warning("Cvolo.Compiler.Tooling.dll is unavailable; compiler-backed language features are disabled.");
        return true;
#endif
    }
}
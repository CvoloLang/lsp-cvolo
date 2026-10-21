using Cvolo.LanguageServer.Logging;
using StreamJsonRpc;
using global::Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// <c>workspace/didChangeConfiguration</c> handler. The editor owns the decoration settings, so a
/// change to one is applied in place and answered with a lens and hint refresh: the annotations on
/// screen change without restarting the session and without regenerating a project snapshot (§65).
/// </summary>
internal sealed class ConfigurationHandler(
    ILspLogger logger,
    Func<Newtonsoft.Json.Linq.JObject, bool> applyConfiguration,
    Action requestRefresh,
    ICompilerObserver? observer = null)
{
    private readonly ICompilerObserver _observer = observer ?? NoopCompilerObserver.Instance;

    [JsonRpcMethod("workspace/didChangeConfiguration", UseSingleObjectParameterDeserialization = true)]
    public void DidChangeConfiguration(DidChangeConfigurationParams? parameters)
    {
        if (parameters?.Settings is not { } settings)
        {
            logger.Debug("[didChangeConfiguration] notification without a settings payload; ignored.");
            return;
        }

        if (!applyConfiguration(settings))
        {
            logger.Debug("[didChangeConfiguration] the editor intelligence settings are unchanged.");
            EmitConfiguration("unchanged");
            return;
        }

        logger.Info("Editor intelligence settings changed; requesting a CodeLens and inlay hint refresh.");
        EmitConfiguration("applied");
        requestRefresh();
    }

    private void EmitConfiguration(string outcome)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Lifecycle, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = "configuration.applied",
            Stage = "lifecycle",
            Kind = CompilerEventKind.Lifecycle,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P2,
            Outcome = outcome,
        });
    }
}

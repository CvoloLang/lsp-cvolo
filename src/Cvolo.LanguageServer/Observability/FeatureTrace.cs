using global::Cvolo.Observability.Abstractions;

namespace Cvolo.LanguageServer.Observability;

/// <summary>
/// A uniform trace for one LSP feature request. Every handler that answers a
/// request opens one of these so its path through validation, backend lookup,
/// freshness checking and response formatting stays explainable, and so the
/// feature is distinguishable from every other feature in the same trace.
///
/// The trace never records text, symbols or wire payloads: it records only the
/// feature name, the machine outcome, the machine reason and the operation ids
/// that link the stages together (§38, §39).
/// </summary>
internal sealed class FeatureTrace : IDisposable
{
    private const string RequestStartedEvent = "feature.request.started";
    private const string BackendStartedEvent = "feature.backend.started";
    private const string FreshnessCheckedEvent = "feature.freshness.checked";
    private const string MappingRejectedEvent = "feature.mapping.rejected";

    private readonly ICompilerObserver _observer;
    private readonly string _feature;
    private readonly IOperationScope _request;
    private readonly string? _requestUid;
    private readonly CancellationToken _cancellationToken;
    private bool _completed;

    private FeatureTrace(
        ICompilerObserver observer,
        string feature,
        IOperationScope request,
        string? requestUid,
        CancellationToken cancellationToken)
    {
        _observer = observer;
        _feature = feature;
        _request = request;
        _requestUid = requestUid;
        _cancellationToken = cancellationToken;
    }

    /// <summary>True when the observer accepts the feature family; false for the no-op scope.</summary>
    public bool IsEnabled => !ReferenceEquals(_request, NoopOperationScope.Instance);

    /// <summary>The request operation id, or an empty string when tracing is disabled.</summary>
    public string OperationId => _request.OperationId;

    public static FeatureTrace Start(ICompilerObserver observer, string feature, CancellationToken cancellationToken = default)
    {
        if (!observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return new FeatureTrace(observer, feature, NoopOperationScope.Instance, null, cancellationToken);

        var requestUid = RpcObservability.CurrentRequestUid;
        IOperationScope request = observer.BeginOperation(new OperationStart
        {
            Event = RequestStartedEvent,
            RequestUid = requestUid,
            Stage = feature,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            Attributes = [new KeyValuePair<string, string>("feature", feature)],
        });

        return new FeatureTrace(observer, feature, request, requestUid, cancellationToken);
    }

    /// <summary>Opens the backend lookup stage, linked to the request operation.</summary>
    public IOperationScope Backend()
    {
        if (!IsEnabled)
            return NoopOperationScope.Instance;

        return _observer.BeginOperation(new OperationStart
        {
            Event = BackendStartedEvent,
            RequestUid = _requestUid,
            Stage = _feature,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P1,
            ParentOperationId = OperationId,
            Attributes = [new KeyValuePair<string, string>("feature", _feature)],
        });
    }

    /// <summary>Closes the backend lookup stage.</summary>
    public void CompleteBackend(IOperationScope stage, string outcome, string? reason = null)
    {
        if (ReferenceEquals(stage, NoopOperationScope.Instance))
            return;

        stage.Complete(outcome, reason);
    }

    /// <summary>Records the freshness decision taken after the backend lookup.</summary>
    public void Freshness(bool fresh)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L1))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = FreshnessCheckedEvent,
            RequestUid = _requestUid,
            Stage = _feature,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L1,
            Priority = EventPriority.P2,
            Outcome = fresh ? "fresh" : "stale",
            OperationId = IsEnabled ? OperationId : null,
            Attributes = [new KeyValuePair<string, string>("feature", _feature)],
        });
    }

    /// <summary>Records that one candidate result could not be mapped onto the document.</summary>
    public void MappingRejected(string reason)
    {
        if (!_observer.IsEnabled(CompilerEventKind.Phase, DiagnosticDetail.L0))
            return;

        _observer.Emit(new CompilerEvent
        {
            Event = MappingRejectedEvent,
            RequestUid = _requestUid,
            Stage = _feature,
            Kind = CompilerEventKind.Phase,
            Detail = DiagnosticDetail.L0,
            Priority = EventPriority.P2,
            Outcome = "rejected",
            Reason = reason,
            OperationId = IsEnabled ? OperationId : null,
            Attributes = [new KeyValuePair<string, string>("feature", _feature)],
        });
    }

    /// <summary>Completes the request with its machine outcome and optional reason.</summary>
    public void Complete(string outcome, string? reason = null)
    {
        if (_completed || ReferenceEquals(_request, NoopOperationScope.Instance))
        {
            _completed = true;
            return;
        }

        _completed = true;
        _request.Complete(outcome, reason, attributes: [new KeyValuePair<string, string>("feature", _feature)]);
    }

    /// <summary>
    /// A handler that returns without completing its trace is a defect; the trace
    /// still terminates so the record is never silently truncated. A request that
    /// was cancelled while in flight is recorded as cancelled, not as a fault.
    /// </summary>
    public void Dispose()
    {
        if (_completed)
            return;

        Complete(
            _cancellationToken.IsCancellationRequested ? "cancelled" : "faulted",
            _cancellationToken.IsCancellationRequested ? "request_cancelled" : "scope_disposed_without_completion");
    }
}

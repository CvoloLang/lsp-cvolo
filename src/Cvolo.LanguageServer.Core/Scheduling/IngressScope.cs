using System.Runtime.CompilerServices;

namespace Cvolo.LanguageServer.Core.Scheduling;

/// <summary>
/// Flows the <see cref="IngressSequence"/> of the frame currently being handled
/// to the handler body. The transport stamps the value while dispatching a frame
/// (StreamJsonRpc invokes the handler continuation synchronously on the thread
/// that runs the dispatch callback) and clears it when the dispatch returns;
/// handlers read <see cref="Current"/> synchronously on entry, before any await.
/// </summary>
/// <remarks>
/// This is deliberately a <see cref="ThreadStaticAttribute"/> slot rather than an
/// <see cref="AsyncLocal{T}"/>: StreamJsonRpc's dispatch continuation does not
/// carry the caller's <see cref="ExecutionContext"/>, so an async-local value set
/// at admission is lost before the handler runs, whereas the thread-affine value
/// survives because the handler is invoked on the dispatcher's thread. Reading the
/// value after an await is therefore unsupported; capture it into a local first.
/// </remarks>
public static class IngressScope
{
    [ThreadStatic]
    private static IngressSequence _slot;

    /// <summary>The sequence of the frame being handled, or <see cref="IngressSequence.None"/>.</summary>
    public static IngressSequence Current
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _slot;
    }

    /// <summary>Sets the ambient sequence for the current thread.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Set(IngressSequence sequence) => _slot = sequence;

    /// <summary>Clears the ambient sequence.</summary>
    public static void Clear() => _slot = IngressSequence.None;
}

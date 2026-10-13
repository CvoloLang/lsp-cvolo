using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// The resource budget the editor owns for semantic highlighting (§16, §18). It bounds the size of a
/// document that is answered interactively; a document above the budget is refused with a typed
/// resource-limit error rather than answered with a misleading empty result. Changing it takes effect
/// for the next request, without restarting the session (§65).
/// </summary>
internal sealed record SemanticTokensSettings
{
    /// <summary>The recommended default budget, in UTF-16 code units.</summary>
    public const int DefaultMaxInteractiveTextLength = 500_000;

    /// <summary>The recommended default set.</summary>
    public static SemanticTokensSettings Default { get; } = new();

    /// <summary>
    /// The largest document, in characters, answered by a full interactive semantic-token request. A
    /// value that is absent, not an integer, or not positive is treated as absent, so a malformed
    /// payload cannot disable the guard or refuse every document.
    /// </summary>
    public int MaxInteractiveTextLength { get; init; } = DefaultMaxInteractiveTextLength;

    /// <summary>
    /// Reads the semantic-token settings out of a <c>workspace/didChangeConfiguration</c> payload,
    /// under the key <c>cvolo.semanticTokens.maxInteractiveLength</c>. Both shapes a client may send
    /// are accepted: the fully qualified dotted key and the nested <c>cvolo</c> section. A setting the
    /// client did not send keeps its current value: the server never resets a preference.
    /// </summary>
    public static SemanticTokensSettings FromConfiguration(JObject? settings, SemanticTokensSettings current)
    {
        if (settings is null)
        {
            return current;
        }

        int? requested = ConfigurationReader.AsInt(
            ConfigurationReader.ReadToken(settings, "semanticTokens", "maxInteractiveLength"));

        // Only a positive integer counts. Zero or negative would refuse every document, which is never
        // a budget a reader meant to set, so it is treated as absent rather than honoured.
        return requested is > 0 ? current with { MaxInteractiveTextLength = requested.Value } : current;
    }
}

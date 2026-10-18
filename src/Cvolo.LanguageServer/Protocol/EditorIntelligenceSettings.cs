using Cvolo.LanguageServer.Core.Backend;
using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Protocol;

/// <summary>
/// The presentation settings the editor owns (§64). They select which annotations are computed and
/// presented; they are never project semantics, so changing one takes effect through a
/// configuration notification and a refresh, without restarting the session (§65). A setting the
/// client did not send keeps its current value: the server never resets a preference the user has
/// already chosen.
/// </summary>
internal sealed record EditorIntelligenceSettings
{
    /// <summary>The recommended default set.</summary>
    public static EditorIntelligenceSettings Default { get; } = new();

    /// <summary>Which CodeLens categories are computed and presented.</summary>
    public BackendCodeLensOptions CodeLens { get; init; } = BackendCodeLensOptions.Default;

    /// <summary>Which inlay-hint categories are computed and presented.</summary>
    public BackendInlayHintOptions InlayHints { get; init; } = BackendInlayHintOptions.Default;

    /// <summary>How layout numbers are written in the annotations and the layout view.</summary>
    public BackendLayoutOptions Layout { get; init; } = BackendLayoutOptions.Default;

    /// <summary>
    /// Whether accepting a callable completion fills its parameter-name placeholders.
    /// Turning this off selects the compiler's plain insertion text (for example "Begin()")
    /// while leaving non-callable snippets untouched.
    /// </summary>
    public bool InsertArgumentPlaceholders { get; init; } = true;

    /// <summary>
    /// Reads the editor intelligence settings out of a <c>workspace/didChangeConfiguration</c>
    /// payload. Both shapes a client may send are accepted: the fully qualified dotted keys and the
    /// nested <c>cvolo</c> section. Anything absent or not a boolean is left as it is.
    /// </summary>
    public static EditorIntelligenceSettings FromConfiguration(JObject? settings, EditorIntelligenceSettings current)
    {
        if (settings is null)
        {
            return current;
        }

        return new EditorIntelligenceSettings
        {
            CodeLens = ApplyCodeLens(settings, current.CodeLens),
            InlayHints = ApplyInlayHints(settings, current.InlayHints),
            Layout = ApplyLayout(settings, current.Layout),
            InsertArgumentPlaceholders = ReadBool(settings, "completion", "insertArgumentPlaceholders")
                ?? current.InsertArgumentPlaceholders,
        };
    }

    /// <summary>
    /// The per-field lenses have their own master gate so a reader can choose references only, layout
    /// only, both or neither. The gate lives here, in presentation, rather than in a semantic query,
    /// so every combination is the same request with a different selection. The earlier
    /// <c>codeLens.members</c> key is still read as a synonym of <c>fieldReferences</c>, so a setting
    /// a reader already has keeps working.
    /// </summary>
    private static BackendCodeLensOptions ApplyCodeLens(JObject settings, BackendCodeLensOptions current)
    {
        bool? fields = ReadBool(settings, "codeLens", "fields");
        bool? fieldReferences = ReadBool(settings, "codeLens", "fieldReferences") ?? ReadBool(settings, "codeLens", "members");
        bool? fieldLayout = ReadBool(settings, "codeLens", "fieldLayout");
        bool enabled = fields ?? true;

        return new BackendCodeLensOptions
        {
            References = ReadBool(settings, "codeLens", "references") ?? current.References,
            Layout = ReadBool(settings, "codeLens", "layout") ?? current.Layout,
            Members = enabled && (fieldReferences ?? current.Members),
            FieldLayout = enabled && (fieldLayout ?? current.FieldLayout),
            NativeInterop = ReadBool(settings, "codeLens", "nativeInterop") ?? current.NativeInterop,
        };
    }

    private static BackendInlayHintOptions ApplyInlayHints(JObject settings, BackendInlayHintOptions current)
    {
        return new BackendInlayHintOptions
        {
            Types = ReadBool(settings, "inlayHints", "types") ?? current.Types,
            Parameters = ReadBool(settings, "inlayHints", "parameters") ?? current.Parameters,
            ReceiverMutability = ReadBool(settings, "inlayHints", "receiverMutability") ?? current.ReceiverMutability,
            Layout = ReadBool(settings, "inlayHints", "layout") ?? current.Layout,
            EnumValues = ReadBool(settings, "inlayHints", "enumValues") ?? current.EnumValues,
            GenericArguments = ReadBool(settings, "inlayHints", "genericArguments") ?? current.GenericArguments,
        };
    }

    private static BackendLayoutOptions ApplyLayout(JObject settings, BackendLayoutOptions current)
    {
        return new BackendLayoutOptions
        {
            OffsetFormat = ReadOffsetFormat(settings) ?? current.OffsetFormat,
            ShowPaddingPercentage = ReadBool(settings, "layout", "showPaddingPercentage") ?? current.ShowPaddingPercentage,
            AutoRefresh = ReadBool(settings, "layout", "autoRefresh") ?? current.AutoRefresh,
        };
    }

    /// <summary>
    /// Reads the offset notation. An unknown word is treated as absent rather than guessed, so a
    /// typo cannot silently select a notation the user did not choose.
    /// </summary>
    private static BackendOffsetFormat? ReadOffsetFormat(JObject settings)
    {
        JToken? token = ConfigurationReader.ReadToken(settings, "layout", "offsetFormat");
        if (token?.Type != JTokenType.String)
        {
            return null;
        }

        return token.Value<string>() switch
        {
            "decimal" => BackendOffsetFormat.Decimal,
            "hex" => BackendOffsetFormat.Hex,
            "decimalAndHex" => BackendOffsetFormat.DecimalAndHex,
            _ => null,
        };
    }

    /// <summary>
    /// Reads one boolean setting, trying the fully qualified key first, then the section object the
    /// client may already have nested under <c>cvolo</c>. Only a real boolean counts, so a value of
    /// the wrong type is treated as absent rather than as a preference.
    /// </summary>
    private static bool? ReadBool(JObject settings, string section, string name) =>
        ConfigurationReader.AsBool(ConfigurationReader.ReadToken(settings, section, name));
}

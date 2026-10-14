using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Protocol;
using Cvolo.LanguageServer.Tests.TestSupport;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Protocol;

/// <summary>
/// End-to-end semantic-token coverage against the real Cvolo-backed store: capability gating,
/// relative encoding, kind/modifier mapping and empty results.
/// </summary>
[Collection(ProtocolConcurrencyCollection.Name)]
public class SemanticTokensHandlerTests : IDisposable
{
    private static readonly string[] CanonicalTypes =
    [
        "namespace", "type", "struct", "enum", "interface", "typeParameter",
        "parameter", "variable", "property", "enumMember", "function", "method", "operator",
    ];

    private static readonly string[] CanonicalModifiers = ["declaration", "readonly", "static"];

    private readonly TestWorkspace _workspace;
    private readonly ProtocolSession _session;

    public SemanticTokensHandlerTests()
    {
        _workspace = TestWorkspace.CreateProject(["main.cvl"]);
        _session = ProtocolSession.Start();
    }

    public void Dispose()
    {
        _session.Dispose();
        _workspace.Dispose();
    }

    private static InitializeRequestParams Capabilities(bool semanticTokens = true, bool refresh = true, bool full = true, string[]? formats = null, string[]? modifiers = null)
    {
        return new InitializeRequestParams
        {
            Capabilities = new ClientCapabilitiesPayload
            {
                TextDocument = new TextDocumentClientCapabilitiesPayload
                {
                    SemanticTokens = semanticTokens
                        ? new SemanticTokensClientCapabilitiesPayload
                        {
                            Requests = new SemanticTokensRequestsPayload { Full = full },
                            TokenTypes = CanonicalTypes,
                            TokenModifiers = modifiers ?? CanonicalModifiers,
                            Formats = formats ?? ["relative"],
                            AugmentsSyntaxTokens = true,
                        }
                        : null,
                },
                Workspace = new WorkspaceClientCapabilitiesPayload
                {
                    SemanticTokens = new SemanticTokensWorkspaceClientCapabilitiesPayload { RefreshSupport = refresh },
                },
            },
        };
    }

    private async Task<InitializeResponse> InitializeAsync(InitializeRequestParams payload)
    {
        InitializeResponse response = await _session.Client.InitializeWithAsync(payload).WithTimeout("initialize");
        await _session.Client.NotifyInitializedAsync().WithTimeout("initialized");
        return response;
    }

    private async Task OpenAsync(string text)
    {
        await _session.Client.NotifyDidOpenAsync(_workspace.DocumentUri("main.cvl"), "cvolo", 1, text).WithTimeout("didOpen");
        var coreUri = DocumentUri.Create(_workspace.PathOf("main.cvl"));
        await _session.Client.WaitUntilAsync(() => _session.Server.Store.TryGet(coreUri, out _), "didOpen applied");
    }

    [Fact]
    public async Task Initialize_WithSemanticTokens_AdvertisesFullProviderWithLegend()
    {
        InitializeResponse response = await InitializeAsync(Capabilities());

        SemanticTokensOptions? provider = response.Capabilities.SemanticTokensProvider;
        Assert.NotNull(provider);
        Assert.True(provider!.Full);
        Assert.False(provider.Range);
        Assert.Equal(CanonicalTypes, provider.Legend!.TokenTypes);
        Assert.Equal(CanonicalModifiers, provider.Legend.TokenModifiers);
    }

    [Fact]
    public async Task Initialize_WithoutSemanticTokens_DoesNotAdvertiseProvider()
    {
        InitializeResponse response = await InitializeAsync(Capabilities(semanticTokens: false));

        Assert.Null(response.Capabilities.SemanticTokensProvider);
    }

    [Fact]
    public async Task Initialize_WithoutRelativeFormat_DoesNotAdvertiseProvider()
    {
        InitializeResponse response = await InitializeAsync(Capabilities(formats: ["token"]));

        Assert.Null(response.Capabilities.SemanticTokensProvider);
    }

    [Fact]
    public async Task Full_ReturnsRelativeEncodedTokens()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("int Twice(int value) { return value + value; }\nint main() { return Twice(1); }\n");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var data = result!["data"]!.Values<int>().ToArray();
        Assert.NotEmpty(data);
        Assert.Equal(0, data.Length % 5);

        // First token: line 0, char 4, length 5, type "function", modifier declaration (bit 0).
        Assert.Equal(0, data[0]);
        Assert.Equal(4, data[1]);
        Assert.Equal(5, data[2]);
        Assert.Equal(Array.IndexOf(CanonicalTypes, "function"), data[3]);
        Assert.Equal(1, data[4]);
    }

    [Fact]
    public async Task Full_EmptyDocument_ReturnsEmptyData()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        Assert.Empty(result!["data"]!.Values<int>());
    }

    [Fact]
    public async Task Full_UnopenedDocument_ReturnsNull()
    {
        await InitializeAsync(Capabilities());

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.Null(result);
    }

    [Fact]
    public async Task Full_ClassifiesCallWithMemberArgument()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("struct Point { int x; }\nint Twice(int value) { return value + value; }\nint main() {\n    val Point p = Point { x: 1 };\n    val int local = Twice(p.x);\n    return local;\n}\n");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var tokens = Decode(result!["data"]!.Values<int>().ToArray());

        // Line 4 (0-based) is "    val int local = Twice(p.x);": local(12), Twice(20), p(26), x(28).
        int functionType = Array.IndexOf(CanonicalTypes, "function");
        Assert.Contains(tokens, t => t.Line == 4 && t.Character == 20 && t.Length == 5 && t.Type == functionType);
        Assert.Contains(tokens, t => t.Line == 4 && t.Character == 12 && t.Length == 5); // local
    }

    [Fact]
    public async Task Full_ConstantModifier_IsEmittedWhenAdvertised()
    {
        await InitializeAsync(Capabilities(modifiers: ["declaration", "readonly", "static", "cvoloConstant"]));
        await OpenAsync("global int Seed = 1;\nint main() { return Seed; }\n");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var tokens = Decode(result!["data"]!.Values<int>().ToArray());

        const int constantBit = 1 << 3; // cvoloConstant position in the advertised legend.
        Assert.Contains(tokens, t => t.Line == 0 && t.Character == 11 && t.Length == 4 && (t.Modifiers & constantBit) == constantBit);
        Assert.Contains(tokens, t => t.Line == 1 && t.Character == 20 && t.Length == 4 && (t.Modifiers & constantBit) == constantBit);
    }

    [Fact]
    public async Task Full_ConstantModifier_IsNotEmittedWhenNotAdvertised()
    {
        await InitializeAsync(Capabilities());
        await OpenAsync("global int Seed = 1;\nint main() { return Seed; }\n");

        JObject? result = await _session.Client.SemanticTokensAsync(_workspace.DocumentUri("main.cvl")).WithTimeout("semanticTokens");

        Assert.NotNull(result);
        var tokens = Decode(result!["data"]!.Values<int>().ToArray());

        // The client did not advertise cvoloConstant, so no token may carry any bit beyond the three standard modifiers.
        Assert.All(tokens, t => Assert.True((t.Modifiers & ~0b111) == 0));
        Assert.Contains(tokens, t => t.Line == 1 && t.Character == 20 && t.Length == 4);
    }

    private static List<(int Line, int Character, int Length, int Type, int Modifiers)> Decode(int[] data)
    {
        var tokens = new List<(int, int, int, int, int)>();
        var line = 0;
        var character = 0;
        for (var i = 0; i + 4 < data.Length; i += 5)
        {
            line += data[i];
            character = data[i] == 0 ? character + data[i + 1] : data[i + 1];
            tokens.Add((line, character, data[i + 2], data[i + 3], data[i + 4]));
        }

        return tokens;
    }
}

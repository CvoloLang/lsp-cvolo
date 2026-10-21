using Cvolo.LanguageServer.Protocol;
using Microsoft.VisualStudio.LanguageServer.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;
using Xunit.Sdk;
using LspRange = Microsoft.VisualStudio.LanguageServer.Protocol.Range;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Client-side JSON-RPC endpoint used to drive the server in tests.
/// </summary>
internal sealed class TestClient : IDisposable
{
    private readonly HeaderDelimitedMessageHandler _handler;
    private readonly JsonRpc _rpc;
    private readonly RecordingStream _clientOutput;
    private bool _disposed;

    public TestClient(DuplexTestTransport transport)
    {
        _clientOutput = new RecordingStream(transport.ClientOutput);
        JsonMessageFormatter formatter = new();
        formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
        formatter.JsonSerializer.NullValueHandling = NullValueHandling.Ignore;
        _handler = new HeaderDelimitedMessageHandler(_clientOutput, transport.ClientInput, formatter);
        _rpc = new JsonRpc(_handler);
        _rpc.StartListening();
    }

    public async Task<InitializeResponse> InitializeAsync(int? processId = null, string? rootPath = null)
    {
        InitializeResponse? response = await _rpc
            .InvokeWithParameterObjectAsync<InitializeResponse>(
                Methods.InitializeName,
                new InitializeParams { ProcessId = processId, RootUri = rootPath is null ? null : new Uri(rootPath) })
            .ConfigureAwait(false);
        return response!;
    }

    /// <summary>
    /// Initializes with the server's own params shape, so tests can send
    /// <c>workspaceFolders</c> (absent from the protocol library's model).
    /// </summary>
    public Task<InitializeResponse> InitializeWithAsync(InitializeRequestParams payload)
    {
        return _rpc.InvokeWithParameterObjectAsync<InitializeResponse>(Methods.InitializeName, payload);
    }

    /// <summary>
    /// Initializes advertising the anchored-lens (CodeLens v3) experimental capability,
    /// so the server emits clickable <c>cvolo.executeAnchoredLens</c> commands.
    /// </summary>
    public Task<InitializeResponse> InitializeWithAnchoredLensAsync()
    {
        return InitializeWithAsync(new InitializeRequestParams
        {
            Capabilities = new ClientCapabilitiesPayload
            {
                Experimental = new JObject
                {
                    ["cvoloAnchoredLens"] = new JObject
                    {
                        ["version"] = 3,
                        ["validateAction"] = true,
                    },
                },
            },
        });
    }

    /// <summary>Invokes the anchored CodeLens v3 resolve command.</summary>
    public Task<AnchoredActionResultV3> ExecuteAnchoredLensAsync(JObject request)
    {
        return _rpc.InvokeWithParameterObjectAsync<AnchoredActionResultV3>(
            "workspace/executeCommand",
            new ExecuteCommandParamsPayload
            {
                Command = "cvolo.resolveAnchoredLens.v3",
                Arguments = new JArray(request),
            });
    }

    /// <summary>Invokes the anchored CodeLens v3 action-validation request (single-use token).</summary>
    public Task<AnchoredValidationResultV3> ValidateAnchoredActionAsync(string actionToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<AnchoredValidationResultV3>(
            "cvolo/validateAnchoredAction.v3",
            new AnchoredActionValidationV3(actionToken));
    }

    public Task<JObject> GetRecordingStatusAsync() =>
        _rpc.InvokeAsync<JObject>("cvolo/observability/status.v1");

    public Task<JObject> GetRecordingCheckpointAsync() =>
        _rpc.InvokeAsync<JObject>("cvolo/observability/checkpoint.v1");

    public Task<JObject> SetRecordingPolicyAsync(string mode, int protocolVersion = 1,
        bool captureSources = false, bool capturePaths = false, bool forensicScope = false) =>
        _rpc.InvokeWithParameterObjectAsync<JObject>(
            "cvolo/observability/setRecording.v1",
            new
            {
                protocolVersion,
                mode,
                captureSources,
                capturePaths,
                forensicScope,
            });

    public Task NotifyInitializedAsync()
    {
        return _rpc.NotifyAsync(Methods.InitializedName, new InitializedParams());
    }

    public Task NotifyDidOpenAsync(Uri uri, string languageId, int version, string text)
    {
        return _rpc.NotifyAsync(Methods.TextDocumentDidOpenName, new DidOpenTextDocumentParams
        {
            TextDocument = new TextDocumentItem { Uri = uri, LanguageId = languageId, Version = version, Text = text },
        });
    }

    public Task NotifyDidChangeAsync(Uri uri, int version, params TextDocumentContentChangeEvent[] changes)
    {
        return _rpc.NotifyAsync(Methods.TextDocumentDidChangeName, new DidChangeTextDocumentParams
        {
            TextDocument = new VersionedTextDocumentIdentifier { Uri = uri, Version = version },
            ContentChanges = changes,
        });
    }

    public Task NotifyDidCloseAsync(Uri uri)
    {
        return _rpc.NotifyAsync(Methods.TextDocumentDidCloseName, new DidCloseTextDocumentParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
        });
    }

    public Task<CompletionList?> CompletionAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<CompletionList?>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            });
    }

    public Task<CompletionList?> CompletionAsync(Uri uri, int line, int character, bool explicitInvocation)
    {
        return _rpc.InvokeWithParameterObjectAsync<CompletionList?>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
                Context = explicitInvocation
                    ? new CompletionContext { TriggerKind = CompletionTriggerKind.Invoked }
                    : null,
            });
    }

    public Task<CompletionList?> CompletionWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<CompletionList?>(
            Methods.TextDocumentCompletionName,
            new CompletionParams
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            },
            cancellationToken);
    }

    public Task<CompletionItem?> ResolveCompletionItemAsync(CompletionItem item)
    {
        return _rpc.InvokeWithParameterObjectAsync<CompletionItem?>(
            Methods.TextDocumentCompletionResolveName,
            item);
    }

    public Task<CompletionItem?> ResolveCompletionItemWithTokenAsync(CompletionItem item, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<CompletionItem?>(
            Methods.TextDocumentCompletionResolveName,
            item,
            cancellationToken);
    }

    public Task<CodeActionPayload[]?> CodeActionAsync(Uri uri, LspRange range, string[]? only = null)
    {
        return _rpc.InvokeWithParameterObjectAsync<CodeActionPayload[]?>(
            "textDocument/codeAction",
            new CodeActionParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Range = range,
                Context = only is null ? null : new CodeActionContextPayload { Only = only },
            });
    }

    public Task<Dictionary<string, object?>?> ResolveCodeActionAsync(Dictionary<string, object?> action)
    {
        return _rpc.InvokeWithParameterObjectAsync<Dictionary<string, object?>?>("codeAction/resolve", action);
    }

    public Task<SignatureHelp?> SignatureHelpAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<SignatureHelp?>(
            Methods.TextDocumentSignatureHelpName,
            PositionParams(uri, line, character));
    }

    public Task<SignatureHelp?> SignatureHelpWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<SignatureHelp?>(
            Methods.TextDocumentSignatureHelpName,
            PositionParams(uri, line, character),
            cancellationToken);
    }

    /// <summary>
    /// Round-trips an unknown method so any notification sent before it is
    /// guaranteed to have been dispatched by the server before this returns.
    /// </summary>
    public async Task DrainNotificationsAsync()
    {
        await ExpectErrorAsync("cvolo/unknownRequest", null).ConfigureAwait(false);
    }

    public bool HasSentCancelRequest()
    {
        foreach (string frame in FrameParser.ParseAll(_clientOutput.RecordedBytes))
        {
            if (frame.Contains("\"method\":\"$/cancelRequest\"", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public Task<object?> InvokeDelayedAsync(CancellationToken cancellationToken = default)
    {
        return _rpc.InvokeWithParameterObjectAsync<object?>("cvolo/delayed", null, cancellationToken);
    }

    public Task<Hover?> HoverAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<Hover?>(
            Methods.TextDocumentHoverName,
            PositionParams(uri, line, character));
    }

    public Task<Location[]?> DefinitionAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            Methods.TextDocumentDefinitionName,
            PositionParams(uri, line, character));
    }

    public Task<Location[]?> TypeDefinitionAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            Methods.TextDocumentTypeDefinitionName,
            PositionParams(uri, line, character));
    }

    public Task<Location[]?> ImplementationAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            Methods.TextDocumentImplementationName,
            PositionParams(uri, line, character));
    }

    public Task<Location[]?> ReferencesAsync(Uri uri, int line, int character, bool includeDeclaration = false)
    {
        return _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            "textDocument/references",
            new ReferenceParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
                Context = new ReferenceContextPayload { IncludeDeclaration = includeDeclaration },
            });
    }

    public Task<PrepareRenameResultPayload?> PrepareRenameAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<PrepareRenameResultPayload?>(
            "textDocument/prepareRename",
            new PrepareRenameParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            });
    }

    public Task<WorkspaceEditPayload> RenameAsync(Uri uri, int line, int character, string newName)
    {
        return _rpc.InvokeWithParameterObjectAsync<WorkspaceEditPayload>(
            "textDocument/rename",
            new RenameParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
                NewName = newName,
            });
    }

    public Task<JArray?> PrepareTypeHierarchyAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/prepareTypeHierarchy",
            PositionParams(uri, line, character));
    }

    public Task<JArray?> TypeHierarchySupertypesAsync(TypeHierarchyItemPayload item)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "typeHierarchy/supertypes",
            new TypeHierarchySupertypesParams { Item = item });
    }

    public Task<JArray?> TypeHierarchySubtypesAsync(TypeHierarchyItemPayload item)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "typeHierarchy/subtypes",
            new TypeHierarchySubtypesParams { Item = item });
    }

    public Task<JArray?> DocumentSymbolAsync(Uri uri)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            Methods.TextDocumentDocumentSymbolName,
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });
    }

    public Task<JObject?> SemanticTokensAsync(Uri uri)
    {
        return _rpc.InvokeWithParameterObjectAsync<JObject?>(
            "textDocument/semanticTokens/full",
            new global::Cvolo.LanguageServer.Protocol.SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } });
    }

    public Task<JObject?> SemanticTokensWithTokenAsync(Uri uri, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JObject?>(
            "textDocument/semanticTokens/full",
            new global::Cvolo.LanguageServer.Protocol.SemanticTokensParams { TextDocument = new TextDocumentIdentifier { Uri = uri } },
            cancellationToken);
    }

    public Task<JArray?> CodeLensAsync(Uri uri)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/codeLens",
            new CodeLensParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri } });
    }

    public Task<JArray?> CodeLensWithTokenAsync(Uri uri, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/codeLens",
            new CodeLensParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri } },
            cancellationToken);
    }

    public Task<JArray?> InlayHintsAsync(Uri uri, LspRange range)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/inlayHint",
            new InlayHintParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri }, Range = range });
    }

    public Task<JArray?> InlayHintsWithTokenAsync(Uri uri, LspRange range, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/inlayHint",
            new InlayHintParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri }, Range = range },
            cancellationToken);
    }

    public Task<JArray?> DocumentHighlightsAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/documentHighlight",
            new DocumentHighlightParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            });
    }

    public Task<JArray?> DocumentHighlightsWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/documentHighlight",
            new DocumentHighlightParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            },
            cancellationToken);
    }

    public Task<JArray?> FoldingRangesAsync(Uri uri)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/foldingRange",
            new FoldingRangeParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri } });
    }

    public Task<JArray?> FoldingRangesWithTokenAsync(Uri uri, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/foldingRange",
            new FoldingRangeParamsPayload { TextDocument = new TextDocumentIdentifier { Uri = uri } },
            cancellationToken);
    }

    public Task<JArray?> SelectionRangesAsync(Uri uri, params (int Line, int Character)[] positions)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/selectionRange",
            new SelectionRangeParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Positions = Array.ConvertAll(positions, p => new Position { Line = p.Line, Character = p.Character }),
            });
    }

    public Task<JArray?> SelectionRangesWithTokenAsync(Uri uri, CancellationToken cancellationToken, params (int Line, int Character)[] positions)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            "textDocument/selectionRange",
            new SelectionRangeParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Positions = Array.ConvertAll(positions, p => new Position { Line = p.Line, Character = p.Character }),
            },
            cancellationToken);
    }

    public Task<TypeLayoutResponse?> TypeLayoutAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<TypeLayoutResponse?>(
            "cvolo/typeLayout",
            new TypeLayoutParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            });
    }

    public Task<TypeLayoutResponse?> TypeLayoutWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<TypeLayoutResponse?>(
            "cvolo/typeLayout",
            new TypeLayoutParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            },
            cancellationToken);
    }

    /// <summary>
    /// The raw response of the layout request, for wire-shape assertions: every field name a client
    /// depends on is checked exactly as it travels (§22).
    /// </summary>
    public Task<JObject?> TypeLayoutJsonAsync(Uri uri, int line, int character)
    {
        return _rpc.InvokeWithParameterObjectAsync<JObject?>(
            "cvolo/typeLayout",
            new TypeLayoutParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Position = new Position { Line = line, Character = character },
            });
    }

    /// <summary>
    /// Re-asks for a layout by the subject the server handed out, which is how the view refreshes
    /// after the project changes (§28, §42).
    /// </summary>
    public Task<TypeLayoutResponse?> TypeLayoutBySubjectAsync(Uri uri, string subject)
    {
        return _rpc.InvokeWithParameterObjectAsync<TypeLayoutResponse?>(
            "cvolo/typeLayout",
            new TypeLayoutParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Subject = subject,
            });
    }

    public Task<JObject?> TypeLayoutBySubjectJsonAsync(Uri uri, string subject)
    {
        return _rpc.InvokeWithParameterObjectAsync<JObject?>(
            "cvolo/typeLayout",
            new TypeLayoutParamsPayload
            {
                TextDocument = new TextDocumentIdentifier { Uri = uri },
                Subject = subject,
            });
    }

    public Task NotifyDidChangeConfigurationAsync(JObject? settings)
    {
        return _rpc.NotifyAsync("workspace/didChangeConfiguration", new global::Cvolo.LanguageServer.Protocol.DidChangeConfigurationParams { Settings = settings });
    }

    public Task<Hover?> HoverWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<Hover?>(
            Methods.TextDocumentHoverName,
            PositionParams(uri, line, character),
            cancellationToken);
    }

    public Task<Location[]?> DefinitionWithTokenAsync(Uri uri, int line, int character, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<Location[]?>(
            Methods.TextDocumentDefinitionName,
            PositionParams(uri, line, character),
            cancellationToken);
    }

    public Task<JArray?> DocumentSymbolWithTokenAsync(Uri uri, CancellationToken cancellationToken)
    {
        return _rpc.InvokeWithParameterObjectAsync<JArray?>(
            Methods.TextDocumentDocumentSymbolName,
            new DocumentSymbolParams { TextDocument = new TextDocumentIdentifier { Uri = uri } },
            cancellationToken);
    }

    /// <summary>
    /// Initializes with the server's own params shape, including the client
    /// capabilities LSP-4 reads (hover content formats, hierarchical symbols).
    /// </summary>
    public Task<InitializeResponse> InitializeAsync(int? processId, string? rootPath, string[]? hoverContentFormat, bool hierarchicalSymbols)
    {
        return _rpc.InvokeWithParameterObjectAsync<InitializeResponse>(
            Methods.InitializeName,
            new InitializeRequestParams
            {
                ProcessId = processId,
                RootUri = rootPath is null ? null : new Uri(rootPath),
                Capabilities = new ClientCapabilitiesPayload
                {
                    TextDocument = new TextDocumentClientCapabilitiesPayload
                    {
                        Hover = hoverContentFormat is null
                            ? null
                            : new HoverClientCapabilitiesPayload { ContentFormat = hoverContentFormat },
                        DocumentSymbol = new DocumentSymbolClientCapabilitiesPayload
                        {
                            HierarchicalDocumentSymbolSupport = hierarchicalSymbols,
                        },
                    },
                },
            });
    }

    private static TextDocumentPositionParams PositionParams(Uri uri, int line, int character)
    {
        return new TextDocumentPositionParams
        {
            TextDocument = new TextDocumentIdentifier { Uri = uri },
            Position = new Position { Line = line, Character = character },
        };
    }

    public Task<object?> ShutdownAsync()
    {
        return _rpc.InvokeWithParameterObjectAsync<object?>(Methods.ShutdownName, null);
    }

    public Task NotifyExitAsync()
    {
        return _rpc.NotifyAsync(Methods.ExitName);
    }

    public Task NotifyUnknownAsync()
    {
        return _rpc.NotifyAsync("cvolo/unknownNotification", null);
    }

    public Task NotifySetTraceAsync(string value)
    {
        return _rpc.NotifyAsync("$/setTrace", new SetTraceParams(value));
    }

    /// <summary>Invokes a method expected to produce a JSON-RPC error.</summary>
    public async Task<RemoteRpcException> ExpectErrorAsync(string method, object? parameters)
    {
        try
        {
            await _rpc.InvokeWithParameterObjectAsync<object?>(method, parameters).ConfigureAwait(false);
        }
        catch (RemoteRpcException ex)
        {
            return ex;
        }

        throw new XunitException($"Expected a JSON-RPC error response from '{method}', but the call succeeded.");
    }

    public async Task<RemoteRpcException?> TryUnknownRequestAsync()
    {
        try
        {
            await _rpc.InvokeWithParameterObjectAsync<object?>("cvolo/unknownRequest", null).ConfigureAwait(false);
            return null;
        }
        catch (RemoteRpcException ex)
        {
            return ex;
        }
    }

    /// <summary>Writes a raw JSON body as a framed JSON-RPC message.</summary>
    public void SendRawJson(string json)
    {
        _clientOutput.Write(FrameParser.EncodeFrame(json));
    }

    /// <summary>Polls until a condition holds, because notifications dispatch asynchronously.</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string label, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new XunitException($"Timed out waiting for: {label}");
            }

            await Task.Delay(10);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rpc.Dispose();
        _handler.DisposeAsync().GetAwaiter().GetResult();
    }
}

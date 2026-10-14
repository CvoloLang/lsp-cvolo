using Cvolo.Compiler.Tooling;
using Cvolo.Compiler.Tooling.Completion;
using Cvolo.Compiler.Tooling.SignatureHelp;
using Cvolo.LanguageServer.Core.Backend;
using Cvolo.LanguageServer.Core.Completion;
using Cvolo.LanguageServer.Core.Diagnostics;
using Cvolo.LanguageServer.Core.Documents;
using Cvolo.LanguageServer.Core.Instrumentation;
using Cvolo.LanguageServer.Core.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using CoreTextSpan = Cvolo.LanguageServer.Core.Diagnostics.TextSpan;
using ToolingTextSpan = Cvolo.Compiler.Tooling.TextSpan;

namespace Cvolo.LanguageServer.Cvolo;

/// <summary>
/// Adapts the Cvolo compiler tooling to the core backend boundary. Owns the
/// Cvolo workspace, per-project sessions (one immutable ProjectSnapshot per
/// project) and the serialization gate that keeps snapshot advancement safe
/// across concurrent requests for the same project.
/// </summary>
internal sealed partial class CvoloLanguageBackend(
    IReadOnlyList<string> workspaceFolders,
    string? fallbackRoot,
    ICoreLogger? logger = null,
    IReadOnlyList<string>? libraryPaths = null,
    ServerMetrics? metrics = null) : ILanguageBackend
{
    private readonly ICoreLogger _logger = logger ?? new NullCoreLogger();
    private readonly string? _fallbackRoot = fallbackRoot;
    private readonly IReadOnlyList<string> _libraryPaths = libraryPaths ?? Array.Empty<string>();
    private readonly ServerMetrics? _metrics = metrics;
    private readonly ConcurrentDictionary<string, CvoloProjectSession> _sessions = new();

    private void LogTiming(string operation, Stopwatch stopwatch, string detail, int thresholdMs = 50)
    {
        if (stopwatch.ElapsedMilliseconds >= thresholdMs)
            _logger.Write(CoreLogLevel.Debug, $"[perf] {operation} took {stopwatch.ElapsedMilliseconds} ms: {detail}");
        else
            _logger.Write(CoreLogLevel.Debug, $"[perf] {operation} took {stopwatch.ElapsedMilliseconds} ms: {detail}");
    }

    public BackendProject? OpenProject(DocumentUri document)
    {
        var stopwatch = Stopwatch.StartNew();
        var boundary = SelectBoundary(document.LocalPath);
        ProjectDiscoveryResult discovery = ProjectDiscovery.FindProject(document.LocalPath, boundary);

        if (discovery.Status is ProjectDiscoveryStatus.NoProject or ProjectDiscoveryStatus.LooseWorkspace
            && TryFindOwningSession(document.LocalPath, out var owner))
        {
            return owner;
        }

        if (discovery.Status == ProjectDiscoveryStatus.AmbiguousProject)
        {
            _logger.Write(CoreLogLevel.Warning, $"Ambiguous project for '{document}': '{discovery.ProjectDirectory}' contains more than one .cvlproj.");
            return null;
        }

        if (discovery.ProjectDirectory is null)
        {
            _logger.Write(CoreLogLevel.Warning, $"No filesystem root could be selected for '{document}'.");
            return null;
        }

        var root = Path.GetFullPath(discovery.ProjectDirectory);
        var sessionKey = discovery.Status == ProjectDiscoveryStatus.LooseWorkspace
            ? "loose:" + root
            : "project:" + root;

        try
        {
            CvoloProjectSession projectSession = _sessions.GetOrAdd(
                sessionKey,
                _ => CreateSession(root, discovery.Status == ProjectDiscoveryStatus.LooseWorkspace));

            CvoloProjectSession result = CreatePackageSession(projectSession, document.LocalPath) ?? projectSession;
            LogTiming("OpenProject", stopwatch, $"document='{document.LocalPath}', root='{root}', status={discovery.Status}, documents={result.Current.Documents.Count}, generation={result.Generation}", 100);
            return result;
        }
        catch (Exception ex)
        {
            var mode = discovery.Status == ProjectDiscoveryStatus.LooseWorkspace ? "loose workspace" : "Cvolo project";
            _logger.Write(CoreLogLevel.Error, $"Opening {mode} '{root}' failed: {ex.Message}");
            return null;
        }
    }

    private CvoloProjectSession? CreatePackageSession(CvoloProjectSession owner, string filePath)
    {
        if (!owner.Project.InitialSnapshot.TryGetPackageSource(filePath, out PackageSourceDocument source))
        {
            return null;
        }

        var packageKey = "package:" + owner.Project.ProjectPath + ":" + source.PackageId + ":" + source.Version;

        return _sessions.GetOrAdd(packageKey, _ => BuildPackageSession(owner, source));
    }

    private CvoloProjectSession BuildPackageSession(CvoloProjectSession owner, PackageSourceDocument source)
    {
        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<PackageSourceDocument> sources = owner.Project.InitialSnapshot.GetPackageSources(source.PackageId, source.Version);
        var workspace = CvoloWorkspace.Create();
        var project = workspace.OpenPackageSource(owner.Project.ProjectPath, sources);
        _logger.Write(
            CoreLogLevel.Info,
            $"Opened package source session '{source.PackageId}@{source.Version}' for '{owner.Project.ProjectPath}'.");
        LogTiming("BuildPackageSession", stopwatch, $"package='{source.PackageId}@{source.Version}', sources={sources.Count}, owner='{owner.Project.ProjectPath}'", 100);
        return new CvoloProjectSession(workspace, project, isPackageSource: true)
        {
            Analysis = new GenerationAnalysisRegistry(_metrics),
        };
    }

    private bool TryFindOwningSession(string path, out CvoloProjectSession session)
    {
        foreach (var candidate in _sessions.Values)
        {
            if (candidate.Project.TryGetDocumentId(path, out _))
            {
                session = candidate;
                return true;
            }
        }

        session = null!;
        return false;
    }

    public bool TryResolveDocument(BackendProject project, DocumentUri document, out BackendDocumentHandle handle)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = (CvoloProjectSession)project;
        if (session.Project.TryGetDocumentId(document.LocalPath, out DocumentId documentId))
        {
            handle = new CvoloDocumentHandle(documentId);
            LogTiming("TryResolveDocument", stopwatch, $"document='{document.LocalPath}', project='{session.Project.ProjectPath}', refreshed=false", 50);
            return true;
        }

        if (IsUnderProject(session.Project.ProjectPath, document.LocalPath)
            && TryRefreshSession(session)
            && session.Project.TryGetDocumentId(document.LocalPath, out documentId))
        {
            handle = new CvoloDocumentHandle(documentId);
            LogTiming("TryResolveDocument", stopwatch, $"document='{document.LocalPath}', project='{session.Project.ProjectPath}', refreshed=true", 50);
            return true;
        }

        _logger.Write(CoreLogLevel.Error, $"Document '{document}' is not part of project '{session.Project.ProjectPath}'.");
        handle = null!;
        return false;
    }

    private bool TryRefreshSession(CvoloProjectSession session)
    {
        // A package-source session deliberately does not re-discover documents: its universe is the
        // extracted package, not the consuming project under the same path (see IsPackageSource).
        if (session.IsPackageSource)
            return false;

        lock (session.Gate)
        {
            var stopwatch = Stopwatch.StartNew();
            var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            var overrides = new Dictionary<string, string>(comparer);
            foreach (DocumentSnapshot document in session.Current.Documents.Values)
                overrides[document.FilePath] = document.Text.ToString();

            try
            {
                CvoloProject advanced = session.Project.Advance(overrides);
                session.SetProject(advanced);
                session.Advance(advanced.InitialSnapshot);
                LogTiming("TryRefreshSession", stopwatch, $"project='{session.Project.ProjectPath}', overrides={overrides.Count}, documents={session.Current.Documents.Count}, generation={session.Generation}", 100);
                return true;
            }
            catch (Exception ex)
            {
                _logger.Write(CoreLogLevel.Debug, $"Project refresh for a newly added file failed: {ex.Message}");
                return false;
            }
        }
    }

    private static bool IsUnderProject(string projectPath, string filePath)
    {
        var root = Directory.Exists(projectPath) ? projectPath : Path.GetDirectoryName(projectPath);
        if (string.IsNullOrEmpty(root))
            return false;

        var fullRoot = Path.GetFullPath(root);
        var fullFile = Path.GetFullPath(filePath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (fullFile.Equals(fullRoot, comparison))
            return true;

        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullFile.StartsWith(prefix, comparison);
    }

    public BackendSnapshot UpdateDocument(BackendProject project, BackendDocumentHandle handle, string text)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;

        lock (session.Gate)
        {
            ProjectSnapshot next = AdvanceSnapshot(session, document.DocumentId, text);
            LogTiming("UpdateDocument", stopwatch, $"project='{session.Project.ProjectPath}', documentId={document.DocumentId}, textLength={text.Length}, generation={session.Generation}", 50);
            return new ToolingBackendSnapshot(next, session.Generation);
        }
    }

    public (BackendSnapshot snapshot, long proposedGeneration) StageDocumentUpdate(BackendProject project, BackendDocumentHandle handle, string text)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;

        lock (session.Gate)
        {
            long proposed = session.Generation + 1;
            ProjectSnapshot next = ComputeSnapshot(session, document.DocumentId, text);
            LogTiming("StageDocumentUpdate", stopwatch, $"project='{session.Project.ProjectPath}', documentId={document.DocumentId}, textLength={text.Length}, proposed={proposed}", 50);
            return (new ToolingBackendSnapshot(next, proposed), proposed);
        }
    }

    public void CommitDocumentUpdate(BackendProject project, BackendDocumentHandle handle, BackendSnapshot snapshot, long generationCommitted)
    {
        var stopwatch = Stopwatch.StartNew();
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;
        var toolingSnapshot = (ToolingBackendSnapshot)snapshot;

        lock (session.Gate)
        {
            if (generationCommitted <= session.Generation)
            {
                // Stale commit - ignore. Generation must be strictly increasing.
                LogTiming("CommitDocumentUpdate(stale)", stopwatch, $"project='{session.Project.ProjectPath}', documentId={document.DocumentId}, committed={generationCommitted}, current={session.Generation}", 50);
                return;
            }
            session.CommitStaged(toolingSnapshot.Snapshot, generationCommitted);
            LogTiming("CommitDocumentUpdate", stopwatch, $"project='{session.Project.ProjectPath}', documentId={document.DocumentId}, generation={generationCommitted}", 50);
        }
    }

    public BackendSnapshot RestoreBaseline(BackendProject project, BackendDocumentHandle handle)
    {
        var session = (CvoloProjectSession)project;
        var document = (CvoloDocumentHandle)handle;

        lock (session.Gate)
        {
            // The disk baseline can change while a document is open (save, rename edits applied by
            // the client, external tools). Read it at close so removing the editor overlay never
            // resurrects the project text captured when the language server first opened it.
            SourceText baseline = session.ReadDiskBaseline(document.DocumentId);
            ProjectSnapshot next = AdvanceSnapshot(session, document.DocumentId, baseline.ToString());
            return new ToolingBackendSnapshot(next, session.Generation);
        }
    }

    private ProjectSnapshot AdvanceSnapshot(CvoloProjectSession session, DocumentId documentId, string text)
    {
        ProjectSnapshot next = ComputeSnapshot(session, documentId, text);
        session.Advance(next);
        return next;
    }

    private ProjectSnapshot ComputeSnapshot(CvoloProjectSession session, DocumentId documentId, string text)
    {
        if (!session.Current.TryGetDocument(documentId, out DocumentSnapshot? edited))
            return session.Current;

        // WithDocument is the cheap per-keystroke path: keep all overlays and reuse
        // the existing project universe. Imports are an exception because they can
        // grow or shrink the library source closure.
        if (!session.IsPackageSource && ImportsChanged(edited.Text.ToString(), text))
            return ComputeImportClosure(session, documentId, edited.FilePath, text);

        return session.Current.WithDocument(documentId, SourceText.From(text));
    }

    private ProjectSnapshot ComputeImportClosure(CvoloProjectSession session, DocumentId documentId, string filePath, string text)
    {
        var stopwatch = Stopwatch.StartNew();
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var overrides = new Dictionary<string, string>(comparer);
        foreach (DocumentSnapshot document in session.Current.Documents.Values)
            overrides[document.FilePath] = document.Text.ToString();
        overrides[filePath] = text;

        // STAGE S1/S2 (Incremental Semantic Snapshots): capture the semantic input
        // universe through a recording reader and rebuild a generation-owned snapshot
        // from the captured bytes. The rebuild performs zero disk I/O and produces a
        // fresh syntax/context tree, so an imported-file change never aliases the
        // previous generation's analysis.
        if (TryComputeImportClosureFromCapture(session, overrides, out ProjectSnapshot? capturedNext))
        {
            LogTiming("ComputeImportClosure(capture)", stopwatch, $"file='{filePath}', overrides={overrides.Count}, documents={capturedNext!.Documents.Count}", 100);
            return capturedNext;
        }

        try
        {
            CvoloProject advanced = session.Project.Advance(overrides);
            session.SetProject(advanced);
            LogTiming("ComputeImportClosure", stopwatch, $"file='{filePath}', overrides={overrides.Count}, documents={advanced.InitialSnapshot.Documents.Count}", 100);
            return advanced.InitialSnapshot;
        }
        catch (Exception ex)
        {
            _logger.Write(CoreLogLevel.Warning, $"Import closure refresh failed for '{filePath}': {ex.Message}");
            return session.Current.WithDocument(documentId, SourceText.From(text));
        }
    }

    private ProjectSnapshot AdvanceImportClosure(CvoloProjectSession session, DocumentId documentId, string filePath, string text)
    {
        ProjectSnapshot next = ComputeImportClosure(session, documentId, filePath, text);
        session.Advance(next);
        return next;
    }

    private bool TryComputeImportClosureFromCapture(
        CvoloProjectSession session,
        IReadOnlyDictionary<string, string> overrides,
        out ProjectSnapshot? next)
    {
        next = null;
        try
        {
            var toolingAssembly = session.Workspace.GetType().Assembly;
            Type? readerType = toolingAssembly.GetType("Cvolo.Compiler.Tooling.RecordingCompilerInputReader");
            Type? fileSystemReaderType = toolingAssembly.GetType("Cvolo.Compiler.Tooling.FileSystemCompilerInputReader");
            Type? requestType = toolingAssembly.GetType("Cvolo.Compiler.Tooling.ToolingCaptureRequest");
            var captureMethod = session.Workspace.GetType().GetMethod("CaptureUniverseV1");
            var buildMethod = session.Workspace.GetType().GetMethod("BuildSnapshotFromCaptureV1");
            if (readerType is null || fileSystemReaderType is null || requestType is null || captureMethod is null || buildMethod is null)
                return false;

            object? fileSystemReader = fileSystemReaderType.GetProperty("Instance")?.GetValue(null);
            if (fileSystemReader is null)
                return false;

            object? reader = Activator.CreateInstance(readerType, fileSystemReader);
            object? request = Activator.CreateInstance(requestType, session.Project.ProjectPath, overrides, _libraryPaths, null, false);
            if (reader is null || request is null)
                return false;

            object? capture = captureMethod.Invoke(session.Workspace, new[] { request, reader });
            if (capture?.GetType().Name != "Captured")
                return false;

            object? frozenCapture = capture.GetType().GetProperty("Capture")?.GetValue(capture);
            if (frozenCapture is null)
                return false;

            object? build = buildMethod.Invoke(session.Workspace, new[] { frozenCapture, session.Project });
            if (build?.GetType().Name != "Ready")
                return false;

            if (build.GetType().GetProperty("Project")?.GetValue(build) is not CvoloProject readyProject ||
                build.GetType().GetProperty("Snapshot")?.GetValue(build) is not ProjectSnapshot readySnapshot)
                return false;

            session.SetProject(readyProject);
            next = readySnapshot;
            return true;
        }
        catch (Exception ex)
        {
            // Capture/build is an optimization over the in-process advance; any failure
            // falls back to the legacy path rather than dropping the edit.
            _logger.Write(CoreLogLevel.Debug, $"Capture-based import closure refresh unavailable: {ex.Message}");
            return false;
        }
    }

    private bool TryAdvanceImportClosureFromCapture(CvoloProjectSession session, IReadOnlyDictionary<string, string> overrides, out ProjectSnapshot? next)
    {
        if (TryComputeImportClosureFromCapture(session, overrides, out next))
        {
            session.Advance(next!);
            return true;
        }
        return false;
    }

    private static bool ImportsChanged(string before, string after)
    {
        static string[] Extract(string source) => System.Text.RegularExpressions.Regex
            .Matches(source, @"(?m)^[ \t]*using[ \t]+[^;\r\n]*(?:;|$)")
            .Select(match => match.Value.Trim())
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();

        return !Extract(before).SequenceEqual(Extract(after), StringComparer.Ordinal);
    }

    public BackendSnapshot CaptureCurrentSnapshot(BackendProject project)
    {
        var session = (CvoloProjectSession)project;
        lock (session.Gate)
        {
            return new ToolingBackendSnapshot(session.Current, session.Generation);
        }
    }

    public BackendSnapshot SynchronizeClosedDocuments(BackendProject project, IReadOnlyList<BackendDocumentHandle> openDocuments)
    {
        var session = (CvoloProjectSession)project;
        var openIds = openDocuments
            .OfType<CvoloDocumentHandle>()
            .Select(handle => handle.DocumentId)
            .ToHashSet();

        lock (session.Gate)
        {
            ProjectSnapshot next = session.Current;
            var changed = false;

            foreach (DocumentId documentId in next.DocumentIds)
            {
                if (openIds.Contains(documentId))
                    continue;

                DocumentSnapshot document = next.GetDocument(documentId);
                if (!File.Exists(document.FilePath))
                    continue;

                string diskText;
                try
                {
                    diskText = File.ReadAllText(document.FilePath);
                }
                catch (IOException ex)
                {
                    _logger.Write(CoreLogLevel.Debug, $"Closed-document refresh skipped for '{document.FilePath}': {ex.Message}");
                    continue;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.Write(CoreLogLevel.Debug, $"Closed-document refresh skipped for '{document.FilePath}': {ex.Message}");
                    continue;
                }

                session.SetDiskBaseline(documentId, SourceText.From(diskText));
                if (string.Equals(document.Text.ToString(), diskText, StringComparison.Ordinal))
                    continue;

                next = next.WithDocument(documentId, SourceText.From(diskText));
                changed = true;
            }

            if (changed)
                session.Advance(next);

            return new ToolingBackendSnapshot(session.Current, session.Generation);
        }
    }

    public bool IsCurrentSnapshot(BackendProject project, BackendSnapshot snapshot)
    {
        var session = (CvoloProjectSession)project;
        var toolingSnapshot = (ToolingBackendSnapshot)snapshot;
        lock (session.Gate)
        {
            return session.Generation == toolingSnapshot.Generation;
        }
    }

    public bool IsAnalysisReady(BackendProject project)
    {
        var session = (CvoloProjectSession)project;
        long generation;
        lock (session.Gate)
        {
            generation = session.Generation;
        }

        return session.Analysis?.IsReady(generation) ?? true;
    }

    public BackendDiagnosticRun GetDiagnostics(BackendSnapshot snapshot, IReadOnlyList<BackendDocumentHandle> targets)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var texts = new Dictionary<DocumentUri, string>();
        var diagnostics = new List<BackendDiagnostic>();

        foreach (BackendDocumentHandle handle in targets)
        {
            var documentStopwatch = Stopwatch.StartNew();
            var documentId = ((CvoloDocumentHandle)handle).DocumentId;
            // A target captured against an earlier snapshot can be gone by the time the run executes
            // (the document closed, or a session was rebuilt). One stale target must not fail the
            // whole diagnostic run, so it is skipped rather than dereferenced.
            if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? document))
            {
                _logger.Write(CoreLogLevel.Debug, "Skipped a diagnostic target that is not present in the captured snapshot.");
                continue;
            }

            DocumentUri documentUri = ToDocumentUri(document.FilePath);
            texts[documentUri] = document.Text.ToString();

            foreach (Diagnostic diagnostic in document.GetDiagnostics())
            {
                if (!TryMapLocation(toolingSnapshot, diagnostic.Location, texts, out BackendDiagnosticLocation? location))
                {
                    _logger.Write(CoreLogLevel.Warning, $"Skipping diagnostic '{diagnostic.Id}' with an unresolved primary location in '{document.FilePath}'.");
                    continue;
                }

                var related = new List<BackendDiagnosticLocation>(diagnostic.RelatedLocations.Count);
                foreach (DiagnosticLocation relatedLocation in diagnostic.RelatedLocations)
                {
                    if (TryMapLocation(toolingSnapshot, relatedLocation, texts, out BackendDiagnosticLocation? mapped))
                    {
                        related.Add(mapped);
                    }
                    else
                    {
                        _logger.Write(CoreLogLevel.Debug, $"Skipping unresolved related location of diagnostic '{diagnostic.Id}'.");
                    }
                }

                diagnostics.Add(new BackendDiagnostic(
                    MapSeverity(diagnostic.Severity),
                    diagnostic.Id,
                    diagnostic.Message,
                    location,
                    related));
            }

            LogTiming("GetDiagnostics(document)", documentStopwatch, $"file='{document.FilePath}', diagnosticsSoFar={diagnostics.Count}, textLength={document.Text.Length}", 50);
        }

        LogTiming("GetDiagnostics", stopwatch, $"targets={targets.Count}, texts={texts.Count}, diagnostics={diagnostics.Count}", 100);
        return new BackendDiagnosticRun(texts, diagnostics);
    }

    private static bool TryMapLocation(
        ProjectSnapshot snapshot,
        DiagnosticLocation location,
        Dictionary<DocumentUri, string> texts,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out BackendDiagnosticLocation? mapped)
    {
        if (snapshot.TryGetDocument(location.DocumentId, out DocumentSnapshot? document)
            && DocumentUri.TryCreate(document.FilePath, out DocumentUri uri))
        {
            texts.TryAdd(uri, document.Text.ToString());
            mapped = new BackendDiagnosticLocation(
                uri,
                new CoreTextSpan(location.Span.Start, location.Span.Length),
                location.Message);
            return true;
        }

        mapped = null!;
        return false;
    }

    private static DocumentUri ToDocumentUri(string filePath)
    {
        return DocumentUri.TryCreate(filePath, out DocumentUri uri) ? uri : DocumentUri.Create(filePath);
    }

    private static BackendDiagnosticSeverity MapSeverity(DiagnosticSeverity severity)
    {
        return severity switch
        {
            DiagnosticSeverity.Error => BackendDiagnosticSeverity.Error,
            DiagnosticSeverity.Warning => BackendDiagnosticSeverity.Warning,
            DiagnosticSeverity.Info => BackendDiagnosticSeverity.Info,
            DiagnosticSeverity.Hint => BackendDiagnosticSeverity.Hint,
            _ => BackendDiagnosticSeverity.Warning,
        };
    }

    private CvoloProjectSession CreateSession(string semanticRoot, bool looseWorkspace)
    {
        var stopwatch = Stopwatch.StartNew();
        // The workspace builds the same semantic universe as the compiler (project sources,
        // standard library, ProjectReference sources and package/.cvlib API units).
        var workspace = CvoloWorkspace.Create();
        var project = looseWorkspace
            ? workspace.OpenProject(semanticRoot, _libraryPaths)
            : workspace.OpenProject(semanticRoot);
        _logger.Write(
            CoreLogLevel.Info,
            looseWorkspace
                ? $"Opened loose Cvolo workspace '{semanticRoot}' with {_libraryPaths.Count} configured library path(s)."
                : $"Opened Cvolo project '{semanticRoot}' from .cvlproj package/project metadata.");
        LogTiming("CreateSession", stopwatch, $"root='{semanticRoot}', loose={looseWorkspace}, documents={project.InitialSnapshot.Documents.Count}", 100);
        return new CvoloProjectSession(workspace, project)
        {
            Analysis = new GenerationAnalysisRegistry(_metrics),
        };
    }

    /// <summary>
    /// The most specific workspace folder containing the document wins. A
    /// document outside every folder falls back to the single fallback root
    /// (rootUri, then rootPath); only when neither exists is discovery
    /// unbounded.
    /// </summary>
    private string? SelectBoundary(string documentPath)
    {
        string? best = null;
        foreach (var folder in workspaceFolders)
        {
            if (folder is null || !IsContaining(folder, documentPath))
            {
                continue;
            }

            if (best is null || folder.Length > best.Length)
            {
                best = folder;
            }
        }

        return best ?? _fallbackRoot;
    }

    private static bool IsContaining(string root, string documentPath)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(root);
        if (string.Equals(normalizedRoot, documentPath, comparison))
        {
            return true;
        }

        return documentPath.Length > normalizedRoot.Length
            && documentPath.StartsWith(normalizedRoot, comparison)
            && (documentPath[normalizedRoot.Length] == Path.DirectorySeparatorChar || documentPath[normalizedRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    public BackendCompletionResult GetCompletions(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            // A document that is not part of the supplied snapshot is stale relative to it (a closed
            // file, or a rebuilt session), not an error. Answer with no items rather than failing the
            // request, so a stale handle never surfaces as a warning in the client log (§20).
            return new BackendCompletionResult(default, []);
        }

        int textLength = toolingDocument.Text.Length;
        if (position < 0 || position > textLength)
        {
            // Never clamp or invent a replacement span for an impossible offset.
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The completion position is outside the captured document text.");
        }

        CompletionResult result = toolingDocument.GetCompletions(position);
        LogTiming("GetCompletions", stopwatch, $"file='{toolingDocument.FilePath}', position={position}, candidates={result.Candidates.Count}, textLength={textLength}", 50);
        var toolingSpan = result.ReplacementRange;

        if (!IsValidReplacementSpan(toolingSpan.Start, toolingSpan.Length, position, textLength))
        {
            // Reject malformed Tooling output rather than repairing it: a bad
            // span must never become an unsafe TextEdit (§14, §17).
            throw new InvalidOperationException(
                $"The completion result returned an invalid replacement span " +
                $"(start={toolingSpan.Start}, length={toolingSpan.Length}) for position {position} over {textLength} code unit(s).");
        }

        var items = new List<BackendCompletionItem>(result.Candidates.Count);
        foreach (CompletionCandidate candidate in result.Candidates)
        {
            items.Add(new BackendCompletionItem(
                candidate.Label,
                candidate.PlainInsertText,
                MapCompletionKind(candidate.Kind),
                candidate.Detail,
                MapInsertionPlan(candidate.InsertionPlan),
                candidate.ItemId is { } itemId ? new CvoloCompletionResolveHandle(toolingSnapshot, itemId) : null,
                MapResolvableFields(candidate.ResolvableFields)));
        }

        return new BackendCompletionResult(
            new CoreTextSpan(toolingSpan.Start, toolingSpan.Length),
            items);
    }

    private static BackendCompletionInsertionPlan? MapInsertionPlan(CompletionInsertionPlan? plan)
    {
        if (plan is null)
            return null;

        var segments = new List<BackendCompletionInsertSegment>(plan.SnippetSegments.Count);
        foreach (CompletionInsertSegment segment in plan.SnippetSegments)
        {
            switch (segment)
            {
                case CompletionLiteral literal:
                    segments.Add(new BackendCompletionLiteral(literal.Text));
                    break;
                case CompletionPlaceholder placeholder:
                    segments.Add(new BackendCompletionPlaceholder(placeholder.DefaultText));
                    break;
                case CompletionFinalCursor:
                    segments.Add(new BackendCompletionFinalCursor());
                    break;
                default:
                    throw new InvalidOperationException($"Unknown completion insertion segment '{segment.GetType().Name}'.");
            }
        }

        return new BackendCompletionInsertionPlan(segments);
    }

    private static BackendCompletionResolvableFields MapResolvableFields(CompletionResolvableFields fields)
    {
        var result = BackendCompletionResolvableFields.None;
        if (fields.HasFlag(CompletionResolvableFields.Detail))
            result |= BackendCompletionResolvableFields.Detail;
        if (fields.HasFlag(CompletionResolvableFields.Documentation))
            result |= BackendCompletionResolvableFields.Documentation;
        return result;
    }

    public BackendCompletionResolvedInfo? ResolveCompletion(BackendSnapshot snapshot, BackendCompletionResolveHandle handle)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (handle is not CvoloCompletionResolveHandle completionHandle || !ReferenceEquals(completionHandle.Snapshot, toolingSnapshot))
        {
            // A foreign or mismatched completion handle must never resolve against the wrong
            // snapshot; return no result rather than an unrelated item (§28, §30).
            return null;
        }

        CompletionResolvedInfo? resolved = toolingSnapshot.ResolveCompletion(completionHandle.ItemId);
        if (resolved is null)
            return null;

        return new BackendCompletionResolvedInfo(resolved.Detail, resolved.Documentation);
    }

    public BackendSignatureHelpResult? GetSignatureHelp(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            return null;

        if (position < 0 || position > toolingDocument.Text.Length)
            throw new ArgumentOutOfRangeException(nameof(position), position, "The signature-help position is outside the captured document text.");

        SignatureHelpInfo? result = toolingDocument.GetSignatureHelp(position);
        LogTiming("GetSignatureHelp", stopwatch, $"file='{toolingDocument.FilePath}', position={position}, signatures={result?.Signatures.Count ?? 0}, textLength={toolingDocument.Text.Length}", 50);
        if (result is null)
            return null;

        var signatures = result.Signatures
            .Select(signature => new BackendSignatureCandidate(
                signature.Label,
                signature.Documentation,
                signature.Parameters.Select(parameter => new BackendSignatureParameter(
                    new BackendSignatureLabelSpan(parameter.LabelSpan.Start, parameter.LabelSpan.Length),
                    parameter.Documentation)).ToArray(),
                signature.ActiveParameter))
            .ToArray();

        return new BackendSignatureHelpResult(signatures, result.ActiveSignature);
    }

    public BackendSymbolInfo? GetSymbolAtPosition(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            // A document that is not part of the supplied snapshot is stale relative to it, so the
            // symbol is simply not resolvable here; a null answer is the honest result (§23).
            return null;
        }

        if (position < 0 || position > toolingDocument.Text.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                position,
                "The navigation position is outside the captured document text.");
        }

        SymbolLookupResult? result = toolingDocument.GetSymbolAtPosition(position);
        LogTiming("GetSymbolAtPosition", stopwatch, $"file='{toolingDocument.FilePath}', position={position}, found={result is not null}, kind={result?.Kind.ToString() ?? "none"}, name='{result?.Name ?? string.Empty}', textLength={toolingDocument.Text.Length}", 50);
        if (result is null)
        {
            return null;
        }

        return new BackendSymbolInfo(
            new CvoloSymbolHandle(toolingSnapshot, result.SymbolId),
            new CoreTextSpan(result.SubjectSpan.Start, result.SubjectSpan.Length),
            result.Name,
            MapSymbolKind(result.Kind),
            result.DisplayText,
            result.Documentation,
            MapNativeInterop(result.NativeInterop));
    }

    public BackendDefinitionResult GetDefinitions(BackendSnapshot snapshot, BackendSymbolHandle symbol)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
        {
            // A foreign or mismatched symbol handle must never resolve against the
            // wrong snapshot; return no result rather than an unrelated symbol (§15).
            LogTiming("GetDefinitions", stopwatch, "handle-mismatch, targets=0", 50);
            return EmptyDefinitions;
        }

        IReadOnlyList<SymbolDefinition> definitions = toolingSnapshot.GetDefinitions(handle.SymbolId);
        IReadOnlyList<PackageSourceDefinition> packageDefinitions = toolingSnapshot.GetPackageSourceDefinitions(handle.SymbolId);
        if (definitions.Count == 0 && packageDefinitions.Count == 0)
        {
            LogTiming("GetDefinitions", stopwatch, "no-targets, targets=0", 50);
            return EmptyDefinitions;
        }

        var texts = new Dictionary<DocumentUri, string>();
        var targets = new List<BackendDefinitionTarget>(definitions.Count + packageDefinitions.Count);
        foreach (SymbolDefinition definition in definitions)
        {
            if (!toolingSnapshot.TryGetDocument(definition.DocumentId, out DocumentSnapshot? targetDocument))
            {
                continue;
            }

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            targets.Add(new BackendDefinitionTarget(
                uri,
                new CoreTextSpan(definition.Range.Start, definition.Range.Length),
                new CoreTextSpan(definition.SelectionSpan.Start, definition.SelectionSpan.Length)));
        }

        foreach (PackageSourceDefinition definition in packageDefinitions)
        {
            DocumentUri uri = ToDocumentUri(definition.FilePath);
            texts.TryAdd(uri, definition.Source);
            targets.Add(new BackendDefinitionTarget(
                uri,
                new CoreTextSpan(definition.Range.Start, definition.Range.Length),
                new CoreTextSpan(definition.SelectionSpan.Start, definition.SelectionSpan.Length)));
        }

        LogTiming("GetDefinitions", stopwatch, $"symbol='{handle.SymbolId}', sourceTargets={definitions.Count}, packageTargets={packageDefinitions.Count}, targets={targets.Count}, texts={texts.Count}", 50);
        return new BackendDefinitionResult(texts, targets);
    }

    public BackendReferenceResult GetReferences(BackendSnapshot snapshot, BackendSymbolHandle symbol, bool includeDeclaration)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
            return new BackendReferenceResult(new Dictionary<DocumentUri, string>(), []);

        IReadOnlyList<SymbolReference> references = toolingSnapshot.GetReferences(handle.SymbolId, includeDeclaration);
        var texts = new Dictionary<DocumentUri, string>();
        var locations = new List<BackendReferenceLocation>(references.Count);
        foreach (SymbolReference reference in references)
        {
            if (!toolingSnapshot.TryGetDocument(reference.DocumentId, out DocumentSnapshot? targetDocument))
                continue;

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            locations.Add(new BackendReferenceLocation(
                uri,
                new CoreTextSpan(reference.Span.Start, reference.Span.Length),
                reference.IsDeclaration));
        }

        return new BackendReferenceResult(texts, locations);
    }

    public BackendRenamePreparation? PrepareRename(BackendSnapshot snapshot, BackendDocumentHandle document, int position)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;
        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            return null;

        RenamePreparation? preparation = toolingDocument.PrepareRename(position);
        if (preparation is null)
            return null;

        return new BackendRenamePreparation(
            new CvoloSymbolHandle(toolingSnapshot, preparation.SymbolId),
            new CoreTextSpan(preparation.SubjectSpan.Start, preparation.SubjectSpan.Length),
            preparation.Placeholder);
    }

    public BackendRenameResult RenameSymbol(BackendSnapshot snapshot, BackendSymbolHandle symbol, string newName)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (symbol is not CvoloSymbolHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
            return new BackendRenameFailure("The selected symbol does not belong to the captured project snapshot.");

        RenameResult result = toolingSnapshot.RenameSymbol(handle.SymbolId, newName);
        if (result is RenameFailure failure)
            return new BackendRenameFailure(failure.Message);

        var success = (RenameSuccess)result;
        var texts = new Dictionary<DocumentUri, string>();
        var edits = new List<BackendRenameEdit>(success.Edits.Count);
        foreach (RenameEdit edit in success.Edits)
        {
            if (!toolingSnapshot.TryGetDocument(edit.DocumentId, out DocumentSnapshot? targetDocument))
                return new BackendRenameFailure("Rename produced an edit outside the captured project snapshot.");

            DocumentUri uri = ToDocumentUri(targetDocument.FilePath);
            texts.TryAdd(uri, targetDocument.Text.ToString());
            edits.Add(new BackendRenameEdit(uri, new CoreTextSpan(edit.Span.Start, edit.Span.Length), edit.NewText));
        }

        return new BackendRenameSuccess(texts, edits);
    }

    public IReadOnlyList<BackendDocumentSymbol> GetDocumentSymbols(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
        {
            // A document missing from the supplied snapshot is stale, so it contributes no outline
            // rather than failing the request (§19.5's invalid-range spirit).
            return [];
        }

        IReadOnlyList<DocumentSymbolInfo> symbols = toolingDocument.GetDocumentSymbols();
        var mapped = new List<BackendDocumentSymbol>(symbols.Count);
        foreach (DocumentSymbolInfo symbol in symbols)
        {
            mapped.Add(MapDocumentSymbol(symbol));
        }

        return mapped;
    }

    private static BackendDocumentSymbol MapDocumentSymbol(DocumentSymbolInfo symbol)
    {
        var children = new List<BackendDocumentSymbol>(symbol.Children.Count);
        foreach (DocumentSymbolInfo child in symbol.Children)
        {
            children.Add(MapDocumentSymbol(child));
        }

        return new BackendDocumentSymbol(
            symbol.Name,
            symbol.Detail,
            MapSymbolKind(symbol.Kind),
            new CoreTextSpan(symbol.Range.Start, symbol.Range.Length),
            new CoreTextSpan(symbol.SelectionSpan.Start, symbol.SelectionSpan.Length),
            children);
    }

    public BackendSemanticTokenResult GetSemanticTokens(BackendSnapshot snapshot, BackendDocumentHandle document)
    {
        var stopwatch = Stopwatch.StartNew();
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            return new BackendSemanticTokenResult([]);

        IReadOnlyList<SemanticTokenInfo> tokens = toolingDocument.GetSemanticTokens();
        LogTiming("GetSemanticTokens", stopwatch, $"file='{toolingDocument.FilePath}', tokens={tokens.Count}, textLength={toolingDocument.Text.Length}", 50);
        var mapped = new List<BackendSemanticToken>(tokens.Count);
        foreach (SemanticTokenInfo token in tokens)
        {
            mapped.Add(new BackendSemanticToken(
                new CoreTextSpan(token.Span.Start, token.Span.Length),
                MapSymbolKind(token.Kind),
                MapTokenModifiers(token.Modifiers)));
        }

        return new BackendSemanticTokenResult(mapped);
    }

    public BackendCodeFixResult GetCodeFixes(BackendSnapshot snapshot, BackendDocumentHandle document, CoreTextSpan range)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        var documentId = ((CvoloDocumentHandle)document).DocumentId;

        if (!toolingSnapshot.TryGetDocument(documentId, out DocumentSnapshot? toolingDocument))
            return new BackendCodeFixResult([]);

        IReadOnlyList<CodeFixInfo> fixes = toolingSnapshot.GetCodeFixes(
            documentId,
            new ToolingTextSpan(range.Start, range.Length));

        var texts = new Dictionary<DocumentUri, string>();
        DocumentUri documentUri = ToDocumentUri(toolingDocument.FilePath);
        texts[documentUri] = toolingDocument.Text.ToString();

        var infos = new List<BackendCodeFixInfo>(fixes.Count);
        foreach (CodeFixInfo fix in fixes)
        {
            var diagnostics = new List<BackendDiagnostic>(fix.Diagnostics.Count);
            foreach (Diagnostic diagnostic in fix.Diagnostics)
            {
                if (!TryMapLocation(toolingSnapshot, diagnostic.Location, texts, out BackendDiagnosticLocation? location))
                {
                    continue;
                }

                var related = new List<BackendDiagnosticLocation>(diagnostic.RelatedLocations.Count);
                foreach (DiagnosticLocation relatedLocation in diagnostic.RelatedLocations)
                {
                    if (TryMapLocation(toolingSnapshot, relatedLocation, texts, out BackendDiagnosticLocation? mapped))
                        related.Add(mapped);
                }

                diagnostics.Add(new BackendDiagnostic(
                    MapSeverity(diagnostic.Severity),
                    diagnostic.Id,
                    diagnostic.Message,
                    location,
                    related));
            }

            if (diagnostics.Count == 0)
                continue;

            infos.Add(new BackendCodeFixInfo(
                new CvoloCodeFixHandle(toolingSnapshot, fix.Id),
                fix.Title,
                diagnostics));
        }

        return new BackendCodeFixResult(infos);
    }

    public BackendCodeFixResolution ResolveCodeFix(BackendSnapshot snapshot, BackendCodeFixHandle fix)
    {
        var toolingSnapshot = ((ToolingBackendSnapshot)snapshot).Snapshot;
        if (fix is not CvoloCodeFixHandle handle || !ReferenceEquals(handle.Snapshot, toolingSnapshot))
            return new BackendCodeFixFailure("The code fix is not available for this project state.");

        CodeFixResolution resolution = toolingSnapshot.ResolveCodeFix(handle.FixId);
        if (resolution is not CodeFixSuccess success)
            return new BackendCodeFixFailure(resolution is CodeFixFailure failure ? failure.Message : "The code fix could not be resolved.");

        var texts = new Dictionary<DocumentUri, string>();
        var edits = new List<BackendCodeFixEdit>(success.Edits.Count);
        foreach (CodeFixEdit edit in success.Edits)
        {
            if (!toolingSnapshot.TryGetDocument(edit.Document, out DocumentSnapshot? editDocument))
                return new BackendCodeFixFailure("The code fix targets a document outside the project state.");

            DocumentUri uri = ToDocumentUri(editDocument.FilePath);
            texts.TryAdd(uri, editDocument.Text.ToString());
            edits.Add(new BackendCodeFixEdit(
                uri,
                new CoreTextSpan(edit.Span.Start, edit.Span.Length),
                edit.NewText));
        }

        return new BackendCodeFixSuccess(texts, edits);
    }

    private static BackendSemanticTokenModifiers MapTokenModifiers(SemanticTokenModifiers modifiers)
    {
        var result = BackendSemanticTokenModifiers.None;
        if (modifiers.HasFlag(SemanticTokenModifiers.Declaration))
            result |= BackendSemanticTokenModifiers.Declaration;
        if (modifiers.HasFlag(SemanticTokenModifiers.Readonly))
            result |= BackendSemanticTokenModifiers.Readonly;
        if (modifiers.HasFlag(SemanticTokenModifiers.Static))
            result |= BackendSemanticTokenModifiers.Static;
        if (modifiers.HasFlag(SemanticTokenModifiers.Constant))
            result |= BackendSemanticTokenModifiers.Constant;
        return result;
    }

    private static readonly BackendDefinitionResult EmptyDefinitions =
        new(new Dictionary<DocumentUri, string>(), []);

    private static BackendNativeInteropMetadata? MapNativeInterop(NativeInteropMetadata? metadata)
    {
        if (metadata is null || metadata.Kind == NativeInteropKind.None)
            return null;

        var kind = metadata.Kind switch
        {
            NativeInteropKind.NativeDelegate => BackendNativeInteropKind.NativeDelegate,
            NativeInteropKind.RawUnion => BackendNativeInteropKind.RawUnion,
            NativeInteropKind.ForeignGlobal => BackendNativeInteropKind.ForeignGlobal,
            _ => throw new ArgumentOutOfRangeException(nameof(metadata), metadata.Kind, "Unknown native interop kind."),
        };

        return new BackendNativeInteropMetadata(
            kind,
            metadata.CallingConvention,
            metadata.ImportName,
            metadata.LibraryName,
            metadata.WinPath,
            metadata.LinuxPath,
            metadata.MacPath);
    }

    private static BackendSymbolKind MapSymbolKind(ToolingSymbolKind kind)
    {
        return kind switch
        {
            ToolingSymbolKind.Namespace => BackendSymbolKind.Namespace,
            ToolingSymbolKind.Module => BackendSymbolKind.Module,
            ToolingSymbolKind.Struct => BackendSymbolKind.Struct,
            ToolingSymbolKind.Union => BackendSymbolKind.Union,
            ToolingSymbolKind.Enum => BackendSymbolKind.Enum,
            ToolingSymbolKind.EnumMember => BackendSymbolKind.EnumMember,
            ToolingSymbolKind.Interface => BackendSymbolKind.Interface,
            ToolingSymbolKind.Protocol => BackendSymbolKind.Protocol,
            ToolingSymbolKind.Delegate => BackendSymbolKind.Delegate,
            ToolingSymbolKind.TypeAlias => BackendSymbolKind.TypeAlias,
            ToolingSymbolKind.TypeParameter => BackendSymbolKind.TypeParameter,
            ToolingSymbolKind.Function => BackendSymbolKind.Function,
            ToolingSymbolKind.Method => BackendSymbolKind.Method,
            ToolingSymbolKind.ExtensionMethod => BackendSymbolKind.ExtensionMethod,
            ToolingSymbolKind.AssociatedFunction => BackendSymbolKind.AssociatedFunction,
            ToolingSymbolKind.Constructor => BackendSymbolKind.Constructor,
            ToolingSymbolKind.Destructor => BackendSymbolKind.Destructor,
            ToolingSymbolKind.Field => BackendSymbolKind.Field,
            ToolingSymbolKind.Parameter => BackendSymbolKind.Parameter,
            ToolingSymbolKind.Local => BackendSymbolKind.Local,
            ToolingSymbolKind.Global => BackendSymbolKind.Global,
            ToolingSymbolKind.Constant => BackendSymbolKind.Constant,
            ToolingSymbolKind.Operator => BackendSymbolKind.Operator,
            ToolingSymbolKind.OtherType => BackendSymbolKind.OtherType,
            ToolingSymbolKind.Keyword => BackendSymbolKind.Keyword,
            _ => BackendSymbolKind.Unknown,
        };
    }

    /// <summary>
    /// Validates a Tooling replacement span against the captured text and cursor:
    /// it must be non-negative, lie within the text, and contain the request
    /// offset (<c>Start &lt;= position &lt;= End</c>, §17). Exposed internally so
    /// malformed spans can be covered deterministically without faking Tooling.
    /// </summary>
    internal static bool IsValidReplacementSpan(int start, int length, int position, int textLength)
    {
        return start >= 0
            && length >= 0
            && start + length <= textLength
            && start <= position
            && position <= start + length;
    }

    private static BackendCompletionKind MapCompletionKind(CompletionKind kind)
    {
        return kind switch
        {
            CompletionKind.Local => BackendCompletionKind.Local,
            CompletionKind.Parameter => BackendCompletionKind.Parameter,
            CompletionKind.Global => BackendCompletionKind.Global,
            CompletionKind.Function => BackendCompletionKind.Function,
            CompletionKind.Method => BackendCompletionKind.Method,
            CompletionKind.Type => BackendCompletionKind.Type,
            CompletionKind.Namespace => BackendCompletionKind.Namespace,
            CompletionKind.StructField => BackendCompletionKind.StructField,
            CompletionKind.UnionVariant => BackendCompletionKind.UnionVariant,
            CompletionKind.EnumVariant => BackendCompletionKind.EnumMember,
            CompletionKind.EnumMetadata => BackendCompletionKind.EnumMetadata,
            CompletionKind.ArrayLength => BackendCompletionKind.ArrayLength,
            CompletionKind.Keyword => BackendCompletionKind.Keyword,
            _ => BackendCompletionKind.OtherType,
        };
    }
}

/// <summary>
/// One live project session: a Cvolo project plus its current immutable snapshot
/// and the adapter-owned generation that identifies snapshot advancements. The
/// generation is language-server-internal and never added to the tooling.
/// </summary>
internal sealed class CvoloProjectSession : BackendProject
{
    private readonly Dictionary<DocumentId, SourceText> _diskBaselines;

    public CvoloProjectSession(CvoloWorkspace workspace, CvoloProject project, bool isPackageSource = false)
    {
        Workspace = workspace;
        Project = project;
        IsPackageSource = isPackageSource;
        Current = project.InitialSnapshot;
        _diskBaselines = project.InitialSnapshot.Documents
            .ToDictionary(pair => pair.Key, pair => pair.Value.Text);
    }

    public CvoloWorkspace Workspace { get; }

    /// <summary>
    /// True when this session was opened over an extracted package's own sources rather than the
    /// consuming project. Such a session must never re-discover from the project path: the text it
    /// serves is the package's, and advancing through <see cref="CvoloProject.Advance"/> would
    /// replace its documents with the consuming project's and drop every open package file.
    /// </summary>
    public bool IsPackageSource { get; }

    public CvoloProject Project { get; private set; }

    public ProjectSnapshot Baseline => Project.InitialSnapshot;

    public ProjectSnapshot Current { get; private set; }

    public long Generation { get; private set; }

    public SourceText ReadDiskBaseline(DocumentId documentId)
    {
        DocumentSnapshot document = Current.GetDocument(documentId);
        if (File.Exists(document.FilePath))
        {
            try
            {
                var source = SourceText.From(File.ReadAllText(document.FilePath));
                _diskBaselines[documentId] = source;
                return source;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return _diskBaselines.TryGetValue(documentId, out SourceText? baseline)
            ? baseline
            : Project.InitialSnapshot.GetDocument(documentId).Text;
    }

    public void SetDiskBaseline(DocumentId documentId, SourceText source)
    {
        _diskBaselines[documentId] = source;
    }

    public void SetProject(CvoloProject project)
    {
        Project = project;
    }

    /// <summary>
    /// Serializes advancement of <see cref="Current"/> for this project.
    /// </summary>
    public object Gate { get; } = new();

    /// <summary>
    /// Optional background generation-analysis registry. When present, every
    /// committed advance requests a single-flight full analysis of the newest
    /// generation (migration stage S4).
    /// </summary>
    public GenerationAnalysisRegistry? Analysis { get; init; }

    /// <summary>
    /// Publishes <paramref name="snapshot"/> as the new current snapshot and
    /// increments the generation. Callers must hold <see cref="Gate"/>.
    /// </summary>
    public void Advance(ProjectSnapshot snapshot)
    {
        Current = snapshot;
        Generation++;
        Analysis?.Request(snapshot, Generation);
    }

    /// <summary>
    /// Commits a staged snapshot at the specified generation without incrementing.
    /// Callers must hold <see cref="Gate"/>.
    /// </summary>
    public void CommitStaged(ProjectSnapshot snapshot, long generation)
    {
        Current = snapshot;
        Generation = generation;
        Analysis?.Request(snapshot, Generation);
    }
}

/// <summary>
/// Opaque document handle backed by the tooling's session-local DocumentId.
/// </summary>
internal sealed class CvoloDocumentHandle(DocumentId documentId) : BackendDocumentHandle
{
    public DocumentId DocumentId { get; } = documentId;
}

internal sealed class ToolingBackendSnapshot(ProjectSnapshot snapshot, long generation) : BackendSnapshot
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public long Generation { get; } = generation;
}

/// <summary>
/// Opaque symbol handle backed by the tooling's snapshot-scoped SymbolId plus the
/// tooling snapshot it was resolved from, so a handle can never resolve against a
/// different snapshot.
/// </summary>
internal sealed class CvoloSymbolHandle(ProjectSnapshot snapshot, SymbolId symbolId) : BackendSymbolHandle
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public SymbolId SymbolId { get; } = symbolId;
}

/// <summary>
/// Opaque completion-resolve handle backed by the tooling's snapshot-scoped CompletionItemId
/// plus the tooling snapshot it was minted from, so a handle can never resolve against a
/// different snapshot.
/// </summary>
internal sealed class CvoloCompletionResolveHandle(ProjectSnapshot snapshot, CompletionItemId itemId) : BackendCompletionResolveHandle
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public CompletionItemId ItemId { get; } = itemId;
}

/// <summary>
/// Opaque code-fix handle backed by the tooling's snapshot-scoped CodeFixId plus the tooling
/// snapshot it was minted from, so a handle can never resolve against a different snapshot.
/// </summary>
internal sealed class CvoloCodeFixHandle(ProjectSnapshot snapshot, CodeFixId fixId) : BackendCodeFixHandle
{
    public ProjectSnapshot Snapshot { get; } = snapshot;

    public CodeFixId FixId { get; } = fixId;
}

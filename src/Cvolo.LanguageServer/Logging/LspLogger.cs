using System.Text;

namespace Cvolo.LanguageServer.Logging;

public interface ILspLogger
{
    bool IsVerboseEnabled { get; }
    TextWriter? RawSink { get; }
    void Debug(string message);
    void Info(string message);
    void Warning(string message);
    void Verbose(string message);
    void Error(string message);
}

/// <summary>
/// Writes diagnostics to stderr and, when configured, to a log file. Never
/// writes to stdout (stdout is reserved for the JSON-RPC protocol). Logging is
/// off by default: informational messages are suppressed unless a log file or
/// verbose mode is configured. Raw protocol payloads are only logged when
/// <c>--verbose</c> is active.
/// </summary>
/// <remarks>
/// INC 3.1 LOG-2..5: even without <c>--log</c>, the logger keeps a small bounded ring of recent
/// redacted entries. The first real internal <see cref="Error"/> materializes a session-specific
/// incident file in <see cref="DiagnosticLogPath"/>'s directory (the client supplies the directory;
/// the server cannot infer the VS Code managed storage location). The ring is flushed immediately,
/// and every later error is appended and flushed promptly. A successful session that never logs an
/// error creates no file. Logging never throws, never blocks document edits, and never writes source.
/// </remarks>
public sealed class LspLogger : ILspLogger, IDisposable
{
    private const int RingCapacity = 200;
    private const int MaxLineLength = 4096;
    private const int RetainedIncidentFiles = 10;
    private const string IncidentFilePrefix = "cvolo-lsp-";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _gate = new();
    private readonly TextWriter? _file;
    private readonly string? _incidentDirectory;
    private readonly string _sessionId = Guid.NewGuid().ToString("N")[..8];
    private readonly Queue<string> _ring = new();
    private long _eventId;
    private TextWriter? _incidentFile;

    public LspLogger(bool verbose, string? logFilePath, string? diagnosticLogDirectory = null)
    {
        IsVerboseEnabled = verbose;
        _file = logFilePath is null
            ? null
            : new StreamWriter(new FileStream(logFilePath, FileMode.Create, FileAccess.Write, FileShare.Read), Utf8NoBom);
        _incidentDirectory = string.IsNullOrWhiteSpace(diagnosticLogDirectory) ? null : diagnosticLogDirectory;
    }

    public bool IsVerboseEnabled { get; }

    public TextWriter? RawSink => IsVerboseEnabled ? (_file ?? Console.Error) : null;

    /// <summary>The incident file created after the first internal error, or null if none was written.</summary>
    public string? DiagnosticLogPath { get { lock (_gate) return _incidentFile is null ? null : _incidentPath; } }

    private string? _incidentPath;

    public void Debug(string message)
    {
        if (!IsVerboseEnabled && _file is null)
        {
            return;
        }

        Write("debug", message, consoleWhenNoFile: true);
    }

    public void Info(string message) => Write("info", message, consoleWhenNoFile: true);

    public void Warning(string message) => Write("warning", message, consoleWhenNoFile: true, consoleAlways: true);

    public void Verbose(string message)
    {
        if (!IsVerboseEnabled)
        {
            return;
        }

        Write("verbose", message, consoleWhenNoFile: true);
    }

    public void Error(string message)
    {
        string line = Format("error", message);
        lock (_gate)
        {
            if (_file is not null)
            {
                SafeWriteLine(_file, line);
                TryFlush(_file);
            }
            else
            {
                Console.Error.WriteLine(line);
            }

            EnqueueRing(line);

            if (_file is null && _incidentDirectory is not null)
                WriteIncident(line);
        }
    }

    private void Write(string level, string message, bool consoleWhenNoFile, bool consoleAlways = false)
    {
        string line = Format(level, message);
        lock (_gate)
        {
            EnqueueRing(line);
            if (_file is not null)
            {
                SafeWriteLine(_file, line);
            }
            else if (consoleAlways || (consoleWhenNoFile && IsVerboseEnabled))
            {
                Console.Error.WriteLine(line);
            }
        }
    }

    private void WriteIncident(string line)
    {
        if (_incidentFile is null)
        {
            if (!TryCreateIncidentFile())
                return;

            // First error materializes the file: flush the whole retained context at once.
            foreach (string entry in _ring)
                SafeWriteLine(_incidentFile, entry);
            TryFlush(_incidentFile);
            return;
        }

        SafeWriteLine(_incidentFile, line);
        TryFlush(_incidentFile);
    }

    private bool TryCreateIncidentFile()
    {
        try
        {
            Directory.CreateDirectory(_incidentDirectory!);

            _incidentPath = Path.Combine(
                _incidentDirectory!,
                $"{IncidentFilePrefix}{DateTime.UtcNow:yyyyMMdd-HHmmss}-{_sessionId}.log");
            _incidentFile = new StreamWriter(
                new FileStream(_incidentPath, FileMode.Create, FileAccess.Write, FileShare.Read),
                Utf8NoBom);
            // Prune after creating so the quota counts this session's file too.
            PruneIncidentFiles(_incidentDirectory!);
            SafeWriteLine(_incidentFile, $"# cvolo language server diagnostic log; session={_sessionId}; started={DateTime.UtcNow:O}");
            return true;
        }
        catch (Exception ex)
        {
            // A diagnostic log that cannot be written must not break the session. Warn once, drop
            // the directory so we do not retry on every error, and keep working.
            _incidentFile = null;
            _incidentPath = null;
            Console.Error.WriteLine($"warning: could not create the diagnostic log; continuing without one ({ex.GetType().Name}).");
            return false;
        }
    }

    private static void PruneIncidentFiles(string directory)
    {
        try
        {
            var files = new DirectoryInfo(directory).GetFiles($"{IncidentFilePrefix}*.log");
            if (files.Length <= RetainedIncidentFiles)
                return;

            foreach (FileInfo file in files
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(RetainedIncidentFiles))
            {
                try { file.Delete(); } catch { /* retention is best-effort */ }
            }
        }
        catch
        {
            // Best-effort retention; never fail the session because pruning failed.
        }
    }

    private void EnqueueRing(string line)
    {
        _ring.Enqueue(line);
        while (_ring.Count > RingCapacity)
            _ring.Dequeue();
    }

    private string Format(string level, string message)
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        long eventId = Interlocked.Increment(ref _eventId);
        return $"{timestamp} [{level}] #{eventId} {Redact(message)}";
    }

    /// <summary>
    /// Removes the user profile prefix from paths and bounds the line length. Source text is never
    /// passed to the logger in the first place; this additionally keeps raw paths, arguments and
    /// oversized frames out of the durable file.
    /// </summary>
    private static string Redact(string message)
    {
        if (string.IsNullOrEmpty(message))
            return message;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
            message = message.Replace(home, "~", StringComparison.OrdinalIgnoreCase);

        return message.Length > MaxLineLength ? message[..MaxLineLength] + "…[truncated]" : message;
    }

    private static void SafeWriteLine(TextWriter writer, string line)
    {
        try
        {
            writer.WriteLine(line);
        }
        catch
        {
            // Durability is best-effort; a failed write must never surface as a crash.
        }
    }

    private static void TryFlush(TextWriter writer)
    {
        try
        {
            writer.Flush();
        }
        catch
        {
            // Best-effort flush.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_file is not null)
            {
                TryFlush(_file);
                _file.Dispose();
            }

            if (_incidentFile is not null)
            {
                TryFlush(_incidentFile);
                _incidentFile.Dispose();
            }
        }
    }
}

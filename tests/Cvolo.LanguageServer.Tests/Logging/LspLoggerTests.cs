using Cvolo.LanguageServer.Logging;
using Xunit;

namespace Cvolo.LanguageServer.Tests.Logging;

/// <summary>
/// INC 3.1 LOG-2..5: a clean session must not create an incident file, the first internal error
/// must materialize one that also contains the retained context, and later errors must append to
/// the same file. Paths are redacted and retention is bounded.
/// </summary>
public sealed class LspLoggerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cvolo-lsp-log-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static string[] LogFiles(string dir) =>
        Directory.Exists(dir) ? Directory.GetFiles(dir, "*.log") : [];

    // The durable file stays open for the session; read it without demanding exclusive share.
    private static string ReadAll(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void CleanSession_WritesNoIncidentFile()
    {
        using var logger = new LspLogger(verbose: false, logFilePath: null, diagnosticLogDirectory: _dir);

        logger.Info("started");
        logger.Warning("recoverable degradation");
        logger.Debug("suppressed without verbose");

        Assert.Null(logger.DiagnosticLogPath);
        Assert.Empty(LogFiles(_dir));
    }

    [Fact]
    public void FirstError_MaterializesFileWithRetainedContext()
    {
        using var logger = new LspLogger(false, null, _dir);

        logger.Info("context: opened project");
        logger.Error("boom: internal failure");

        Assert.NotNull(logger.DiagnosticLogPath);
        Assert.True(File.Exists(logger.DiagnosticLogPath));
        string content = ReadAll(logger.DiagnosticLogPath!);
        Assert.Contains("context: opened project", content);
        Assert.Contains("boom: internal failure", content);
        Assert.Contains("[error]", content);
    }

    [Fact]
    public void SubsequentErrors_AppendToTheSameFile()
    {
        using var logger = new LspLogger(false, null, _dir);

        logger.Error("first failure");
        string? path = logger.DiagnosticLogPath;
        logger.Error("second failure");

        Assert.Equal(path, logger.DiagnosticLogPath);
        Assert.Single(LogFiles(_dir));
        string content = ReadAll(logger.DiagnosticLogPath!);
        Assert.Contains("first failure", content);
        Assert.Contains("second failure", content);
    }

    [Fact]
    public void RedactsUserProfilePathFromDurableFile()
    {
        using var logger = new LspLogger(false, null, _dir);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        logger.Warning($"not a real error, but mentions {home}\\secret\\path");
        logger.Error("trigger");

        string content = ReadAll(logger.DiagnosticLogPath!);
        Assert.Contains("~", content);
        if (!string.IsNullOrEmpty(home))
            Assert.DoesNotContain(home, content);
    }

    [Fact]
    public void StructuredDebugLog_IsReadableBeforeTheSessionEnds()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "debug.log");
        using (var logger = new LspLogger(verbose: false, logFilePath: file, diagnosticLogDirectory: _dir))
        {
            Assert.Null(logger.RawSink);
            logger.Debug("[signatureHelp] compiler returned null");
            logger.Info("[provenance] tooling=0.0.21.1");
            var content = ReadAll(file);
            Assert.Contains("compiler returned null", content);
            Assert.Contains("tooling=0.0.21.1", content);
            Assert.Null(logger.DiagnosticLogPath);
        }
    }

    [Fact]
    public void ExplicitLogFile_StaysContinuousAndCreatesNoIncidentFile()
    {
        string explicitLog = Path.Combine(_dir, "explicit.log");
        Directory.CreateDirectory(_dir);
        using (var logger = new LspLogger(verbose: true, logFilePath: explicitLog, diagnosticLogDirectory: _dir))
        {
            logger.Info("hello");
            logger.Error("failure");
        }

        string content = ReadAll(explicitLog);
        Assert.Contains("hello", content);
        Assert.Contains("failure", content);
        Assert.False(File.Exists(Path.Combine(_dir, "cvolo-lsp-x.log")));
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => Path.GetFileName(f).StartsWith("cvolo-lsp-") && f != explicitLog);
    }

    [Fact]
    public void UnwritableDirectory_DoesNotThrow()
    {
        using var logger = new LspLogger(false, null, "\0invalid\0path");
        logger.Error("failure");
        Assert.Null(logger.DiagnosticLogPath);
    }

    [Fact]
    public void Retention_KeepsAtMostTenIncidentFiles()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 12; i++)
            File.WriteAllText(Path.Combine(_dir, $"cvolo-lsp-old{i:D2}.log"), "old");

        using var logger = new LspLogger(false, null, _dir);
        logger.Error("trigger");

        Assert.True(LogFiles(_dir).Length <= 10);
    }
}

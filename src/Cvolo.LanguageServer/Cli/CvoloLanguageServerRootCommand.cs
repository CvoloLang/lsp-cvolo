using System.CommandLine;

namespace Cvolo.LanguageServer.Cli;

/// <summary>
/// The <c>cvolo-language-server [options]</c> command line surface. Parsing is
/// delegated to System.CommandLine so unknown options fail fast with a
/// non-zero exit code before the JSON-RPC transport is started. The built-in
/// <c>--version</c> option is removed so the server can print its own version
/// banner: <c>--version</c> prints a four-line human summary and
/// <c>--version --json</c> prints the same provenance as one JSON object for
/// scripts. <c>--verbose</c> remains a logging switch and is unrelated to either.
/// </summary>
internal sealed class CvoloLanguageServerRootCommand : RootCommand
{
    private readonly Option<bool> _stdioOption = new("--stdio")
    {
        Description = "Send and receive JSON-RPC over standard input and output (the default and only transport).",
    };

    private readonly Option<string?> _logOption = new("--log")
    {
        Description = "Write diagnostics to the given file path. Logging is off by default.",
    };

    private readonly Option<string?> _diagnosticLogDirOption = new("--diagnostic-log-dir")
    {
        Description = "Directory in which to write a session incident log after the first internal error. No file is written for a clean session.",
    };

    private readonly Option<bool> _verboseOption = new("--verbose")
    {
        Description = "Enable verbose logging, including raw JSON-RPC payloads.",
    };

    private readonly Option<bool> _versionOption = new("--version")
    {
        Description = "Print version information and exit without starting the server.",
    };

    private readonly Option<bool> _jsonOption = new("--json")
    {
        Description = "With --version, print the same provenance as one JSON object for scripts.",
    };

    public CvoloLanguageServerRootCommand() : base("Language Server Protocol (LSP 3.17) server for the Cvolo language.")
    {
        Options.Remove(Options.First(o => o is VersionOption or { Name: "--version" }));

        Add(_stdioOption);
        Add(_logOption);
        Add(_diagnosticLogDirOption);
        Add(_verboseOption);
        Add(_versionOption);
        Add(_jsonOption);

        SetAction(HandleAsync);
    }

    private async Task<int> HandleAsync(ParseResult parseResult)
    {
        try
        {
            var versionRequested = parseResult.Tokens.Any(token => token.Value == _versionOption.Name);
            var jsonRequested = parseResult.GetValue(_jsonOption);

            if (jsonRequested && !versionRequested)
            {
                // --json only shapes --version output; on its own it is a usage error
                // rather than a silent request to start the server.
                Console.Error.WriteLine($"{ServerMetadata.ServerName}: --json is only valid together with --version.");
                return 2;
            }

            if (versionRequested)
            {
                if (jsonRequested)
                {
                    ServerPipeline.PrintVersionJson();
                }
                else
                {
                    ServerPipeline.PrintVersion();
                }

                return 0;
            }

            var verbose = parseResult.GetValue(_verboseOption);
            var logPath = parseResult.GetValue(_logOption);
            var diagnosticLogDir = parseResult.GetValue(_diagnosticLogDirOption);
            return await ServerPipeline.RunAsync(logPath, verbose, diagnosticLogDir).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{ServerMetadata.ServerName}: fatal error: {ex}");
            return 1;
        }
    }
}

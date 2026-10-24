using System.Diagnostics;
using System.Text;
using Newtonsoft.Json.Linq;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// A real <c>cvolo-language-server</c> process with redirected stdio. All
/// stdout bytes are pumped continuously so pipe backpressure cannot stall the
/// server, and raw frames can be inspected after the fact.
/// </summary>
internal sealed class ServerProcess : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private readonly Process _process;
    private readonly Stream _stdin;
    private readonly object _stdoutGate = new();
    private readonly MemoryStream _stdout = new();
    private readonly object _stderrGate = new();
    private readonly MemoryStream _stderr = new();

    private ServerProcess(Process process, Stream stdin)
    {
        _process = process;
        _stdin = stdin;
        _ = Task.Run(PumpStdout);
        _ = Task.Run(PumpStderr);
    }

    public static ServerProcess Start(params string[] args)
        => StartWithEnvironment(null, args);

    /// <summary>
    /// Per-child environment overrides: never mutate the test runner's
    /// process-wide environment (other test collections run concurrently).
    /// A null value removes the variable from this child only.
    /// </summary>
    public static ServerProcess StartWithEnvironment(
        IReadOnlyDictionary<string, string?>? overrides, params string[] args)
    {
        (var fileName, var leadingArg) = ServerProcessLocator.Locate();
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (value is null)
                    psi.Environment.Remove(key);
                else
                    psi.Environment[key] = value;
            }
        }

        if (leadingArg is not null)
        {
            psi.ArgumentList.Add(leadingArg);
        }

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        Process process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start the server process.");
        return new ServerProcess(process, process.StandardInput.BaseStream);
    }

    public int Id => _process.Id;
    public bool HasExited => _process.HasExited;

    public void SendJson(string json)
    {
        var frame = FrameParser.EncodeFrame(json);
        _stdin.Write(frame, 0, frame.Length);
        _stdin.Flush();
    }

    public async Task<JObject> WaitForResponseAsync(int id, TimeSpan? timeout = null)
    {
        var effective = timeout ?? TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow + effective;
        while (DateTime.UtcNow < deadline)
        {
            foreach (JObject response in SnapshotResponses())
            {
                if (response["id"]?.Value<int>() == id)
                {
                    return response;
                }
            }

            await Task.Delay(20).ConfigureAwait(false);
        }

        throw new TimeoutException(
            $"Timed out after {effective.TotalSeconds:0.#}s waiting for JSON-RPC response id {id}. " +
            $"hasExited={_process.HasExited}, " +
            $"stdout: {GetStdoutText()}, stderr: {GetStderrText()}");
    }

    public async Task<int> WaitForExitAsync(TimeSpan? timeout = null)
    {
        var effective = timeout ?? ProcessTimeout;
        using var cts = new CancellationTokenSource(effective);
        try
        {
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"Server process did not exit within {effective.TotalSeconds:0.#}s. stdout: {GetStdoutText()}");
        }

        return _process.ExitCode;
    }

    public IReadOnlyList<string> SnapshotFrames()
    {
        byte[] bytes;
        lock (_stdoutGate)
        {
            bytes = _stdout.ToArray();
        }

        return FrameParser.ParseAll(bytes);
    }

    public byte[] GetAllStdoutBytes()
    {
        lock (_stdoutGate)
        {
            return _stdout.ToArray();
        }
    }

    /// <summary>
    /// Waits until redirected stdout has stopped growing (the pump has drained
    /// the pipe) and returns the complete byte stream.
    /// </summary>
    public async Task<byte[]> WaitForStdoutStableAsync(TimeSpan? timeout = null)
    {
        TimeSpan effective = timeout ?? TimeSpan.FromSeconds(5);
        DateTime deadline = DateTime.UtcNow + effective;
        var previous = -1;
        while (DateTime.UtcNow < deadline)
        {
            var current = GetAllStdoutBytes().Length;
            if (current == previous && current > 0)
            {
                return GetAllStdoutBytes();
            }

            previous = current;
            await Task.Delay(50).ConfigureAwait(false);
        }

        return GetAllStdoutBytes();
    }

    public string GetStdoutText()
    {
        return Encoding.UTF8.GetString(GetAllStdoutBytes());
    }

    public string GetStderrText()
    {
        lock (_stderrGate)
        {
            return Encoding.UTF8.GetString(_stderr.ToArray());
        }
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }

        _process.Dispose();
    }

    private IEnumerable<JObject> SnapshotResponses()
    {
        foreach (var frame in SnapshotFrames())
        {
            if (JObjectParse.TryParse(frame, out JObject? json))
            {
                yield return json!;
            }
        }
    }

    private void PumpStdout()
    {
        Pump(_process.StandardOutput.BaseStream, _stdoutGate, _stdout);
    }

    private void PumpStderr()
    {
        Pump(_process.StandardError.BaseStream, _stderrGate, _stderr);
    }

    private void Pump(Stream source, object gate, MemoryStream destination)
    {
        var buffer = new byte[8192];
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                lock (gate)
                {
                    destination.Write(buffer, 0, read);
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static class JObjectParse
    {
        public static bool TryParse(string text, out JObject? json)
        {
            try
            {
                json = JObject.Parse(text);
                return true;
            }
            catch (Newtonsoft.Json.JsonException)
            {
                json = null;
                return false;
            }
        }
    }
}
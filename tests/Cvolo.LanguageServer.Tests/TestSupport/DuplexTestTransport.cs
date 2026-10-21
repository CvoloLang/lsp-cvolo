using System.IO.Pipelines;

namespace Cvolo.LanguageServer.Tests.TestSupport;

/// <summary>
/// Bidirectional in-memory transport backed by two pipes, so a server and a
/// client can talk to each other in-process without sockets or stdio.
/// </summary>
internal sealed class DuplexTestTransport : IDisposable
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();

    /// <summary>Stream the server reads from (client -> server).</summary>
    public Stream ServerInput => _clientToServer.Reader.AsStream();

    /// <summary>Stream the server writes to (server -> client).</summary>
    public Stream ServerOutput => _serverToClient.Writer.AsStream();

    /// <summary>Stream the client reads from (server -> client).</summary>
    public Stream ClientInput => _serverToClient.Reader.AsStream();

    /// <summary>Stream the client writes to (client -> server).</summary>
    public Stream ClientOutput => _clientToServer.Writer.AsStream();

    public void Dispose()
    {
        _clientToServer.Reader.CancelPendingRead();
        _clientToServer.Writer.Complete();
        _serverToClient.Reader.CancelPendingRead();
        _serverToClient.Writer.Complete();
    }
}

/// <summary>
/// Stream wrapper that records every byte written or read so tests can inspect
/// raw JSON-RPC frames exactly as they crossed the transport.
/// </summary>
internal sealed class RecordingStream : Stream
{
    private readonly Stream _inner;
    private readonly MemoryStream _recorded = new();
    private readonly object _writeLock = new();
    private readonly SemaphoreSlim _writeSemaphore = new(1, 1);

    public RecordingStream(Stream inner)
    {
        _inner = inner;
    }

    public byte[] RecordedBytes
    {
        get
        {
            lock (_writeLock)
            {
                return _recorded.ToArray();
            }
        }
    }

    public void ResetRecording()
    {
        lock (_writeLock)
        {
            _recorded.SetLength(0);
        }
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanWrite => _inner.CanWrite;

    public override bool CanSeek => false;

    public override long Length => _recorded.Length;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush() => _inner.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        // Serialize synchronous and asynchronous writes through the same gate.
        // Record only bytes successfully accepted by the underlying stream.
        _writeSemaphore.Wait();
        try
        {
            _inner.Write(buffer, offset, count);
            lock (_writeLock)
            {
                _recorded.Write(buffer, offset, count);
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // StreamJsonRpc writes asynchronously. Inheriting Stream.WriteAsync gives
        // the recorder no reliable async boundary and can make a real sent event
        // observable before the test recorder contains the complete frame.
        await _writeSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            lock (_writeLock)
            {
                _recorded.Write(buffer.Span);
            }
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = _inner.Read(buffer, offset, count);
        if (read > 0)
        {
            _recorded.Write(buffer, offset, read);
        }

        return read;
    }
}
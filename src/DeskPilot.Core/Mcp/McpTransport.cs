using System.Text;

namespace DeskPilot.Core.Mcp;

/// <summary>
/// Reads newline-delimited UTF-8 lines from a stream without over-reading past what it returns, so the
/// same reader can first consume the pipe's token line and then serve MCP on the rest of the stream.
/// </summary>
internal sealed class McpLineReader
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _start;
    private int _end;
    // Bytes after _start already searched for a newline (avoids rescanning a long partial line).
    private int _scanned;
    private bool _eof;

    public McpLineReader(Stream stream, int initialBufferSize = 16 * 1024)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _buffer = new byte[Math.Max(256, initialBufferSize)];
    }

    /// <summary>
    /// Returns the next line without its terminator ("\n" or "\r\n"), or null at end of stream.
    /// Throws <see cref="InvalidDataException"/> when a line grows beyond maxLineBytes.
    /// </summary>
    public async ValueTask<string?> ReadLineAsync(int maxLineBytes, CancellationToken ct)
    {
        while (true)
        {
            var from = _start + _scanned;
            var idx = _buffer.AsSpan(from, _end - from).IndexOf((byte)'\n');
            if (idx >= 0)
            {
                var lineEnd = from + idx;
                var line = Decode(_start, lineEnd - _start);
                _start = lineEnd + 1;
                _scanned = 0;
                if (_start == _end) _start = _end = 0;
                return line;
            }

            _scanned = _end - _start;
            if (_scanned > maxLineBytes) throw new InvalidDataException($"Line longer than {maxLineBytes} bytes.");

            if (_eof)
            {
                if (_end == _start) return null;
                var rest = Decode(_start, _end - _start);
                _start = _end = _scanned = 0;
                return rest;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }
            if (_end == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);

            var n = await _stream.ReadAsync(_buffer.AsMemory(_end), ct).ConfigureAwait(false);
            if (n == 0) _eof = true;
            else _end += n;
        }
    }

    private string Decode(int offset, int count)
    {
        if (count > 0 && _buffer[offset + count - 1] == (byte)'\r') count--;
        return Encoding.UTF8.GetString(_buffer, offset, count);
    }
}

/// <summary>Writes complete lines to a stream, one at a time, so concurrent responses never interleave.</summary>
internal sealed class McpLineWriter
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _broken;

    public McpLineWriter(Stream stream) => _stream = stream ?? throw new ArgumentNullException(nameof(stream));

    /// <summary>True once a write failed (the other side went away); later writes are dropped.</summary>
    public bool IsBroken => _broken;

    /// <summary>Writes one line (the data must already end with "\n"). Returns false when the stream is gone.</summary>
    public async Task<bool> WriteLineAsync(ReadOnlyMemory<byte> line, CancellationToken ct)
    {
        if (_broken) return false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        try
        {
            if (_broken) return false;
            await _stream.WriteAsync(line, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or NotSupportedException or InvalidOperationException)
        {
            // A partially written line would corrupt the stream for the reader, so stop writing altogether.
            _broken = true;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Core.Mcp;

/// <summary>
/// Serves IToolHost as an MCP server over a per-user named pipe. CLI agents start
/// "DeskPilot.exe --mcp-bridge PIPE TOKEN" as a stdio MCP server; the bridge relays stdio to this pipe.
/// </summary>
/// <remarks>
/// The pipe only admits the same Windows user (PipeOptions.CurrentUserOnly), and every connection must
/// first send <c>{"deskpilot_token":"TOKEN"}</c> on its own line, so other programs of the same user cannot
/// drive the desktop without being handed the endpoint. Several bridges may be connected at once (an agent
/// can restart its MCP server); each connection gets its own protocol handler.
/// </remarks>
public sealed class McpPipeServer : IAsyncDisposable
{
    public const string PipeNamePrefix = "DeskPilot-mcp-";
    private const int MaxTokenLineBytes = 1024;
    private static readonly TimeSpan TokenTimeout = TimeSpan.FromSeconds(10);

    private readonly IToolHost _tools;
    private readonly byte[] _expectedTokenLine;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly HashSet<Connection> _connections = new();
    private Task? _acceptLoop;
    private Task? _disposeTask;
    private bool _disposed;
    private int _connectionCount;

    public McpPipeServer(IToolHost tools, string serverName = "deskpilot")
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        ServerName = string.IsNullOrWhiteSpace(serverName) ? "deskpilot" : serverName;
        PipeName = PipeNamePrefix + Guid.NewGuid().ToString("N");
        Token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        _expectedTokenLine = Encoding.UTF8.GetBytes(BuildTokenLine(Token));
    }

    public string ServerName { get; }
    public string PipeName { get; }
    public string Token { get; }

    /// <summary>Bridges currently connected and authenticated or authenticating.</summary>
    public int ActiveConnections { get { lock (_gate) return _connections.Count; } }

    /// <summary>Connections accepted so far (including rejected ones).</summary>
    public int TotalConnections => Volatile.Read(ref _connectionCount);

    /// <summary>Diagnostics sink (never receives the token or tool arguments). Defaults to the app log.</summary>
    internal Action<string> Diagnostics { get; set; } = Log.Info;

    /// <summary>Upper bound for DisposeAsync, even when a tool ignores cancellation.</summary>
    internal TimeSpan DisposeTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long each connection waits for its cancelled calls before closing.</summary>
    internal TimeSpan ConnectionDrainTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>The exact first line a bridge must send for a token (without the newline).</summary>
    internal static string BuildTokenLine(string token) => "{\"deskpilot_token\":\"" + token + "\"}";

    /// <summary>Starts accepting bridge connections in the background. Idempotent.</summary>
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _acceptLoop ??= Task.Run(AcceptLoopAsync);
        }
    }

    /// <summary>Command + args a CLI agent should run to reach this server.</summary>
    public McpEndpointInfo GetEndpoint(string bridgeExePath) =>
        new(ServerName, bridgeExePath, new[] { McpBridge.Switch, PipeName, Token });

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposeTask == null)
            {
                _disposed = true;
                // Run outside the lock: cancellation callbacks may complete connection tasks inline.
                _disposeTask = Task.Run(DisposeCoreAsync);
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Connection[] connections;
        Task? acceptLoop;
        lock (_gate)
        {
            connections = _connections.ToArray();
            acceptLoop = _acceptLoop;
        }

        try { _cts.Cancel(); }
        catch (AggregateException) { }

        // Closing the streams also unblocks reads and writes that do not observe cancellation.
        foreach (var c in connections) c.Close();

        var tasks = connections.Select(c => c.Task).Where(t => t != null).Select(t => t!).ToList();
        if (acceptLoop != null) tasks.Add(acceptLoop);
        if (tasks.Count == 0) return;
        try
        {
            await Task.WhenAll(tasks).WaitAsync(DisposeTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out (a tool ignored cancellation); the streams are closed, so nothing can reach the agent anymore.
        }
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                failures = 0;
                if (!TryAddConnection(pipe)) pipe.Dispose();
                pipe = null;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                pipe?.Dispose();
                break;
            }
            catch (Exception ex)
            {
                pipe?.Dispose();
                if (ct.IsCancellationRequested) break;
                // E.g. a client that connected and vanished at once. Back off so a persistent failure cannot spin.
                failures++;
                Report($"MCP pipe: accept failed ({ex.GetType().Name}: {ex.Message})");
                try { await Task.Delay(Math.Min(2000, 25 * failures * failures), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private bool TryAddConnection(NamedPipeServerStream pipe)
    {
        lock (_gate)
        {
            if (_disposed) return false;
            var connection = new Connection(pipe);
            _connections.Add(connection);
            Interlocked.Increment(ref _connectionCount);
            connection.Task = Task.Run(() => ServeAsync(connection));
            return true;
        }
    }

    private async Task ServeAsync(Connection connection)
    {
        var ct = _cts.Token;
        try
        {
            var reader = new McpLineReader(connection.Pipe, 4096);
            string? first;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TokenTimeout);
                first = await reader.ReadLineAsync(MaxTokenLineBytes, timeout.Token).ConfigureAwait(false);
            }

            if (first == null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), _expectedTokenLine))
            {
                Report("MCP pipe: closed a connection that did not present the session token");
                return;
            }

            Report("MCP pipe: agent connected");
            using var handler = new McpProtocolHandler(_tools, ServerName) { DrainTimeout = ConnectionDrainTimeout };
            connection.Handler = handler;
            await handler.RunAsync(reader, connection.Pipe, ct).ConfigureAwait(false);
            Report("MCP pipe: agent disconnected");
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or InvalidDataException)
        {
            // Disconnected, timed out before the token, or the server is shutting down.
        }
        catch (Exception ex)
        {
            Report($"MCP pipe: connection failed ({ex.GetType().Name}: {ex.Message})");
        }
        finally
        {
            connection.Close();
            lock (_gate) _connections.Remove(connection);
        }
    }

    private void Report(string message)
    {
        try { Diagnostics?.Invoke(message); }
        catch (Exception) { }
    }

    private sealed class Connection(NamedPipeServerStream pipe)
    {
        private int _closed;

        public NamedPipeServerStream Pipe { get; } = pipe;
        public Task? Task { get; set; }
        public McpProtocolHandler? Handler { get; set; }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            Handler?.CancelAll();
            try { Pipe.Dispose(); }
            catch (Exception) { }
        }
    }
}

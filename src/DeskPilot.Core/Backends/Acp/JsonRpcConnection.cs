using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeskPilot.Core.Backends.Acp;

/// <summary>A JSON-RPC error object returned by the other side (or raised by a local request handler).</summary>
internal sealed class JsonRpcException : Exception
{
    public JsonRpcException(int code, string message, string? dataJson = null) : base(message)
    {
        Code = code;
        DataJson = dataJson;
    }

    public int Code { get; }
    public string? DataJson { get; }
}

/// <summary>The connection closed (for example the agent exited) before a response arrived.</summary>
internal sealed class JsonRpcConnectionClosedException : Exception
{
    public JsonRpcConnectionClosedException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// JSON-RPC 2.0 over a pair of streams, one message per line (newline-delimited JSON), as ACP uses on stdio.
/// Correlates our requests with responses, answers the other side's requests through a handler and passes
/// notifications to another handler. Notifications are delivered in order on the read loop, so everything an
/// agent streams before a response has been handled by the time that response completes its task.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    public const int MethodNotFound = -32601;
    public const int InternalError = -32603;

    public delegate Task<JsonNode?> RequestHandler(string method, JsonElement parameters, CancellationToken ct);

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly Stream _input;
    private readonly Stream _output;
    private readonly RequestHandler? _onRequest;
    private readonly Action<string, JsonElement>? _onNotification;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _closeGate = new();
    private long _nextId;
    private Task? _readLoop;
    private volatile bool _isClosed;
    private string _closeReason = "The connection is closed.";

    /// <param name="input">What the other side writes (the agent's stdout).</param>
    /// <param name="output">Where we write (the agent's stdin).</param>
    public JsonRpcConnection(Stream input, Stream output, RequestHandler? onRequest, Action<string, JsonElement>? onNotification)
    {
        _input = input;
        _output = output;
        _onRequest = onRequest;
        _onNotification = onNotification;
    }

    /// <summary>Optional diagnostics sink (ignored lines, handler failures). Never receives message bodies.</summary>
    public Action<string>? Trace { get; set; }

    /// <summary>Completes when the input stream ends or the connection is disposed.</summary>
    public Task Completion => _closed.Task;

    public bool IsClosed => _isClosed;

    public string CloseReason => _closeReason;

    public void Start()
    {
        lock (_closeGate)
        {
            if (_readLoop != null || _isClosed) return;
            _readLoop = Task.Run(ReadLoopAsync);
        }
    }

    /// <summary>Sends a request and waits for its result. Throws JsonRpcException for an error response and
    /// JsonRpcConnectionClosedException when the connection ends first.</summary>
    public async Task<JsonElement> SendRequestAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        // Close() sets the flag before sweeping _pending, so checking after the add cannot miss a close.
        if (_isClosed && _pending.TryRemove(id, out _)) throw new JsonRpcConnectionClosedException(_closeReason);

        var message = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        try
        {
            await WriteAsync(message, ct).ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }

        using var registration = ct.CanBeCanceled
            ? ct.Register(() => { if (_pending.TryRemove(id, out var p)) p.TrySetCanceled(ct); })
            : default;
        return await tcs.Task.ConfigureAwait(false);
    }

    public Task SendNotificationAsync(string method, JsonNode? parameters, CancellationToken ct = default)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters != null) message["params"] = parameters;
        return WriteAsync(message, ct);
    }

    private async Task WriteAsync(JsonObject message, CancellationToken ct)
    {
        if (_isClosed) throw new JsonRpcConnectionClosedException(_closeReason);
        var bytes = Utf8NoBom.GetBytes(message.ToJsonString() + "\n");
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _output.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _output.FlushAsync(ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            throw new JsonRpcConnectionClosedException("Could not write to the agent: " + ex.Message, ex);
        }
        catch (ObjectDisposedException ex)
        {
            throw new JsonRpcConnectionClosedException("The agent's input is closed.", ex);
        }
        catch (NotSupportedException ex)
        {
            throw new JsonRpcConnectionClosedException("The agent's input is closed.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "The agent closed its output.";
        try
        {
            using var reader = new StreamReader(_input, Utf8NoBom, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);
            while (true)
            {
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line == null) break;
                line = line.TrimStart('﻿');
                if (string.IsNullOrWhiteSpace(line)) continue;
                HandleLine(line);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "The connection was closed.";
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            reason = "Reading from the agent failed: " + ex.Message;
        }
        catch (Exception ex)
        {
            reason = "Reading from the agent failed: " + ex.Message;
            Trace?.Invoke("JSON-RPC read loop failed: " + ex);
        }
        finally
        {
            Close(reason);
        }
    }

    private void HandleLine(string line)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            // Some agents print log lines on stdout; they are not protocol messages.
            Trace?.Invoke("Ignored a non-JSON line from the agent (" + line.Length + " chars).");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;

            if (hasMethod)
            {
                var method = methodElement.GetString() ?? "";
                if (hasId)
                {
                    // Not awaited: the handler's synchronous part still runs in order with the notifications
                    // around it, but writing the answer never blocks reading (no pipe deadlock).
                    _ = HandleIncomingRequestAsync(idElement.GetRawText(), method, parameters);
                }
                else
                {
                    try
                    {
                        _onNotification?.Invoke(method, parameters);
                    }
                    catch (Exception ex)
                    {
                        Trace?.Invoke($"Notification handler for '{method}' failed: {ex.GetType().Name}: {ex.Message}");
                    }
                }
                return;
            }

            if (!hasId || !TryReadId(idElement, out var id) || !_pending.TryRemove(id, out var tcs)) return;

            if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out var ci) ? ci : InternalError;
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : "";
                var data = error.TryGetProperty("data", out var d) && d.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? d.GetRawText() : null;
                tcs.TrySetException(new JsonRpcException(code, message.Length > 0 ? message : $"Error {code}", data));
            }
            else
            {
                tcs.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }
        }
    }

    private async Task HandleIncomingRequestAsync(string rawId, string method, JsonElement parameters)
    {
        JsonObject response;
        try
        {
            if (_onRequest == null) throw new JsonRpcException(MethodNotFound, $"Method not found: {method}");
            var result = await _onRequest(method, parameters, _cts.Token).ConfigureAwait(false);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(rawId), ["result"] = result };
        }
        catch (JsonRpcException ex)
        {
            response = ErrorResponse(rawId, ex.Code, ex.Message);
        }
        catch (Exception ex)
        {
            Trace?.Invoke($"Request handler for '{method}' failed: {ex.GetType().Name}: {ex.Message}");
            response = ErrorResponse(rawId, InternalError, "Internal error: " + ex.Message);
        }

        try
        {
            await WriteAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (JsonRpcConnectionClosedException)
        {
            // The agent is gone; nobody is waiting for this answer.
        }
    }

    private static JsonObject ErrorResponse(string rawId, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = JsonNode.Parse(rawId),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    private static bool TryReadId(JsonElement element, out long id)
    {
        if (element.ValueKind == JsonValueKind.Number) return element.TryGetInt64(out id);
        return long.TryParse(element.GetString(), out id);
    }

    private void Close(string reason)
    {
        lock (_closeGate)
        {
            if (_isClosed) return;
            _closeReason = reason;
            _isClosed = true;
        }
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var tcs)) tcs.TrySetException(new JsonRpcConnectionClosedException(reason));
        }
        _closed.TrySetResult();
    }

    public async ValueTask DisposeAsync()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        Close("The connection was closed.");
        var loop = _readLoop;
        if (loop != null)
        {
            // A blocked pipe read only ends when the stream closes; the owner closes it, so do not wait long.
            try { await loop.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
            catch (Exception) { }
        }
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Core.Mcp;

/// <summary>A JSON-RPC error raised while handling a request.</summary>
public sealed class McpProtocolException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// The MCP server protocol (JSON-RPC 2.0, one UTF-8 JSON message per line, MCP stdio transport) on top of
/// an <see cref="IToolHost"/>. Use one instance per connection. Requests run concurrently so a long
/// tools/call never blocks ping or cancellation; responses are written one complete line at a time.
/// </summary>
public sealed class McpProtocolHandler : IDisposable
{
    /// <summary>Protocol revisions this server speaks, newest first.</summary>
    public static readonly IReadOnlyList<string> SupportedProtocolVersions = new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" };

    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    public const string DefaultInstructions =
        "DeskPilot operates this Windows PC for the user. Call screenshot to see the screen, then act with the mouse, " +
        "keyboard, window and UI Automation tools, one step at a time. Use coordinates exactly as the system prompt and " +
        "the screenshot text describe. Action tools return a fresh screenshot: check it before the next step. " +
        "If a tool result starts with STOPPED, stop calling tools and end the turn.";

    /// <summary>Largest incoming message accepted; client messages are tiny, so this only guards against garbage.</summary>
    internal const int MaxMessageBytes = 16 * 1024 * 1024;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // Not HTML: no need to escape <, >, & or non-ASCII. Control characters (including newlines) are still escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    private readonly IToolHost _tools;
    private readonly string _serverName;
    private readonly string _instructions;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, InflightRequest> _inflight = new(StringComparer.Ordinal);
    private readonly object _tasksGate = new();
    private readonly HashSet<Task> _tasks = new();

    public McpProtocolHandler(IToolHost tools, string serverName = "deskpilot", string? instructions = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _serverName = string.IsNullOrWhiteSpace(serverName) ? "deskpilot" : serverName;
        _instructions = instructions ?? DefaultInstructions;
    }

    public static string LatestProtocolVersion => SupportedProtocolVersions[0];

    /// <summary>The version reported in serverInfo: the product version of DeskPilot.Core.</summary>
    public static string ServerVersion { get; } = ComputeServerVersion();

    /// <summary>The protocol version agreed in initialize, or null before initialize.</summary>
    public string? NegotiatedProtocolVersion { get; private set; }

    /// <summary>clientInfo.name from initialize (for diagnostics), or null.</summary>
    public string? ClientName { get; private set; }

    /// <summary>True once the client sent notifications/initialized.</summary>
    public bool ClientInitialized { get; private set; }

    /// <summary>Requests that are still being processed.</summary>
    public int InflightCount => _inflight.Count;

    /// <summary>How long RunAsync waits for cancelled calls to finish once the connection ends.</summary>
    internal TimeSpan DrainTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Echoes the client's version when supported, otherwise offers the newest one.</summary>
    public static string NegotiateVersion(string? requested) =>
        requested != null && SupportedProtocolVersions.Contains(requested, StringComparer.Ordinal) ? requested : LatestProtocolVersion;

    /// <summary>
    /// Serves MCP on a stream pair until the input ends, ct is cancelled or the output breaks. Then cancels
    /// the calls still in flight and waits briefly for them. Call at most once per instance.
    /// </summary>
    public Task RunAsync(Stream input, Stream output, CancellationToken ct) =>
        RunAsync(new McpLineReader(input), output, ct);

    internal async Task RunAsync(McpLineReader reader, Stream output, CancellationToken ct)
    {
        var writer = new McpLineWriter(output);
        using var link = ct.Register(static s => ((McpProtocolHandler)s!).CancelAll(), this);
        try
        {
            while (!_shutdown.IsCancellationRequested && !writer.IsBroken)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(MaxMessageBytes, _shutdown.Token).ConfigureAwait(false);
                }
                catch (InvalidDataException)
                {
                    await writer.WriteLineAsync(Serialize(Error(null, InvalidRequest, "Invalid Request: message too large")), ct).ConfigureAwait(false);
                    break;
                }
                if (line == null) break;

                var work = Dispatch(line);
                if (work != null) Track(SendWhenDoneAsync(work, writer, ct));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // The connection ended; nothing to report to anyone.
        }
        finally
        {
            CancelAll();
            await DrainAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Handles one incoming line (a message or a batch) and returns the response line without the trailing
    /// newline, or null when no response is due (notifications, responses, cancelled requests).
    /// Requests are registered before this returns, so a cancellation sent right after is never missed.
    /// </summary>
    public Task<string?> HandleMessageAsync(string json)
    {
        var work = Dispatch(json);
        return work == null ? Task.FromResult<string?>(null) : ToLineAsync(work);

        static async Task<string?> ToLineAsync(Task<JsonNode?> w)
        {
            var node = await w.ConfigureAwait(false);
            if (node == null) return null;
            var bytes = Serialize(node);
            return Encoding.UTF8.GetString(bytes, 0, bytes.Length - 1);
        }
    }

    /// <summary>Cancels every in-flight request and refuses new ones.</summary>
    public void CancelAll()
    {
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }
    }

    public void Dispose() => CancelAll();

    // ---- dispatch ----

    private Task<JsonNode?>? Dispatch(string line)
    {
        if (_shutdown.IsCancellationRequested) return null;
        var text = line.TrimStart('﻿').Trim();
        if (text.Length == 0) return null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return Done(Error(null, ParseError, "Parse error: the message is not valid JSON"));
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) return DispatchSingle(root);

            if (root.GetArrayLength() == 0) return Done(Error(null, InvalidRequest, "Invalid Request: empty batch"));
            var parts = new List<Task<JsonNode?>>();
            foreach (var item in root.EnumerateArray())
            {
                var t = DispatchSingle(item);
                if (t != null) parts.Add(t);
            }
            return parts.Count == 0 ? null : CombineBatchAsync(parts);
        }
    }

    private static async Task<JsonNode?> CombineBatchAsync(List<Task<JsonNode?>> parts)
    {
        var results = await Task.WhenAll(parts).ConfigureAwait(false);
        var array = new JsonArray();
        foreach (var r in results)
            if (r != null) array.Add(r);
        return array.Count == 0 ? null : array;
    }

    private Task<JsonNode?>? DispatchSingle(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object)
            return Done(Error(null, InvalidRequest, "Invalid Request: expected a JSON object"));

        var hasId = msg.TryGetProperty("id", out var idElement);
        var method = msg.TryGetProperty("method", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
        var parameters = msg.TryGetProperty("params", out var p) ? p.Clone() : default;

        if (method == null)
        {
            // This server never sends requests, so a response from the client needs no reply.
            if (msg.TryGetProperty("result", out _) || msg.TryGetProperty("error", out _)) return null;
            return Done(Error(hasId ? IdNode(idElement) : null, InvalidRequest, "Invalid Request: 'method' is missing"));
        }

        if (!hasId)
        {
            HandleNotification(method, parameters);
            return null;
        }

        var key = IdKey(idElement);
        if (key == null) return Done(Error(null, InvalidRequest, "Invalid Request: 'id' must be a string or a number"));
        var id = IdNode(idElement);

        var request = new InflightRequest(CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token));
        // A duplicate id still in flight is a client bug; the call still runs but cannot be cancelled by id.
        var registered = _inflight.TryAdd(key, request);
        return Task.Run(() => ExecuteAsync(method, id, parameters, key, request, registered));
    }

    private void HandleNotification(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "notifications/initialized":
                ClientInitialized = true;
                break;
            case "notifications/cancelled":
                if (parameters.ValueKind == JsonValueKind.Object && parameters.TryGetProperty("requestId", out var requestId))
                {
                    var key = IdKey(requestId);
                    if (key != null && _inflight.TryGetValue(key, out var request)) request.CancelByClient();
                }
                break;
            // Every other notification (progress, roots changed, ...) is ignored, never answered.
        }
    }

    private async Task<JsonNode?> ExecuteAsync(string method, JsonNode? id, JsonElement parameters, string key, InflightRequest request, bool registered)
    {
        try
        {
            JsonNode result = method switch
            {
                "initialize" => Initialize(parameters),
                "ping" => new JsonObject(),
                "tools/list" => ListTools(),
                "tools/call" => await CallToolAsync(parameters, request.Cts.Token).ConfigureAwait(false),
                // Not advertised, but some clients ask anyway; empty lists are friendlier than errors.
                "prompts/list" => new JsonObject { ["prompts"] = new JsonArray() },
                "resources/list" => new JsonObject { ["resources"] = new JsonArray() },
                "resources/templates/list" => new JsonObject { ["resourceTemplates"] = new JsonArray() },
                _ => throw new McpProtocolException(MethodNotFound, $"Method not found: {method}"),
            };
            // MCP: no response for a request the client cancelled.
            return request.CancelledByClient ? null : Success(id, result);
        }
        catch (McpProtocolException ex)
        {
            return request.CancelledByClient ? null : Error(id, ex.Code, ex.Message);
        }
        catch (OperationCanceledException) when (request.Cts.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return request.CancelledByClient ? null : Error(id, InternalError, $"Internal error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            if (registered) _inflight.TryRemove(new KeyValuePair<string, InflightRequest>(key, request));
            request.Cts.Dispose();
        }
    }

    // ---- methods ----

    private JsonNode Initialize(JsonElement parameters)
    {
        string? requested = null;
        if (parameters.ValueKind == JsonValueKind.Object)
        {
            if (parameters.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String) requested = v.GetString();
            if (parameters.TryGetProperty("clientInfo", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String)
                ClientName = name.GetString();
        }

        var version = NegotiateVersion(requested);
        NegotiatedProtocolVersion = version;
        return new JsonObject
        {
            ["protocolVersion"] = version,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = _serverName, ["title"] = "DeskPilot", ["version"] = ServerVersion },
            ["instructions"] = _instructions,
        };
    }

    private JsonNode ListTools()
    {
        var tools = new JsonArray();
        foreach (var spec in _tools.GetTools())
        {
            JsonNode schema = spec.InputSchema.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(spec.InputSchema.GetRawText())!
                : new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() };
            tools.Add(new JsonObject
            {
                ["name"] = spec.Name,
                ["description"] = spec.Description ?? "",
                ["inputSchema"] = schema,
            });
        }
        return new JsonObject { ["tools"] = tools };
    }

    private async Task<JsonNode> CallToolAsync(JsonElement parameters, CancellationToken ct)
    {
        if (parameters.ValueKind != JsonValueKind.Object)
            throw new McpProtocolException(InvalidParams, "Invalid params: expected an object with 'name' and 'arguments'");
        var name = parameters.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new McpProtocolException(InvalidParams, "Invalid params: 'name' (the tool to call) is required");

        JsonElement arguments;
        if (!parameters.TryGetProperty("arguments", out var a) || a.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            arguments = JsonArgs.EmptyObject();
        else if (a.ValueKind == JsonValueKind.Object)
            arguments = a;
        else if (a.ValueKind == JsonValueKind.String)
            arguments = JsonArgs.ParseArguments(a.GetString());
        else
            throw new McpProtocolException(InvalidParams, "Invalid params: 'arguments' must be an object");

        ToolResult? result;
        try
        {
            result = await _tools.ExecuteAsync(name, arguments, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Tool failures belong in the result (isError) so the model can react, not in a protocol error.
            result = ToolResult.Error($"Tool '{name}' failed: {ex.GetType().Name}: {ex.Message}");
        }
        return BuildCallResult(result ?? ToolResult.Error($"Tool '{name}' returned no result."));
    }

    /// <summary>The MCP CallToolResult for a tool result: one text item first, then the images.</summary>
    internal static JsonObject BuildCallResult(ToolResult result)
    {
        var content = new JsonArray
        {
            new JsonObject { ["type"] = "text", ["text"] = string.IsNullOrWhiteSpace(result.Text) ? "(no output)" : result.Text },
        };
        foreach (var image in result.Images ?? Array.Empty<ToolImage>())
        {
            if (image == null || string.IsNullOrEmpty(image.Base64Data)) continue;
            content.Add(new JsonObject
            {
                ["type"] = "image",
                ["data"] = image.Base64Data,
                ["mimeType"] = string.IsNullOrWhiteSpace(image.MediaType) ? "image/png" : image.MediaType,
            });
        }
        return new JsonObject { ["content"] = content, ["isError"] = result.IsError };
    }

    // ---- plumbing ----

    private static Task<JsonNode?> Done(JsonNode node) => Task.FromResult<JsonNode?>(node);

    private static JsonObject Success(JsonNode? id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    /// <summary>Lookup key for a request id; string "1" and number 1 are different ids.</summary>
    private static string? IdKey(JsonElement id) => id.ValueKind switch
    {
        JsonValueKind.String => "s:" + id.GetString(),
        JsonValueKind.Number => "n:" + (id.TryGetInt64(out var l) ? l.ToString(CultureInfo.InvariantCulture) : id.GetRawText()),
        _ => null,
    };

    private static JsonNode? IdNode(JsonElement id) =>
        id.ValueKind is JsonValueKind.String or JsonValueKind.Number ? JsonNode.Parse(id.GetRawText()) : null;

    /// <summary>One message as UTF-8 JSON plus "\n" (no BOM, no embedded newlines).</summary>
    internal static byte[] Serialize(JsonNode node)
    {
        try
        {
            return SerializeCore(node);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            // E.g. text with invalid UTF-16 from a window title. Still answer the request so the client does not hang.
            var id = node is JsonObject o && o.TryGetPropertyValue("id", out var idNode) ? idNode?.DeepClone() : null;
            return SerializeCore(Error(id, InternalError, "Internal error: the response could not be encoded"));
        }
    }

    private static byte[] SerializeCore(JsonNode node)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions)) node.WriteTo(writer);
        buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }

    private static async Task SendWhenDoneAsync(Task<JsonNode?> work, McpLineWriter writer, CancellationToken ct)
    {
        JsonNode? response;
        try
        {
            response = await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return;
        }
        if (response != null) await writer.WriteLineAsync(Serialize(response), ct).ConfigureAwait(false);
    }

    private void Track(Task task)
    {
        lock (_tasksGate) _tasks.Add(task);
        task.ContinueWith(static (done, state) =>
        {
            var self = (McpProtocolHandler)state!;
            lock (self._tasksGate) self._tasks.Remove(done);
        }, this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainAsync()
    {
        Task[] pending;
        lock (_tasksGate) pending = _tasks.ToArray();
        if (pending.Length == 0) return;
        try
        {
            // A tool host that ignores cancellation must not keep the connection (or app shutdown) hanging.
            await Task.WhenAll(pending).WaitAsync(DrainTimeout).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Timed out, or a send failed; either way the connection is over.
        }
    }

    private static string ComputeServerVersion()
    {
        var assembly = typeof(McpProtocolHandler).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }
        return assembly.GetName().Version?.ToString(3) ?? "1.0.0";
    }

    private sealed class InflightRequest(CancellationTokenSource cts)
    {
        private int _cancelledByClient;

        public CancellationTokenSource Cts { get; } = cts;

        public bool CancelledByClient => Volatile.Read(ref _cancelledByClient) != 0;

        public void CancelByClient()
        {
            if (Interlocked.Exchange(ref _cancelledByClient, 1) != 0) return;
            // Cancellation callbacks can run the tool's remaining code inline; keep that off the reader loop.
            ThreadPool.QueueUserWorkItem(static request =>
            {
                try { request.Cts.Cancel(); }
                catch (ObjectDisposedException) { }
                catch (AggregateException) { }
            }, this, preferLocal: false);
        }
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Mcp;
using MemoryPipe = System.IO.Pipelines.Pipe;

namespace DeskPilot.Tests;

public class McpTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    // ---------------------------------------------------------------- fakes and helpers

    private sealed class FakeToolHost : IToolHost
    {
        public readonly ConcurrentQueue<(string Name, string Args)> Calls = new();
        public readonly TaskCompletionSource SlowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource SlowCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<ToolResult> Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource GateStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static readonly ToolImage Image = ToolImage.FromBytes(new byte[] { 1, 2, 3, 4, 5 }, "image/jpeg", 2, 2);

        public IReadOnlyList<ToolSpec> GetTools() => new[]
        {
            ToolSpec.Create("echo", "Echoes the text argument.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            ToolSpec.Create("screenshot", "Takes a screenshot.", """{"type":"object","properties":{}}"""),
            ToolSpec.Create("slow", "Never finishes unless cancelled.", """{"type":"object"}"""),
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Calls.Enqueue((name, arguments.ValueKind == JsonValueKind.Undefined ? "" : arguments.GetRawText()));
            switch (name)
            {
                case "echo":
                    return ToolResult.Ok("echo: " + (arguments.TryGetProperty("text", out var t) ? t.GetString() : "?"));
                case "screenshot":
                    return ToolResult.Ok("1280x800, foreground: Notepad", Image);
                case "fail":
                    return ToolResult.Error("something went wrong");
                case "empty":
                    return ToolResult.Ok("");
                case "throws":
                    throw new InvalidOperationException("boom");
                case "bad_text":
                    return ToolResult.Ok("bad \ud800 text");
                case "slow":
                    SlowStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        SlowCancelled.TrySetResult();
                        throw;
                    }
                    return ToolResult.Ok("unreachable");
                case "gate":
                    GateStarted.TrySetResult();
                    return await Gate.Task.WaitAsync(ct);
                case "stubborn":
                    // Ignores cancellation completely (a misbehaving tool host).
                    GateStarted.TrySetResult();
                    return await Gate.Task;
                default:
                    return ToolResult.Error($"Unknown tool '{name}'");
            }
        }
    }

    private static async Task<T> Within<T>(Task<T> task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Patience));
        Assert.True(ReferenceEquals(done, task), "timed out");
        return await task;
    }

    private static async Task Within(Task task)
    {
        var done = await Task.WhenAny(task, Task.Delay(Patience));
        Assert.True(ReferenceEquals(done, task), "timed out");
        await task;
    }

    private static JsonElement Parse(string? json)
    {
        Assert.NotNull(json);
        using var doc = JsonDocument.Parse(json!);
        return doc.RootElement.Clone();
    }

    private static string Request(object id, string method, string paramsJson = "{}") =>
        $$"""{"jsonrpc":"2.0","id":{{JsonSerializer.Serialize(id)}},"method":"{{method}}","params":{{paramsJson}}}""";

    private static string Call(object id, string tool, string argsJson = "{}") =>
        Request(id, "tools/call", $$"""{"name":"{{tool}}","arguments":{{argsJson}}}""");

    private static string Initialize(object id, string version = "2025-06-18") =>
        Request(id, "initialize", $$$"""{"protocolVersion":"{{{version}}}","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0"}}""");

    private static async Task<JsonElement> HandleAsync(McpProtocolHandler handler, string json) =>
        Parse(await Within(handler.HandleMessageAsync(json)));

    private static int ErrorCode(JsonElement response) => response.GetProperty("error").GetProperty("code").GetInt32();

    /// <summary>A client end of an MCP stdio connection made of two in-memory pipes.</summary>
    private sealed class StdioPair : IAsyncDisposable
    {
        private readonly MemoryPipe _toServer = new();
        private readonly MemoryPipe _fromServer = new();
        private readonly Stream _clientWrite;
        private readonly StreamReader _clientRead;

        public StdioPair()
        {
            ServerInput = _toServer.Reader.AsStream();
            ServerOutput = _fromServer.Writer.AsStream();
            _clientWrite = _toServer.Writer.AsStream();
            _clientRead = new StreamReader(_fromServer.Reader.AsStream(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        }

        public Stream ServerInput { get; }
        public Stream ServerOutput { get; }

        public async Task SendAsync(string line)
        {
            await _clientWrite.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await _clientWrite.FlushAsync();
        }

        public async Task<string> ReadLineAsync()
        {
            using var cts = new CancellationTokenSource(Patience);
            var line = await _clientRead.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            return line!;
        }

        public async Task<JsonElement> ReadMessageAsync() => Parse(await ReadLineAsync());

        /// <summary>Reads lines until the response with this id arrives (other responses are collected).</summary>
        public async Task<JsonElement> ReadResponseAsync(int id)
        {
            while (true)
            {
                var msg = await ReadMessageAsync();
                if (msg.TryGetProperty("id", out var i) && i.ValueKind == JsonValueKind.Number && i.GetInt32() == id) return msg;
            }
        }

        public void CloseInput() => _toServer.Writer.Complete();

        /// <summary>Returns true when the server side finished writing (end of stream) within the patience window.</summary>
        public async Task<string?> ReadToEndOrNullAsync()
        {
            using var cts = new CancellationTokenSource(Patience);
            return await _clientRead.ReadLineAsync(cts.Token);
        }

        public void CompleteServerOutput() => _fromServer.Writer.Complete();

        public ValueTask DisposeAsync()
        {
            _toServer.Writer.Complete();
            _toServer.Reader.Complete();
            _fromServer.Writer.Complete();
            _fromServer.Reader.Complete();
            return ValueTask.CompletedTask;
        }
    }

    private static McpPipeServer NewServer(FakeToolHost tools, ConcurrentQueue<string>? diagnostics = null)
    {
        var server = new McpPipeServer(tools);
        server.Diagnostics = m => diagnostics?.Enqueue(m);
        return server;
    }

    // ---------------------------------------------------------------- initialize / version negotiation

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2025-06-18")]
    [InlineData("2025-03-26")]
    [InlineData("2024-11-05")]
    public async Task Initialize_echoes_a_supported_version(string version)
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, Initialize(1, version));
        Assert.Equal(version, response.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal(version, handler.NegotiatedProtocolVersion);
    }

    [Theory]
    [InlineData("1999-01-01")]
    [InlineData("2030-01-01")]
    [InlineData("")]
    public async Task Initialize_offers_the_newest_version_when_the_requested_one_is_unknown(string version)
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, Initialize(1, version));
        Assert.Equal("2025-11-25", response.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public async Task Initialize_without_params_still_answers_with_the_newest_version()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, """{"jsonrpc":"2.0","id":1,"method":"initialize"}""");
        Assert.Equal(McpProtocolHandler.LatestProtocolVersion, response.GetProperty("result").GetProperty("protocolVersion").GetString());
    }

    [Fact]
    public void NegotiateVersion_matches_exactly()
    {
        Assert.Equal("2024-11-05", McpProtocolHandler.NegotiateVersion("2024-11-05"));
        Assert.Equal("2025-11-25", McpProtocolHandler.NegotiateVersion(null));
        Assert.Equal("2025-11-25", McpProtocolHandler.NegotiateVersion("2024-11-05 "));
        Assert.Equal(new[] { "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05" }, McpProtocolHandler.SupportedProtocolVersions);
    }

    [Fact]
    public async Task Initialize_reports_server_info_capabilities_and_instructions()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, Initialize("init-1"));

        Assert.Equal("2.0", response.GetProperty("jsonrpc").GetString());
        Assert.Equal("init-1", response.GetProperty("id").GetString());
        var result = response.GetProperty("result");
        Assert.False(result.GetProperty("capabilities").GetProperty("tools").GetProperty("listChanged").GetBoolean());
        var info = result.GetProperty("serverInfo");
        Assert.Equal("deskpilot", info.GetProperty("name").GetString());
        var version = info.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Equal(McpProtocolHandler.ServerVersion, version);
        Assert.DoesNotContain("+", version);
        Assert.False(string.IsNullOrWhiteSpace(result.GetProperty("instructions").GetString()));
        Assert.Equal("test-client", handler.ClientName);
    }

    [Fact]
    public async Task Initialize_uses_the_configured_server_name()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost(), "custom", "Custom instructions.");
        var result = (await HandleAsync(handler, Initialize(1))).GetProperty("result");
        Assert.Equal("custom", result.GetProperty("serverInfo").GetProperty("name").GetString());
        Assert.Equal("Custom instructions.", result.GetProperty("instructions").GetString());
    }

    // ---------------------------------------------------------------- notifications, ping, errors

    [Fact]
    public async Task Notifications_never_get_a_reply()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""")));
        Assert.True(handler.ClientInitialized);
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/progress","params":{"progressToken":1,"progress":5}}""")));
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"something/unknown"}""")));
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":12345}}""")));
    }

    [Fact]
    public async Task Responses_from_the_client_are_ignored()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","id":5,"result":{}}""")));
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","id":6,"error":{"code":-1,"message":"x"}}""")));
    }

    [Fact]
    public async Task Ping_returns_an_empty_object()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, """{"jsonrpc":"2.0","id":42,"method":"ping"}""");
        Assert.Equal(42, response.GetProperty("id").GetInt32());
        var result = response.GetProperty("result");
        Assert.Equal(JsonValueKind.Object, result.ValueKind);
        Assert.Empty(result.EnumerateObject());
        Assert.False(response.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Unknown_method_is_method_not_found_with_the_same_id()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, """{"jsonrpc":"2.0","id":"abc","method":"sampling/createMessage","params":{}}""");
        Assert.Equal(-32601, ErrorCode(response));
        Assert.Equal("abc", response.GetProperty("id").GetString());
        Assert.False(response.TryGetProperty("result", out _));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":")]
    [InlineData("nonsense")]
    public async Task Malformed_json_is_a_parse_error_with_null_id(string line)
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, line);
        Assert.Equal(-32700, ErrorCode(response));
        Assert.Equal(JsonValueKind.Null, response.GetProperty("id").ValueKind);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":3}""")]
    [InlineData("""42""")]
    [InlineData("""{"jsonrpc":"2.0","id":{"x":1},"method":"ping"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":null,"method":"ping"}""")]
    public async Task Invalid_requests_get_invalid_request(string line)
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, line);
        Assert.Equal(-32600, ErrorCode(response));
    }

    [Fact]
    public async Task Blank_lines_are_ignored_and_a_bom_is_tolerated()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        Assert.Null(await Within(handler.HandleMessageAsync("   ")));
        var response = await HandleAsync(handler, "﻿{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}");
        Assert.Equal(1, response.GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Batches_return_an_array_of_responses_without_notifications()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler,
            """[{"jsonrpc":"2.0","id":1,"method":"ping"},{"jsonrpc":"2.0","method":"notifications/initialized"},{"jsonrpc":"2.0","id":2,"method":"nope"},{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"echo","arguments":{"text":"hi"}}}]""");
        Assert.Equal(JsonValueKind.Array, response.ValueKind);
        var items = response.EnumerateArray().ToList();
        Assert.Equal(3, items.Count);
        Assert.Equal(1, items[0].GetProperty("id").GetInt32());
        Assert.Equal(-32601, ErrorCode(items[1]));
        Assert.Equal("echo: hi", items[2].GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        Assert.True(handler.ClientInitialized);
    }

    [Fact]
    public async Task Batches_of_only_notifications_get_no_reply_and_empty_batches_are_invalid()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        Assert.Null(await Within(handler.HandleMessageAsync("""[{"jsonrpc":"2.0","method":"notifications/initialized"}]""")));
        var response = await HandleAsync(handler, "[]");
        Assert.Equal(-32600, ErrorCode(response));
    }

    [Fact]
    public async Task Prompts_and_resources_lists_are_empty()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        Assert.Empty((await HandleAsync(handler, Request(1, "prompts/list"))).GetProperty("result").GetProperty("prompts").EnumerateArray());
        Assert.Empty((await HandleAsync(handler, Request(2, "resources/list"))).GetProperty("result").GetProperty("resources").EnumerateArray());
    }

    // ---------------------------------------------------------------- tools

    [Fact]
    public async Task ToolsList_comes_from_the_tool_host()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var tools = (await HandleAsync(handler, Request(1, "tools/list"))).GetProperty("result").GetProperty("tools").EnumerateArray().ToList();

        Assert.Equal(host.GetTools().Select(t => t.Name), tools.Select(t => t.GetProperty("name").GetString()));
        var echo = tools[0];
        Assert.Equal("Echoes the text argument.", echo.GetProperty("description").GetString());
        var schema = echo.GetProperty("inputSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("text").GetProperty("type").GetString());
        Assert.Equal("text", schema.GetProperty("required")[0].GetString());
    }

    [Fact]
    public async Task ToolsCall_passes_arguments_and_returns_text()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var result = (await HandleAsync(handler, Call(5, "echo", """{"text":"héllo <world> & \"quotes\"\nline2"}"""))).GetProperty("result");

        Assert.False(result.GetProperty("isError").GetBoolean());
        var content = result.GetProperty("content").EnumerateArray().ToList();
        Assert.Single(content);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("echo: héllo <world> & \"quotes\"\nline2", content[0].GetProperty("text").GetString());
        Assert.True(host.Calls.TryDequeue(out var call));
        Assert.Equal("echo", call.Name);
    }

    [Fact]
    public async Task ToolsCall_returns_images_after_the_text()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var content = (await HandleAsync(handler, Call(1, "screenshot"))).GetProperty("result").GetProperty("content").EnumerateArray().ToList();

        Assert.Equal(2, content.Count);
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("1280x800, foreground: Notepad", content[0].GetProperty("text").GetString());
        Assert.Equal("image", content[1].GetProperty("type").GetString());
        Assert.Equal(FakeToolHost.Image.Base64Data, content[1].GetProperty("data").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("mimeType").GetString());
    }

    [Fact]
    public async Task ToolsCall_error_results_set_isError()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var result = (await HandleAsync(handler, Call(1, "fail"))).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Equal("something went wrong", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_unknown_tool_is_an_error_result_from_the_host()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var result = (await HandleAsync(handler, Call(1, "no_such_tool"))).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("no_such_tool", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_empty_text_becomes_no_output()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var content = (await HandleAsync(handler, Call(1, "empty"))).GetProperty("result").GetProperty("content");
        Assert.Equal("(no output)", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_exceptions_become_error_results()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var result = (await HandleAsync(handler, Call(1, "throws"))).GetProperty("result");
        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("boom", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task ToolsCall_with_invalid_utf16_text_still_answers_the_request()
    {
        using var handler = new McpProtocolHandler(new FakeToolHost());
        var response = await HandleAsync(handler, Call(9, "bad_text"));
        Assert.Equal(9, response.GetProperty("id").GetInt32());
        // The writer replaces the lone surrogate instead of failing the whole response.
        var text = response.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString();
        Assert.StartsWith("bad ", text);
        Assert.EndsWith(" text", text);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"","arguments":{}}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":5}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call"}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":[1,2]}}""")]
    public async Task ToolsCall_with_bad_params_is_invalid_params(string line)
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var response = await HandleAsync(handler, line);
        Assert.Equal(-32602, ErrorCode(response));
        Assert.Equal(1, response.GetProperty("id").GetInt32());
        Assert.Empty(host.Calls);
    }

    [Fact]
    public async Task ToolsCall_without_arguments_passes_an_empty_object()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        await HandleAsync(handler, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"screenshot"}}""");
        Assert.True(host.Calls.TryDequeue(out var call));
        Assert.Equal("{}", call.Args);
    }

    [Fact]
    public async Task ToolsCall_accepts_string_encoded_arguments()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var result = (await HandleAsync(handler, """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"echo","arguments":"{\"text\":\"x\"}"}}""")).GetProperty("result");
        Assert.Equal("echo: x", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    // ---------------------------------------------------------------- concurrency and cancellation

    [Fact]
    public async Task Cancelled_notification_cancels_the_call_and_suppresses_its_response()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var call = handler.HandleMessageAsync(Call(7, "slow"));
        await Within(host.SlowStarted.Task);
        Assert.Equal(1, handler.InflightCount);

        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7,"reason":"user pressed stop"}}""")));

        await Within(host.SlowCancelled.Task);
        Assert.Null(await Within(call));
        Assert.Equal(0, handler.InflightCount);

        // The handler keeps working after a cancellation.
        Assert.Equal(8, (await HandleAsync(handler, """{"jsonrpc":"2.0","id":8,"method":"ping"}""")).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Cancellation_matches_string_ids_exactly()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var call = handler.HandleMessageAsync(Call("7", "slow"));
        await Within(host.SlowStarted.Task);

        // Number 7 is a different id than string "7".
        await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":7}}"""));
        await Task.Delay(50);
        Assert.False(host.SlowCancelled.Task.IsCompleted);

        await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":"7"}}"""));
        await Within(host.SlowCancelled.Task);
        Assert.Null(await Within(call));
    }

    [Fact]
    public async Task Ping_is_answered_while_a_slow_call_runs()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        var call = handler.HandleMessageAsync(Call(1, "gate"));
        await Within(host.GateStarted.Task);

        var ping = await HandleAsync(handler, """{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        Assert.Equal(2, ping.GetProperty("id").GetInt32());
        Assert.False(call.IsCompleted);

        host.Gate.SetResult(ToolResult.Ok("released"));
        var result = Parse(await Within(call));
        Assert.Equal("released", result.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task CancelAll_cancels_in_flight_calls_and_refuses_new_messages()
    {
        var host = new FakeToolHost();
        var handler = new McpProtocolHandler(host);
        var call = handler.HandleMessageAsync(Call(1, "slow"));
        await Within(host.SlowStarted.Task);
        handler.Dispose();
        await Within(host.SlowCancelled.Task);
        Assert.Null(await Within(call));
        Assert.Null(await Within(handler.HandleMessageAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""")));
    }

    // ---------------------------------------------------------------- RunAsync over streams

    [Fact]
    public async Task RunAsync_serves_lines_concurrently_and_writes_one_message_per_line()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        await using var io = new StdioPair();
        var run = handler.RunAsync(io.ServerInput, io.ServerOutput, CancellationToken.None);

        await io.SendAsync(Initialize(1));
        var init = await io.ReadMessageAsync();
        Assert.Equal(1, init.GetProperty("id").GetInt32());
        await io.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        // A slow call must not block anything behind it.
        await io.SendAsync(Call(2, "gate"));
        await Within(host.GateStarted.Task);
        await io.SendAsync("""{"jsonrpc":"2.0","id":3,"method":"ping"}""");
        Assert.Equal(3, (await io.ReadMessageAsync()).GetProperty("id").GetInt32());
        await io.SendAsync(Call(4, "screenshot"));
        var shot = await io.ReadMessageAsync();
        Assert.Equal(4, shot.GetProperty("id").GetInt32());
        Assert.Equal(2, shot.GetProperty("result").GetProperty("content").GetArrayLength());

        host.Gate.SetResult(ToolResult.Ok("multi\nline\r\ntext"));
        var raw = await io.ReadLineAsync();
        Assert.False(raw.StartsWith('﻿'));
        var gate = Parse(raw);
        Assert.Equal(2, gate.GetProperty("id").GetInt32());
        Assert.Equal("multi\nline\r\ntext", gate.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        // Malformed input does not end the session.
        await io.SendAsync("{oops");
        Assert.Equal(-32700, ErrorCode(await io.ReadMessageAsync()));
        await io.SendAsync("""{"jsonrpc":"2.0","id":5,"method":"ping"}""");
        Assert.Equal(5, (await io.ReadMessageAsync()).GetProperty("id").GetInt32());

        io.CloseInput();
        await Within(run);
    }

    [Fact]
    public async Task RunAsync_cancellation_notification_over_the_stream()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        await using var io = new StdioPair();
        var run = handler.RunAsync(io.ServerInput, io.ServerOutput, CancellationToken.None);

        await io.SendAsync(Call(1, "slow"));
        await Within(host.SlowStarted.Task);
        await io.SendAsync("""{"jsonrpc":"2.0","method":"notifications/cancelled","params":{"requestId":1}}""");
        await Within(host.SlowCancelled.Task);
        await io.SendAsync("""{"jsonrpc":"2.0","id":2,"method":"ping"}""");
        // The next line is the ping: the cancelled call never answers.
        Assert.Equal(2, (await io.ReadMessageAsync()).GetProperty("id").GetInt32());

        io.CloseInput();
        await Within(run);
    }

    [Fact]
    public async Task RunAsync_end_of_input_cancels_in_flight_calls()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        await using var io = new StdioPair();
        var run = handler.RunAsync(io.ServerInput, io.ServerOutput, CancellationToken.None);

        await io.SendAsync(Call(1, "slow"));
        await Within(host.SlowStarted.Task);
        io.CloseInput();
        await Within(host.SlowCancelled.Task);
        await Within(run);
    }

    [Fact]
    public async Task RunAsync_stops_when_the_token_is_cancelled()
    {
        var host = new FakeToolHost();
        using var handler = new McpProtocolHandler(host);
        await using var io = new StdioPair();
        using var cts = new CancellationTokenSource();
        var run = handler.RunAsync(io.ServerInput, io.ServerOutput, cts.Token);

        await io.SendAsync(Call(1, "slow"));
        await Within(host.SlowStarted.Task);
        cts.Cancel();
        await Within(host.SlowCancelled.Task);
        await Within(run);
    }

    [Fact]
    public async Task LineReader_splits_lines_and_handles_crlf_partial_reads_and_limits()
    {
        var bytes = Encoding.UTF8.GetBytes("first\r\nsecond\n\nthird-no-newline");
        var reader = new McpLineReader(new TrickleStream(bytes), 256);
        Assert.Equal("first", await reader.ReadLineAsync(1000, CancellationToken.None));
        Assert.Equal("second", await reader.ReadLineAsync(1000, CancellationToken.None));
        Assert.Equal("", await reader.ReadLineAsync(1000, CancellationToken.None));
        Assert.Equal("third-no-newline", await reader.ReadLineAsync(1000, CancellationToken.None));
        Assert.Null(await reader.ReadLineAsync(1000, CancellationToken.None));

        var big = new McpLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 5000) + "\n")), 256);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await big.ReadLineAsync(1000, CancellationToken.None));

        var grow = new McpLineReader(new MemoryStream(Encoding.UTF8.GetBytes(new string('y', 5000) + "\nnext\n")), 256);
        Assert.Equal(5000, (await grow.ReadLineAsync(10_000, CancellationToken.None))!.Length);
        Assert.Equal("next", await grow.ReadLineAsync(10_000, CancellationToken.None));
    }

    /// <summary>Returns at most 3 bytes per read, to exercise line reassembly.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(3, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // ---------------------------------------------------------------- pipe server + bridge

    [Fact]
    public async Task Server_identity_and_endpoint()
    {
        await using var a = NewServer(new FakeToolHost());
        await using var b = NewServer(new FakeToolHost());

        Assert.Equal("deskpilot", a.ServerName);
        Assert.StartsWith("DeskPilot-mcp-", a.PipeName);
        Assert.Matches("^DeskPilot-mcp-[0-9a-f]{32}$", a.PipeName);
        Assert.Matches("^[0-9a-f]{64}$", a.Token);
        Assert.NotEqual(a.PipeName, b.PipeName);
        Assert.NotEqual(a.Token, b.Token);

        var exe = Path.Combine(Path.GetTempPath(), "DeskPilot.exe");
        var endpoint = a.GetEndpoint(exe);
        Assert.Equal("deskpilot", endpoint.ServerName);
        Assert.Equal(exe, endpoint.Command);
        Assert.Equal(new[] { "--mcp-bridge", a.PipeName, a.Token }, endpoint.Args);
        Assert.Equal(McpBridge.Switch, endpoint.Args[0]);
    }

    [Fact]
    public async Task Server_start_and_dispose_are_idempotent()
    {
        var server = NewServer(new FakeToolHost());
        server.Start();
        server.Start();
        await Within(server.DisposeAsync().AsTask());
        await Within(server.DisposeAsync().AsTask());
        Assert.Throws<ObjectDisposedException>(() => server.Start());

        // Disposing a server that never started is fine too.
        var idle = NewServer(new FakeToolHost());
        await Within(idle.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Bridge_and_server_end_to_end()
    {
        var host = new FakeToolHost();
        await using var server = NewServer(host);
        server.Start();
        await using var io = new StdioPair();
        var stderr = new MemoryStream();
        var bridge = McpBridge.RunAsync(io.ServerInput, io.ServerOutput, stderr, server.PipeName, server.Token, CancellationToken.None);

        await io.SendAsync(Initialize(1, "2025-03-26"));
        var init = await io.ReadMessageAsync();
        Assert.Equal("2025-03-26", init.GetProperty("result").GetProperty("protocolVersion").GetString());
        Assert.Equal("deskpilot", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
        await io.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

        await io.SendAsync(Request(2, "tools/list"));
        var list = await io.ReadMessageAsync();
        Assert.Equal(2, list.GetProperty("id").GetInt32());
        Assert.Equal(3, list.GetProperty("result").GetProperty("tools").GetArrayLength());

        await io.SendAsync(Call(3, "echo", """{"text":"through the pipe"}"""));
        var echo = await io.ReadMessageAsync();
        Assert.Equal("echo: through the pipe", echo.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

        await io.SendAsync(Call(4, "screenshot"));
        var shot = await io.ReadMessageAsync();
        Assert.Equal(FakeToolHost.Image.Base64Data, shot.GetProperty("result").GetProperty("content")[1].GetProperty("data").GetString());
        Assert.Equal(1, server.ActiveConnections);

        io.CloseInput();
        Assert.Equal(McpBridge.ExitOk, await Within(bridge));
        Assert.Equal(0, stderr.Length);

        // The server notices the bridge left.
        var sw = Stopwatch.StartNew();
        while (server.ActiveConnections > 0 && sw.Elapsed < Patience) await Task.Delay(20);
        Assert.Equal(0, server.ActiveConnections);
    }

    [Fact]
    public async Task Server_accepts_concurrent_and_sequential_bridges()
    {
        var host = new FakeToolHost();
        await using var server = NewServer(host);
        server.Start();

        async Task<int> SessionAsync(int id)
        {
            await using var io = new StdioPair();
            var bridge = McpBridge.RunAsync(io.ServerInput, io.ServerOutput, Stream.Null, server.PipeName, server.Token, CancellationToken.None);
            await io.SendAsync(Initialize(id));
            Assert.Equal(id, (await io.ReadMessageAsync()).GetProperty("id").GetInt32());
            await io.SendAsync(Call(id + 1, "echo", $$"""{"text":"s{{id}}"}"""));
            var echo = await io.ReadMessageAsync();
            Assert.Equal($"echo: s{id}", echo.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
            io.CloseInput();
            return await Within(bridge);
        }

        var codes = await Within(Task.WhenAll(SessionAsync(10), SessionAsync(20), SessionAsync(30)));
        Assert.All(codes, c => Assert.Equal(McpBridge.ExitOk, c));

        // An agent restarting its MCP server connects again later.
        Assert.Equal(McpBridge.ExitOk, await SessionAsync(40));
        Assert.True(server.TotalConnections >= 4);
    }

    [Fact]
    public async Task Wrong_token_gets_disconnected_without_a_response()
    {
        var diagnostics = new ConcurrentQueue<string>();
        await using var server = NewServer(new FakeToolHost(), diagnostics);
        server.Start();

        using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(10_000);
        try
        {
            var payload = Encoding.UTF8.GetBytes("{\"deskpilot_token\":\"" + new string('0', 64) + "\"}\n" + Initialize(1) + "\n");
            await client.WriteAsync(payload);
            await client.FlushAsync();
        }
        catch (IOException)
        {
            // The server may already have closed the pipe.
        }

        var buffer = new byte[1024];
        int read;
        try
        {
            read = await Within(client.ReadAsync(buffer).AsTask());
        }
        catch (IOException)
        {
            read = 0;
        }
        Assert.Equal(0, read);
        Assert.Contains(diagnostics, m => m.Contains("token"));
        Assert.DoesNotContain(diagnostics, m => m.Contains(server.Token));
    }

    [Theory]
    [InlineData("{\"deskpilot_token\": \"TOKEN\"}")]
    [InlineData("{\"deskpilot_token\":\"TOKEN\"} ")]
    [InlineData("TOKEN")]
    [InlineData("")]
    public async Task Token_line_must_match_exactly(string template)
    {
        await using var server = NewServer(new FakeToolHost());
        server.Start();
        using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(10_000);
        try
        {
            await client.WriteAsync(Encoding.UTF8.GetBytes(template.Replace("TOKEN", server.Token) + "\n" + Request(1, "ping") + "\n"));
            await client.FlushAsync();
        }
        catch (IOException)
        {
        }
        var buffer = new byte[1024];
        int read;
        try { read = await Within(client.ReadAsync(buffer).AsTask()); }
        catch (IOException) { read = 0; }
        Assert.Equal(0, read);
    }

    [Fact]
    public async Task Raw_pipe_client_with_the_right_token_is_served()
    {
        await using var server = NewServer(new FakeToolHost());
        server.Start();
        using var client = new NamedPipeClientStream(".", server.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await client.ConnectAsync(10_000);
        await client.WriteAsync(Encoding.UTF8.GetBytes("{\"deskpilot_token\":\"" + server.Token + "\"}\r\n" + Request(1, "ping") + "\n"));
        await client.FlushAsync();
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        using var cts = new CancellationTokenSource(Patience);
        var line = await reader.ReadLineAsync(cts.Token);
        Assert.Equal(1, Parse(line).GetProperty("id").GetInt32());
    }

    [Fact]
    public async Task Dispose_while_a_call_is_in_flight_does_not_hang()
    {
        var host = new FakeToolHost();
        var server = NewServer(host);
        server.Start();
        await using var io = new StdioPair();
        var stderr = new MemoryStream();
        var bridge = McpBridge.RunAsync(io.ServerInput, io.ServerOutput, stderr, server.PipeName, server.Token, CancellationToken.None);

        await io.SendAsync(Initialize(1));
        await io.ReadMessageAsync();
        await io.SendAsync(Call(2, "slow"));
        await Within(host.SlowStarted.Task);

        var sw = Stopwatch.StartNew();
        await Within(server.DisposeAsync().AsTask());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"dispose took {sw.Elapsed}");
        await Within(host.SlowCancelled.Task);

        // The bridge sees the pipe close and exits normally, writing nothing more to stdout.
        Assert.Equal(McpBridge.ExitOk, await Within(bridge));
        Assert.Contains("closed the connection", Encoding.UTF8.GetString(stderr.ToArray()));
        io.CompleteServerOutput();
        Assert.Null(await io.ReadToEndOrNullAsync());
    }

    [Fact]
    public async Task Dispose_is_bounded_even_when_a_tool_ignores_cancellation()
    {
        var host = new FakeToolHost();
        var server = NewServer(host);
        server.DisposeTimeout = TimeSpan.FromMilliseconds(600);
        server.ConnectionDrainTimeout = TimeSpan.FromMilliseconds(200);
        server.Start();
        await using var io = new StdioPair();
        var bridge = McpBridge.RunAsync(io.ServerInput, io.ServerOutput, Stream.Null, server.PipeName, server.Token, CancellationToken.None);

        await io.SendAsync(Call(1, "stubborn"));
        await Within(host.GateStarted.Task);

        var sw = Stopwatch.StartNew();
        await Within(server.DisposeAsync().AsTask());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"dispose took {sw.Elapsed}");
        Assert.Equal(McpBridge.ExitOk, await Within(bridge));

        // Let the stuck call finish; its late response must not throw anywhere.
        host.Gate.SetResult(ToolResult.Ok("late"));
        await Task.Delay(50);
    }

    [Fact]
    public async Task Bridge_reports_when_it_cannot_connect()
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var missing = McpPipeServer.PipeNamePrefix + Guid.NewGuid().ToString("N");
        var sw = Stopwatch.StartNew();
        var code = await Within(McpBridge.RunAsync(new MemoryStream(), stdout, stderr, missing, "abc", TimeSpan.FromMilliseconds(400), CancellationToken.None));

        Assert.Equal(McpBridge.ExitCannotConnect, code);
        Assert.Equal(0, stdout.Length);
        Assert.Equal(McpBridge.NotRunningMessage + Environment.NewLine, Encoding.UTF8.GetString(stderr.ToArray()));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Bridge_cannot_connect_after_the_server_is_disposed()
    {
        var server = NewServer(new FakeToolHost());
        server.Start();
        var pipe = server.PipeName;
        var token = server.Token;
        await server.DisposeAsync();

        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var code = await Within(McpBridge.RunAsync(new MemoryStream(), stdout, stderr, pipe, token, TimeSpan.FromMilliseconds(400), CancellationToken.None));
        Assert.Equal(McpBridge.ExitCannotConnect, code);
        Assert.Equal(0, stdout.Length);
    }

    // Arguments joined with '|' (InlineData cannot carry a string[] parameter cleanly).
    [Theory]
    [InlineData("")]
    [InlineData("--mcp-bridge")]
    [InlineData("--mcp-bridge|pipe")]
    [InlineData("--mcp-bridge||token")]
    [InlineData("--mcp-bridge|pipe|")]
    [InlineData("--mcp-bridge|a\\b|token")]
    [InlineData("--mcp-bridge|pipe|to\"ken")]
    [InlineData("--mcp-bridge|pipe|to ken")]
    [InlineData("--mcp-bridge|pipe|token|extra")]
    [InlineData("--other|pipe|token")]
    public void Bridge_rejects_bad_arguments(string joined)
    {
        var args = joined.Length == 0 ? Array.Empty<string>() : joined.Split('|');
        Assert.False(McpBridge.TryParseArgs(args, out _, out _));
        Assert.False(McpBridge.TryParseArgs(null, out _, out _));
    }

    [Fact]
    public void Bridge_accepts_the_endpoint_arguments()
    {
        Assert.True(McpBridge.TryParseArgs(new[] { "--mcp-bridge", "DeskPilot-mcp-0123", "abcdef" }, out var pipe, out var token));
        Assert.Equal("DeskPilot-mcp-0123", pipe);
        Assert.Equal("abcdef", token);
        Assert.True(McpBridge.TryParseArgs(new[] { "--MCP-BRIDGE", "p", "t" }, out _, out _));
    }

    [Fact]
    public async Task Bridge_RunAsync_with_bad_arguments_returns_2_without_stdout()
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var code = await McpBridge.RunAsync(new MemoryStream(), stdout, stderr, "bad\\pipe", "token", CancellationToken.None);
        Assert.Equal(McpBridge.ExitBadArguments, code);
        Assert.Equal(0, stdout.Length);
        Assert.True(stderr.Length > 0);
    }

    [Fact]
    public void Bridge_Run_with_bad_arguments_returns_2()
    {
        Assert.Equal(McpBridge.ExitBadArguments, McpBridge.Run(new[] { "--mcp-bridge" }));
    }
}

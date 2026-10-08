using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Http;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Tests;

public class HttpBackendTests
{
    private const string Key = "sk-test-SECRET-abcdef123456";
    private static readonly ToolImage Png = new("image/png", "aGVsbG8=", 10, 10);

    // =====================================================================================
    // Test infrastructure: a recording HTTP handler, a fake tool host and a backend harness
    // =====================================================================================

    private sealed record Recorded(HttpMethod Method, Uri Uri, Dictionary<string, string> Headers, string Body)
    {
        public JsonObject Json => (JsonObject)JsonNode.Parse(Body)!;
        public string? Header(string name) => Headers.TryGetValue(name, out var v) ? v : null;
        public JsonArray Messages => (JsonArray)Json["messages"]!;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Queue<Func<Recorded, CancellationToken, Task<HttpResponseMessage>>> _queue = new();
        public List<Recorded> Requests { get; } = new();
        public Func<Recorded, CancellationToken, Task<HttpResponseMessage>>? Default { get; set; }

        public FakeHandler Reply(string body, int status = 200, Action<HttpResponseMessage>? configure = null)
        {
            _queue.Enqueue((_, _) =>
            {
                var r = Response(body, status);
                configure?.Invoke(r);
                return Task.FromResult(r);
            });
            return this;
        }

        public FakeHandler Throw(Exception ex)
        {
            _queue.Enqueue((_, _) => Task.FromException<HttpResponseMessage>(ex));
            return this;
        }

        public FakeHandler Hang()
        {
            _queue.Enqueue(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                throw new InvalidOperationException("unreachable");
            });
            return this;
        }

        public FakeHandler Then(Func<Recorded, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _queue.Enqueue(responder);
            return this;
        }

        public static HttpResponseMessage Response(string body, int status = 200) =>
            new((HttpStatusCode)status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in request.Headers) headers[h.Key] = string.Join(",", h.Value);
            if (request.Content != null)
                foreach (var h in request.Content.Headers) headers[h.Key] = string.Join(",", h.Value);
            var body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(ct);
            var rec = new Recorded(request.Method, request.RequestUri!, headers, body);
            Func<Recorded, CancellationToken, Task<HttpResponseMessage>>? responder;
            lock (Requests)
            {
                Requests.Add(rec);
                responder = _queue.Count > 0 ? _queue.Dequeue() : Default;
            }
            if (responder == null) throw new InvalidOperationException($"No canned response for {request.Method} {request.RequestUri}");
            return await responder(rec, ct);
        }
    }

    private sealed class FakeTools : IToolHost
    {
        public List<(string Name, string Args)> Calls { get; } = new();
        public Func<string, JsonElement, CancellationToken, Task<ToolResult>>? Handler { get; set; }

        public IReadOnlyList<ToolSpec> GetTools() => new[]
        {
            ToolSpec.Create("screenshot", "Take a screenshot", "{\"type\":\"object\",\"properties\":{}}"),
            ToolSpec.Create("click", "Click at a point", "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\"},\"y\":{\"type\":\"integer\"}},\"required\":[\"x\",\"y\"]}"),
            ToolSpec.Create("type_text", "Type text", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}"),
        };

        public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Calls.Add((name, arguments.GetRawText()));
            if (Handler != null) return await Handler(name, arguments, ct);
            return name == "screenshot" ? ToolResult.Ok("Screen 1280x800", Png) : ToolResult.Ok($"{name} done");
        }
    }

    private sealed class Harness
    {
        public FakeHandler Http { get; } = new();
        public FakeTools Tools { get; } = new();
        public List<AgentEvent> Events { get; } = new();
        public AgentRunControl Control { get; } = new();
        public AppSettings Settings { get; } = new();
        public List<TimeSpan> Delays { get; } = new();
        public List<string> Logs { get; } = new();
        public HttpAgentBackend Backend { get; }
        public ProviderProfile Profile { get; }

        public Harness(ProviderProfile profile)
        {
            Profile = profile;
            Backend = new HttpAgentBackend(new HttpClient(Http))
            {
                Delay = (d, _) =>
                {
                    Delays.Add(d);
                    return Task.CompletedTask;
                },
                LogWarning = m => Logs.Add(m),
            };
        }

        public AgentBackendContext Context(string key = Key) => new()
        {
            Settings = Settings,
            Profile = Profile,
            SystemPrompt = "SYSTEM PROMPT",
            Tools = Tools,
            Emit = e => { lock (Events) Events.Add(e); },
            Control = Control,
            WorkingDirectory = "unused",
            ApiKey = key,
        };

        public async Task<Harness> StartAsync(string key = Key)
        {
            Control.BeginTurn();
            await Backend.StartAsync(Context(key), CancellationToken.None);
            return this;
        }

        public Task<TurnResult> TurnAsync(string text, CancellationToken ct = default) => Backend.RunTurnAsync(new UserTurn(text), ct);

        public string AllEventText() => string.Join("\n", Events.Select(e => e.ToString()));
    }

    private static ProviderProfile AnthropicProfile(string model = "claude-haiku-5-5", bool caching = false, bool contextEditing = false)
    {
        var p = ProviderPresets.CreateProfile(ProviderPresets.AnthropicApiId);
        p.Model = model;
        p.UsePromptCaching = caching;
        p.UseContextEditing = contextEditing;
        return p;
    }

    private static ProviderProfile OpenAiProfile(string baseUrl = "https://example.test/v1", string model = "vision-model", ReasoningStyle style = ReasoningStyle.None)
    {
        var p = ProviderPresets.CreateProfile(ProviderPresets.LocalOpenAiId);
        p.Name = "Test Server";
        p.BaseUrl = baseUrl;
        p.Model = model;
        p.ReasoningStyle = style;
        return p;
    }

    private static ProviderProfile OllamaProfile(string model = "qwen2.5vl:7b")
    {
        var p = ProviderPresets.CreateProfile(ProviderPresets.OllamaId);
        p.Model = model;
        return p;
    }

    // ---- Canned responses ----

    private static JsonObject AText(string text) => new() { ["type"] = "text", ["text"] = text };
    private static JsonObject AThinking(string text, string signature) => new() { ["type"] = "thinking", ["thinking"] = text, ["signature"] = signature };
    private static JsonObject AToolUse(string id, string name, string input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = JsonNode.Parse(input) };

    private static string AnthropicReply(string stopReason, params JsonObject[] content) => new JsonObject
    {
        ["id"] = "msg_test",
        ["type"] = "message",
        ["role"] = "assistant",
        ["model"] = "claude-haiku-5-5",
        ["content"] = new JsonArray(content.Select(c => (JsonNode)c).ToArray()),
        ["stop_reason"] = stopReason,
        ["usage"] = new JsonObject { ["input_tokens"] = 10, ["output_tokens"] = 5 },
    }.ToJsonString();

    private static string AnthropicError(string message, string type = "invalid_request_error") =>
        new JsonObject { ["type"] = "error", ["error"] = new JsonObject { ["type"] = type, ["message"] = message } }.ToJsonString();

    private static string OaReply(string? content, JsonArray? toolCalls = null, string finish = "stop", string? reasoningContent = null)
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (toolCalls != null) message["tool_calls"] = toolCalls;
        if (reasoningContent != null) message["reasoning_content"] = reasoningContent;
        return new JsonObject
        {
            ["id"] = "chatcmpl-test",
            ["model"] = "vision-model",
            ["choices"] = new JsonArray(new JsonObject { ["index"] = 0, ["message"] = message, ["finish_reason"] = finish }),
            ["usage"] = new JsonObject { ["prompt_tokens"] = 7, ["completion_tokens"] = 3 },
        }.ToJsonString();
    }

    private static JsonObject OaToolCall(string? id, string name, JsonNode? args)
    {
        var call = new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = name, ["arguments"] = args } };
        if (id != null) call["id"] = id;
        return call;
    }

    private static string OllamaReply(string content, JsonArray? toolCalls = null, string? thinking = null, string doneReason = "stop")
    {
        var message = new JsonObject { ["role"] = "assistant", ["content"] = content };
        if (thinking != null) message["thinking"] = thinking;
        if (toolCalls != null) message["tool_calls"] = toolCalls;
        return new JsonObject
        {
            ["model"] = "qwen2.5vl:7b",
            ["message"] = message,
            ["done"] = true,
            ["done_reason"] = doneReason,
            ["prompt_eval_count"] = 11,
            ["eval_count"] = 4,
        }.ToJsonString();
    }

    private static int CountType(JsonNode? node, string type)
    {
        int n = 0;
        if (node is JsonObject o)
        {
            if (o["type"] is JsonValue v && v.TryGetValue<string>(out var t) && t == type) n++;
            foreach (var (_, child) in o) n += CountType(child, type);
        }
        else if (node is JsonArray a)
        {
            foreach (var child in a) n += CountType(child, type);
        }
        return n;
    }

    private static int CountText(JsonNode? node, string text) =>
        node is null ? 0 : CountOccurrences(node.ToJsonString(), JsonEncodedText.Encode(text).ToString());

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    // =====================================================================================
    // Anthropic Messages API
    // =====================================================================================

    [Fact]
    public async Task Anthropic_request_has_headers_system_tools_cache_and_context_editing()
    {
        var h = await new Harness(AnthropicProfile(caching: true, contextEditing: true)).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("Hello!")));

        var result = await h.TurnAsync("hi");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Hello!", result.FinalText);
        var req = Assert.Single(h.Http.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", req.Uri.ToString());
        Assert.Equal(Key, req.Header("x-api-key"));
        Assert.Equal("2023-06-01", req.Header("anthropic-version"));
        Assert.Equal("context-management-2025-06-27", req.Header("anthropic-beta"));
        Assert.StartsWith("application/json", req.Header("Content-Type"));
        Assert.Null(req.Header("Authorization"));

        var body = req.Json;
        Assert.Equal("claude-haiku-5-5", (string?)body["model"]);
        Assert.Equal(8192, (int)body["max_tokens"]!);
        Assert.Equal("SYSTEM PROMPT", (string?)body["system"]);
        Assert.Equal("ephemeral", (string?)body["cache_control"]!["type"]);
        Assert.Equal("clear_tool_uses_20250919", (string?)body["context_management"]!["edits"]![0]!["type"]);
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.False(body.ContainsKey("thinking"));
        Assert.False(body.ContainsKey("temperature"));
        Assert.False(body.ContainsKey("output_config"));

        var tools = (JsonArray)body["tools"]!;
        Assert.Equal(3, tools.Count);
        Assert.Equal("click", (string?)tools[1]!["name"]);
        Assert.Equal("Click at a point", (string?)tools[1]!["description"]);
        Assert.Equal("integer", (string?)tools[1]!["input_schema"]!["properties"]!["x"]!["type"]);

        var msg = Assert.Single(req.Messages);
        Assert.Equal("user", (string?)msg!["role"]);
        Assert.Equal("hi", (string?)msg["content"]![0]!["text"]);
        Assert.Contains(h.Events, e => e is AssistantTextEvent { Text: "Hello!" });
        Assert.Equal(10, result.Stats.InputTokens);
        Assert.Equal(5, result.Stats.OutputTokens);
    }

    [Fact]
    public async Task Anthropic_without_caching_or_context_editing_sends_neither()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h.TurnAsync("hi");
        var req = h.Http.Requests[0];
        Assert.Null(req.Header("anthropic-beta"));
        Assert.False(req.Json.ContainsKey("cache_control"));
        Assert.False(req.Json.ContainsKey("context_management"));
    }

    [Theory]
    [InlineData("claude-haiku-5-5", ThinkingMode.On, "adaptive")]
    [InlineData("claude-opus-5", ThinkingMode.On, "adaptive")]
    [InlineData("claude-sonnet-4-6", ThinkingMode.On, "adaptive")]
    [InlineData("claude-haiku-4-5", ThinkingMode.On, "enabled")]
    [InlineData("claude-haiku-4-5-20251001", ThinkingMode.On, "enabled")]
    [InlineData("claude-sonnet-4-5-20250929", ThinkingMode.On, "enabled")]
    [InlineData("claude-opus-4-5", ThinkingMode.On, "enabled")]
    [InlineData("claude-opus-4-1", ThinkingMode.On, "enabled")]
    [InlineData("claude-opus-4-0", ThinkingMode.On, "enabled")]
    [InlineData("claude-sonnet-4-0", ThinkingMode.On, "enabled")]
    [InlineData("claude-sonnet-4-20250514", ThinkingMode.On, "enabled")]
    [InlineData("claude-3-7-sonnet-latest", ThinkingMode.On, "enabled")]
    [InlineData("claude-haiku-4-5", ThinkingMode.Off, null)]
    [InlineData("claude-3-5-haiku-latest", ThinkingMode.Off, null)]
    [InlineData("claude-opus-5", ThinkingMode.Off, "disabled")]
    [InlineData("claude-haiku-5-5", ThinkingMode.Off, "disabled")]
    [InlineData("claude-sonnet-4-6", ThinkingMode.Off, "disabled")]
    [InlineData("claude-opus-5-5", ThinkingMode.Off, null)]
    [InlineData("claude-fable-5-1", ThinkingMode.Off, null)]
    [InlineData("claude-sonnet-5-5", ThinkingMode.Off, "between_tools")]
    [InlineData("claude-haiku-5-5", ThinkingMode.Auto, null)]
    [InlineData("claude-haiku-4-5", ThinkingMode.Auto, null)]
    public async Task Anthropic_thinking_parameter_per_model_family(string model, ThinkingMode mode, string? expectedType)
    {
        var profile = AnthropicProfile(model);
        profile.Thinking = mode;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h.TurnAsync("hi");

        var thinking = h.Http.Requests[0].Json["thinking"];
        if (expectedType == null)
        {
            Assert.Null(thinking);
            return;
        }
        Assert.Equal(expectedType, (string?)thinking!["type"]);
        if (expectedType == "enabled") Assert.Equal(4096, (int)thinking["budget_tokens"]!);
        if (expectedType == "adaptive") Assert.Equal("summarized", (string?)thinking["display"]);
    }

    [Fact]
    public void Anthropic_legacy_model_detection()
    {
        Assert.True(AnthropicProvider.IsLegacyThinkingModel("claude-haiku-4-5"));
        Assert.True(AnthropicProvider.IsLegacyThinkingModel("Claude-Opus-4-1-20250805"));
        Assert.True(AnthropicProvider.IsLegacyThinkingModel("claude-3-opus-20240229"));
        Assert.False(AnthropicProvider.IsLegacyThinkingModel("claude-haiku-5-5"));
        Assert.False(AnthropicProvider.IsLegacyThinkingModel("claude-opus-4-8"));
        Assert.False(AnthropicProvider.IsLegacyThinkingModel("claude-sonnet-5"));
    }

    [Theory]
    [InlineData(100, 8192, 1024, 8192)]       // minimum budget is 1024
    [InlineData(4096, 8192, 4096, 8192)]
    [InlineData(9000, 8192, 7168, 8192)]      // budget must stay below max_tokens
    [InlineData(1024, 1000, 1024, 2048)]      // max_tokens raised when there is no room
    public async Task Anthropic_legacy_thinking_budget_is_clamped(int budget, int maxTokens, int expectedBudget, int expectedMax)
    {
        var profile = AnthropicProfile("claude-haiku-4-5");
        profile.Thinking = ThinkingMode.On;
        profile.ThinkingBudgetTokens = budget;
        profile.MaxOutputTokens = maxTokens;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h.TurnAsync("hi");

        var body = h.Http.Requests[0].Json;
        Assert.Equal(expectedBudget, (int)body["thinking"]!["budget_tokens"]!);
        Assert.Equal(expectedMax, (int)body["max_tokens"]!);
        Assert.True((int)body["thinking"]!["budget_tokens"]! < (int)body["max_tokens"]!);
    }

    [Fact]
    public async Task Anthropic_effort_goes_in_output_config_and_temperature_only_without_thinking_on()
    {
        var profile = AnthropicProfile();
        profile.Effort = "High";
        profile.Temperature = 0.3;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h.TurnAsync("hi");
        var body = h.Http.Requests[0].Json;
        Assert.Equal("high", (string?)body["output_config"]!["effort"]);
        Assert.Equal(0.3, (double)body["temperature"]!);

        var thinkingOn = AnthropicProfile();
        thinkingOn.Thinking = ThinkingMode.On;
        thinkingOn.Temperature = 0.3;
        var h2 = await new Harness(thinkingOn).StartAsync();
        h2.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h2.TurnAsync("hi");
        Assert.False(h2.Http.Requests[0].Json.ContainsKey("temperature"));
    }

    [Fact]
    public async Task Anthropic_full_loop_runs_every_tool_call_and_returns_results_in_one_message()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Tools.Handler = (name, args, _) => Task.FromResult(name switch
        {
            "screenshot" => ToolResult.Ok("Screen 1280x800", Png),
            "click" => ToolResult.Error("Nothing to click there"),
            _ => ToolResult.Ok("ok"),
        });
        var firstContent = new[]
        {
            AThinking("I should look first.", "sig-abc=="),
            AText("Let me look at the screen."),
            AToolUse("toolu_1", "screenshot", "{}"),
            AToolUse("toolu_2", "click", "{\"x\":10,\"y\":20}"),
        };
        h.Http.Reply(AnthropicReply("tool_use", firstContent));
        h.Http.Reply(AnthropicReply("end_turn", AText("All done.")));

        var result = await h.TurnAsync("open notepad");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("All done.", result.FinalText);
        Assert.Equal(2, result.Stats.Steps);
        Assert.Equal(20, result.Stats.InputTokens);
        Assert.Equal(10, result.Stats.OutputTokens);
        Assert.Equal("claude-haiku-5-5", result.Stats.Model);
        Assert.Equal(new[] { ("screenshot", "{}"), ("click", "{\"x\":10,\"y\":20}") }, h.Tools.Calls);

        Assert.Contains(h.Events, e => e is ThinkingEvent { Text: "I should look first." });
        Assert.Contains(h.Events, e => e is AssistantTextEvent { Text: "Let me look at the screen." });
        Assert.Contains(h.Events, e => e is AssistantTextEvent { Text: "All done." });

        var second = h.Http.Requests[1].Messages;
        Assert.Equal(3, second.Count);
        // The assistant turn is sent back exactly as received, signature included.
        Assert.Equal("assistant", (string?)second[1]!["role"]);
        Assert.True(JsonNode.DeepEquals(new JsonArray(firstContent.Select(c => (JsonNode)c.DeepClone()).ToArray()), second[1]!["content"]));

        var results = (JsonArray)second[2]!["content"]!;
        Assert.Equal("user", (string?)second[2]!["role"]);
        Assert.Equal(2, results.Count);
        Assert.Equal("tool_result", (string?)results[0]!["type"]);
        Assert.Equal("toolu_1", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("Screen 1280x800", (string?)results[0]!["content"]![0]!["text"]);
        var image = results[0]!["content"]![1]!;
        Assert.Equal("image", (string?)image["type"]);
        Assert.Equal("base64", (string?)image["source"]!["type"]);
        Assert.Equal("image/png", (string?)image["source"]!["media_type"]);
        Assert.Equal(Png.Base64Data, (string?)image["source"]!["data"]);
        Assert.False(results[0]!.AsObject().ContainsKey("is_error"));
        Assert.Equal("toolu_2", (string?)results[1]!["tool_use_id"]);
        Assert.True((bool)results[1]!["is_error"]!);
        Assert.Equal("Nothing to click there", (string?)results[1]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task Anthropic_thinking_rejected_with_400_is_retried_without_and_remembered()
    {
        var profile = AnthropicProfile("claude-opus-5");
        profile.Thinking = ThinkingMode.Off;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicError("thinking.type: 'disabled' is not supported for this model"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("first")));
        h.Http.Reply(AnthropicReply("end_turn", AText("second")));

        var r1 = await h.TurnAsync("one");
        var r2 = await h.TurnAsync("two");

        Assert.Equal(TurnOutcome.Completed, r1.Outcome);
        Assert.Equal(TurnOutcome.Completed, r2.Outcome);
        Assert.Equal(3, h.Http.Requests.Count);
        Assert.Equal("disabled", (string?)h.Http.Requests[0].Json["thinking"]!["type"]);
        Assert.False(h.Http.Requests[1].Json.ContainsKey("thinking"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("thinking"));
        Assert.Single(h.Events.OfType<StatusEvent>(), s => s.Level == StatusLevel.Warning && s.Message.Contains("thinking"));
    }

    [Fact]
    public async Task Anthropic_thinking_display_rejected_drops_only_display()
    {
        var profile = AnthropicProfile("claude-sonnet-4-6");
        profile.Thinking = ThinkingMode.On;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicError("thinking.adaptive.display: Extra inputs are not permitted"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("hi")).Outcome);
        var retried = h.Http.Requests[1].Json["thinking"]!;
        Assert.Equal("adaptive", (string?)retried["type"]);
        Assert.Null(retried["display"]);
    }

    [Fact]
    public async Task Anthropic_effort_rejected_with_400_is_retried_without()
    {
        var profile = AnthropicProfile("claude-haiku-4-5");
        profile.Effort = "low";
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicError("output_config.effort: effort is not supported for this model"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));

        await h.TurnAsync("one");
        await h.TurnAsync("two");
        Assert.True(h.Http.Requests[0].Json.ContainsKey("output_config"));
        Assert.False(h.Http.Requests[1].Json.ContainsKey("output_config"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("output_config"));
    }

    [Fact]
    public async Task Anthropic_context_management_rejected_is_retried_without_beta_and_remembered()
    {
        var h = await new Harness(AnthropicProfile(contextEditing: true)).StartAsync();
        h.Http.Reply(AnthropicError("context_management: Extra inputs are not permitted"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));

        await h.TurnAsync("one");
        await h.TurnAsync("two");
        Assert.Equal("context-management-2025-06-27", h.Http.Requests[0].Header("anthropic-beta"));
        Assert.Null(h.Http.Requests[1].Header("anthropic-beta"));
        Assert.False(h.Http.Requests[1].Json.ContainsKey("context_management"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("context_management"));
    }

    [Fact]
    public async Task Anthropic_temperature_and_cache_control_rejections_are_retried_without()
    {
        var profile = AnthropicProfile(caching: true);
        profile.Temperature = 0.7;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicError("temperature: non-default values are not supported for this model"), 400);
        h.Http.Reply(AnthropicError("cache_control: Extra inputs are not permitted"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("hi")).Outcome);
        Assert.True(h.Http.Requests[0].Json.ContainsKey("temperature"));
        Assert.False(h.Http.Requests[1].Json.ContainsKey("temperature"));
        Assert.True(h.Http.Requests[1].Json.ContainsKey("cache_control"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("cache_control"));
        Assert.Equal(2, h.Events.OfType<StatusEvent>().Count(s => s.Level == StatusLevel.Warning));
    }

    [Fact]
    public async Task Anthropic_unrelated_400_fails_with_provider_message()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicError("messages: text content blocks must be non-empty"), 400);
        var r = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.Contains("HTTP 400", r.Error);
        Assert.Contains("text content blocks must be non-empty", r.Error);
        Assert.Single(h.Http.Requests);
    }

    [Fact]
    public async Task Anthropic_refusal_fails_and_forgets_the_declined_turn()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicReply("refusal"));
        h.Http.Reply(AnthropicReply("end_turn", AText("Sure.")));

        var r1 = await h.TurnAsync("something declined");
        Assert.Equal(TurnOutcome.Failed, r1.Outcome);
        Assert.Equal("The model declined this request.", r1.Error);

        var r2 = await h.TurnAsync("something fine");
        Assert.Equal(TurnOutcome.Completed, r2.Outcome);
        var msg = Assert.Single(h.Http.Requests[1].Messages);
        Assert.Equal("something fine", (string?)msg!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task Anthropic_pause_turn_continues_without_a_new_user_message()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicReply("pause_turn", AText("Working...")));
        h.Http.Reply(AnthropicReply("end_turn", AText("Finished.")));

        var r = await h.TurnAsync("go");
        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal("Finished.", r.FinalText);
        var second = h.Http.Requests[1].Messages;
        Assert.Equal(2, second.Count);
        Assert.Equal("assistant", (string?)second[1]!["role"]);
    }

    [Fact]
    public async Task Anthropic_prunes_old_screenshots_when_history_has_no_thinking()
    {
        var h = new Harness(AnthropicProfile());
        h.Settings.Screen.ScreenshotsToKeep = 2;
        await h.StartAsync();
        for (int i = 0; i < 4; i++) h.Http.Reply(AnthropicReply("tool_use", AToolUse("toolu_" + i, "screenshot", "{}")));
        h.Http.Reply(AnthropicReply("end_turn", AText("done")));

        var r = await h.TurnAsync("look 4 times");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        var last = h.Http.Requests[^1].Json["messages"];
        Assert.Equal(2, CountType(last, "image"));
        Assert.Equal(2, CountText(last, "[older screenshot removed]"));
        // The newest screenshots are the ones kept.
        var lastResult = h.Http.Requests[^1].Messages[^1]!["content"]![0]!["content"]!;
        Assert.Equal(1, CountType(lastResult, "image"));
    }

    [Fact]
    public async Task Anthropic_does_not_prune_when_history_has_thinking_blocks()
    {
        var h = new Harness(AnthropicProfile());
        h.Settings.Screen.ScreenshotsToKeep = 1;
        await h.StartAsync();
        for (int i = 0; i < 3; i++)
            h.Http.Reply(AnthropicReply("tool_use", AThinking("", "sig" + i), AToolUse("toolu_" + i, "screenshot", "{}")));
        h.Http.Reply(AnthropicReply("end_turn", AText("done")));

        await h.TurnAsync("look");

        var last = h.Http.Requests[^1].Json["messages"];
        Assert.Equal(3, CountType(last, "image"));
        Assert.Equal(0, CountText(last, "[older screenshot removed]"));
        // Empty (omitted) thinking is not shown.
        Assert.DoesNotContain(h.Events, e => e is ThinkingEvent);
    }

    [Fact]
    public async Task Anthropic_text_only_profile_never_sends_images()
    {
        var profile = AnthropicProfile();
        profile.SupportsVision = false;
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicReply("tool_use", AToolUse("toolu_1", "screenshot", "{}")));
        h.Http.Reply(AnthropicReply("end_turn", AText("done")));

        await h.Backend.RunTurnAsync(new UserTurn("what is this", new[] { Png }), CancellationToken.None);

        foreach (var req in h.Http.Requests)
        {
            Assert.Equal(0, CountType(req.Json, "image"));
            Assert.DoesNotContain(Png.Base64Data, req.Body);
        }
        Assert.Contains("[image omitted: this model cannot see images]", h.Http.Requests[0].Body);
        Assert.Equal(2, CountText(h.Http.Requests[1].Json, "[image omitted: this model cannot see images]"));
    }

    [Fact]
    public async Task Anthropic_conversation_persists_between_turns_until_reset()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("one")));
        h.Http.Reply(AnthropicReply("end_turn", AText("two")));
        h.Http.Reply(AnthropicReply("end_turn", AText("three")));

        await h.TurnAsync("first");
        await h.TurnAsync("second");
        Assert.Equal(3, h.Http.Requests[1].Messages.Count);

        await h.Backend.ResetConversationAsync(CancellationToken.None);
        await h.TurnAsync("third");
        var msg = Assert.Single(h.Http.Requests[2].Messages);
        Assert.Equal("third", (string?)msg!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task Anthropic_failed_turn_keeps_roles_alternating_on_the_next_turn()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicError("bad request"), 400);
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));

        Assert.Equal(TurnOutcome.Failed, (await h.TurnAsync("first")).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("second")).Outcome);
        var msg = Assert.Single(h.Http.Requests[1].Messages);
        Assert.Equal(2, ((JsonArray)msg!["content"]!).Count);
    }

    [Fact]
    public async Task Anthropic_extra_body_and_base_url_with_v1_suffix()
    {
        var profile = AnthropicProfile();
        profile.BaseUrl = "https://proxy.example.test/v1/";
        profile.ExtraBodyJson = "{\"metadata\":{\"user_id\":\"abc\"},\"max_tokens\":1234}";
        profile.ExtraHeaders["X-Proxy"] = "yes";
        var h = await new Harness(profile).StartAsync();
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        await h.TurnAsync("hi");
        var req = h.Http.Requests[0];
        Assert.Equal("https://proxy.example.test/v1/messages", req.Uri.ToString());
        Assert.Equal("abc", (string?)req.Json["metadata"]!["user_id"]);
        Assert.Equal(1234, (int)req.Json["max_tokens"]!);
        Assert.Equal("yes", req.Header("X-Proxy"));
    }

    // =====================================================================================
    // Retries, errors, cancellation, step limit (shared loop behavior)
    // =====================================================================================

    [Fact]
    public async Task Retries_429_and_529_honoring_retry_after()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicError("rate limited", "rate_limit_error"), 429, r => r.Headers.TryAddWithoutValidation("retry-after", "7"));
        h.Http.Reply(AnthropicError("Overloaded", "overloaded_error"), 529);
        h.Http.Reply(AnthropicReply("end_turn", AText("finally")));

        var r = await h.TurnAsync("hi");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal(3, h.Http.Requests.Count);
        Assert.Equal(new[] { TimeSpan.FromSeconds(7), TimeSpan.FromSeconds(2) }, h.Delays);
        Assert.Equal(2, h.Events.OfType<StatusEvent>().Count(s => s.Message.Contains("retrying")));
    }

    [Fact]
    public async Task Retries_give_up_after_three_and_report_the_provider_message()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        for (int i = 0; i < 4; i++) h.Http.Reply(AnthropicError("Service temporarily unavailable", "api_error"), 503);

        var r = await h.TurnAsync("hi");

        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.Equal(4, h.Http.Requests.Count);
        Assert.Equal(new[] { 1.0, 2.0, 4.0 }, h.Delays.Select(d => d.TotalSeconds));
        Assert.Contains("HTTP 503", r.Error);
        Assert.Contains("Service temporarily unavailable", r.Error);
    }

    [Fact]
    public async Task Status_401_says_key_rejected_and_never_leaks_the_key()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Reply(AnthropicError($"invalid x-api-key {Key}", "authentication_error"), 401);

        var r = await h.TurnAsync("hi");

        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.StartsWith("The API key was rejected by Anthropic API (console key)", r.Error);
        Assert.DoesNotContain(Key, r.Error);
        Assert.DoesNotContain(Key, h.AllEventText());
        Assert.DoesNotContain(Key, string.Join("\n", h.Logs));
        Assert.Single(h.Http.Requests);
    }

    [Fact]
    public async Task Status_403_scrubs_key_from_provider_message()
    {
        var p = OpenAiProfile();
        var h = await new Harness(p).StartAsync();
        h.Http.Reply("{\"error\":{\"message\":\"key " + Key + " lacks access\"}}", 403);
        var r = await h.TurnAsync("hi");
        Assert.StartsWith("The API key was rejected by Test Server", r.Error);
        Assert.DoesNotContain(Key, r.Error);
        Assert.Contains("[redacted]", r.Error);
    }

    [Fact]
    public async Task Status_404_says_model_not_found_at_base_url()
    {
        var h = await new Harness(OpenAiProfile(model: "no-such-model")).StartAsync();
        h.Http.Reply("{\"error\":{\"message\":\"The model does not exist\"}}", 404);
        var r = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.StartsWith("Model 'no-such-model' not found at https://example.test/v1", r.Error);
    }

    [Fact]
    public async Task Connection_refused_on_a_local_server_says_it_is_not_running()
    {
        var h = await new Harness(OllamaProfile()).StartAsync();
        h.Http.Throw(new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it.",
            new SocketException((int)SocketError.ConnectionRefused)));

        var r = await h.TurnAsync("hi");

        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.Equal("Ollama (local) is not running at http://127.0.0.1:11434. Start it and try again.", r.Error);
        Assert.Single(h.Http.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task Remote_network_errors_are_retried()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Http.Throw(new HttpRequestException("The response ended prematurely."));
        h.Http.Reply(OaReply("ok"));
        var r = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Single(h.Delays);
    }

    [Fact]
    public async Task Cancelling_the_token_during_a_request_cancels_the_turn()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Hang();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var r = await h.TurnAsync("hi", cts.Token);
        Assert.Equal(TurnOutcome.Cancelled, r.Outcome);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Stop_signal_and_interrupt_cancel_a_running_request()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Http.Hang();
        var turn = h.TurnAsync("hi");
        while (h.Http.Requests.Count == 0) await Task.Delay(5);
        h.Control.RequestStop("Stopped by user");
        Assert.Equal(TurnOutcome.Cancelled, (await turn).Outcome);

        h.Control.BeginTurn();
        h.Http.Hang();
        var turn2 = h.TurnAsync("again");
        while (h.Http.Requests.Count < 2) await Task.Delay(5);
        await h.Backend.InterruptAsync();
        Assert.Equal(TurnOutcome.Cancelled, (await turn2).Outcome);
    }

    [Fact]
    public async Task Stop_during_tools_skips_the_rest_and_keeps_history_valid()
    {
        var h = await new Harness(AnthropicProfile()).StartAsync();
        h.Tools.Handler = (name, _, _) =>
        {
            h.Control.RequestStop("Stopped by user");
            return Task.FromResult(ToolResult.Ok("clicked"));
        };
        h.Http.Reply(AnthropicReply("tool_use", AToolUse("toolu_a", "click", "{\"x\":1,\"y\":2}"), AToolUse("toolu_b", "type_text", "{\"text\":\"x\"}")));

        var r = await h.TurnAsync("do it");
        Assert.Equal(TurnOutcome.Cancelled, r.Outcome);
        Assert.Single(h.Tools.Calls);
        Assert.Equal(1, r.Stats.Steps);

        // The next turn sends a result for every tool_use, so the API accepts the history.
        h.Control.BeginTurn();
        h.Tools.Handler = null;
        h.Http.Reply(AnthropicReply("end_turn", AText("ok")));
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("continue")).Outcome);
        var results = (JsonArray)h.Http.Requests[1].Messages[2]!["content"]!;
        Assert.Equal("toolu_a", (string?)results[0]!["tool_use_id"]);
        Assert.Equal("toolu_b", (string?)results[1]!["tool_use_id"]);
        Assert.True((bool)results[1]!["is_error"]!);
        Assert.Equal("text", (string?)results[2]!["type"]);   // the new user text joins the same user message
    }

    [Fact]
    public async Task Model_calls_are_bounded_by_the_step_limit()
    {
        var h = new Harness(AnthropicProfile());
        h.Settings.Safety.MaxStepsPerTurn = 1;
        await h.StartAsync();
        int n = 0;
        h.Http.Default = (_, _) => Task.FromResult(FakeHandler.Response(AnthropicReply("tool_use", AToolUse("toolu_" + n++, "screenshot", "{}"))));

        var r = await h.TurnAsync("loop forever");

        Assert.Equal(TurnOutcome.StepLimit, r.Outcome);
        Assert.Equal(6, h.Http.Requests.Count);
        Assert.Equal(6, r.Stats.Steps);
    }

    [Fact]
    public async Task Step_limit_stop_from_the_tool_host_reports_step_limit()
    {
        var h = new Harness(AnthropicProfile());
        h.Settings.Safety.MaxStepsPerTurn = 2;
        await h.StartAsync();
        h.Tools.Handler = (_, _, _) =>
        {
            if (h.Tools.Calls.Count > 2) h.Control.RequestStop("Step limit (2) reached");
            return Task.FromResult(ToolResult.Ok("ok"));
        };
        h.Http.Default = (_, _) => Task.FromResult(FakeHandler.Response(AnthropicReply("tool_use", AToolUse("toolu_x" + Guid.NewGuid().ToString("N"), "click", "{\"x\":1,\"y\":1}"))));

        var r = await h.TurnAsync("go");
        Assert.Equal(TurnOutcome.StepLimit, r.Outcome);
        Assert.Equal(3, h.Http.Requests.Count);
    }

    [Fact]
    public async Task Run_before_start_fails_readably()
    {
        await using var backend = new HttpAgentBackend(new HttpClient(new FakeHandler()));
        var r = await backend.RunTurnAsync(new UserTurn("hi"), CancellationToken.None);
        Assert.Equal(TurnOutcome.Failed, r.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(r.Error));
        await backend.InterruptAsync();   // safe when idle
    }

    // =====================================================================================
    // StartAsync validation
    // =====================================================================================

    [Fact]
    public async Task Start_requires_an_api_key_for_anthropic()
    {
        var h = new Harness(AnthropicProfile());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.StartAsync(key: ""));
        Assert.Contains("No API key", ex.Message);
        Assert.Contains("ANTHROPIC_API_KEY", ex.Message);
    }

    [Fact]
    public async Task Start_requires_a_key_only_when_the_openai_preset_needs_one()
    {
        var openai = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(openai).StartAsync(key: ""));

        var lmStudio = ProviderPresets.CreateProfile(ProviderPresets.LmStudioId);   // local, no key, no model needed
        await new Harness(lmStudio).StartAsync(key: "");
    }

    [Fact]
    public async Task Start_rejects_bad_configuration_readably()
    {
        var noUrl = OpenAiProfile(baseUrl: "");
        Assert.Contains("No base URL", (await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(noUrl).StartAsync())).Message);

        var badUrl = OpenAiProfile(baseUrl: "not a url");
        Assert.Contains("not a valid", (await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(badUrl).StartAsync())).Message);

        var azure = ProviderPresets.CreateProfile(ProviderPresets.AzureOpenAiId);
        Assert.Contains("YOUR-RESOURCE", (await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(azure).StartAsync())).Message);

        var noModel = OllamaProfile(model: "");
        Assert.Contains("No model", (await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(noModel).StartAsync())).Message);

        var badBody = OpenAiProfile();
        badBody.ExtraBodyJson = "[1,2]";
        Assert.Contains("JSON object", (await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(badBody).StartAsync())).Message);

        var cli = ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new Harness(cli).StartAsync());
    }

    // =====================================================================================
    // OpenAI-compatible chat completions
    // =====================================================================================

    [Fact]
    public async Task OpenAi_request_shape_headers_and_tools()
    {
        var p = OpenAiProfile();
        p.Temperature = 0.2;
        p.ExtraHeaders["HTTP-Referer"] = "https://deskpilot.example";
        var h = await new Harness(p).StartAsync();
        h.Http.Reply(OaReply("Hi there"));

        var r = await h.TurnAsync("hello");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal("Hi there", r.FinalText);
        Assert.Equal(7, r.Stats.InputTokens);
        Assert.Equal(3, r.Stats.OutputTokens);
        var req = Assert.Single(h.Http.Requests);
        Assert.Equal("https://example.test/v1/chat/completions", req.Uri.ToString());
        Assert.Equal("Bearer " + Key, req.Header("Authorization"));
        Assert.Equal("https://deskpilot.example", req.Header("HTTP-Referer"));
        var body = req.Json;
        Assert.Equal("vision-model", (string?)body["model"]);
        Assert.Equal(8192, (int)body["max_tokens"]!);
        Assert.False(body.ContainsKey("max_completion_tokens"));
        Assert.Equal(0.2, (double)body["temperature"]!);
        Assert.False(body.ContainsKey("tool_choice"));
        Assert.False(body.ContainsKey("reasoning_effort"));
        Assert.Equal("system", (string?)req.Messages[0]!["role"]);
        Assert.Equal("SYSTEM PROMPT", (string?)req.Messages[0]!["content"]);
        Assert.Equal("user", (string?)req.Messages[1]!["role"]);
        Assert.Equal("hello", (string?)req.Messages[1]!["content"]);
        var tool = body["tools"]![1]!;
        Assert.Equal("function", (string?)tool["type"]);
        Assert.Equal("click", (string?)tool["function"]!["name"]);
        Assert.Equal("Click at a point", (string?)tool["function"]!["description"]);
        Assert.Equal("object", (string?)tool["function"]!["parameters"]!["type"]);
    }

    [Fact]
    public async Task OpenAi_without_key_sends_no_authorization_header()
    {
        var h = await new Harness(OpenAiProfile(baseUrl: "http://127.0.0.1:1234/v1")).StartAsync(key: "");
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        Assert.Null(h.Http.Requests[0].Header("Authorization"));
    }

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-4.1", true)]
    [InlineData("https://openrouter.ai/api/v1", "o3-mini", true)]
    [InlineData("https://example.test/v1", "o1", true)]
    [InlineData("https://example.test/v1", "o4-mini", true)]
    [InlineData("https://my.azure.test/openai/v1", "gpt-5-mini", true)]
    [InlineData("https://openrouter.ai/api/v1", "openai/gpt-5-mini", false)]
    [InlineData("http://127.0.0.1:1234/v1", "qwen2.5-vl-7b", false)]
    public async Task OpenAi_max_completion_tokens_rule(string baseUrl, string model, bool expected)
    {
        Assert.Equal(expected, OpenAiCompatProvider.UsesMaxCompletionTokens(baseUrl, model));
        var h = await new Harness(OpenAiProfile(baseUrl, model)).StartAsync();
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        var body = h.Http.Requests[0].Json;
        Assert.Equal(expected, body.ContainsKey("max_completion_tokens"));
        Assert.Equal(!expected, body.ContainsKey("max_tokens"));
    }

    [Theory]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.Auto, "", "https://api.openai.com/v1", null)]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.On, "high", "https://api.openai.com/v1", "high")]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.Auto, "low", "https://example.test/v1", "low")]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.Off, "", "http://localhost:11434/v1", "none")]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.Off, "", "http://127.0.0.1:11434/v1", "none")]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.Off, "", "https://api.openai.com/v1", null)]
    [InlineData(ReasoningStyle.ReasoningEffort, ThinkingMode.On, "", "https://api.openai.com/v1", null)]
    [InlineData(ReasoningStyle.None, ThinkingMode.Off, "high", "http://127.0.0.1:1234/v1", null)]
    public async Task OpenAi_reasoning_effort_rules(ReasoningStyle style, ThinkingMode mode, string effort, string baseUrl, string? expected)
    {
        var p = OpenAiProfile(baseUrl, "some-model", style);
        p.Thinking = mode;
        p.Effort = effort;
        var h = await new Harness(p).StartAsync();
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        var body = h.Http.Requests[0].Json;
        Assert.Equal(expected, (string?)body["reasoning_effort"]);
        Assert.False(body.ContainsKey("reasoning"));
    }

    [Theory]
    [InlineData(ThinkingMode.Off, "", "{\"enabled\":false}")]
    [InlineData(ThinkingMode.Off, "high", "{\"enabled\":false}")]
    [InlineData(ThinkingMode.Auto, "low", "{\"effort\":\"low\"}")]
    [InlineData(ThinkingMode.On, "", "{\"enabled\":true}")]
    [InlineData(ThinkingMode.Auto, "", null)]
    public async Task OpenRouter_reasoning_object(ThinkingMode mode, string effort, string? expectedJson)
    {
        var p = OpenAiProfile("https://openrouter.ai/api/v1", "anthropic/claude-haiku-4.5", ReasoningStyle.OpenRouter);
        p.Thinking = mode;
        p.Effort = effort;
        var h = await new Harness(p).StartAsync();
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        var body = h.Http.Requests[0].Json;
        Assert.False(body.ContainsKey("reasoning_effort"));
        if (expectedJson == null) Assert.False(body.ContainsKey("reasoning"));
        else Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), body["reasoning"]));
    }

    [Fact]
    public async Task OpenAi_reasoning_rejected_is_retried_without_and_remembered()
    {
        var p = OpenAiProfile(style: ReasoningStyle.ReasoningEffort);
        p.Effort = "medium";
        var h = await new Harness(p).StartAsync();
        h.Http.Reply("{\"error\":{\"message\":\"Unrecognized request argument supplied: reasoning_effort\"}}", 400);
        h.Http.Reply(OaReply("one"));
        h.Http.Reply(OaReply("two"));

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("a")).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("b")).Outcome);
        Assert.Equal("medium", (string?)h.Http.Requests[0].Json["reasoning_effort"]);
        Assert.False(h.Http.Requests[1].Json.ContainsKey("reasoning_effort"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("reasoning_effort"));
        Assert.Contains(h.Events, e => e is StatusEvent { Level: StatusLevel.Warning } s && s.Message.Contains("reasoning"));
    }

    [Fact]
    public async Task OpenAi_temperature_and_max_tokens_rejections_adapt()
    {
        var p = OpenAiProfile(model: "custom-reasoner");
        p.Temperature = 0.5;
        var h = await new Harness(p).StartAsync();
        h.Http.Reply("{\"error\":{\"message\":\"Unsupported parameter: 'max_tokens' is not supported with this model. Use 'max_completion_tokens' instead.\"}}", 400);
        h.Http.Reply("{\"error\":{\"message\":\"Unsupported value: 'temperature' does not support 0.5 with this model.\"}}", 400);
        h.Http.Reply(OaReply("ok"));

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("hi")).Outcome);
        var last = h.Http.Requests[2].Json;
        Assert.True(last.ContainsKey("max_completion_tokens"));
        Assert.False(last.ContainsKey("max_tokens"));
        Assert.False(last.ContainsKey("temperature"));
    }

    [Fact]
    public async Task OpenAi_extra_body_is_deep_merged_last()
    {
        var p = OpenAiProfile();
        p.Temperature = 0.5;
        p.ExtraBodyJson = "{\"max_tokens\":100,\"top_p\":0.9,\"provider\":{\"order\":[\"a\"]},\"temperature\":null,\"stream_options\":{\"x\":1}}";
        var h = await new Harness(p).StartAsync();
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        var body = h.Http.Requests[0].Json;
        Assert.Equal(100, (int)body["max_tokens"]!);
        Assert.Equal(0.9, (double)body["top_p"]!);
        Assert.Equal("a", (string?)body["provider"]!["order"]![0]);
        Assert.False(body.ContainsKey("temperature"));
        Assert.Equal("vision-model", (string?)body["model"]);
    }

    [Fact]
    public void Deep_merge_merges_objects_and_replaces_values()
    {
        var target = JsonNode.Parse("{\"a\":{\"x\":1,\"y\":2},\"b\":1,\"c\":[1]}")!.AsObject();
        JsonUtil.DeepMerge(target, JsonNode.Parse("{\"a\":{\"y\":3,\"z\":4},\"b\":null,\"c\":[2,3]}")!.AsObject());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":{\"x\":1,\"y\":3,\"z\":4},\"c\":[2,3]}"), target));
    }

    [Fact]
    public async Task OpenAi_loop_with_tool_calls_images_and_reasoning()
    {
        var p = OpenAiProfile();
        var h = await new Harness(p).StartAsync();
        h.Tools.Handler = (name, _, _) => Task.FromResult(name == "click" ? ToolResult.Ok("clicked", Png) : ToolResult.Ok("typed"));
        var calls = new JsonArray(
            OaToolCall("call_a", "click", JsonValue.Create("{\"x\":5,\"y\":6}")),
            OaToolCall(null, "type_text", JsonNode.Parse("{\"text\":\"hi\"}")!));
        h.Http.Reply(OaReply(null, calls, "tool_calls", reasoningContent: "I will click then type."));
        h.Http.Reply(OaReply("Done."));

        var r = await h.TurnAsync("do it");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal("Done.", r.FinalText);
        Assert.Equal(2, r.Stats.Steps);
        Assert.Equal(new[] { ("click", "{\"x\":5,\"y\":6}"), ("type_text", "{\"text\":\"hi\"}") }, h.Tools.Calls);
        Assert.Contains(h.Events, e => e is ThinkingEvent { Text: "I will click then type." });

        var msgs = h.Http.Requests[1].Messages;
        Assert.Equal(6, msgs.Count);   // system, user, assistant, tool, tool, user (screenshot)
        var assistant = msgs[2]!.AsObject();
        Assert.Equal("assistant", (string?)assistant["role"]);
        Assert.True(assistant.ContainsKey("content"));
        Assert.Null(assistant["content"]);
        Assert.False(assistant.ContainsKey("reasoning_content"));
        var toolCalls = (JsonArray)assistant["tool_calls"]!;
        Assert.Equal("call_a", (string?)toolCalls[0]!["id"]);
        var generatedId = (string?)toolCalls[1]!["id"];
        Assert.False(string.IsNullOrWhiteSpace(generatedId));
        Assert.Equal("{\"text\":\"hi\"}", (string?)toolCalls[1]!["function"]!["arguments"]);   // arguments are always a string

        Assert.Equal("tool", (string?)msgs[3]!["role"]);
        Assert.Equal("call_a", (string?)msgs[3]!["tool_call_id"]);
        Assert.Equal("clicked", (string?)msgs[3]!["content"]);
        Assert.Equal(generatedId, (string?)msgs[4]!["tool_call_id"]);

        var imageMsg = msgs[5]!;
        Assert.Equal("user", (string?)imageMsg["role"]);
        Assert.Equal("Screenshot from the tool result above:", (string?)imageMsg["content"]![0]!["text"]);
        Assert.Equal("image_url", (string?)imageMsg["content"]![1]!["type"]);
        Assert.Equal("data:image/png;base64," + Png.Base64Data, (string?)imageMsg["content"]![1]!["image_url"]!["url"]);
    }

    [Fact]
    public async Task OpenAi_tool_error_results_are_marked()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Tools.Handler = (_, _, _) => Task.FromResult(ToolResult.Error("window not found"));
        h.Http.Reply(OaReply(null, new JsonArray(OaToolCall("c1", "click", JsonValue.Create("{\"x\":1,\"y\":1}"))), "tool_calls"));
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("go");
        Assert.Equal("Error: window not found", (string?)h.Http.Requests[1].Messages[3]!["content"]);
    }

    [Fact]
    public async Task OpenAi_returns_reasoning_content_when_the_server_requires_it()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Http.Reply(OaReply(null, new JsonArray(OaToolCall("c1", "screenshot", JsonValue.Create("{}"))), "tool_calls", reasoningContent: "plan"));
        h.Http.Reply("{\"error\":{\"message\":\"Missing `reasoning_content` field in the assistant message at message index 2\"}}", 400);
        h.Http.Reply(OaReply("ok"));

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("go")).Outcome);
        Assert.False(h.Http.Requests[1].Messages[2]!.AsObject().ContainsKey("reasoning_content"));
        Assert.Equal("plan", (string?)h.Http.Requests[2].Messages[2]!["reasoning_content"]);
    }

    [Fact]
    public async Task OpenAi_text_tool_call_fallback_with_tool_call_tags()
    {
        var h = await new Harness(OpenAiProfile(baseUrl: "http://127.0.0.1:8000/v1")).StartAsync(key: "");
        h.Http.Reply(OaReply("I'll click it.\n<tool_call>\n{\"name\": \"click\", \"arguments\": {\"x\": 3, \"y\": 4}}\n</tool_call>"));
        h.Http.Reply(OaReply("Clicked."));

        var r = await h.TurnAsync("click the button");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal(("click", "{\"x\":3,\"y\":4}"), Assert.Single(h.Tools.Calls));
        Assert.Contains(h.Events, e => e is AssistantTextEvent { Text: "I'll click it." });
        var msgs = h.Http.Requests[1].Messages;
        var assistant = msgs[2]!;
        Assert.Equal("I'll click it.", (string?)assistant["content"]);
        var id = (string?)assistant["tool_calls"]![0]!["id"];
        Assert.Equal("click", (string?)assistant["tool_calls"]![0]!["function"]!["name"]);
        Assert.Equal(id, (string?)msgs[3]!["tool_call_id"]);
    }

    [Fact]
    public async Task OpenAi_text_fallback_ignores_unknown_tools_and_reads_fenced_json()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Http.Reply(OaReply("```json\n{\"name\": \"format_disk\", \"arguments\": {}}\n```"));
        var r = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Empty(h.Tools.Calls);

        h.Http.Reply(OaReply("Typing now:\n```json\n{\"name\": \"type_text\", \"arguments\": {\"text\": \"abc\"}}\n```"));
        h.Http.Reply(OaReply("typed"));
        await h.TurnAsync("type abc");
        Assert.Equal(("type_text", "{\"text\":\"abc\"}"), Assert.Single(h.Tools.Calls));
    }

    [Fact]
    public async Task OpenAi_think_tags_become_thinking()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Http.Reply(OaReply("<think>Let me reason.</think>\n\nThe answer is 4."));
        var r = await h.TurnAsync("2+2?");
        Assert.Equal("The answer is 4.", r.FinalText);
        Assert.Contains(h.Events, e => e is ThinkingEvent { Text: "Let me reason." });
        Assert.DoesNotContain(h.Events, e => e is AssistantTextEvent a && a.Text.Contains("<think>"));
    }

    [Fact]
    public async Task OpenAi_prunes_older_screenshots()
    {
        var h = new Harness(OpenAiProfile());
        h.Settings.Screen.ScreenshotsToKeep = 1;
        await h.StartAsync();
        for (int i = 0; i < 3; i++) h.Http.Reply(OaReply(null, new JsonArray(OaToolCall("c" + i, "screenshot", JsonValue.Create("{}"))), "tool_calls"));
        h.Http.Reply(OaReply("done"));

        await h.TurnAsync("look");
        var last = h.Http.Requests[^1].Json["messages"];
        Assert.Equal(1, CountType(last, "image_url"));
        Assert.Equal(2, CountText(last, "[older screenshot removed]"));
        Assert.Equal("image_url", (string?)h.Http.Requests[^1].Messages[^1]!["content"]![1]!["type"]);
    }

    [Fact]
    public async Task OpenAi_text_only_profile_never_sends_image_urls()
    {
        var p = OpenAiProfile();
        p.SupportsVision = false;
        var h = await new Harness(p).StartAsync();
        h.Http.Reply(OaReply(null, new JsonArray(OaToolCall("c1", "screenshot", JsonValue.Create("{}"))), "tool_calls"));
        h.Http.Reply(OaReply("done"));
        await h.Backend.RunTurnAsync(new UserTurn("see this", new[] { Png }), CancellationToken.None);

        foreach (var req in h.Http.Requests)
        {
            Assert.Equal(0, CountType(req.Json, "image_url"));
            Assert.DoesNotContain(Png.Base64Data, req.Body);
        }
        var msgs = h.Http.Requests[1].Messages;
        Assert.Equal(4, msgs.Count);   // no extra screenshot message
        Assert.Contains("[image omitted", (string?)msgs[3]!["content"]);
    }

    [Fact]
    public async Task OpenAi_user_images_use_content_parts()
    {
        var h = await new Harness(OpenAiProfile()).StartAsync();
        h.Http.Reply(OaReply("a cat"));
        await h.Backend.RunTurnAsync(new UserTurn("what is this", new[] { Png }), CancellationToken.None);
        var content = (JsonArray)h.Http.Requests[0].Messages[1]!["content"]!;
        Assert.Equal("what is this", (string?)content[0]!["text"]);
        Assert.Equal("data:image/png;base64," + Png.Base64Data, (string?)content[1]!["image_url"]!["url"]);
    }

    [Fact]
    public async Task OpenAi_local_server_without_model_omits_the_model_field()
    {
        var p = OpenAiProfile(baseUrl: "http://127.0.0.1:8080/v1", model: "");
        var h = await new Harness(p).StartAsync(key: "");
        h.Http.Reply(OaReply("ok"));
        await h.TurnAsync("hi");
        Assert.False(h.Http.Requests[0].Json.ContainsKey("model"));
    }

    // =====================================================================================
    // Ollama native /api/chat
    // =====================================================================================

    [Theory]
    [InlineData(ThinkingMode.On, true)]
    [InlineData(ThinkingMode.Off, false)]
    [InlineData(ThinkingMode.Auto, null)]
    public async Task Ollama_request_shape(ThinkingMode mode, bool? expectedThink)
    {
        var p = OllamaProfile();
        p.Thinking = mode;
        p.Temperature = 0.1;
        p.MaxOutputTokens = 2048;
        var h = await new Harness(p).StartAsync(key: "");
        h.Http.Reply(OllamaReply("hello"));

        var r = await h.Backend.RunTurnAsync(new UserTurn("look", new[] { Png }), CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal(11, r.Stats.InputTokens);
        Assert.Equal(4, r.Stats.OutputTokens);
        var req = h.Http.Requests[0];
        Assert.Equal("http://127.0.0.1:11434/api/chat", req.Uri.ToString());
        Assert.Null(req.Header("Authorization"));
        var body = req.Json;
        Assert.Equal("qwen2.5vl:7b", (string?)body["model"]);
        Assert.False((bool)body["stream"]!);
        Assert.Equal(expectedThink, (bool?)body["think"]);
        Assert.Equal(2048, (int)body["options"]!["num_predict"]!);
        Assert.Equal(0.1, (double)body["options"]!["temperature"]!);
        Assert.Equal("function", (string?)body["tools"]![0]!["type"]);
        Assert.Equal("system", (string?)req.Messages[0]!["role"]);
        Assert.Equal("look", (string?)req.Messages[1]!["content"]);
        Assert.Equal(Png.Base64Data, (string?)req.Messages[1]!["images"]![0]);
    }

    [Fact]
    public async Task Ollama_loop_with_object_arguments_tool_name_and_thinking()
    {
        var h = await new Harness(OllamaProfile()).StartAsync(key: "");
        var calls = new JsonArray(new JsonObject { ["function"] = new JsonObject { ["name"] = "screenshot", ["arguments"] = new JsonObject() } },
            new JsonObject { ["function"] = new JsonObject { ["name"] = "click", ["arguments"] = JsonNode.Parse("{\"x\":7,\"y\":8}") } });
        h.Http.Reply(OllamaReply("", calls, thinking: "Need to see the screen."));
        h.Http.Reply(OllamaReply("All good."));

        var r = await h.TurnAsync("check");

        Assert.Equal(TurnOutcome.Completed, r.Outcome);
        Assert.Equal("All good.", r.FinalText);
        Assert.Equal(new[] { ("screenshot", "{}"), ("click", "{\"x\":7,\"y\":8}") }, h.Tools.Calls);
        Assert.Contains(h.Events, e => e is ThinkingEvent { Text: "Need to see the screen." });

        var msgs = h.Http.Requests[1].Messages;
        Assert.Equal(6, msgs.Count);   // system, user, assistant, tool, tool, user (screenshot)
        Assert.Equal(7, (int)msgs[2]!["tool_calls"]![1]!["function"]!["arguments"]!["x"]!);   // arguments stay objects
        Assert.False(msgs[2]!.AsObject().ContainsKey("thinking"));
        Assert.Equal("tool", (string?)msgs[3]!["role"]);
        Assert.Equal("screenshot", (string?)msgs[3]!["tool_name"]);
        Assert.Equal("Screen 1280x800", (string?)msgs[3]!["content"]);
        Assert.Equal("click", (string?)msgs[4]!["tool_name"]);
        Assert.Equal("user", (string?)msgs[5]!["role"]);
        Assert.Equal(Png.Base64Data, (string?)msgs[5]!["images"]![0]);
    }

    [Fact]
    public async Task Ollama_think_rejection_is_retried_without_and_remembered()
    {
        var p = OllamaProfile("gemma3:12b");
        p.Thinking = ThinkingMode.On;
        var h = await new Harness(p).StartAsync(key: "");
        h.Http.Reply("{\"error\":\"\\\"gemma3:12b\\\" does not support thinking\"}", 400);
        h.Http.Reply(OllamaReply("one"));
        h.Http.Reply(OllamaReply("two"));

        await h.TurnAsync("a");
        await h.TurnAsync("b");
        Assert.True(h.Http.Requests[0].Json.ContainsKey("think"));
        Assert.False(h.Http.Requests[1].Json.ContainsKey("think"));
        Assert.False(h.Http.Requests[2].Json.ContainsKey("think"));
    }

    [Fact]
    public async Task Ollama_missing_model_and_no_tool_support_are_explained()
    {
        var h = await new Harness(OllamaProfile("nope:1b")).StartAsync(key: "");
        h.Http.Reply("{\"error\":\"model 'nope:1b' not found\"}", 404);
        var r = await h.TurnAsync("hi");
        Assert.StartsWith("Model 'nope:1b' not found at http://127.0.0.1:11434", r.Error);
        Assert.Contains("ollama pull nope:1b", r.Error);

        var h2 = await new Harness(OllamaProfile("gemma2:2b")).StartAsync(key: "");
        h2.Http.Reply("{\"error\":\"registry.ollama.ai/library/gemma2:2b does not support tools\"}", 400);
        var r2 = await h2.TurnAsync("hi");
        Assert.Contains("cannot call tools", r2.Error);
    }

    [Fact]
    public async Task Ollama_prunes_older_screenshots_and_accepts_v1_base_url()
    {
        var p = OllamaProfile();
        p.BaseUrl = "http://localhost:11434/v1";
        var h = new Harness(p);
        h.Settings.Screen.ScreenshotsToKeep = 2;
        await h.StartAsync(key: "");
        for (int i = 0; i < 3; i++)
            h.Http.Reply(OllamaReply("", new JsonArray(new JsonObject { ["function"] = new JsonObject { ["name"] = "screenshot", ["arguments"] = new JsonObject() } })));
        h.Http.Reply(OllamaReply("done"));

        await h.TurnAsync("look");

        Assert.Equal("http://localhost:11434/api/chat", h.Http.Requests[0].Uri.ToString());
        var msgs = h.Http.Requests[^1].Messages;
        Assert.Equal(2, msgs.Sum(m => (m!["images"] as JsonArray)?.Count ?? 0));
        Assert.Equal(1, msgs.Count(m => (string?)m!["content"] == "[older screenshot removed]"));
    }

    [Fact]
    public async Task Ollama_text_tool_call_fallback()
    {
        var h = await new Harness(OllamaProfile()).StartAsync(key: "");
        h.Http.Reply(OllamaReply("{\"name\": \"click\", \"arguments\": {\"x\": 1, \"y\": 2}}"));
        h.Http.Reply(OllamaReply("ok"));
        await h.TurnAsync("click");
        Assert.Equal(("click", "{\"x\":1,\"y\":2}"), Assert.Single(h.Tools.Calls));
        Assert.Equal(1, (int)h.Http.Requests[1].Messages[2]!["tool_calls"]![0]!["function"]!["arguments"]!["x"]!);
    }

    // =====================================================================================
    // Text tool-call parser, think tags, error extraction, transport timeout
    // =====================================================================================

    [Fact]
    public void Text_tool_call_parser_variants()
    {
        var known = new HashSet<string> { "click", "type_text" };

        Assert.True(TextToolCallParser.TryParse("<tool_call>{\"name\":\"click\",\"arguments\":{\"x\":1}}</tool_call><tool_call>{\"name\":\"type_text\",\"arguments\":\"{\\\"text\\\":\\\"a\\\"}\"}</tool_call>",
            known, out var calls, out var rest));
        Assert.Equal(2, calls.Count);
        Assert.Equal("{\"x\":1}", calls[0].ArgumentsJson);
        Assert.Equal("{\"text\":\"a\"}", calls[1].ArgumentsJson);
        Assert.Equal("", rest);

        Assert.True(TextToolCallParser.TryParse("ok <tool_call>{\"name\":\"click\",\"arguments\":{\"x\":{\"nested\":true}}}", known, out calls, out rest));
        Assert.Single(calls);
        Assert.Equal("ok", rest);

        Assert.True(TextToolCallParser.TryParse("```\n{\"function\":{\"name\":\"click\",\"parameters\":{\"x\":2}}}\n```", known, out calls, out _));
        Assert.Equal("{\"x\":2}", calls[0].ArgumentsJson);

        Assert.False(TextToolCallParser.TryParse("Just text with {braces}.", known, out _, out _));
        Assert.False(TextToolCallParser.TryParse("<tool_call>{\"name\":\"rm\",\"arguments\":{}}</tool_call>", known, out _, out _));
        Assert.False(TextToolCallParser.TryParse("{\"name\":\"click\",\"arguments\":5}", known, out _, out _));
    }

    [Theory]
    [InlineData("<think>a</think>b", "a", "b")]
    [InlineData("a</think>b", "a", "b")]
    [InlineData("<think>unfinished", "unfinished", "")]
    [InlineData("plain", "", "plain")]
    [InlineData("text <think>x</think>", "", "text <think>x</think>")]
    public void Think_tags_split(string content, string thinking, string visible)
    {
        Assert.Equal((thinking, visible), ThinkTags.Split(content));
    }

    [Theory]
    [InlineData("{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", "Overloaded (overloaded_error)")]
    [InlineData("{\"error\":{\"message\":\"Bad key\",\"type\":\"invalid_request_error\"}}", "Bad key (invalid_request_error)")]
    [InlineData("{\"error\":\"model not found\"}", "model not found")]
    [InlineData("[{\"error\":{\"code\":400,\"message\":\"Gemini says no\",\"status\":\"INVALID_ARGUMENT\"}}]", "Gemini says no (INVALID_ARGUMENT)")]
    [InlineData("{\"detail\":\"Not Found\"}", "Not Found")]
    [InlineData("<html><body>502 Bad Gateway</body></html>", "")]
    [InlineData("upstream   timed\nout", "upstream timed out")]
    public void Error_message_extraction(string body, string expected)
    {
        Assert.Equal(expected, Endpoints.ExtractErrorMessage(JsonUtil.TryParse(body), body));
    }

    [Theory]
    [InlineData("http://localhost:1234/v1", true)]
    [InlineData("http://127.0.0.1:11434", true)]
    [InlineData("http://[::1]:8000/v1", true)]
    [InlineData("http://ollama.localhost", true)]
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("http://192.168.1.5:11434", false)]
    public void Local_url_detection(string url, bool expected)
    {
        Assert.Equal(expected, Endpoints.IsLocal(url));
    }

    [Fact]
    public async Task Transport_retries_a_timed_out_request()
    {
        var handler = new FakeHandler().Hang().Reply("{\"ok\":true}");
        var delays = new List<TimeSpan>();
        var events = new List<AgentEvent>();
        var transport = new HttpTransport(new HttpClient(handler), "Test", "https://example.test", "", TimeSpan.FromMilliseconds(100), null, events.Add,
            (d, _) => { delays.Add(d); return Task.CompletedTask; });

        var reply = await transport.SendAsync(() => transport.CreateRequest(HttpMethod.Get, "https://example.test/x", null, Array.Empty<KeyValuePair<string, string>>()), CancellationToken.None);

        Assert.True(reply.IsSuccess);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(delays));
        Assert.Contains(events, e => e is StatusEvent s && s.Message.Contains("timed out"));
    }

    [Fact]
    public async Task Transport_gives_up_after_repeated_timeouts()
    {
        var handler = new FakeHandler();
        handler.Default = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException(); };
        var transport = new HttpTransport(new HttpClient(handler), "Slow Server", "https://example.test", "", TimeSpan.FromMilliseconds(30), null, null,
            (_, _) => Task.CompletedTask);

        var ex = await Assert.ThrowsAsync<ProviderException>(() =>
            transport.SendAsync(() => transport.CreateRequest(HttpMethod.Get, "https://example.test/x", null, Array.Empty<KeyValuePair<string, string>>()), CancellationToken.None));
        Assert.Contains("Slow Server did not respond", ex.Message);
        Assert.Equal(4, handler.Requests.Count);
    }

    // =====================================================================================
    // Model catalog
    // =====================================================================================

    [Fact]
    public async Task Catalog_claude_cli_lists_presets_with_friendly_names()
    {
        var catalog = new ModelCatalog(new HttpClient(new FakeHandler()));
        var result = await catalog.ListModelsAsync(ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId), "", CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(ProviderPresets.ClaudeModels, result.Models.Select(m => m.Id));
        Assert.Equal("haiku (newest Haiku)", result.Models.Single(m => m.Id == "haiku").DisplayName);
        Assert.Equal("opus (newest Opus)", result.Models.Single(m => m.Id == "opus").DisplayName);
        Assert.Equal("Claude Sonnet 5.5", result.Models.Single(m => m.Id == "claude-sonnet-5-5").DisplayName);
        Assert.Equal("Claude Fable 5.1", result.Models.Single(m => m.Id == "claude-fable-5-1").DisplayName);
        Assert.All(result.Models, m => Assert.True(m.SupportsVision));
    }

    [Theory]
    [InlineData("claude-haiku-4-5-20251001", "Claude Haiku 4.5 (20251001)")]
    [InlineData("claude-sonnet-5", "Claude Sonnet 5")]
    [InlineData("claude-3-7-sonnet-latest", "claude-3-7-sonnet-latest")]
    [InlineData("sonnet", "sonnet (newest Sonnet)")]
    [InlineData("custom", "custom")]
    public void Claude_display_names(string id, string expected)
    {
        Assert.Equal(expected, ModelCatalog.ClaudeDisplayName(id));
    }

    [Fact]
    public async Task Catalog_acp_agent_uses_preset_suggestions()
    {
        var catalog = new ModelCatalog(new HttpClient(new FakeHandler()));
        var result = await catalog.ListModelsAsync(ProviderPresets.CreateProfile(ProviderPresets.GeminiCliId), "", CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "gemini-2.5-pro", "gemini-2.5-flash" }, result.Models.Select(m => m.Id));
    }

    [Fact]
    public async Task Catalog_anthropic_lists_live_models()
    {
        var handler = new FakeHandler().Reply("{\"data\":[{\"type\":\"model\",\"id\":\"claude-haiku-5-5\",\"display_name\":\"Claude Haiku 5.5\"},{\"type\":\"model\",\"id\":\"claude-opus-5-5\"}],\"has_more\":false}");
        var catalog = new ModelCatalog(new HttpClient(handler));
        var result = await catalog.ListModelsAsync(AnthropicProfile(), Key, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "claude-haiku-5-5", "claude-opus-5-5" }, result.Models.Select(m => m.Id));
        Assert.Equal("Claude Haiku 5.5", result.Models[0].DisplayName);
        Assert.Equal("Claude Opus 5.5", result.Models[1].DisplayName);
        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.StartsWith("https://api.anthropic.com/v1/models", req.Uri.ToString());
        Assert.Equal(Key, req.Header("x-api-key"));
        Assert.Equal("2023-06-01", req.Header("anthropic-version"));
    }

    [Fact]
    public async Task Catalog_anthropic_falls_back_to_presets_with_an_error()
    {
        var handler = new FakeHandler().Reply(AnthropicError($"invalid x-api-key {Key}", "authentication_error"), 401);
        var catalog = new ModelCatalog(new HttpClient(handler));
        var result = await catalog.ListModelsAsync(AnthropicProfile(), Key, CancellationToken.None);
        Assert.Contains("The API key was rejected", result.Error);
        Assert.DoesNotContain(Key, result.Error);
        Assert.Contains(result.Models, m => m.Id == "claude-haiku-5-5");

        var noKey = await new ModelCatalog(new HttpClient(new FakeHandler())).ListModelsAsync(AnthropicProfile(), "", CancellationToken.None);
        Assert.NotNull(noKey.Error);
        Assert.NotEmpty(noKey.Models);
    }

    [Fact]
    public async Task Catalog_openrouter_reads_modalities_and_merges_suggestions()
    {
        var json = """
        {"data":[
          {"id":"zeta/text-only","name":"Zeta: Text","architecture":{"input_modalities":["text"]},"supported_parameters":["tools","temperature"]},
          {"id":"anthropic/claude-sonnet-4.5","name":"Anthropic: Claude Sonnet 4.5","architecture":{"input_modalities":["text","image"]},"supported_parameters":["tools","reasoning"]},
          {"id":"alpha/vision","name":"Alpha Vision","architecture":{"input_modalities":["image","text"]}}
        ]}
        """;
        var handler = new FakeHandler().Reply(json);
        var profile = ProviderPresets.CreateProfile(ProviderPresets.OpenRouterId);
        profile.ExtraHeaders["X-Title"] = "DeskPilot";
        var catalog = new ModelCatalog(new HttpClient(handler));
        var result = await catalog.ListModelsAsync(profile, Key, CancellationToken.None);

        Assert.Null(result.Error);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("https://openrouter.ai/api/v1/models", req.Uri.ToString());
        Assert.Equal("Bearer " + Key, req.Header("Authorization"));
        Assert.Equal("DeskPilot", req.Header("X-Title"));

        var ids = result.Models.Select(m => m.Id).ToList();
        var preset = ProviderPresets.Find(ProviderPresets.OpenRouterId)!;
        Assert.Equal(preset.SuggestedModels, ids.Take(preset.SuggestedModels.Count));   // suggestions first
        Assert.Equal(new[] { "alpha/vision", "zeta/text-only" }, ids.Skip(preset.SuggestedModels.Count));
        var sonnet = result.Models.Single(m => m.Id == "anthropic/claude-sonnet-4.5");
        Assert.True(sonnet.SupportsVision);
        Assert.True(sonnet.SupportsTools);
        Assert.True(sonnet.SupportsThinking);
        Assert.Equal("Anthropic: Claude Sonnet 4.5 (anthropic/claude-sonnet-4.5)", sonnet.DisplayName);
        var textOnly = result.Models.Single(m => m.Id == "zeta/text-only");
        Assert.False(textOnly.SupportsVision);
        Assert.False(textOnly.SupportsThinking);
        Assert.Null(result.Models.Single(m => m.Id == "openai/gpt-5-mini").SupportsVision);   // suggestion not served live
    }

    [Fact]
    public async Task Catalog_openai_compatible_filters_non_chat_models_and_strips_prefixes()
    {
        var handler = new FakeHandler().Reply("{\"object\":\"list\",\"data\":[{\"id\":\"models/gemini-2.5-flash\"},{\"id\":\"text-embedding-3-small\"},{\"id\":\"whisper-1\"},{\"id\":\"local-vlm\"}]}");
        var profile = OpenAiProfile(baseUrl: "http://127.0.0.1:1234/v1/");
        var result = await new ModelCatalog(new HttpClient(handler)).ListModelsAsync(profile, "", CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(new[] { "gemini-2.5-flash", "local-vlm" }, result.Models.Select(m => m.Id));
        Assert.Equal("http://127.0.0.1:1234/v1/models", handler.Requests[0].Uri.ToString());
        Assert.Null(handler.Requests[0].Header("Authorization"));
    }

    [Fact]
    public async Task Catalog_ollama_reads_tags_and_capabilities()
    {
        var handler = new FakeHandler();
        handler.Default = (req, _) =>
        {
            if (req.Uri.AbsolutePath == "/api/tags")
                return Task.FromResult(FakeHandler.Response("{\"models\":[{\"name\":\"qwen2.5vl:7b\",\"model\":\"qwen2.5vl:7b\"},{\"name\":\"qwen3:8b\"},{\"name\":\"broken:1b\"}]}"));
            var model = (string?)req.Json["model"];
            return Task.FromResult(model switch
            {
                "qwen2.5vl:7b" => FakeHandler.Response("{\"capabilities\":[\"completion\",\"vision\",\"tools\"]}"),
                "qwen3:8b" => FakeHandler.Response("{\"capabilities\":[\"completion\",\"tools\",\"thinking\"]}"),
                _ => FakeHandler.Response("{\"error\":\"boom\"}", 500),
            });
        };
        var result = await new ModelCatalog(new HttpClient(handler)).ListModelsAsync(OllamaProfile(), "", CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(new[] { "qwen2.5vl:7b", "qwen3:8b", "broken:1b" }, result.Models.Select(m => m.Id));
        Assert.Equal(new ModelInfo("qwen2.5vl:7b", "qwen2.5vl:7b", true, true, false), result.Models[0]);
        Assert.Equal(new ModelInfo("qwen3:8b", "qwen3:8b", false, true, true), result.Models[1]);
        Assert.Equal(new ModelInfo("broken:1b", "broken:1b", null, null, null), result.Models[2]);
        Assert.Equal("http://127.0.0.1:11434/api/tags", handler.Requests[0].Uri.ToString());
        Assert.Equal(3, handler.Requests.Count(r => r.Uri.AbsolutePath == "/api/show" && r.Method == HttpMethod.Post));
    }

    [Fact]
    public async Task Catalog_ollama_not_running_returns_suggestions_and_an_error()
    {
        var handler = new FakeHandler().Throw(new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException((int)SocketError.ConnectionRefused)));
        var result = await new ModelCatalog(new HttpClient(handler)).ListModelsAsync(OllamaProfile(), "", CancellationToken.None);
        Assert.Equal("Ollama (local) is not running at http://127.0.0.1:11434. Start it and try again.", result.Error);
        Assert.Contains(result.Models, m => m.Id == "qwen2.5vl:7b");
    }

    [Fact]
    public async Task Catalog_never_throws_on_garbage_or_cancellation()
    {
        var handler = new FakeHandler().Reply("this is not json");
        var result = await new ModelCatalog(new HttpClient(handler)).ListModelsAsync(OpenAiProfile(), Key, CancellationToken.None);
        Assert.Contains("could not read", result.Error);
        Assert.Empty(result.Models);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancelled = await new ModelCatalog(new HttpClient(new FakeHandler().Hang())).ListModelsAsync(OllamaProfile(), "", cts.Token);
        Assert.NotNull(cancelled.Error);
    }
}

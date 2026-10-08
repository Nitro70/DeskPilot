using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

/// <summary>Anthropic Messages API (POST {base}/v1/messages).</summary>
internal sealed class AnthropicProvider : IChatProvider
{
    public const string DefaultBaseUrl = "https://api.anthropic.com";
    public const string ApiVersion = "2023-06-01";
    public const string ContextManagementBeta = "context-management-2025-06-27";

    private static readonly Regex LegacyThinkingModels = new(
        @"^claude-(haiku-4-5|sonnet-4-5|opus-4-5|opus-4-1|opus-4-0|sonnet-4-0|[a-z]+-4-2025|3)",
        RegexOptions.CultureInvariant);

    private readonly List<JsonObject> _messages = new();
    private readonly HttpTransport _transport;
    private readonly ProviderProfile _profile;
    private readonly string _systemPrompt;
    private readonly string _apiKey;
    private readonly JsonObject? _extraBody;
    private readonly Action<AgentEvent> _emit;
    private readonly bool _vision;
    private readonly string _url;

    // Features the model or endpoint rejected once; remembered for the rest of the session.
    private bool _noThinking, _noThinkingDisplay, _noEffort, _noContextEditing, _noTemperature, _noCacheControl;

    public AnthropicProvider(HttpTransport transport, ProviderProfile profile, string systemPrompt, string apiKey, JsonObject? extraBody, Action<AgentEvent> emit)
    {
        _transport = transport;
        _profile = profile;
        _systemPrompt = systemPrompt ?? "";
        _apiKey = apiKey ?? "";
        _extraBody = extraBody;
        _emit = emit;
        _vision = profile.SupportsVision;
        _url = ResolveBaseUrl(profile) + "/v1/messages";
    }

    public string ProviderName => _transport.ProviderName;
    public int MessageCount => _messages.Count;
    internal IReadOnlyList<JsonObject> Messages => _messages;

    public static string ResolveBaseUrl(ProviderProfile profile)
    {
        var b = Endpoints.TrimBase(profile.BaseUrl);
        if (b.Length == 0) b = DefaultBaseUrl;
        return Endpoints.StripSuffix(b, "/v1/messages", "/v1");
    }

    /// <summary>Lower-case model id starting at "claude-" (tolerates prefixes like "anthropic/").</summary>
    internal static string ModelKey(string? model)
    {
        var m = (model ?? "").Trim().ToLowerInvariant();
        var i = m.IndexOf("claude-", StringComparison.Ordinal);
        return i > 0 ? m[i..] : m;
    }

    /// <summary>Models that still use thinking {type: enabled, budget_tokens} (Haiku 4.5, Sonnet/Opus 4.5 and older).</summary>
    internal static bool IsLegacyThinkingModel(string? model) => LegacyThinkingModels.IsMatch(ModelKey(model));

    /// <summary>Models where thinking cannot be switched off (an explicit disabled is a 400); effort is the only control.</summary>
    internal static bool ThinkingAlwaysOn(string? model)
    {
        var k = ModelKey(model);
        return k.StartsWith("claude-opus-5-5", StringComparison.Ordinal) || k.StartsWith("claude-fable", StringComparison.Ordinal) ||
               k.StartsWith("claude-mythos", StringComparison.Ordinal);
    }

    public void Reset() => _messages.Clear();

    public void TruncateTo(int count)
    {
        if (count >= 0 && count < _messages.Count) _messages.RemoveRange(count, _messages.Count - count);
    }

    public void AddUserTurn(UserTurn turn)
    {
        var blocks = new List<JsonObject>();
        if (_vision)
            foreach (var img in turn.Images) blocks.Add(ImageBlock(img));
        var text = HttpText.UserText(turn, _vision);
        if (text.Length > 0 || blocks.Count == 0)
            blocks.Add(TextBlock(text.Length > 0 ? text : "(empty message)"));
        AppendUser(blocks);
    }

    public void AddToolResults(IReadOnlyList<(ProviderToolCall Call, ToolResult Result)> results)
    {
        var blocks = new List<JsonObject>();
        foreach (var (call, result) in results)
        {
            var content = new JsonArray();
            var text = result.Text ?? "";
            if (!_vision && result.Images.Count > 0) text = text.Length == 0 ? HttpText.ImageOmittedNote : text + "\n" + HttpText.ImageOmittedNote;
            if (text.Length > 0) content.Add(TextBlock(text));
            if (_vision)
                foreach (var img in result.Images) content.Add(ImageBlock(img));
            if (content.Count == 0) content.Add(TextBlock("(no output)"));
            var block = new JsonObject
            {
                ["type"] = "tool_result",
                ["tool_use_id"] = call.Id,
                ["content"] = content,
            };
            if (result.IsError) block["is_error"] = true;
            blocks.Add(block);
        }
        // All results of one assistant step go back in a single user message.
        if (blocks.Count > 0) AppendUser(blocks);
    }

    public void PruneImages(int keep)
    {
        // Editing history that contains thinking blocks invalidates them; context editing handles those conversations.
        if (HasThinkingBlocks()) return;
        var slots = new List<(JsonArray Parent, int Index)>();
        foreach (var msg in _messages)
        {
            if (JsonUtil.Str(msg["role"]) != "user" || msg["content"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
            {
                if (block is not JsonObject b || JsonUtil.Str(b["type"]) != "tool_result" || b["content"] is not JsonArray inner) continue;
                for (int i = 0; i < inner.Count; i++)
                    if (inner[i] is JsonObject part && JsonUtil.Str(part["type"]) == "image") slots.Add((inner, i));
            }
        }
        var remove = slots.Count - Math.Max(0, keep);
        for (int i = 0; i < remove; i++) slots[i].Parent[slots[i].Index] = TextBlock(HttpText.PrunedImageNote);
    }

    internal bool HasThinkingBlocks()
    {
        foreach (var msg in _messages)
        {
            if (JsonUtil.Str(msg["role"]) != "assistant" || msg["content"] is not JsonArray blocks) continue;
            foreach (var block in blocks)
                if (JsonUtil.Str(block?["type"]) is "thinking" or "redacted_thinking") return true;
        }
        return false;
    }

    public async Task<ProviderResponse> CompleteAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        for (int adjustments = 0; ; adjustments++)
        {
            var request = BuildRequest(tools);
            var reply = await _transport.SendAsync(() => CreateHttpRequest(request), ct).ConfigureAwait(false);
            if (reply.IsSuccess) return ParseReply(reply);
            if (reply.StatusCode == 400 && adjustments < 6 && TryRelax(reply.ErrorMessage, request)) continue;
            throw _transport.ErrorFor(reply, _profile.Model);
        }
    }

    internal sealed record BuiltRequest(JsonObject Body, bool Thinking, bool ThinkingDisplay, bool Effort, bool ContextEditing, bool Temperature, bool CacheControl);

    internal BuiltRequest BuildRequest(IReadOnlyList<ToolSpec> tools)
    {
        int maxTokens = Math.Max(1, _profile.MaxOutputTokens);
        var body = new JsonObject { ["model"] = _profile.Model.Trim() };

        var thinking = BuildThinking(ref maxTokens, out bool withDisplay);
        body["max_tokens"] = maxTokens;
        if (!string.IsNullOrWhiteSpace(_systemPrompt)) body["system"] = _systemPrompt;
        if (tools.Count > 0)
        {
            var arr = new JsonArray();
            foreach (var t in tools)
                arr.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = JsonUtil.SchemaNode(t) });
            body["tools"] = arr;
        }
        var messages = new JsonArray();
        foreach (var m in _messages) messages.Add(m.DeepClone());
        body["messages"] = messages;

        if (thinking != null) body["thinking"] = thinking;

        bool effort = !_noEffort && !string.IsNullOrWhiteSpace(_profile.Effort);
        if (effort) body["output_config"] = new JsonObject { ["effort"] = _profile.Effort.Trim().ToLowerInvariant() };

        bool temperature = !_noTemperature && _profile.Temperature is not null && _profile.Thinking != ThinkingMode.On;
        if (temperature) body["temperature"] = _profile.Temperature!.Value;

        bool cache = !_noCacheControl && _profile.UsePromptCaching;
        if (cache) body["cache_control"] = new JsonObject { ["type"] = "ephemeral" };

        bool contextEditing = !_noContextEditing && _profile.UseContextEditing;
        if (contextEditing)
            body["context_management"] = new JsonObject
            {
                ["edits"] = new JsonArray(new JsonObject { ["type"] = "clear_tool_uses_20250919" }),
            };

        if (_extraBody != null) JsonUtil.DeepMerge(body, _extraBody);
        return new BuiltRequest(body, thinking != null, withDisplay, effort, contextEditing, temperature, cache);
    }

    private JsonObject? BuildThinking(ref int maxTokens, out bool withDisplay)
    {
        withDisplay = false;
        if (_noThinking) return null;
        bool legacy = IsLegacyThinkingModel(_profile.Model);
        switch (_profile.Thinking)
        {
            case ThinkingMode.On when legacy:
            {
                int budget = Math.Max(1024, _profile.ThinkingBudgetTokens);
                if (budget >= maxTokens)
                {
                    // budget_tokens must stay below max_tokens; leave room for the visible answer.
                    if (maxTokens - 1024 >= 1024) budget = maxTokens - 1024;
                    else maxTokens = budget + 1024;
                }
                return new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget };
            }
            case ThinkingMode.On:
            {
                var t = new JsonObject { ["type"] = "adaptive" };
                // Current models hide thinking text unless asked; summarized makes it readable in the log.
                if (!_noThinkingDisplay)
                {
                    t["display"] = "summarized";
                    withDisplay = true;
                }
                return t;
            }
            case ThinkingMode.Off:
            {
                if (legacy || ThinkingAlwaysOn(_profile.Model)) return null;
                var effort = (_profile.Effort ?? "").Trim().ToLowerInvariant();
                if (effort is "xhigh" or "max") return null;   // disabling thinking is rejected at the top effort levels
                if (ModelKey(_profile.Model).StartsWith("claude-sonnet-5-5", StringComparison.Ordinal))
                    return new JsonObject { ["type"] = "between_tools" };
                return new JsonObject { ["type"] = "disabled" };
            }
            default:
                return null;
        }
    }

    private bool TryRelax(string errorMessage, BuiltRequest sent)
    {
        var m = (errorMessage ?? "").ToLowerInvariant();
        if (sent.Temperature && m.Contains("temperature"))
            return Disable(ref _noTemperature, "temperature");
        if (sent.ThinkingDisplay && m.Contains("display"))
        {
            _noThinkingDisplay = true;
            return true;
        }
        if (sent.Thinking && m.Contains("thinking"))
            return Disable(ref _noThinking, "thinking");
        if (sent.Effort && (m.Contains("effort") || m.Contains("output_config")))
            return Disable(ref _noEffort, "effort");
        if (sent.ContextEditing && (m.Contains("context_management") || m.Contains("context-management") || m.Contains("beta")))
            return Disable(ref _noContextEditing, "context editing");
        if (sent.CacheControl && m.Contains("cache_control"))
            return Disable(ref _noCacheControl, "prompt caching");
        return false;
    }

    private bool Disable(ref bool flag, string feature)
    {
        flag = true;
        _emit(new StatusEvent($"{ProviderName} rejected the {feature} setting for model '{_profile.Model}'; continuing without it.", StatusLevel.Warning));
        return true;
    }

    private HttpRequestMessage CreateHttpRequest(BuiltRequest request)
    {
        var headers = new List<KeyValuePair<string, string>>
        {
            new("x-api-key", _apiKey),
            new("anthropic-version", ApiVersion),
        };
        if (request.ContextEditing) headers.Add(new("anthropic-beta", ContextManagementBeta));
        return _transport.CreateRequest(HttpMethod.Post, _url, request.Body, headers);
    }

    private ProviderResponse ParseReply(HttpReply reply)
    {
        if (reply.Json is not JsonObject root || root["content"] is not JsonArray content)
            throw new ProviderException($"{ProviderName} returned a response DeskPilot could not read.");

        var texts = new List<string>();
        var thoughts = new List<string>();
        var calls = new List<ProviderToolCall>();
        foreach (var node in content)
        {
            if (node is not JsonObject block) continue;
            switch (JsonUtil.Str(block["type"]))
            {
                case "text":
                    if (JsonUtil.Str(block["text"]) is { Length: > 0 } t) texts.Add(t);
                    break;
                case "thinking":
                    if (JsonUtil.Str(block["thinking"]) is { } th && th.Trim().Length > 0) thoughts.Add(th.Trim());
                    break;
                case "tool_use":
                    var name = JsonUtil.Str(block["name"]) ?? "";
                    var id = JsonUtil.Str(block["id"]);
                    if (string.IsNullOrWhiteSpace(id))
                    {
                        id = JsonUtil.NewToolCallId();
                        block["id"] = id;
                    }
                    calls.Add(new ProviderToolCall(id, name, JsonUtil.ArgumentsText(block["input"])));
                    break;
            }
        }

        var stopReason = JsonUtil.Str(root["stop_reason"]);
        var stop = stopReason switch
        {
            "end_turn" or "stop_sequence" => ProviderStop.EndTurn,
            "tool_use" => ProviderStop.ToolUse,
            "max_tokens" or "model_context_window_exceeded" => ProviderStop.MaxTokens,
            "refusal" => ProviderStop.Refusal,
            "pause_turn" => ProviderStop.PauseTurn,
            _ => ProviderStop.Other,
        };

        var usage = root["usage"] as JsonObject;
        int input = JsonUtil.Int(usage?["input_tokens"]) + JsonUtil.Int(usage?["cache_read_input_tokens"]) + JsonUtil.Int(usage?["cache_creation_input_tokens"]);
        int output = JsonUtil.Int(usage?["output_tokens"]);

        // Content goes back exactly as returned: thinking signatures must stay intact.
        if (content.Count > 0) _messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });

        return new ProviderResponse(string.Join("\n\n", texts), string.Join("\n\n", thoughts), calls, input, output, stop, stopReason,
            JsonUtil.Str(root["model"]));
    }

    private void AppendUser(List<JsonObject> blocks)
    {
        if (_messages.Count > 0 && JsonUtil.Str(_messages[^1]["role"]) == "user")
        {
            // A previous turn ended without a reply (failed or stopped); keep the roles alternating.
            var last = _messages[^1];
            if (last["content"] is not JsonArray existing)
            {
                existing = new JsonArray();
                if (JsonUtil.Str(last["content"]) is { Length: > 0 } s) existing.Add(TextBlock(s));
                last["content"] = existing;
            }
            foreach (var b in blocks) existing.Add(b);
            return;
        }
        var arr = new JsonArray();
        foreach (var b in blocks) arr.Add(b);
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = arr });
    }

    private static JsonObject TextBlock(string text) => new() { ["type"] = "text", ["text"] = text };

    private static JsonObject ImageBlock(ToolImage img) => new()
    {
        ["type"] = "image",
        ["source"] = new JsonObject
        {
            ["type"] = "base64",
            ["media_type"] = img.MediaType,
            ["data"] = img.Base64Data,
        },
    };
}

using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

/// <summary>Ollama's native POST {base}/api/chat (non-streaming).</summary>
internal sealed class OllamaProvider : IChatProvider
{
    public const string DefaultBaseUrl = "http://127.0.0.1:11434";

    private readonly List<JsonObject> _messages = new();
    private readonly HashSet<JsonObject> _toolImageMessages = new(ReferenceEqualityComparer.Instance);
    private readonly HttpTransport _transport;
    private readonly ProviderProfile _profile;
    private readonly string _systemPrompt;
    private readonly JsonObject? _extraBody;
    private readonly Action<AgentEvent> _emit;
    private readonly bool _vision;
    private readonly string _url;

    private bool _noThink;

    public OllamaProvider(HttpTransport transport, ProviderProfile profile, string systemPrompt, JsonObject? extraBody, Action<AgentEvent> emit)
    {
        _transport = transport;
        _profile = profile;
        _systemPrompt = systemPrompt ?? "";
        _extraBody = extraBody;
        _emit = emit;
        _vision = profile.SupportsVision;
        _url = ResolveBaseUrl(profile.BaseUrl) + "/api/chat";
    }

    public string ProviderName => _transport.ProviderName;
    public int MessageCount => _messages.Count;
    internal IReadOnlyList<JsonObject> Messages => _messages;

    /// <summary>The server root, tolerating a pasted /api or OpenAI-style /v1 suffix.</summary>
    public static string ResolveBaseUrl(string? baseUrl)
    {
        var b = Endpoints.TrimBase(baseUrl);
        if (b.Length == 0) b = DefaultBaseUrl;
        return Endpoints.StripSuffix(b, "/api/chat", "/api", "/v1");
    }

    public void Reset()
    {
        _messages.Clear();
        _toolImageMessages.Clear();
    }

    public void TruncateTo(int count)
    {
        if (count < 0 || count >= _messages.Count) return;
        foreach (var m in _messages.Skip(count)) _toolImageMessages.Remove(m);
        _messages.RemoveRange(count, _messages.Count - count);
    }

    public void AddUserTurn(UserTurn turn)
    {
        var text = HttpText.UserText(turn, _vision);
        var msg = new JsonObject { ["role"] = "user", ["content"] = text.Length > 0 ? text : "(empty message)" };
        if (_vision && turn.Images.Count > 0) msg["images"] = Images(turn.Images);
        _messages.Add(msg);
    }

    public void AddToolResults(IReadOnlyList<(ProviderToolCall Call, ToolResult Result)> results)
    {
        var images = new List<ToolImage>();
        foreach (var (call, result) in results)
        {
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["content"] = HttpText.ToolResultText(result, includeImageNote: !_vision),
                ["tool_name"] = call.Name,
            });
            if (_vision) images.AddRange(result.Images);
        }
        if (images.Count == 0) return;
        var msg = new JsonObject
        {
            ["role"] = "user",
            ["content"] = HttpText.ScreenshotFollowsNote,
            ["images"] = Images(images),
        };
        _messages.Add(msg);
        _toolImageMessages.Add(msg);
    }

    public void PruneImages(int keep)
    {
        int total = 0;
        foreach (var m in _messages)
            if (_toolImageMessages.Contains(m) && m["images"] is JsonArray imgs) total += imgs.Count;
        int remove = total - Math.Max(0, keep);
        foreach (var m in _messages)
        {
            if (remove <= 0) break;
            if (!_toolImageMessages.Contains(m) || m["images"] is not JsonArray imgs || imgs.Count == 0) continue;
            int n = Math.Min(remove, imgs.Count);
            for (int i = 0; i < n; i++) imgs.RemoveAt(0);
            if (imgs.Count == 0)
            {
                m.Remove("images");
                m["content"] = HttpText.PrunedImageNote;
            }
            else
            {
                m["content"] = (JsonUtil.Str(m["content"]) ?? "") + "\n" + HttpText.PrunedImageNote;
            }
            remove -= n;
        }
    }

    public async Task<ProviderResponse> CompleteAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        for (int adjustments = 0; ; adjustments++)
        {
            var (body, sentThink) = BuildRequest(tools);
            var reply = await _transport.SendAsync(() => _transport.CreateRequest(HttpMethod.Post, _url, body, Array.Empty<KeyValuePair<string, string>>()), ct)
                .ConfigureAwait(false);
            if (reply.IsSuccess) return ParseReply(reply, tools);

            var message = reply.ErrorMessage;
            var lower = message.ToLowerInvariant();
            if (reply.StatusCode == 400 && adjustments < 3 && sentThink && lower.Contains("think"))
            {
                _noThink = true;
                _emit(new StatusEvent($"Model '{_profile.Model}' does not support the thinking switch; continuing without it.", StatusLevel.Warning));
                continue;
            }
            if (lower.Contains("does not support tools"))
                throw new ProviderException($"Model '{_profile.Model}' cannot call tools in Ollama, so it cannot operate the computer. Pick a model with tool support (the model list marks them).", reply.StatusCode);
            if (reply.StatusCode == 404)
                throw new ProviderException($"Model '{_profile.Model}' not found at {_transport.BaseUrl}. Download it first with: ollama pull {_profile.Model}", 404);
            throw _transport.ErrorFor(reply, _profile.Model);
        }
    }

    internal (JsonObject Body, bool SentThink) BuildRequest(IReadOnlyList<ToolSpec> tools)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(_systemPrompt)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = _systemPrompt });
        foreach (var m in _messages) messages.Add(m.DeepClone());

        var body = new JsonObject
        {
            ["model"] = _profile.Model.Trim(),
            ["messages"] = messages,
            ["stream"] = false,
        };
        if (tools.Count > 0) body["tools"] = JsonUtil.FunctionTools(tools);

        bool sentThink = false;
        if (!_noThink && _profile.Thinking != ThinkingMode.Auto)
        {
            body["think"] = _profile.Thinking == ThinkingMode.On;
            sentThink = true;
        }

        var options = new JsonObject { ["num_predict"] = Math.Max(1, _profile.MaxOutputTokens) };
        if (_profile.Temperature is double t) options["temperature"] = t;
        body["options"] = options;

        if (_extraBody != null) JsonUtil.DeepMerge(body, _extraBody);
        return (body, sentThink);
    }

    private ProviderResponse ParseReply(HttpReply reply, IReadOnlyList<ToolSpec> tools)
    {
        if (reply.Json is not JsonObject root || root["message"] is not JsonObject message)
        {
            var err = reply.ErrorMessage;
            throw new ProviderException($"{ProviderName} returned a response DeskPilot could not read" + (err.Length > 0 ? $": {_transport.Scrub(err)}" : "."));
        }

        var content = JsonUtil.Str(message["content"]) ?? "";
        var thinking = (JsonUtil.Str(message["thinking"]) ?? "").Trim();
        var (tagThinking, visible) = ThinkTags.Split(content);
        if (thinking.Length == 0) thinking = tagThinking;

        var calls = new List<ProviderToolCall>();
        var history = new JsonObject { ["role"] = "assistant" };
        var toolCalls = new JsonArray();
        if (message["tool_calls"] is JsonArray native)
        {
            foreach (var node in native)
            {
                if (node is not JsonObject tc) continue;
                var copy = (JsonObject)tc.DeepClone();
                if (copy["function"] is not JsonObject fn || JsonUtil.Str(fn["name"]) is not { Length: > 0 } name) continue;
                var id = JsonUtil.Str(copy["id"]);
                if (string.IsNullOrWhiteSpace(id)) id = JsonUtil.NewToolCallId();
                toolCalls.Add(copy);
                calls.Add(new ProviderToolCall(id, name, JsonUtil.ArgumentsText(fn["arguments"])));
            }
        }

        var historyContent = content;
        if (calls.Count == 0)
        {
            var known = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
            if (TextToolCallParser.TryParse(visible, known, out var parsed, out var remaining))
            {
                foreach (var p in parsed)
                {
                    toolCalls.Add(new JsonObject
                    {
                        ["function"] = new JsonObject { ["name"] = p.Name, ["arguments"] = JsonUtil.TryParse(p.ArgumentsJson) ?? new JsonObject() },
                    });
                    calls.Add(new ProviderToolCall(JsonUtil.NewToolCallId(), p.Name, p.ArgumentsJson));
                }
                visible = remaining;
                historyContent = remaining;
            }
        }

        history["content"] = historyContent;
        if (calls.Count > 0) history["tool_calls"] = toolCalls;
        _messages.Add(history);

        var doneReason = JsonUtil.Str(root["done_reason"]);
        var stop = calls.Count > 0 ? ProviderStop.ToolUse
            : doneReason == "length" ? ProviderStop.MaxTokens
            : doneReason is null or "stop" ? ProviderStop.EndTurn
            : ProviderStop.Other;

        return new ProviderResponse(visible, thinking, calls, JsonUtil.Int(root["prompt_eval_count"]), JsonUtil.Int(root["eval_count"]),
            stop, doneReason, JsonUtil.Str(root["model"]));
    }

    private static JsonArray Images(IEnumerable<ToolImage> images)
    {
        var arr = new JsonArray();
        foreach (var img in images) arr.Add(img.Base64Data);
        return arr;
    }
}

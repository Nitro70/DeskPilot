using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

/// <summary>Any OpenAI-compatible POST {base}/chat/completions endpoint.</summary>
internal sealed class OpenAiCompatProvider : IChatProvider
{
    private readonly List<JsonObject> _messages = new();
    private readonly HashSet<JsonObject> _toolImageMessages = new(ReferenceEqualityComparer.Instance);
    // reasoning_content per assistant message, kept out of history unless the server demands it back.
    private readonly Dictionary<JsonObject, string> _reasoning = new(ReferenceEqualityComparer.Instance);
    private readonly HttpTransport _transport;
    private readonly ProviderProfile _profile;
    private readonly string _systemPrompt;
    private readonly string _apiKey;
    private readonly JsonObject? _extraBody;
    private readonly Action<AgentEvent> _emit;
    private readonly bool _vision;
    private readonly string _url;

    private bool _noReasoning, _noTemperature, _forceMaxCompletionTokens, _includeReasoningContent;

    public OpenAiCompatProvider(HttpTransport transport, ProviderProfile profile, string systemPrompt, string apiKey, JsonObject? extraBody, Action<AgentEvent> emit)
    {
        _transport = transport;
        _profile = profile;
        _systemPrompt = systemPrompt ?? "";
        _apiKey = apiKey ?? "";
        _extraBody = extraBody;
        _emit = emit;
        _vision = profile.SupportsVision;
        _url = ResolveUrl(profile.BaseUrl);
    }

    public string ProviderName => _transport.ProviderName;
    public int MessageCount => _messages.Count;
    internal IReadOnlyList<JsonObject> Messages => _messages;

    public static string ResolveUrl(string? baseUrl)
    {
        var b = Endpoints.TrimBase(baseUrl);
        return b.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? b : b + "/chat/completions";
    }

    /// <summary>OpenAI's own API and its reasoning models take max_completion_tokens instead of max_tokens.</summary>
    internal static bool UsesMaxCompletionTokens(string? baseUrl, string? model)
    {
        if (Endpoints.Host(Endpoints.TrimBase(baseUrl)).Equals("api.openai.com", StringComparison.OrdinalIgnoreCase)) return true;
        var m = (model ?? "").Trim().ToLowerInvariant();
        return m.StartsWith("o1", StringComparison.Ordinal) || m.StartsWith("o3", StringComparison.Ordinal) ||
               m.StartsWith("o4", StringComparison.Ordinal) || m.StartsWith("gpt-5", StringComparison.Ordinal);
    }

    public void Reset()
    {
        _messages.Clear();
        _toolImageMessages.Clear();
        _reasoning.Clear();
    }

    public void TruncateTo(int count)
    {
        if (count < 0 || count >= _messages.Count) return;
        foreach (var m in _messages.Skip(count))
        {
            _toolImageMessages.Remove(m);
            _reasoning.Remove(m);
        }
        _messages.RemoveRange(count, _messages.Count - count);
    }

    public void AddUserTurn(UserTurn turn)
    {
        var text = HttpText.UserText(turn, _vision);
        if (_vision && turn.Images.Count > 0)
        {
            var parts = new JsonArray();
            if (text.Length > 0) parts.Add(new JsonObject { ["type"] = "text", ["text"] = text });
            foreach (var img in turn.Images) parts.Add(ImagePart(img));
            _messages.Add(new JsonObject { ["role"] = "user", ["content"] = parts });
            return;
        }
        _messages.Add(new JsonObject { ["role"] = "user", ["content"] = text.Length > 0 ? text : "(empty message)" });
    }

    public void AddToolResults(IReadOnlyList<(ProviderToolCall Call, ToolResult Result)> results)
    {
        var images = new List<ToolImage>();
        foreach (var (call, result) in results)
        {
            _messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = call.Id,
                ["content"] = HttpText.ToolResultText(result, includeImageNote: !_vision),
            });
            if (_vision) images.AddRange(result.Images);
        }
        if (images.Count == 0) return;
        // Tool messages are text-only in this protocol; screenshots follow in one user message.
        var parts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = HttpText.ScreenshotFollowsNote } };
        foreach (var img in images) parts.Add(ImagePart(img));
        var msg = new JsonObject { ["role"] = "user", ["content"] = parts };
        _messages.Add(msg);
        _toolImageMessages.Add(msg);
    }

    public void PruneImages(int keep)
    {
        var slots = new List<(JsonArray Parts, int Index)>();
        foreach (var msg in _messages)
        {
            if (!_toolImageMessages.Contains(msg) || msg["content"] is not JsonArray parts) continue;
            for (int i = 0; i < parts.Count; i++)
                if (JsonUtil.Str(parts[i]?["type"]) == "image_url") slots.Add((parts, i));
        }
        var remove = slots.Count - Math.Max(0, keep);
        for (int i = 0; i < remove; i++)
            slots[i].Parts[slots[i].Index] = new JsonObject { ["type"] = "text", ["text"] = HttpText.PrunedImageNote };
    }

    public async Task<ProviderResponse> CompleteAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct)
    {
        for (int adjustments = 0; ; adjustments++)
        {
            var request = BuildRequest(tools);
            var reply = await _transport.SendAsync(() => CreateHttpRequest(request.Body), ct).ConfigureAwait(false);
            if (reply.IsSuccess) return ParseReply(reply, tools);
            if (reply.StatusCode == 400 && adjustments < 5 && TryRelax(reply.ErrorMessage, request)) continue;
            throw _transport.ErrorFor(reply, _profile.Model);
        }
    }

    internal sealed record BuiltRequest(JsonObject Body, bool Reasoning, bool Temperature, bool UsedMaxTokens);

    internal BuiltRequest BuildRequest(IReadOnlyList<ToolSpec> tools)
    {
        var messages = new JsonArray();
        if (!string.IsNullOrWhiteSpace(_systemPrompt)) messages.Add(new JsonObject { ["role"] = "system", ["content"] = _systemPrompt });
        foreach (var m in _messages)
        {
            var copy = (JsonObject)m.DeepClone();
            if (_includeReasoningContent && _reasoning.TryGetValue(m, out var r)) copy["reasoning_content"] = r;
            messages.Add(copy);
        }

        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(_profile.Model)) body["model"] = _profile.Model.Trim();
        body["messages"] = messages;
        if (tools.Count > 0) body["tools"] = JsonUtil.FunctionTools(tools);

        bool completionTokens = _forceMaxCompletionTokens || UsesMaxCompletionTokens(_profile.BaseUrl, _profile.Model);
        body[completionTokens ? "max_completion_tokens" : "max_tokens"] = Math.Max(1, _profile.MaxOutputTokens);

        bool temperature = !_noTemperature && _profile.Temperature is not null;
        if (temperature) body["temperature"] = _profile.Temperature!.Value;

        bool reasoning = !_noReasoning && AddReasoning(body);

        if (_extraBody != null) JsonUtil.DeepMerge(body, _extraBody);
        return new BuiltRequest(body, reasoning, temperature, !completionTokens);
    }

    private bool AddReasoning(JsonObject body)
    {
        var effort = (_profile.Effort ?? "").Trim().ToLowerInvariant();
        switch (_profile.ReasoningStyle)
        {
            case ReasoningStyle.ReasoningEffort:
                if (effort.Length > 0)
                {
                    body["reasoning_effort"] = effort;
                    return true;
                }
                // Ollama's and other local servers' OpenAI endpoints accept "none" to switch thinking off.
                if (_profile.Thinking == ThinkingMode.Off && Endpoints.IsLocal(_profile.BaseUrl))
                {
                    body["reasoning_effort"] = "none";
                    return true;
                }
                return false;
            case ReasoningStyle.OpenRouter:
                if (_profile.Thinking == ThinkingMode.Off)
                {
                    body["reasoning"] = new JsonObject { ["enabled"] = false };
                    return true;
                }
                if (effort.Length > 0)
                {
                    body["reasoning"] = new JsonObject { ["effort"] = effort };
                    return true;
                }
                if (_profile.Thinking == ThinkingMode.On)
                {
                    body["reasoning"] = new JsonObject { ["enabled"] = true };
                    return true;
                }
                return false;
            default:
                return false;
        }
    }

    private bool TryRelax(string errorMessage, BuiltRequest sent)
    {
        var m = (errorMessage ?? "").ToLowerInvariant();
        if (!_includeReasoningContent && _reasoning.Count > 0 && m.Contains("reasoning_content"))
        {
            _includeReasoningContent = true;
            return true;
        }
        if (sent.UsedMaxTokens && m.Contains("max_completion_tokens"))
        {
            _forceMaxCompletionTokens = true;
            return true;
        }
        if (sent.Temperature && m.Contains("temperature"))
            return Disable(ref _noTemperature, "temperature");
        if (sent.Reasoning && m.Contains("reasoning"))
            return Disable(ref _noReasoning, "reasoning");
        return false;
    }

    private bool Disable(ref bool flag, string feature)
    {
        flag = true;
        _emit(new StatusEvent($"{ProviderName} rejected the {feature} setting for model '{_profile.Model}'; continuing without it.", StatusLevel.Warning));
        return true;
    }

    private HttpRequestMessage CreateHttpRequest(JsonObject body)
    {
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(_apiKey)) headers.Add(new("Authorization", "Bearer " + _apiKey));
        return _transport.CreateRequest(HttpMethod.Post, _url, body, headers);
    }

    private ProviderResponse ParseReply(HttpReply reply, IReadOnlyList<ToolSpec> tools)
    {
        if (reply.Json is not JsonObject root)
            throw new ProviderException($"{ProviderName} returned a response DeskPilot could not read.");
        if ((root["choices"] as JsonArray)?.FirstOrDefault() is not JsonObject choice)
        {
            var err = reply.ErrorMessage;
            throw new ProviderException($"{ProviderName} returned no answer" + (err.Length > 0 ? $": {_transport.Scrub(err)}" : "."));
        }

        var message = choice["message"] as JsonObject ?? new JsonObject();
        var rawContent = JsonUtil.ContentText(message["content"]);
        var reasoning = JsonUtil.Str(message["reasoning_content"]) ?? JsonUtil.Str(message["reasoning"]) ?? "";
        var (tagThinking, visible) = ThinkTags.Split(rawContent);
        var thinking = reasoning.Trim().Length > 0 ? reasoning.Trim() : tagThinking;

        var calls = new List<ProviderToolCall>();
        var history = new JsonObject { ["role"] = "assistant" };
        var toolCalls = new JsonArray();
        if (message["tool_calls"] is JsonArray native)
        {
            foreach (var node in native)
            {
                if (node is not JsonObject tc) continue;
                // Keep provider-specific fields (e.g. thought signatures) by copying the call as returned.
                var copy = (JsonObject)tc.DeepClone();
                if (copy["function"] is not JsonObject fn || JsonUtil.Str(fn["name"]) is not { Length: > 0 } name) continue;
                var id = JsonUtil.Str(copy["id"]);
                if (string.IsNullOrWhiteSpace(id))
                {
                    id = JsonUtil.NewToolCallId();
                    copy["id"] = id;
                }
                if (copy["type"] is null) copy["type"] = "function";
                var args = JsonUtil.ArgumentsText(fn["arguments"]);
                fn["arguments"] = args;
                toolCalls.Add(copy);
                calls.Add(new ProviderToolCall(id, name, args));
            }
        }

        var historyContent = rawContent;
        if (calls.Count == 0)
        {
            var known = new HashSet<string>(tools.Select(t => t.Name), StringComparer.Ordinal);
            if (TextToolCallParser.TryParse(visible, known, out var parsed, out var remaining))
            {
                foreach (var p in parsed)
                {
                    var id = JsonUtil.NewToolCallId();
                    toolCalls.Add(new JsonObject
                    {
                        ["id"] = id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = p.Name, ["arguments"] = p.ArgumentsJson },
                    });
                    calls.Add(new ProviderToolCall(id, p.Name, p.ArgumentsJson));
                }
                visible = remaining;
                historyContent = remaining;
            }
        }

        if (calls.Count > 0)
        {
            history["content"] = historyContent.Length > 0 ? historyContent : null;
            history["tool_calls"] = toolCalls;
        }
        else
        {
            history["content"] = historyContent;
        }
        _messages.Add(history);
        if (reasoning.Trim().Length > 0) _reasoning[history] = reasoning;

        var finish = JsonUtil.Str(choice["finish_reason"]);
        var refusal = JsonUtil.Str(message["refusal"]);
        var stop = finish switch
        {
            _ when calls.Count > 0 => ProviderStop.ToolUse,
            "length" => ProviderStop.MaxTokens,
            "content_filter" => ProviderStop.Refusal,
            _ when !string.IsNullOrWhiteSpace(refusal) && visible.Length == 0 => ProviderStop.Refusal,
            "stop" or null => ProviderStop.EndTurn,
            _ => ProviderStop.Other,
        };

        var usage = root["usage"] as JsonObject;
        return new ProviderResponse(visible, thinking, calls, JsonUtil.Int(usage?["prompt_tokens"]), JsonUtil.Int(usage?["completion_tokens"]),
            stop, finish, JsonUtil.Str(root["model"]));
    }

    private static JsonObject ImagePart(ToolImage img) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = $"data:{img.MediaType};base64,{img.Base64Data}" },
    };
}

/// <summary>Splits &lt;think&gt;...&lt;/think&gt; reasoning that some local models emit inside the reply text.</summary>
internal static class ThinkTags
{
    private const string Open = "<think>";
    private const string Close = "</think>";

    public static (string Thinking, string Visible) Split(string? content)
    {
        if (string.IsNullOrEmpty(content)) return ("", "");
        int open = content.IndexOf(Open, StringComparison.OrdinalIgnoreCase);
        int close = content.IndexOf(Close, StringComparison.OrdinalIgnoreCase);
        bool leadingOpen = open >= 0 && content[..open].Trim().Length == 0;
        if (close >= 0 && (open < 0 || (leadingOpen && open < close)))
        {
            // The opening tag may be part of the prompt template, so a lone closing tag also counts.
            int start = open >= 0 ? open + Open.Length : 0;
            return (content[start..close].Trim(), content[(close + Close.Length)..].Trim());
        }
        if (leadingOpen && close < 0) return (content[(open + Open.Length)..].Trim(), "");
        return ("", content);
    }
}

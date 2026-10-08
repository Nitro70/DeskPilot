using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Cli;

internal enum ClaudeLineKind
{
    /// <summary>Blank line or a message type DeskPilot does not care about.</summary>
    Ignored,
    /// <summary>Not JSON, or JSON without a type.</summary>
    Garbage,
    Init,
    Assistant,
    /// <summary>The CLI echoing tool results back as a user message (tool events come from ObservedToolHost).</summary>
    UserEcho,
    RateLimit,
    System,
    ControlResponse,
    ControlRequest,
    Result,
}

/// <summary>The `result` line that ends a turn.</summary>
internal sealed record ClaudeResultInfo(
    string Subtype,
    bool IsError,
    string? Text,
    string? TerminalReason,
    double? CostUsd,
    long InputTokens,
    long OutputTokens,
    int? NumTurns,
    IReadOnlyList<string> Errors,
    string? Model)
{
    public bool IsSuccess => !IsError && Subtype == "success";

    /// <summary>The turn was interrupted (interrupt control_request, or the CLI aborted streaming).</summary>
    public bool IsAborted => TerminalReason?.StartsWith("aborted", StringComparison.OrdinalIgnoreCase) == true;
}

/// <summary>What one stdout line means. Only the fields relevant to its Kind are set.</summary>
internal sealed class ClaudeStreamLine
{
    public static readonly ClaudeStreamLine IgnoredLine = new() { Kind = ClaudeLineKind.Ignored };
    public static readonly ClaudeStreamLine GarbageLine = new() { Kind = ClaudeLineKind.Garbage };
    public static readonly ClaudeStreamLine UserEchoLine = new() { Kind = ClaudeLineKind.UserEcho };

    public ClaudeLineKind Kind { get; init; }
    /// <summary>Events to show in the UI, in order.</summary>
    public IReadOnlyList<AgentEvent> Events { get; init; } = Array.Empty<AgentEvent>();
    /// <summary>Init / assistant: the model in use.</summary>
    public string? Model { get; init; }
    /// <summary>Init: status of the expected MCP server ("connected", "failed", "not listed"...), null when not checked.</summary>
    public string? McpStatus { get; init; }
    public ClaudeResultInfo? Result { get; init; }
    /// <summary>control_response / control_request: the request id.</summary>
    public string? RequestId { get; init; }
    /// <summary>control_response: "success" / "error"; control_request: the request subtype; system: the subtype.</summary>
    public string? Subtype { get; init; }
    /// <summary>control_response error text.</summary>
    public string? ControlError { get; init; }
    /// <summary>control_request: the tool a can_use_tool request asks about, and its input as raw JSON.</summary>
    public string? ToolName { get; init; }
    public string? ToolInputJson { get; init; }
    /// <summary>Assistant: the CLI's error tag on a synthetic error message (e.g. "authentication_failed").</summary>
    public string? AssistantError { get; init; }
    /// <summary>Assistant: all text blocks joined (also set for suppressed error messages).</summary>
    public string? AssistantText { get; init; }
}

/// <summary>Turns stream-json stdout lines from `claude -p --output-format stream-json` into meaning. Pure, never throws.</summary>
internal static class ClaudeStreamParser
{
    private const int MaxErrorLength = 600;

    /// <param name="expectedMcpServer">Name of DeskPilot's MCP server to check in system/init, or null to skip the check.</param>
    public static ClaudeStreamLine Parse(string? line, string? expectedMcpServer)
    {
        if (string.IsNullOrWhiteSpace(line)) return ClaudeStreamLine.IgnoredLine;
        var span = line.AsSpan().Trim();
        if (span.Length == 0 || span[0] != '{') return ClaudeStreamLine.GarbageLine;

        // Tool result echoes carry whole screenshots in base64; skip them without parsing.
        if (span.StartsWith("{\"type\":\"user\"", StringComparison.Ordinal)) return ClaudeStreamLine.UserEchoLine;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ClaudeStreamLine.GarbageLine;
            var type = Str(root, "type");
            return type switch
            {
                null => ClaudeStreamLine.GarbageLine,
                "system" => ParseSystem(root, expectedMcpServer),
                "assistant" => ParseAssistant(root),
                "user" => ClaudeStreamLine.UserEchoLine,
                "rate_limit_event" => ParseRateLimit(root),
                "control_response" => ParseControlResponse(root),
                "control_request" => ParseControlRequest(root),
                "result" => new ClaudeStreamLine { Kind = ClaudeLineKind.Result, Result = ParseResult(root) },
                _ => ClaudeStreamLine.IgnoredLine,
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return ClaudeStreamLine.GarbageLine;
        }
    }

    private static ClaudeStreamLine ParseSystem(JsonElement root, string? expectedMcpServer)
    {
        var subtype = Str(root, "subtype");
        switch (subtype)
        {
            case "init":
            {
                string? status = null;
                var events = new List<AgentEvent>();
                if (!string.IsNullOrEmpty(expectedMcpServer))
                {
                    status = "not listed";
                    if (root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var s in servers.EnumerateArray())
                        {
                            if (s.ValueKind == JsonValueKind.Object && string.Equals(Str(s, "name"), expectedMcpServer, StringComparison.Ordinal))
                            {
                                status = Str(s, "status") ?? "unknown";
                                break;
                            }
                        }
                    }
                    if (string.Equals(status, "pending", StringComparison.OrdinalIgnoreCase))
                        events.Add(new StatusEvent("DeskPilot's tools are still connecting to Claude Code.", StatusLevel.Warning));
                    else if (!string.Equals(status, "connected", StringComparison.OrdinalIgnoreCase))
                        events.Add(new StatusEvent($"DeskPilot's tools did not connect to Claude Code: {status}", StatusLevel.Error));
                }
                return new ClaudeStreamLine { Kind = ClaudeLineKind.Init, Model = Str(root, "model"), McpStatus = status, Subtype = subtype, Events = events };
            }
            case "compact_boundary":
                return new ClaudeStreamLine
                {
                    Kind = ClaudeLineKind.System,
                    Subtype = subtype,
                    Events = new AgentEvent[] { new StatusEvent("Claude Code compacted the conversation to save context.") },
                };
            case "api_retry":
            {
                var attempt = Int(root, "attempt");
                var max = Int(root, "max_retries");
                var error = Str(root, "error") ?? (Int(root, "error_status") is { } code ? "HTTP " + code.ToString(CultureInfo.InvariantCulture) : null);
                var msg = "Claude is busy, retrying the request" +
                          (attempt is { } a ? $" (attempt {a}{(max is { } m ? $" of {m}" : "")})" : "") +
                          (string.IsNullOrWhiteSpace(error) ? "." : $": {error}");
                return new ClaudeStreamLine { Kind = ClaudeLineKind.System, Subtype = subtype, Events = new AgentEvent[] { new StatusEvent(msg, StatusLevel.Warning) } };
            }
            default:
                return new ClaudeStreamLine { Kind = ClaudeLineKind.System, Subtype = subtype };
        }
    }

    private static ClaudeStreamLine ParseAssistant(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
            return new ClaudeStreamLine { Kind = ClaudeLineKind.Assistant };

        var model = Str(message, "model");
        var errorTag = Str(root, "error");
        var events = new List<AgentEvent>();
        var text = new StringBuilder();

        if (message.TryGetProperty("content", out var content))
        {
            if (content.ValueKind == JsonValueKind.String)
            {
                AppendText(content.GetString());
            }
            else if (content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.ValueKind != JsonValueKind.Object) continue;
                    switch (Str(block, "type"))
                    {
                        case "text":
                            AppendText(Str(block, "text"));
                            break;
                        case "thinking":
                            // Thinking can be empty with only a signature (thinking summaries off); skip those.
                            var thinking = Str(block, "thinking");
                            if (!string.IsNullOrWhiteSpace(thinking)) events.Add(new ThinkingEvent(thinking));
                            break;
                        // tool_use, redacted_thinking, server tool blocks: tool activity is reported by ObservedToolHost.
                    }
                }
            }
        }

        var joined = text.Length == 0 ? null : text.ToString();
        // The CLI reports API/login failures as a synthetic assistant message followed by an error result.
        // Show the readable turn error instead of the raw message.
        var isErrorMessage = !string.IsNullOrEmpty(errorTag) ||
                             (model == "<synthetic>" && joined != null && (IsAuthFailure(joined) || joined.StartsWith("API Error", StringComparison.OrdinalIgnoreCase)));
        if (isErrorMessage) events.RemoveAll(e => e is AssistantTextEvent);

        return new ClaudeStreamLine
        {
            Kind = ClaudeLineKind.Assistant,
            Model = model == "<synthetic>" ? null : model,
            Events = events,
            AssistantError = isErrorMessage ? errorTag ?? "error" : null,
            AssistantText = joined,
        };

        void AppendText(string? t)
        {
            if (string.IsNullOrWhiteSpace(t)) return;
            events.Add(new AssistantTextEvent(t));
            if (text.Length > 0) text.Append("\n\n");
            text.Append(t);
        }
    }

    private static ClaudeStreamLine ParseRateLimit(JsonElement root)
    {
        var info = root.TryGetProperty("rate_limit_info", out var i) && i.ValueKind == JsonValueKind.Object ? i : default;
        var status = info.ValueKind == JsonValueKind.Object ? Str(info, "status") : null;
        if (status == null || string.Equals(status, "allowed", StringComparison.OrdinalIgnoreCase))
            return new ClaudeStreamLine { Kind = ClaudeLineKind.RateLimit, Subtype = status };

        var window = Str(info, "rateLimitType") switch
        {
            "five_hour" => "5-hour",
            "seven_day" => "weekly",
            "seven_day_opus" => "weekly Opus",
            "seven_day_sonnet" => "weekly Sonnet",
            null or "" => null,
            var other => other.Replace('_', ' '),
        };
        var resets = DescribeReset(info);
        var what = status.Equals("allowed_warning", StringComparison.OrdinalIgnoreCase)
            ? "Approaching your Claude usage limit"
            : status.Equals("rejected", StringComparison.OrdinalIgnoreCase)
                ? "Claude usage limit reached"
                : $"Claude usage limit status: {status}";
        var msg = what + (window != null ? $" ({window} window)" : "") + (resets != null ? $", resets {resets}" : "") + ".";
        return new ClaudeStreamLine
        {
            Kind = ClaudeLineKind.RateLimit,
            Subtype = status,
            Events = new AgentEvent[] { new StatusEvent(msg, StatusLevel.Warning) },
        };
    }

    private static string? DescribeReset(JsonElement info)
    {
        if (!info.TryGetProperty("resetsAt", out var r)) return null;
        long? seconds = r.ValueKind switch
        {
            JsonValueKind.Number when r.TryGetInt64(out var n) => n,
            JsonValueKind.String when long.TryParse(r.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
            _ => null,
        };
        if (seconds is not { } s || s <= 0) return null;
        // Some versions send milliseconds.
        if (s > 100_000_000_000) s /= 1000;
        try
        {
            var local = DateTimeOffset.FromUnixTimeSeconds(s).ToLocalTime();
            var fmt = local.Date == DateTimeOffset.Now.Date ? "HH:mm" : "ddd HH:mm";
            return "at " + local.ToString(fmt, CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static ClaudeStreamLine ParseControlResponse(JsonElement root)
    {
        var resp = root.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        return new ClaudeStreamLine
        {
            Kind = ClaudeLineKind.ControlResponse,
            RequestId = resp.ValueKind == JsonValueKind.Object ? Str(resp, "request_id") : Str(root, "request_id"),
            Subtype = resp.ValueKind == JsonValueKind.Object ? Str(resp, "subtype") : null,
            ControlError = resp.ValueKind == JsonValueKind.Object ? Str(resp, "error") : null,
        };
    }

    private static ClaudeStreamLine ParseControlRequest(JsonElement root)
    {
        var req = root.TryGetProperty("request", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
        string? toolInput = null;
        if (req.ValueKind == JsonValueKind.Object && req.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object)
            toolInput = input.GetRawText();
        return new ClaudeStreamLine
        {
            Kind = ClaudeLineKind.ControlRequest,
            RequestId = Str(root, "request_id"),
            Subtype = req.ValueKind == JsonValueKind.Object ? Str(req, "subtype") : null,
            ToolName = req.ValueKind == JsonValueKind.Object ? Str(req, "tool_name") : null,
            ToolInputJson = toolInput,
        };
    }

    internal static ClaudeResultInfo ParseResult(JsonElement root)
    {
        long input = 0, output = 0;
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            input = Long(usage, "input_tokens") + Long(usage, "cache_creation_input_tokens") + Long(usage, "cache_read_input_tokens");
            output = Long(usage, "output_tokens");
        }

        var errors = new List<string>();
        if (root.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in errs.EnumerateArray())
            {
                var s = e.ValueKind == JsonValueKind.String ? e.GetString() : e.ValueKind == JsonValueKind.Object ? Str(e, "message") ?? e.GetRawText() : null;
                if (!string.IsNullOrWhiteSpace(s)) errors.Add(s);
            }
        }

        string? model = null;
        if (root.TryGetProperty("modelUsage", out var mu) && mu.ValueKind == JsonValueKind.Object)
            model = mu.EnumerateObject().Select(p => p.Name).FirstOrDefault();

        double? cost = null;
        if (root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var cd)) cost = cd;

        return new ClaudeResultInfo(
            Subtype: Str(root, "subtype") ?? "",
            IsError: root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True,
            Text: Str(root, "result"),
            TerminalReason: Str(root, "terminal_reason"),
            CostUsd: cost,
            InputTokens: input,
            OutputTokens: output,
            NumTurns: Int(root, "num_turns"),
            Errors: errors,
            Model: model);
    }

    // ------------------------------------------------------------------ failure text

    /// <summary>True for messages that mean the CLI has no usable login.</summary>
    public static bool IsAuthFailure(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        return text.Contains("not logged in", StringComparison.OrdinalIgnoreCase)
               || text.Contains("/login", StringComparison.OrdinalIgnoreCase)
               || text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
               || text.Contains("authentication", StringComparison.OrdinalIgnoreCase)
               || text.Contains("OAuth", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A readable error for a failed result line.</summary>
    public static string DescribeFailure(ClaudeResultInfo r, string? assistantError)
    {
        var raw = !string.IsNullOrWhiteSpace(r.Text) ? r.Text!.Trim()
            : r.Errors.Count > 0 ? string.Join("; ", r.Errors)
            : null;

        if (string.Equals(assistantError, "authentication_failed", StringComparison.OrdinalIgnoreCase) || IsAuthFailure(raw) || r.Errors.Any(IsAuthFailure))
            return ClaudeCliCommand.NotLoggedInMessage;

        switch (r.Subtype)
        {
            case "error_max_turns":
                return "Claude Code stopped: it reached its maximum number of turns for this request.";
            case "error_max_budget_usd":
                return "Claude Code stopped: the budget limit for this request was reached.";
        }

        if (string.Equals(assistantError, "billing_error", StringComparison.OrdinalIgnoreCase))
            return "Claude Code reported a billing problem" + (raw != null ? ": " + Clip(raw) : ".");
        if (string.Equals(assistantError, "rate_limit", StringComparison.OrdinalIgnoreCase) ||
            (raw != null && (raw.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || raw.Contains("rate limit", StringComparison.OrdinalIgnoreCase))))
            return "Claude usage limit reached" + (raw != null ? ": " + Clip(raw) : ".");

        return raw != null
            ? "Claude Code error: " + Clip(raw)
            : $"Claude Code ended the turn with an error ({(string.IsNullOrEmpty(r.Subtype) ? "unknown" : r.Subtype)}).";
    }

    internal static string Clip(string s, int max = MaxErrorLength) => s.Length <= max ? s : s[..max] + "...";

    // ------------------------------------------------------------------ json helpers

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long Long(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return 0;
        if (v.TryGetInt64(out var l)) return Math.Max(0, l);
        return v.TryGetDouble(out var d) && d > 0 ? (long)Math.Min(d, long.MaxValue) : 0;
    }

    private static int? Int(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var i) ? i : null;
    }
}

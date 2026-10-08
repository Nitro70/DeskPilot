using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Http;

/// <summary>A tool call requested by the model. ArgumentsJson is the raw argument object as JSON text.</summary>
internal sealed record ProviderToolCall(string Id, string Name, string ArgumentsJson);

/// <summary>Why the model stopped, normalized across providers.</summary>
internal enum ProviderStop { EndTurn, ToolUse, MaxTokens, Refusal, PauseTurn, Other }

internal sealed record ProviderResponse(
    string Text,
    string Thinking,
    IReadOnlyList<ProviderToolCall> ToolCalls,
    int InputTokens,
    int OutputTokens,
    ProviderStop Stop,
    string? StopReason,
    string? Model);

/// <summary>
/// One wire protocol. Each provider keeps its own append-only message list in its native JSON shape, so
/// whatever the model returned (thinking signatures, provider-specific fields) goes back unchanged.
/// </summary>
internal interface IChatProvider
{
    /// <summary>Human-readable provider name used in error messages (the profile name).</summary>
    string ProviderName { get; }

    /// <summary>Number of messages in the history (system prompt excluded).</summary>
    int MessageCount { get; }

    void Reset();

    /// <summary>Drops messages added after the history had <paramref name="count"/> messages.</summary>
    void TruncateTo(int count);

    void AddUserTurn(UserTurn turn);

    /// <summary>Sends the conversation and appends the assistant reply to the history.</summary>
    Task<ProviderResponse> CompleteAsync(IReadOnlyList<ToolSpec> tools, CancellationToken ct);

    /// <summary>Appends the results of every tool call of the last reply in one step.</summary>
    void AddToolResults(IReadOnlyList<(ProviderToolCall Call, ToolResult Result)> results);

    /// <summary>Keeps the newest <paramref name="keep"/> tool screenshots; older ones become a short text note.</summary>
    void PruneImages(int keep);
}

/// <summary>A provider failure with a message that is safe to show (never contains the API key).</summary>
internal sealed class ProviderException : Exception
{
    public ProviderException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;

    public int? StatusCode { get; }
}

internal static class HttpText
{
    public const string ImageOmittedNote = "[image omitted: this model cannot see images]";
    public const string PrunedImageNote = "[older screenshot removed]";
    public const string ScreenshotFollowsNote = "Screenshot from the tool result above:";
    public const string NotExecutedNote = "Not executed: the task was stopped before this tool ran.";

    /// <summary>Tool result text for protocols without an is_error flag.</summary>
    public static string ToolResultText(ToolResult result, bool includeImageNote)
    {
        var text = result.Text ?? "";
        if (result.IsError && text.Length > 0 && !text.StartsWith("STOPPED", StringComparison.Ordinal) &&
            !text.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            text = "Error: " + text;
        if (includeImageNote && result.Images.Count > 0)
            text = text.Length == 0 ? ImageOmittedNote : text + "\n" + ImageOmittedNote;
        return text.Length == 0 ? "(no output)" : text;
    }

    public static string UserText(UserTurn turn, bool vision)
    {
        var text = turn.Text ?? "";
        if (!vision && turn.Images.Count > 0)
        {
            var note = turn.Images.Count == 1 ? ImageOmittedNote : $"[{turn.Images.Count} images omitted: this model cannot see images]";
            text = text.Length == 0 ? note : text + "\n" + note;
        }
        return text;
    }
}

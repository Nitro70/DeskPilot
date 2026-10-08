namespace DeskPilot.Core.Abstractions;

public enum StatusLevel { Info, Warning, Error }

public enum AgentState { Idle, Starting, Running, Stopping, Error }

public enum TurnOutcome { Completed, Cancelled, Failed, StepLimit }

public sealed record TurnStats(
    int InputTokens,
    int OutputTokens,
    int Steps,              // tool calls executed this turn
    double? CostUsd,        // when the backend reports one (Claude CLI reports an API-equivalent estimate)
    TimeSpan Duration,
    string? Model);

public sealed record TurnResult(TurnOutcome Outcome, string? FinalText, TurnStats Stats, string? Error);

/// <summary>Something the UI can show in the conversation log. Raised on arbitrary threads.</summary>
public abstract record AgentEvent
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
}

public sealed record UserMessageEvent(string Text) : AgentEvent;

/// <summary>
/// Assistant text. IsPartial = true means a streaming chunk to append to the previous partial
/// AssistantTextEvent; a non-partial event is a complete block.
/// </summary>
public sealed record AssistantTextEvent(string Text, bool IsPartial = false) : AgentEvent;

/// <summary>Model reasoning (only when the provider exposes readable thinking). May be partial.</summary>
public sealed record ThinkingEvent(string Text, bool IsPartial = false) : AgentEvent;

public sealed record ToolCallEvent(string CallId, string ToolName, string ArgumentsJson, string Summary) : AgentEvent;

public sealed record ToolResultEvent(string CallId, string ToolName, bool IsError, string Text, ToolImage? Image, TimeSpan Duration) : AgentEvent;

public sealed record StatusEvent(string Message, StatusLevel Level = StatusLevel.Info) : AgentEvent;

public sealed record TurnCompletedEvent(TurnResult Result) : AgentEvent;

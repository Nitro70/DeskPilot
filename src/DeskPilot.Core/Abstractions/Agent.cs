using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Abstractions;

/// <summary>How a CLI/ACP agent should start DeskPilot's MCP server (a stdio bridge into this process).</summary>
public sealed record McpEndpointInfo(string ServerName, string Command, IReadOnlyList<string> Args);

public sealed record UserTurn(string Text, IReadOnlyList<ToolImage> Images)
{
    public UserTurn(string text) : this(text, Array.Empty<ToolImage>()) { }
}

/// <summary>Everything a backend needs. Built by AgentSession for each backend start.</summary>
public sealed class AgentBackendContext
{
    public required AppSettings Settings { get; init; }
    public required ProviderProfile Profile { get; init; }
    /// <summary>The fully rendered system prompt.</summary>
    public required string SystemPrompt { get; init; }
    /// <summary>All tools (computer + vault), already wrapped so calls/results are reported as events. HTTP backends call this directly.</summary>
    public required IToolHost Tools { get; init; }
    /// <summary>Report events to the UI. Safe to call from any thread.</summary>
    public required Action<AgentEvent> Emit { get; init; }
    public required AgentRunControl Control { get; init; }
    /// <summary>Empty directory to use as the working directory for CLI/ACP agents.</summary>
    public required string WorkingDirectory { get; init; }
    /// <summary>CLI/ACP backends: how to start DeskPilot's MCP server. Null for HTTP backends.</summary>
    public McpEndpointInfo? Mcp { get; init; }
    /// <summary>Resolved API key (decrypted or from the environment); "" when none.</summary>
    public string ApiKey { get; init; } = "";
}

/// <summary>
/// One model provider. Lifetime: StartAsync once, then any number of RunTurnAsync calls
/// (conversation context is kept between turns), ResetConversationAsync to forget, DisposeAsync at the end.
/// </summary>
public interface IAgentBackend : IAsyncDisposable
{
    /// <summary>True when the backend reaches tools through the MCP endpoint (CLI/ACP agents).</summary>
    bool UsesMcp { get; }

    Task StartAsync(AgentBackendContext context, CancellationToken ct);

    /// <summary>
    /// Runs one user turn to completion (the model may call many tools). Emits AssistantText/Thinking/Status
    /// events while running. Returns when the model ends its turn, the turn is interrupted, or it fails.
    /// Must not throw for model/provider errors: return TurnOutcome.Failed with a readable Error.
    /// Cancellation of ct or context.Control.Token ends the turn with TurnOutcome.Cancelled.
    /// </summary>
    Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct);

    /// <summary>Asks the model to stop the current turn as soon as possible. Safe to call when idle.</summary>
    Task InterruptAsync();

    /// <summary>Forgets the conversation so the next turn starts fresh.</summary>
    Task ResetConversationAsync(CancellationToken ct);
}

public enum ConfirmationChoice { Allow, Deny, AllowAllThisTurn }

/// <summary>Asks the human to approve an action (UI implements this with a dialog).</summary>
public interface IUserConfirmation
{
    Task<ConfirmationChoice> ConfirmAsync(ProposedAction action, CancellationToken ct);
}

/// <summary>The agent session the UI drives.</summary>
public interface IAgentSession : IAsyncDisposable
{
    event Action<AgentEvent>? EventRaised;
    event Action<AgentState>? StateChanged;
    AgentState State { get; }
    /// <summary>Display name of the active profile + model, e.g. "Claude (subscription) - haiku".</summary>
    string ActiveDescription { get; }

    /// <summary>Runs one turn. Starts (or restarts) the backend if needed. Returns when the turn ends.</summary>
    Task<TurnResult> SendAsync(string message, CancellationToken ct = default);
    /// <summary>Stops the current turn (interrupt + refuse further tool calls). Safe when idle.</summary>
    Task StopAsync(string reason = "Stopped by user");
    /// <summary>Clears the conversation context.</summary>
    Task NewConversationAsync();
    /// <summary>Call after settings changed; restarts the backend before the next turn if the provider changed.</summary>
    Task ReloadSettingsAsync();
}

public sealed record ModelInfo(string Id, string DisplayName, bool? SupportsVision, bool? SupportsTools, bool? SupportsThinking);

public interface IModelCatalog
{
    /// <summary>Lists models for a profile (live query when possible, else the preset's suggestions). Never throws; returns an empty list on failure with Error set.</summary>
    Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct);
}

public sealed record ModelListResult(IReadOnlyList<ModelInfo> Models, string? Error);

public sealed record CliToolStatus(string Name, string? Path, string? Version, bool? LoggedIn, string? Detail);

public sealed record LocalServerStatus(string Name, string BaseUrl, bool Running, IReadOnlyList<ModelInfo> Models);

public sealed record EnvironmentReport(
    CliToolStatus ClaudeCli,
    CliToolStatus GeminiCli,
    CliToolStatus CodexCli,
    LocalServerStatus Ollama,
    LocalServerStatus LmStudio,
    IReadOnlyList<string> ApiKeyEnvVarsFound,     // names only, never values
    IReadOnlyList<string> ObsidianVaults,         // candidate vault folders
    IReadOnlyList<MonitorInfo> Monitors,
    bool IsElevated);

public interface IEnvironmentDetector
{
    Task<EnvironmentReport> DetectAsync(CancellationToken ct);
}

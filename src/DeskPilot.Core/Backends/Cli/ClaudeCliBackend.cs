using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Cli;

// STUB: owned by the Claude CLI module agent.
public sealed class ClaudeCliBackend : IAgentBackend
{
    public bool UsesMcp => true;
    public Task StartAsync(AgentBackendContext context, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task InterruptAsync() => throw new NotImplementedException("STUB");
    public Task ResetConversationAsync(CancellationToken ct) => throw new NotImplementedException("STUB");
    public ValueTask DisposeAsync() => throw new NotImplementedException("STUB");
}

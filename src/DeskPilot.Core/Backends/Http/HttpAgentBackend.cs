using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Http;

// STUB: owned by the HTTP providers module agent.
/// <summary>Runs the agent loop itself against Anthropic API, OpenAI-compatible and Ollama endpoints.</summary>
public sealed class HttpAgentBackend : IAgentBackend
{
    public HttpAgentBackend(HttpClient? http = null) { }
    public bool UsesMcp => false;
    public Task StartAsync(AgentBackendContext context, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task InterruptAsync() => throw new NotImplementedException("STUB");
    public Task ResetConversationAsync(CancellationToken ct) => throw new NotImplementedException("STUB");
    public ValueTask DisposeAsync() => throw new NotImplementedException("STUB");
}

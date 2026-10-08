using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Agent;

// STUB: owned by the Session module agent.
public sealed class AgentSession : IAgentSession
{
    /// <param name="bridgeExePath">Path of DeskPilot.exe (Environment.ProcessPath) used to launch the MCP bridge.</param>
    /// <param name="backendFactory">Optional override for tests; default creates the backend for the profile's ProviderKind.</param>
    public AgentSession(SettingsStore settings, DesktopServices desktop, IUserConfirmation? confirmation, IInputActionObserver? observer,
        string bridgeExePath, Func<ProviderProfile, IAgentBackend>? backendFactory = null) { }

    public event Action<AgentEvent>? EventRaised { add { } remove { } }
    public event Action<AgentState>? StateChanged { add { } remove { } }
    public AgentState State => throw new NotImplementedException("STUB");
    public string ActiveDescription => throw new NotImplementedException("STUB");
    public Task<TurnResult> SendAsync(string message, CancellationToken ct = default) => throw new NotImplementedException("STUB");
    public Task StopAsync(string reason = "Stopped by user") => throw new NotImplementedException("STUB");
    public Task NewConversationAsync() => throw new NotImplementedException("STUB");
    public Task ReloadSettingsAsync() => throw new NotImplementedException("STUB");
    public ValueTask DisposeAsync() => throw new NotImplementedException("STUB");
}

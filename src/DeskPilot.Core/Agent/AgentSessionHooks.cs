using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Mcp;

namespace DeskPilot.Core.Agent;

/// <summary>
/// Test seams for AgentSession. Every member is optional; null means "use the real implementation".
/// </summary>
internal sealed class AgentSessionHooks
{
    /// <summary>Replaces the computer + vault tool hosts (the session still wraps it in ObservedToolHost).</summary>
    public IToolHost? Tools { get; init; }

    /// <summary>Replaces the screen description that normally comes from ComputerToolHost.CurrentMapper().</summary>
    public Func<string>? ScreenDescription { get; init; }

    /// <summary>Replaces VaultToolHost.IsAvailable.</summary>
    public Func<bool>? VaultAvailable { get; init; }

    /// <summary>Creates the MCP server for CLI/ACP backends (default: McpPipeServer).</summary>
    public Func<IToolHost, IMcpHost>? McpHostFactory { get; init; }

    /// <summary>Working directory handed to CLI/ACP agents (default: AppPaths.AgentWorkDirectory).</summary>
    public string? WorkingDirectory { get; init; }
}

/// <summary>The part of McpPipeServer the session uses, so tests can substitute it.</summary>
internal interface IMcpHost : IAsyncDisposable
{
    void Start();
    McpEndpointInfo GetEndpoint(string bridgeExePath);
}

internal sealed class McpPipeServerHost : IMcpHost
{
    private readonly McpPipeServer _server;

    public McpPipeServerHost(IToolHost tools) => _server = new McpPipeServer(tools);

    public void Start() => _server.Start();

    public McpEndpointInfo GetEndpoint(string bridgeExePath) => _server.GetEndpoint(bridgeExePath);

    public ValueTask DisposeAsync() => _server.DisposeAsync();
}

using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Mcp;

// STUB: owned by the MCP module agent.
/// <summary>
/// Serves IToolHost as an MCP server over a per-user named pipe. CLI agents start
/// "DeskPilot.exe --mcp-bridge PIPE TOKEN" as a stdio MCP server; the bridge relays stdio to this pipe.
/// </summary>
public sealed class McpPipeServer : IAsyncDisposable
{
    public McpPipeServer(IToolHost tools, string serverName = "deskpilot") { }
    public string ServerName => throw new NotImplementedException("STUB");
    public string PipeName => throw new NotImplementedException("STUB");
    public string Token => throw new NotImplementedException("STUB");
    /// <summary>Starts accepting bridge connections in the background. Idempotent.</summary>
    public void Start() => throw new NotImplementedException("STUB");
    /// <summary>Command + args a CLI agent should run to reach this server.</summary>
    public McpEndpointInfo GetEndpoint(string bridgeExePath) => throw new NotImplementedException("STUB");
    public ValueTask DisposeAsync() => throw new NotImplementedException("STUB");
}

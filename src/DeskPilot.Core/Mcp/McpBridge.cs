namespace DeskPilot.Core.Mcp;

// STUB: owned by the MCP module agent.
/// <summary>The "--mcp-bridge PIPE TOKEN" process mode: relays stdin/stdout to the app's named pipe.</summary>
public static class McpBridge
{
    public const string Switch = "--mcp-bridge";
    /// <summary>args = the full command line args (Switch, pipe name, token). Returns the process exit code.</summary>
    public static int Run(string[] args) => throw new NotImplementedException("STUB");
}

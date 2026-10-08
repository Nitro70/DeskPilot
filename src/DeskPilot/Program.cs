using DeskPilot.Core.Mcp;

namespace DeskPilot;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started by a CLI agent (Claude Code, Gemini CLI...) as its stdio MCP server:
        // relay stdio to the running DeskPilot window's named pipe. No UI in this mode.
        if (args.Length > 0 && string.Equals(args[0], McpBridge.Switch, StringComparison.OrdinalIgnoreCase))
            return McpBridge.Run(args);

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}

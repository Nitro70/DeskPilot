using Avalonia;
using DeskPilot.Core.Mcp;

namespace DeskPilot.Linux;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Started by a CLI agent (Claude Code, Gemini CLI...) as its stdio MCP server:
        // relay stdio to the running DeskPilot window's socket. No UI in this mode.
        if (args.Length > 0 && string.Equals(args[0], McpBridge.Switch, StringComparison.Ordinal))
            return McpBridge.Run(args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Also used by the visual designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

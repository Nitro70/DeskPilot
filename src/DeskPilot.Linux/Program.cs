using Avalonia;
using DeskPilot.Core.Mcp;
using DeskPilot.Core.Runtime;
using DeskPilot.Linux.Services;

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

        // One DeskPilot per user: a second launch brings the running one forward and leaves.
        using var instance = new SingleInstanceGuard(SingleInstanceGuard.DefaultSocketPath());
        if (!instance.TryAcquire())
        {
            instance.SignalFirstInstance();
            return 0;
        }
        App.InstanceGuard = instance;

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex) when (IsDisplayFailure(ex))
        {
            // No window can be opened at all, so the only place to say why is the terminal and the log.
            var message = DisplayFailureMessage(ex);
            Log.Error("DeskPilot could not open a window", ex);
            Console.Error.WriteLine(message);
            return 1;
        }
    }

    // Also used by the visual designer and the headless UI tests.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>True for the errors Avalonia's X11 backend throws when there is no display to connect to.</summary>
    internal static bool IsDisplayFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            if (e.Message.Contains("XOpenDisplay", StringComparison.OrdinalIgnoreCase) ||
                (e.Message.Contains("display", StringComparison.OrdinalIgnoreCase) && e.StackTrace?.Contains("X11", StringComparison.Ordinal) == true))
                return true;
        }
        return OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
    }

    internal static string DisplayFailureMessage(Exception ex)
    {
        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        bool x11 = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"));
        var hint = wayland && !x11
            ? "DeskPilot's window runs through Xwayland on Wayland desktops, and DISPLAY is not set. Enable Xwayland in your compositor (it is on by default in GNOME and KDE) and start DeskPilot again."
            : !x11
                ? "No graphical session was found (DISPLAY is not set). Start DeskPilot from your desktop session."
                : $"The X display {Environment.GetEnvironmentVariable("DISPLAY")} could not be opened.";
        return $"DeskPilot could not open a window: {ex.Message}\n{hint}";
    }
}

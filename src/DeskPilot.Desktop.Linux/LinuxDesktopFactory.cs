using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Desktop.Linux.Services;
using DeskPilot.Desktop.Linux.Wayland;
using DeskPilot.Desktop.Linux.X11;

namespace DeskPilot.Desktop.Linux;

public static class LinuxDesktopFactory
{
    internal const string NoSessionMessage =
        "No graphical session found: DeskPilot needs an X11 or Wayland desktop (DISPLAY or WAYLAND_DISPLAY is not set).";

    /// <summary>
    /// The desktop services for the current session: X11 implementations under X11, Wayland implementations
    /// under Wayland, plus the session-independent services (AT-SPI, launcher, clipboard, shell).
    /// </summary>
    public static DesktopServices CreateDefault() => CreateFor(LinuxSession.Detect());

    public static DesktopServices CreateFor(LinuxSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        IScreenCapture screen;
        IInputSimulator input;
        IWindowManager windows;
        switch (session.Kind)
        {
            case LinuxSessionKind.X11:
                screen = new X11ScreenCapture();
                input = new X11InputSimulator();
                windows = new X11WindowManager();
                break;
            case LinuxSessionKind.Wayland:
                // One context so capture, input and windows share the compositor facts and the output layout.
                var context = new WaylandContext(session);
                screen = new WaylandScreenCapture(context);
                input = new WaylandInputSimulator(context);
                windows = new WaylandWindowManager(context);
                break;
            default:
                throw new InvalidOperationException(NoSessionMessage);
        }
        return new DesktopServices(screen, input, windows, new AtSpiInspector(windows), new LinuxAppLauncher(), new LinuxClipboard(session), new LinuxShellRunner());
    }

    /// <summary>Human-readable notes about missing helper tools for this session (shown in the UI), empty when all is well.</summary>
    public static IReadOnlyList<string> DescribeMissingTools(LinuxSessionInfo session) => DescribeMissingTools(session, ToolProbe.ForSession(session));

    internal static IReadOnlyList<string> DescribeMissingTools(LinuxSessionInfo session, ToolProbe probe)
    {
        ArgumentNullException.ThrowIfNull(session);
        var notes = new List<string>();
        switch (session.Kind)
        {
            case LinuxSessionKind.X11:
                DescribeX11(probe, notes);
                break;
            case LinuxSessionKind.Wayland:
                DescribeWayland(session, probe, notes);
                break;
            default:
                notes.Add(NoSessionMessage);
                return notes;
        }

        if (!probe.HasBusName("org.a11y.Bus"))
            notes.Add(Install("Reading buttons and fields of windows (ui_elements) needs the accessibility bus from at-spi2-core, which is not running. Install it and log in again",
                "at-spi2-core", "at-spi2-core", "at-spi2-core"));
        return notes;
    }

    private static void DescribeX11(ToolProbe probe, List<string> notes)
    {
        if (!probe.CanLoadLibrary("libXtst.so.6") && !probe.CanLoadLibrary("libXtst.so"))
            notes.Add(Install("Mouse and keyboard control on X11 needs libXtst", "libxtst6", "libXtst", "libxtst"));
        if (!probe.CanLoadLibrary("libXrandr.so.2") && !probe.CanLoadLibrary("libXrandr.so"))
            notes.Add(Install("Detecting monitors on X11 needs libXrandr", "libxrandr2", "libXrandr", "libxrandr"));
        if (!probe.HasCommand("xclip") && !probe.HasCommand("xsel"))
            notes.Add(Install("Clipboard access on X11 needs xclip or xsel", "xclip", "xclip", "xclip"));
    }

    private static void DescribeWayland(LinuxSessionInfo session, ToolProbe probe, List<string> notes)
    {
        var desktop = WaylandContext.DetectDesktop(session, probe.Env);
        bool wlroots = desktop is WaylandDesktop.Sway or WaylandDesktop.Hyprland or WaylandDesktop.Wlroots;
        string name = desktop switch
        {
            WaylandDesktop.Sway => "sway",
            WaylandDesktop.Hyprland => "Hyprland",
            WaylandDesktop.Gnome => "GNOME",
            WaylandDesktop.Kde => "KDE Plasma",
            WaylandDesktop.Cosmic => "COSMIC",
            _ => session.Desktop ?? "this Wayland desktop",
        };

        if (wlroots)
        {
            if (!probe.HasCommand("grim"))
                notes.Add(Install($"Screenshots on {name} need grim", "grim", "grim", "grim"));
            if (!probe.HasCommand("wtype"))
                notes.Add(Install($"Typing and key presses on {name} need wtype", "wtype", "wtype", "wtype"));
            if (desktop == WaylandDesktop.Sway && !probe.HasCommand("swaymsg") && string.IsNullOrEmpty(probe.Env("SWAYSOCK")))
                notes.Add("Listing and focusing windows on sway needs swaymsg (part of the sway package) or the SWAYSOCK variable sway sets for its session.");
            if (desktop == WaylandDesktop.Hyprland && !probe.HasCommand("hyprctl"))
                notes.Add("Listing and focusing windows on Hyprland needs hyprctl, which comes with Hyprland. Make sure it is on PATH.");
            if (probe.HasWaylandGlobal(VirtualPointerDevice.ManagerInterface) == false && !HasYdotool(probe))
                notes.Add(Install($"{name} does not offer the virtual pointer protocol, so mouse control needs ydotool with ydotoold running", "ydotool", "ydotool", "ydotool"));
        }
        else
        {
            if (!probe.HasBusName("org.freedesktop.portal.Desktop"))
            {
                var backend = desktop switch
                {
                    WaylandDesktop.Kde => "xdg-desktop-portal-kde",
                    WaylandDesktop.Cosmic => "xdg-desktop-portal-cosmic",
                    _ => "xdg-desktop-portal-gnome",
                };
                notes.Add(Install($"Screenshots, mouse and keyboard on {name} go through xdg-desktop-portal, which is not running. Install it with your desktop's backend",
                    $"xdg-desktop-portal {backend}", $"xdg-desktop-portal {backend}", $"xdg-desktop-portal {backend}") +
                    " For mouse and keyboard, ydotool with ydotoold running also works.");
            }
            if (desktop == WaylandDesktop.Kde && !probe.HasCommand("kdotool"))
                notes.Add("Optional: kdotool lets DeskPilot list and focus windows on KDE Plasma (cargo install kdotool, or the kdotool package from the AUR on Arch). Without it DeskPilot works from screenshots only.");
            if (desktop == WaylandDesktop.Gnome && !probe.HasGnomeWindowCalls())
                notes.Add("Optional: the GNOME Shell extension \"Window Calls\" (extensions.gnome.org) lets DeskPilot list and focus windows on GNOME. Without it DeskPilot works from screenshots only.");
        }

        if (!probe.HasCommand("wl-copy") || !probe.HasCommand("wl-paste"))
            notes.Add(Install("Clipboard access on Wayland needs wl-clipboard", "wl-clipboard", "wl-clipboard", "wl-clipboard"));
    }

    private static bool HasYdotool(ToolProbe probe) => probe.HasCommand("ydotool") || probe.HasCommand("dotool");

    internal static string Install(string what, string debian, string fedora, string arch) =>
        $"{what}: sudo apt install {debian} (Debian/Ubuntu), sudo dnf install {fedora} (Fedora), sudo pacman -S {arch} (Arch).";
}

/// <summary>What DescribeMissingTools checks, injectable so tests need no real system.</summary>
internal sealed class ToolProbe
{
    /// <summary>Whether a command is installed (like "which").</summary>
    public required Func<string, bool> HasCommand { get; init; }
    public required Func<string, bool> CanLoadLibrary { get; init; }
    /// <summary>Whether a session-bus name is owned or can be activated.</summary>
    public required Func<string, bool> HasBusName { get; init; }
    /// <summary>Whether the compositor advertises a Wayland interface; null when it cannot be checked.</summary>
    public Func<string, bool?> HasWaylandGlobal { get; init; } = _ => null;
    public Func<bool> HasGnomeWindowCalls { get; init; } = () => false;
    public Func<string, string?> Env { get; init; } = Environment.GetEnvironmentVariable;

    public static ToolProbe ForSession(LinuxSessionInfo session)
    {
        WaylandContext? context = session.Kind == LinuxSessionKind.Wayland ? new WaylandContext(session) : null;
        return new ToolProbe
        {
            HasCommand = c => ExecutableLocator.Find(c) != null,
            CanLoadLibrary = TryLoad,
            HasBusName = BusName,
            HasWaylandGlobal = iface =>
            {
                if (context == null) return null;
                var globals = context.Globals;
                return globals.Count == 0 ? null : globals.Contains(iface);
            },
            HasGnomeWindowCalls = () => GnomeWindowCalls(context),
        };
    }

    private static bool TryLoad(string name)
    {
        if (!NativeLibrary.TryLoad(name, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static bool BusName(string name)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            return InputRoutes.Sync(() => PortalBus.NameAvailableAsync(name, cts.Token));
        }
        catch (Exception)
        {
            // No session bus, or it did not answer: report the name as missing.
            return false;
        }
    }

    private static bool GnomeWindowCalls(WaylandContext? context)
    {
        if (context == null) return false;
        try
        {
            var xml = InputRoutes.Sync(() => context.Portal.CallServiceAsync(WaylandWindowManager.GnomeService, WaylandWindowManager.GnomePath,
                "org.freedesktop.DBus.Introspectable", "Introspect", null, true, CancellationToken.None));
            return xml != null && xml.Contains(WaylandWindowManager.GnomeInterface, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}

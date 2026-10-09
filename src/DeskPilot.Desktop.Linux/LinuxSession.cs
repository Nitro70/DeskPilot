namespace DeskPilot.Desktop.Linux;

public enum LinuxSessionKind { X11, Wayland, None }

/// <summary>What kind of graphical session DeskPilot is running in.</summary>
public sealed record LinuxSessionInfo(
    LinuxSessionKind Kind,
    string? Display,            // $DISPLAY (also set under Wayland when Xwayland runs)
    string? WaylandDisplay,     // $WAYLAND_DISPLAY
    string? Desktop,            // $XDG_CURRENT_DESKTOP, e.g. "GNOME", "KDE", "sway", "Hyprland", "COSMIC"
    string? RuntimeDir)         // $XDG_RUNTIME_DIR
{
    public bool IsGnome => Has("GNOME") || Has("Unity") || Has("ubuntu");
    public bool IsKde => Has("KDE") || Has("plasma");
    public bool IsWlroots => Has("sway") || Has("Hyprland") || Has("river") || Has("wayfire") || Has("labwc") || Has("niri");
    public bool IsCosmic => Has("COSMIC");

    private bool Has(string name) =>
        Desktop != null && Desktop.Split(':').Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public static class LinuxSession
{
    /// <summary>
    /// Detects the session from the environment. XDG_SESSION_TYPE wins; otherwise WAYLAND_DISPLAY means Wayland
    /// (even when DISPLAY is also set for Xwayland), and DISPLAY alone means X11.
    /// The variable DESKPILOT_SESSION=x11|wayland overrides detection (e.g. to force the X11 path under Xwayland).
    /// </summary>
    public static LinuxSessionInfo Detect() => Detect(Environment.GetEnvironmentVariable);

    internal static LinuxSessionInfo Detect(Func<string, string?> env)
    {
        string? Get(string n) => string.IsNullOrWhiteSpace(env(n)) ? null : env(n)!.Trim();
        var display = Get("DISPLAY");
        var wayland = Get("WAYLAND_DISPLAY");
        var forced = Get("DESKPILOT_SESSION")?.ToLowerInvariant();
        var type = Get("XDG_SESSION_TYPE")?.ToLowerInvariant();

        LinuxSessionKind kind = forced switch
        {
            "x11" => LinuxSessionKind.X11,
            "wayland" => LinuxSessionKind.Wayland,
            _ => type switch
            {
                "wayland" when wayland != null || display == null => LinuxSessionKind.Wayland,
                "x11" when display != null => LinuxSessionKind.X11,
                _ => wayland != null ? LinuxSessionKind.Wayland : display != null ? LinuxSessionKind.X11 : LinuxSessionKind.None,
            },
        };
        return new LinuxSessionInfo(kind, display, wayland, Get("XDG_CURRENT_DESKTOP"), Get("XDG_RUNTIME_DIR"));
    }
}

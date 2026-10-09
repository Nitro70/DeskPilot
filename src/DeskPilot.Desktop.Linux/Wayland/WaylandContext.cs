using System.Diagnostics;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>The compositor families DeskPilot has a route for.</summary>
internal enum WaylandDesktop { Sway, Hyprland, Wlroots, Gnome, Kde, Cosmic, Unknown }

/// <summary>Where the pointer was last put by DeskPilot. Wayland has no general way to read the cursor position.</summary>
internal static class WaylandCursorTracker
{
    private static readonly object Gate = new();
    private static ScreenPoint? _last;

    public static ScreenPoint? Last
    {
        get { lock (Gate) return _last; }
    }

    public static void Set(ScreenPoint p)
    {
        lock (Gate) _last = p;
    }

    internal static void Reset()
    {
        lock (Gate) _last = null;
    }
}

/// <summary>
/// What the capture, input and window classes share for one Wayland session: which compositor runs, the helper
/// tools, the protocols the compositor offers, the output layout (cached briefly) and the portal connection.
/// Everything is discovered lazily so constructing the desktop services never touches the session.
/// </summary>
internal sealed class WaylandContext
{
    private const int LayoutCacheMs = 2000;

    private readonly object _gate = new();
    private readonly Stopwatch _layoutAge = new();
    private WaylandLayout? _layout;
    private IReadOnlySet<string>? _globals;
    private PortalBus? _portal;

    public LinuxSessionInfo Session { get; }
    public ICommandRunner Runner { get; }
    public Func<string, string?> Env { get; }
    public WaylandDesktop Desktop { get; }
    public SwayIpc? Sway { get; }
    public string? SocketPath { get; }
    /// <summary>Why the protocol list could not be read, for error messages.</summary>
    public string? GlobalsError { get; private set; }
    /// <summary>A single-output layout sized from a full capture, for desktops with no output listing.</summary>
    public WaylandLayout? CaptureLayout { get; set; }

    public WaylandContext(LinuxSessionInfo session) : this(session, ProcessCommandRunner.Instance, Environment.GetEnvironmentVariable) { }

    internal WaylandContext(LinuxSessionInfo session, ICommandRunner runner, Func<string, string?> env)
    {
        Session = session;
        Runner = runner;
        Env = env;
        Desktop = DetectDesktop(session, env);
        SocketPath = WaylandConnection.ResolveSocketPath(session.WaylandDisplay ?? env("WAYLAND_DISPLAY"), session.RuntimeDir ?? env("XDG_RUNTIME_DIR"));
        if (Desktop == WaylandDesktop.Sway) Sway = new SwayIpc(env("SWAYSOCK"), runner);
    }

    public static WaylandDesktop DetectDesktop(LinuxSessionInfo session, Func<string, string?> env)
    {
        bool Has(string name) => session.Desktop != null &&
            session.Desktop.Split(':').Any(d => d.Equals(name, StringComparison.OrdinalIgnoreCase));

        if (Has("Hyprland")) return WaylandDesktop.Hyprland;
        if (Has("sway")) return WaylandDesktop.Sway;
        if (session.IsWlroots) return WaylandDesktop.Wlroots;
        if (session.IsKde) return WaylandDesktop.Kde;
        if (session.IsGnome) return WaylandDesktop.Gnome;
        if (session.IsCosmic) return WaylandDesktop.Cosmic;
        if (!string.IsNullOrEmpty(env("HYPRLAND_INSTANCE_SIGNATURE"))) return WaylandDesktop.Hyprland;
        if (!string.IsNullOrEmpty(env("SWAYSOCK"))) return WaylandDesktop.Sway;
        if (!string.IsNullOrEmpty(env("KDE_FULL_SESSION"))) return WaylandDesktop.Kde;
        if (!string.IsNullOrEmpty(env("GNOME_SETUP_DISPLAY")) || !string.IsNullOrEmpty(env("GNOME_SHELL_SESSION_MODE"))) return WaylandDesktop.Gnome;
        return WaylandDesktop.Unknown;
    }

    /// <summary>True for compositors built on wlroots (grim, wtype and the virtual pointer protocol work there).</summary>
    public bool IsWlrootsFamily => Desktop is WaylandDesktop.Sway or WaylandDesktop.Hyprland or WaylandDesktop.Wlroots;

    public string DesktopName => Desktop switch
    {
        WaylandDesktop.Sway => "sway",
        WaylandDesktop.Hyprland => "Hyprland",
        WaylandDesktop.Wlroots => Session.Desktop ?? "a wlroots compositor",
        WaylandDesktop.Gnome => "GNOME",
        WaylandDesktop.Kde => "KDE Plasma",
        WaylandDesktop.Cosmic => "COSMIC",
        _ => Session.Desktop ?? "this Wayland desktop",
    };

    /// <summary>The Wayland interfaces the compositor advertises (read once).</summary>
    public IReadOnlySet<string> Globals
    {
        get
        {
            lock (_gate)
            {
                if (_globals != null) return _globals;
                try
                {
                    if (SocketPath == null) throw new WaylandProtocolException("XDG_RUNTIME_DIR is not set, so the Wayland socket cannot be found.");
                    using var c = WaylandConnection.Connect(SocketPath);
                    _globals = c.Globals.Select(g => g.Interface).ToHashSet(StringComparer.Ordinal);
                }
                catch (Exception ex) when (ex is WaylandProtocolException or TimeoutException or IOException)
                {
                    GlobalsError = ex.Message;
                    _globals = new HashSet<string>();
                }
                return _globals;
            }
        }
    }

    public bool HasGlobal(string iface) => Globals.Contains(iface);

    /// <summary>The output layout, or null when nothing describes it (then callers use <see cref="CaptureLayout"/>).</summary>
    public WaylandLayout? TryGetLayout(bool refresh = false)
    {
        lock (_gate)
        {
            if (!refresh && _layout != null && _layoutAge.ElapsedMilliseconds < LayoutCacheMs) return _layout;
            var fresh = QueryLayout();
            if (fresh != null)
            {
                _layout = fresh;
                _layoutAge.Restart();
            }
            return fresh ?? _layout ?? CaptureLayout;
        }
    }

    private WaylandLayout? QueryLayout()
    {
        IReadOnlyList<WaylandOutput> outputs;
        switch (Desktop)
        {
            case WaylandDesktop.Sway when Sway != null:
                var json = Sway.Request(SwayIpc.GetOutputs, "", out _);
                outputs = json == null ? Array.Empty<WaylandOutput>() : WaylandOutputParsers.ParseSway(json);
                if (outputs.Count > 0) return new WaylandLayout(outputs, "sway");
                break;
            case WaylandDesktop.Hyprland:
                var h = Runner.Run("hyprctl", new[] { "monitors", "-j" }, 3000);
                outputs = h.Ok ? WaylandOutputParsers.ParseHyprland(h.Text) : Array.Empty<WaylandOutput>();
                if (outputs.Count > 0) return new WaylandLayout(outputs, "hyprctl");
                break;
            case WaylandDesktop.Kde:
                var k = Runner.Run("kscreen-doctor", new[] { "-j" }, 5000);
                outputs = k.Ok ? WaylandOutputParsers.ParseKscreen(k.Text) : Array.Empty<WaylandOutput>();
                if (outputs.Count > 0) return new WaylandLayout(outputs, "kscreen-doctor");
                break;
        }

        if (SocketPath != null)
        {
            try
            {
                outputs = WaylandOutputParsers.FromProtocol(WaylandOutputQuery.Query(SocketPath));
                if (outputs.Count > 0) return new WaylandLayout(outputs, "xdg-output");
            }
            catch (Exception ex) when (ex is WaylandProtocolException or TimeoutException or IOException) { }
        }

        var w = Runner.Run("wlr-randr", Array.Empty<string>(), 3000);
        outputs = w.Ok ? WaylandOutputParsers.ParseWlrRandr(w.Text) : Array.Empty<WaylandOutput>();
        return outputs.Count > 0 ? new WaylandLayout(outputs, "wlr-randr") : null;
    }

    /// <summary>The compositor's own cursor position (Hyprland only), in physical coordinates.</summary>
    public ScreenPoint? QueryCursor()
    {
        if (Desktop != WaylandDesktop.Hyprland) return null;
        var r = Runner.Run("hyprctl", new[] { "cursorpos" }, 2000);
        if (!r.Ok || !TryParseCursorPos(r.Text, out var lx, out var ly)) return null;
        var layout = TryGetLayout();
        return layout?.ToPhysical(lx, ly) ?? new ScreenPoint((int)Math.Round(lx), (int)Math.Round(ly));
    }

    /// <summary>hyprctl cursorpos prints "x, y".</summary>
    internal static bool TryParseCursorPos(string text, out double x, out double y)
    {
        x = y = 0;
        var parts = text.Trim().Split(',');
        return parts.Length == 2 &&
               double.TryParse(parts[0].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x) &&
               double.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y);
    }

    /// <summary>The session-bus connection used for the xdg-desktop-portal routes.</summary>
    public PortalBus Portal
    {
        get
        {
            lock (_gate) return _portal ??= new PortalBus();
        }
        internal set
        {
            lock (_gate) _portal = value;
        }
    }
}

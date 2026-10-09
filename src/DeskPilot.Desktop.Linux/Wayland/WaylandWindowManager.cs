using System.Diagnostics;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.Services;
using Tmds.DBus.Protocol;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>
/// Top-level windows on Wayland, which only the compositor knows: sway's IPC tree, hyprctl, KWin through kdotool, or
/// GNOME through the "Window Calls" extension. Elsewhere the list is empty and the foreground window unknown;
/// DeskPilot still works from screenshots then.
/// </summary>
public sealed class WaylandWindowManager : IWindowManager
{
    private const int CacheMs = 250;
    private const int MaxDetailQueries = 40;
    internal const string GnomeService = "org.gnome.Shell";
    internal const string GnomePath = "/org/gnome/Shell/Extensions/Windows";
    internal const string GnomeInterface = "org.gnome.Shell.Extensions.Windows";

    private readonly WaylandContext _ctx;
    private readonly object _gate = new();
    private readonly Stopwatch _age = new();
    private readonly Dictionary<string, nint> _kdeHandles = new(StringComparer.Ordinal);
    private readonly Dictionary<nint, string> _kdeIds = new();
    private List<WaylandWindow>? _cached;
    private bool _gnomeUnavailable;

    public WaylandWindowManager(LinuxSessionInfo session) : this(new WaylandContext(session)) { }

    internal WaylandWindowManager(WaylandContext context) => _ctx = context;

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        var windows = Query();
        var layout = _ctx.TryGetLayout();
        return windows.Where(w => w.Title.Length > 0).Select(w => ToInfo(w, layout)).ToList();
    }

    public WindowInfo? GetForegroundWindow()
    {
        var w = Query().FirstOrDefault(x => x.Focused);
        return w == null ? null : ToInfo(w, _ctx.TryGetLayout());
    }

    public WindowInfo? GetWindowAt(int x, int y)
    {
        var layout = _ctx.TryGetLayout();
        var (lx, ly) = layout?.ToLogical(x, y) ?? (x, y);
        var w = WaylandWindowParsers.At(Query(), lx, ly);
        return w == null ? null : ToInfo(w, layout);
    }

    public bool FocusWindow(nint handle)
    {
        bool ok;
        switch (_ctx.Desktop)
        {
            case WaylandDesktop.Sway when _ctx.Sway != null:
                ok = _ctx.Sway.Command($"[con_id={(long)handle}] focus", out _);
                break;
            case WaylandDesktop.Hyprland:
                var r = _ctx.Runner.Run("hyprctl", new[] { "dispatch", "focuswindow", "address:" + WaylandWindowParsers.FormatAddress(handle) }, 3000);
                ok = r.Ok && !r.Text.Contains("error", StringComparison.OrdinalIgnoreCase) && !r.Text.Contains("no such window", StringComparison.OrdinalIgnoreCase);
                break;
            case WaylandDesktop.Kde:
                string? id;
                lock (_gate) _kdeIds.TryGetValue(handle, out id);
                ok = id != null && _ctx.Runner.Run("kdotool", new[] { "windowactivate", id }, 5000).Ok;
                break;
            case WaylandDesktop.Gnome:
                ok = TryGnome(() => _ctx.Portal.CallServiceAsync(GnomeService, GnomePath, GnomeInterface, "Activate", (uint)handle, false, CancellationToken.None), out _);
                break;
            default:
                ok = false;
                break;
        }
        Invalidate();
        if (!ok) return false;

        // Focus changes are applied asynchronously by some compositors; wait briefly until the list agrees.
        for (int i = 0; i < 10; i++)
        {
            if (Query().FirstOrDefault(w => w.Focused)?.Handle == handle) return true;
            Thread.Sleep(30);
            Invalidate();
        }
        return true;
    }

    public bool IsCurrentProcessElevated => LinuxProcessInfo.IsCurrentProcessElevated;

    public bool IsUacPromptActive() => LinuxProcessInfo.IsPrivilegePromptActive();

    // ------------------------------------------------------------------------------------------- queries

    private void Invalidate()
    {
        lock (_gate) _cached = null;
    }

    private List<WaylandWindow> Query()
    {
        lock (_gate)
        {
            if (_cached != null && _age.ElapsedMilliseconds < CacheMs) return _cached;
        }
        var fresh = _ctx.Desktop switch
        {
            WaylandDesktop.Sway => QuerySway(),
            WaylandDesktop.Hyprland => QueryHyprland(),
            WaylandDesktop.Kde => QueryKde(),
            WaylandDesktop.Gnome => QueryGnome(),
            _ => new List<WaylandWindow>(),
        };
        lock (_gate)
        {
            _cached = fresh;
            _age.Restart();
        }
        return fresh;
    }

    private List<WaylandWindow> QuerySway()
    {
        var json = _ctx.Sway?.Request(SwayIpc.GetTree, "", out _);
        return json == null ? new List<WaylandWindow>() : WaylandWindowParsers.ParseSwayTree(json);
    }

    private List<WaylandWindow> QueryHyprland()
    {
        var clients = _ctx.Runner.Run("hyprctl", new[] { "clients", "-j" }, 3000);
        if (!clients.Ok) return new List<WaylandWindow>();
        var monitors = _ctx.Runner.Run("hyprctl", new[] { "monitors", "-j" }, 3000);
        var active = _ctx.Runner.Run("hyprctl", new[] { "activewindow", "-j" }, 3000);
        return WaylandWindowParsers.ParseHyprlandClients(clients.Text, monitors.Ok ? monitors.Text : null, active.Ok ? active.Text : null);
    }

    private nint KdeHandle(string id)
    {
        lock (_gate)
        {
            if (_kdeHandles.TryGetValue(id, out var h)) return h;
            h = _kdeHandles.Count + 1;
            _kdeHandles[id] = h;
            _kdeIds[h] = id;
            return h;
        }
    }

    private List<WaylandWindow> QueryKde()
    {
        if (_ctx.Runner.Find("kdotool") == null) return new List<WaylandWindow>();
        var script = _ctx.Runner.Run("kdotool", new[] { "kwinscript", "--inline", WaylandWindowParsers.KwinListScript }, 5000);
        if (script.Ok)
        {
            var parsed = WaylandWindowParsers.ParseKwinJson(script.Text, KdeHandle);
            if (parsed.Count > 0) return parsed;
        }

        // Older kdotool without kwinscript: search, then ask for each window.
        var search = _ctx.Runner.Run("kdotool", new[] { "search", "--name", "." }, 5000);
        if (!search.Ok) return new List<WaylandWindow>();
        var activeId = _ctx.Runner.Run("kdotool", new[] { "getactivewindow" }, 5000).Text.Trim();
        var list = new List<WaylandWindow>();
        int rank = 0;
        foreach (var id in search.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(MaxDetailQueries))
        {
            var name = _ctx.Runner.Run("kdotool", new[] { "getwindowname", id }, 3000);
            var pid = _ctx.Runner.Run("kdotool", new[] { "getwindowpid", id }, 3000);
            var geo = WaylandWindowParsers.ParseKdotoolGeometry(_ctx.Runner.Run("kdotool", new[] { "getwindowgeometry", id }, 3000).Text);
            if (geo is not { } g) continue;
            int.TryParse(pid.Text.Trim(), out var p);
            list.Add(new WaylandWindow(KdeHandle(id), name.Text.Trim(), "", p, g.X, g.Y, g.W, g.H, id == activeId, true, Rank: rank++));
        }
        return WaylandWindowParsers.Order(list);
    }

    private List<WaylandWindow> QueryGnome()
    {
        if (_gnomeUnavailable) return new List<WaylandWindow>();
        if (!TryGnome(() => _ctx.Portal.CallServiceAsync(GnomeService, GnomePath, GnomeInterface, "List", null, true, CancellationToken.None), out var json) || json == null)
            return new List<WaylandWindow>();

        var entries = WaylandWindowParsers.ParseGnomeList(json);
        var list = new List<WaylandWindow>();
        // List is in stacking order, bottom to top: walk it from the top.
        int rank = 0;
        foreach (var e in Enumerable.Reverse(entries))
        {
            if (e.WindowType is 1 or 2) continue; // desktop, dock
            if (list.Count >= MaxDetailQueries) break;
            if (!TryGnome(() => _ctx.Portal.CallServiceAsync(GnomeService, GnomePath, GnomeInterface, "Details", e.Id, true, CancellationToken.None), out var details) ||
                details == null || WaylandWindowParsers.ParseGnomeDetails(details) is not { } d)
            {
                if (!TryGnome(() => _ctx.Portal.CallServiceAsync(GnomeService, GnomePath, GnomeInterface, "GetFrameRect", e.Id, true, CancellationToken.None), out var frame) ||
                    frame == null || WaylandWindowParsers.ParseGnomeDetails(frame) is not { } f)
                    continue;
                d = f;
            }
            list.Add(new WaylandWindow((nint)e.Id, d.Title ?? e.Title, e.ClassName, e.Pid, d.X, d.Y, d.W, d.H, e.Focus,
                e.InCurrentWorkspace && !d.Minimized, d.Minimized, Rank: rank++));
        }
        return list.OrderByDescending(w => w.Visible).ThenBy(w => w.Rank).ToList();
    }

    /// <summary>Runs a Window Calls request; remembers when the extension is not installed so it is not asked again.</summary>
    private bool TryGnome(Func<Task<string?>> call, out string? result)
    {
        result = null;
        if (_gnomeUnavailable) return false;
        try
        {
            result = InputRoutes.Sync(call);
            return true;
        }
        catch (DBusErrorReplyException ex)
        {
            if (ex.ErrorName is "org.freedesktop.DBus.Error.UnknownMethod" or "org.freedesktop.DBus.Error.UnknownObject" or
                "org.freedesktop.DBus.Error.UnknownInterface" or "org.freedesktop.DBus.Error.ServiceUnknown")
                _gnomeUnavailable = true;
            return false;
        }
        catch (TimeoutException)
        {
            // GNOME Shell busy: skip this query, ask again next time.
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or DBusExceptionBase)
        {
            _gnomeUnavailable = true;
            return false;
        }
    }

    private static WindowInfo ToInfo(WaylandWindow w, WaylandLayout? layout)
    {
        var bounds = layout?.ToPhysicalRect(w.X, w.Y, w.Width, w.Height)
                     ?? new ScreenRect((int)Math.Round(w.X), (int)Math.Round(w.Y), (int)Math.Round(w.Width), (int)Math.Round(w.Height));
        var process = LinuxProcessInfo.GetProcessName(w.Pid) ?? w.ClassName;
        return new WindowInfo(
            w.Handle,
            w.Title,
            w.ClassName,
            process,
            w.Pid,
            bounds,
            w.Visible,
            w.Minimized,
            w.Focused,
            LinuxProcessInfo.IsElevated(w.Pid),
            LinuxProcessInfo.IsPrivilegePromptProcess(process));
    }
}

using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.Services;
using DeskPilot.Desktop.Linux.X11.Interop;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>
/// Top-level windows through EWMH (the properties every common X11 window manager maintains on the root window):
/// stacking order, titles, owning process, frame bounds, minimized state, the active window, and activation.
/// Handles are X window ids of client windows.
/// </summary>
public sealed unsafe class X11WindowManager : IWindowManager, IDisposable
{
    internal const int FocusTimeoutMs = 1000;
    internal const int FocusFallbackTimeoutMs = 500;
    private const uint IconicState = 3;
    private const nuint AllDesktops = 0xFFFFFFFF;

    private readonly XConnection _conn = new();
    private readonly XAtomCache _atoms = new();

    public bool IsCurrentProcessElevated => LinuxProcessInfo.IsCurrentProcessElevated;

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        using var lease = _conn.Acquire();
        var active = ActiveWindow(lease);
        int? desktop = CurrentDesktop(lease);
        var result = new List<WindowInfo>();
        foreach (var w in ClientsFrontToBack(lease))
        {
            var facts = Read(lease, w, active);
            if (facts == null || !ShouldList(facts.Value, desktop)) continue;
            result.Add(facts.Value.Info);
        }
        return result;
    }

    public WindowInfo? GetForegroundWindow()
    {
        using var lease = _conn.Acquire();
        var active = ActiveWindow(lease);
        if (active == X.None) return null;
        return Read(lease, active, active)?.Info;
    }

    public WindowInfo? GetWindowAt(int x, int y)
    {
        using var lease = _conn.Acquire();
        var active = ActiveWindow(lease);
        int? desktop = CurrentDesktop(lease);
        foreach (var w in ClientsFrontToBack(lease))
        {
            var facts = Read(lease, w, active);
            if (facts is not { } f) continue;
            if (!f.Viewable || f.Info.IsMinimized || !OnDesktop(f.Desktop, desktop)) continue;
            if (f.Info.Bounds.Contains(x, y)) return f.Info;
        }
        return null;
    }

    public bool FocusWindow(nint handle)
    {
        if (handle == 0) return false;
        var window = (nuint)handle;
        using var lease = _conn.Acquire();
        XWindowAttributes attrs;
        if (Xlib.XGetWindowAttributes(lease.Display, window, &attrs) == 0 || lease.Sync() != null) return false;
        if (ActiveWindow(lease) == window) return true;

        // EWMH activation request; source indication 2 (pager) asks the window manager to skip focus-stealing
        // prevention, and it also restores a minimized window and switches to its desktop.
        var ev = default(XEvent);
        var cm = (XClientMessageEvent*)&ev;
        cm->type = X.ClientMessage;
        cm->send_event = X.True;
        cm->display = lease.Display;
        cm->window = window;
        cm->message_type = _atoms.Get(lease, "_NET_ACTIVE_WINDOW");
        cm->format = 32;
        cm->l0 = 2;
        cm->l1 = (nint)X.CurrentTime;
        cm->l2 = (nint)ActiveWindow(lease);
        Xlib.XSendEvent(lease.Display, lease.Root, X.False, X.SubstructureRedirectMask | X.SubstructureNotifyMask, &ev);
        Xlib.XFlush(lease.Display);
        if (WaitForActive(lease, window, FocusTimeoutMs)) return true;

        // No (cooperative) EWMH window manager: map, raise and focus directly.
        Xlib.XMapRaised(lease.Display, window);
        Xlib.XRaiseWindow(lease.Display, window);
        lease.Sync();
        Xlib.XSetInputFocus(lease.Display, window, X.RevertToParent, X.CurrentTime);
        lease.Sync();
        return WaitForActive(lease, window, FocusFallbackTimeoutMs);
    }

    public bool IsUacPromptActive()
    {
        if (LinuxProcessInfo.IsPrivilegePromptActive()) return true;
        try { return ListWindows().Any(w => w.IsUacPrompt); }
        catch (Exception) { return false; }
    }

    /// <summary>The client area (without the window manager's frame) in root coordinates, or null.</summary>
    internal ScreenRect? GetClientRect(nint handle)
    {
        using var lease = _conn.Acquire();
        XWindowAttributes attrs;
        int cx, cy;
        nuint child;
        if (Xlib.XGetWindowAttributes(lease.Display, (nuint)handle, &attrs) == 0) { lease.Sync(); return null; }
        Xlib.XTranslateCoordinates(lease.Display, (nuint)handle, lease.Root, 0, 0, &cx, &cy, &child);
        return lease.Sync() == null ? new ScreenRect(cx, cy, attrs.width, attrs.height) : null;
    }

    // ---------------------------------------------------------------- reading windows

    internal readonly record struct WindowFacts(WindowInfo Info, bool Viewable, nuint? Desktop, string? Type);

    /// <summary>The filter for windows a person would see in a task bar: titled, not a panel or the desktop, on this desktop.</summary>
    internal static bool ShouldList(WindowFacts f, int? currentDesktop)
    {
        if (string.IsNullOrWhiteSpace(f.Info.Title)) return false;
        if (f.Type is "_NET_WM_WINDOW_TYPE_DOCK" or "_NET_WM_WINDOW_TYPE_DESKTOP") return false;
        if (!f.Viewable && !f.Info.IsMinimized) return false;
        return OnDesktop(f.Desktop, currentDesktop);
    }

    internal static bool OnDesktop(nuint? windowDesktop, int? currentDesktop) =>
        windowDesktop is not { } d || currentDesktop is not { } c || d == AllDesktops || (long)d == c;

    /// <summary>Client area (root coordinates) grown by the frame the window manager draws, minus client-side shadows.</summary>
    internal static ScreenRect FrameBounds(ScreenRect client, nuint[]? frameExtents, nuint[]? gtkFrameExtents)
    {
        int x = client.X, y = client.Y, w = client.Width, h = client.Height;
        if (gtkFrameExtents is { Length: >= 4 })
        {
            // _GTK_FRAME_EXTENTS: invisible shadow around client-side decorated windows (left, right, top, bottom).
            int l = (int)gtkFrameExtents[0], r = (int)gtkFrameExtents[1], t = (int)gtkFrameExtents[2], b = (int)gtkFrameExtents[3];
            if (l + r < w && t + b < h)
            {
                x += l; y += t; w -= l + r; h -= t + b;
            }
        }
        if (frameExtents is { Length: >= 4 })
        {
            int l = (int)frameExtents[0], r = (int)frameExtents[1], t = (int)frameExtents[2], b = (int)frameExtents[3];
            x -= l; y -= t; w += l + r; h += t + b;
        }
        return new ScreenRect(x, y, Math.Max(0, w), Math.Max(0, h));
    }

    private WindowFacts? Read(in XConnection.Lease lease, nuint window, nuint active)
    {
        var d = lease.Display;
        lease.ResetErrors();
        XWindowAttributes attrs;
        if (Xlib.XGetWindowAttributes(d, window, &attrs) == 0) { lease.Sync(); return null; }
        int cx, cy;
        nuint child;
        Xlib.XTranslateCoordinates(d, window, lease.Root, 0, 0, &cx, &cy, &child);

        var utf8 = _atoms.Get(lease, "UTF8_STRING");
        var title = XProps.GetText(d, window, _atoms.Get(lease, "_NET_WM_NAME"), utf8, utf8)
                    ?? XProps.GetText(d, window, X.XA_WM_NAME, X.AnyPropertyType, utf8)
                    ?? "";
        var (resName, resClass) = ReadClass(d, window);
        int pid = (int)(XProps.GetLong(d, window, _atoms.Get(lease, "_NET_WM_PID"), X.XA_CARDINAL) ?? 0);
        var state = XProps.GetLongs(d, window, _atoms.Get(lease, "_NET_WM_STATE"), X.XA_ATOM, 64);
        var wmState = XProps.GetLong(d, window, _atoms.Get(lease, "WM_STATE"), X.AnyPropertyType);
        var frame = XProps.GetLongs(d, window, _atoms.Get(lease, "_NET_FRAME_EXTENTS"), X.XA_CARDINAL, 4);
        var gtkFrame = XProps.GetLongs(d, window, _atoms.Get(lease, "_GTK_FRAME_EXTENTS"), X.XA_CARDINAL, 4);
        var desktop = XProps.GetLong(d, window, _atoms.Get(lease, "_NET_WM_DESKTOP"), X.XA_CARDINAL);
        var types = XProps.GetLongs(d, window, _atoms.Get(lease, "_NET_WM_WINDOW_TYPE"), X.XA_ATOM, 16);
        // The window vanished while we read it: skip it rather than report half-read facts.
        if (lease.Sync() is { ErrorCode: X.BadWindow }) return null;

        var hidden = _atoms.Get(lease, "_NET_WM_STATE_HIDDEN");
        bool minimized = (state != null && state.Contains(hidden)) || wmState == IconicState;
        string? type = null;
        if (types is { Length: > 0 })
        {
            var dock = _atoms.Get(lease, "_NET_WM_WINDOW_TYPE_DOCK");
            var desk = _atoms.Get(lease, "_NET_WM_WINDOW_TYPE_DESKTOP");
            type = types.Contains(dock) ? "_NET_WM_WINDOW_TYPE_DOCK" : types.Contains(desk) ? "_NET_WM_WINDOW_TYPE_DESKTOP" : null;
        }

        var processName = LinuxProcessInfo.GetProcessName(pid) ?? (pid <= 0 ? resName : null) ?? "";
        bool prompt = LinuxProcessInfo.IsPrivilegePromptProcess(processName) ||
                      (resName != null && LinuxProcessInfo.IsPrivilegePromptProcess(resName)) ||
                      (resClass != null && LinuxProcessInfo.IsPrivilegePromptProcess(resClass));
        bool viewable = attrs.map_state == X.IsViewable;
        var bounds = FrameBounds(new ScreenRect(cx, cy, attrs.width, attrs.height), frame, gtkFrame);
        var info = new WindowInfo(
            (nint)window,
            title,
            resClass ?? "",
            processName,
            pid,
            bounds,
            viewable || minimized,
            minimized,
            window == active,
            LinuxProcessInfo.IsElevated(pid),
            prompt);
        return new WindowFacts(info, viewable, desktop, type);
    }

    /// <summary>WM_CLASS: two NUL-terminated strings, instance name then class name.</summary>
    private static (string? Name, string? Class) ReadClass(nint display, nuint window)
    {
        var bytes = XProps.GetBytes(display, window, X.XA_WM_CLASS, X.XA_STRING, out _, 4096);
        return bytes == null ? (null, null) : ParseWmClass(bytes);
    }

    internal static (string? Name, string? Class) ParseWmClass(byte[] bytes)
    {
        var parts = Encoding.Latin1.GetString(bytes).Split('\0');
        string? name = parts.Length > 0 && parts[0].Length > 0 ? parts[0] : null;
        string? cls = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : null;
        return (name, cls);
    }

    /// <summary>Client windows front to back: _NET_CLIENT_LIST_STACKING (bottom to top) reversed, else _NET_CLIENT_LIST, else the root's children.</summary>
    private List<nuint> ClientsFrontToBack(in XConnection.Lease lease)
    {
        var d = lease.Display;
        var list = XProps.GetLongs(d, lease.Root, _atoms.Get(lease, "_NET_CLIENT_LIST_STACKING"), X.XA_WINDOW);
        bool stacking = list is { Length: > 0 };
        if (!stacking) list = XProps.GetLongs(d, lease.Root, _atoms.Get(lease, "_NET_CLIENT_LIST"), X.XA_WINDOW);
        lease.Sync();
        if (list is { Length: > 0 })
        {
            var clients = list.ToList();
            if (stacking) clients.Reverse();
            else clients = StackOrder(lease, clients);
            return clients;
        }
        return RootChildrenFrontToBack(lease);
    }

    /// <summary>Orders an unordered client list by the root's stacking order (the frame each client sits in).</summary>
    private List<nuint> StackOrder(in XConnection.Lease lease, List<nuint> clients)
    {
        var topLevel = RootChildren(lease);
        var rank = new Dictionary<nuint, int>();
        for (int i = 0; i < topLevel.Count; i++) rank[topLevel[i]] = i;
        int Rank(nuint w, in XConnection.Lease l)
        {
            var frame = TopLevelAncestor(l, w);
            return frame != X.None && rank.TryGetValue(frame, out var r) ? r : -1;
        }
        var ranked = new List<(nuint W, int R)>();
        foreach (var c in clients) ranked.Add((c, Rank(c, lease)));
        lease.Sync();
        return ranked.OrderByDescending(p => p.R).Select(p => p.W).ToList();
    }

    /// <summary>Without an EWMH window manager: viewable top-level windows (or the client inside a frame) front to back.</summary>
    private List<nuint> RootChildrenFrontToBack(in XConnection.Lease lease)
    {
        var result = new List<nuint>();
        var wmState = _atoms.Get(lease, "WM_STATE");
        var children = RootChildren(lease);
        for (int i = children.Count - 1; i >= 0; i--)
        {
            var w = children[i];
            XWindowAttributes attrs;
            if (Xlib.XGetWindowAttributes(lease.Display, w, &attrs) == 0 || attrs.c_class == X.InputOnly) continue;
            if (attrs.override_redirect != 0) continue;
            var client = HasProperty(lease.Display, w, wmState) ? w : FindClient(lease.Display, w, wmState, 3);
            if (client != X.None) result.Add(client);
        }
        lease.Sync();
        return result;
    }

    private List<nuint> RootChildren(in XConnection.Lease lease) => Children(lease.Display, lease.Root);

    private static List<nuint> Children(nint display, nuint window)
    {
        nuint root, parent;
        nuint* children = null;
        uint n = 0;
        var list = new List<nuint>();
        if (Xlib.XQueryTree(display, window, &root, &parent, &children, &n) == 0) return list;
        try
        {
            for (int i = 0; i < n; i++) list.Add(children[i]);
        }
        finally
        {
            if (children != null) Xlib.XFree(children);
        }
        return list;
    }

    private static nuint FindClient(nint display, nuint window, nuint wmState, int depth)
    {
        if (depth <= 0) return X.None;
        foreach (var c in Children(display, window))
        {
            if (HasProperty(display, c, wmState)) return c;
            var deeper = FindClient(display, c, wmState, depth - 1);
            if (deeper != X.None) return deeper;
        }
        return X.None;
    }

    private static bool HasProperty(nint display, nuint window, nuint property)
    {
        nuint actualType, nitems, bytesAfter;
        int format;
        byte* data = null;
        int rc = Xlib.XGetWindowProperty(display, window, property, 0, 0, X.False, X.AnyPropertyType,
            &actualType, &format, &nitems, &bytesAfter, &data);
        if (data != null) Xlib.XFree(data);
        return rc == X.Success && actualType != X.None;
    }

    private static nuint TopLevelAncestor(in XConnection.Lease lease, nuint window)
    {
        var w = window;
        for (int i = 0; i < 64 && w != X.None; i++)
        {
            nuint root, parent;
            nuint* children = null;
            uint n;
            if (Xlib.XQueryTree(lease.Display, w, &root, &parent, &children, &n) == 0) return X.None;
            if (children != null) Xlib.XFree(children);
            if (parent == root || parent == X.None) return w;
            w = parent;
        }
        return X.None;
    }

    private nuint ActiveWindow(in XConnection.Lease lease)
    {
        var active = XProps.GetLong(lease.Display, lease.Root, _atoms.Get(lease, "_NET_ACTIVE_WINDOW"), X.XA_WINDOW);
        lease.Sync();
        if (active is { } a && a != X.None) return a;
        if (active != null) return X.None;

        // No EWMH active window: the client owning the input focus.
        nuint focus;
        int revert;
        Xlib.XGetInputFocus(lease.Display, &focus, &revert);
        if (focus is 0 or 1) return X.None; // None or PointerRoot
        var wmState = _atoms.Get(lease, "WM_STATE");
        var w = focus;
        for (int i = 0; i < 64 && w != X.None && w != lease.Root; i++)
        {
            if (HasProperty(lease.Display, w, wmState)) { lease.Sync(); return w; }
            nuint root, parent;
            nuint* children = null;
            uint n;
            if (Xlib.XQueryTree(lease.Display, w, &root, &parent, &children, &n) == 0) break;
            if (children != null) Xlib.XFree(children);
            w = parent;
        }
        lease.Sync();
        return X.None;
    }

    private int? CurrentDesktop(in XConnection.Lease lease)
    {
        var v = XProps.GetLong(lease.Display, lease.Root, _atoms.Get(lease, "_NET_CURRENT_DESKTOP"), X.XA_CARDINAL);
        lease.Sync();
        return v is { } d ? (int)d : null;
    }

    private bool WaitForActive(in XConnection.Lease lease, nuint window, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (ActiveWindow(lease) == window) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(20);
        }
    }

    public void Dispose() => _conn.Dispose();
}

using System.Globalization;
using System.Text.Json;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>A window as a compositor describes it, in logical layout coordinates.</summary>
internal sealed record WaylandWindow(
    nint Handle,
    string Title,
    string ClassName,
    int Pid,
    double X,
    double Y,
    double Width,
    double Height,
    bool Focused,
    bool Visible,
    bool Minimized = false,
    bool Floating = false,
    bool Fullscreen = false,
    int Rank = 0)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>Parsers for the window listings of sway, Hyprland, KWin (kdotool) and GNOME (Window Calls extension).</summary>
internal static class WaylandWindowParsers
{
    /// <summary>Front to back: visible first, then fullscreen, floating (drawn above tiles), then most recently focused.</summary>
    public static List<WaylandWindow> Order(IEnumerable<WaylandWindow> windows) =>
        windows.OrderByDescending(w => w.Visible)
            .ThenByDescending(w => w.Fullscreen)
            .ThenByDescending(w => w.Floating)
            .ThenByDescending(w => w.Focused)
            .ThenBy(w => w.Rank)
            .ToList();

    /// <summary>The window under a logical point: the front-most visible window containing it.</summary>
    public static WaylandWindow? At(IReadOnlyList<WaylandWindow> ordered, double x, double y) =>
        ordered.FirstOrDefault(w => w.Visible && !w.Minimized && w.Contains(x, y));

    // ------------------------------------------------------------------------------------------- sway

    /// <summary>swaymsg -t get_tree: views are the nodes with a pid; rect is absolute and includes decorations.</summary>
    public static List<WaylandWindow> ParseSwayTree(string json)
    {
        var list = new List<WaylandWindow>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            int rank = 0;
            Walk(doc.RootElement, false, false, list, ref rank);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            list.Clear();
        }
        return Order(list);
    }

    private static void Walk(JsonElement node, bool inScratchpad, bool floating, List<WaylandWindow> list, ref int rank)
    {
        if (node.ValueKind != JsonValueKind.Object) return;
        var type = WaylandOutputParsers.Str(node, "type");
        if (type == "workspace" && WaylandOutputParsers.Str(node, "name") == "__i3_scratch") inScratchpad = true;

        if (node.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number && pidEl.TryGetInt32(out var pid) && pid > 0 &&
            type is "con" or "floating_con")
        {
            var title = WaylandOutputParsers.Str(node, "name") ?? "";
            string cls = WaylandOutputParsers.Str(node, "app_id") ?? "";
            if (cls.Length == 0 && node.TryGetProperty("window_properties", out var wp)) cls = WaylandOutputParsers.Str(wp, "class") ?? "";
            node.TryGetProperty("rect", out var rect);
            bool visible = node.TryGetProperty("visible", out var v) && v.ValueKind == JsonValueKind.True;
            long id = node.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var i) ? i : 0;
            list.Add(new WaylandWindow((nint)id, title, cls, pid,
                WaylandOutputParsers.Num(rect, "x"), WaylandOutputParsers.Num(rect, "y"), WaylandOutputParsers.Num(rect, "width"), WaylandOutputParsers.Num(rect, "height"),
                WaylandOutputParsers.Bool(node, "focused"), visible && !inScratchpad, inScratchpad, floating || type == "floating_con",
                WaylandOutputParsers.NumOr(node, "fullscreen_mode", 0) > 0, rank++));
        }

        // Children in focus order (most recent first) so rank follows the focus history.
        var children = new List<(JsonElement Node, bool Floating)>();
        if (node.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
            foreach (var c in nodes.EnumerateArray()) children.Add((c, floating));
        if (node.TryGetProperty("floating_nodes", out var fl) && fl.ValueKind == JsonValueKind.Array)
            foreach (var c in fl.EnumerateArray()) children.Add((c, true));
        if (node.TryGetProperty("focus", out var focus) && focus.ValueKind == JsonValueKind.Array)
        {
            var order = focus.EnumerateArray().Select(f => f.TryGetInt64(out var x) ? x : -1).ToList();
            children = children.OrderBy(c =>
            {
                long cid = c.Node.TryGetProperty("id", out var e) && e.TryGetInt64(out var x) ? x : -2;
                int idx = order.IndexOf(cid);
                return idx < 0 ? int.MaxValue : idx;
            }).ToList();
        }
        foreach (var (child, fl2) in children) Walk(child, inScratchpad, fl2, list, ref rank);
    }

    // ------------------------------------------------------------------------------------------- Hyprland

    /// <summary>hyprctl clients -j, with monitors -j (active workspaces) and activewindow -j (focus).</summary>
    public static List<WaylandWindow> ParseHyprlandClients(string clientsJson, string? monitorsJson, string? activeJson)
    {
        var list = new List<WaylandWindow>();
        var activeWorkspaces = new HashSet<long>();
        string? activeAddress = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(monitorsJson))
            {
                using var m = JsonDocument.Parse(monitorsJson);
                if (m.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var mon in m.RootElement.EnumerateArray())
                        foreach (var key in new[] { "activeWorkspace", "specialWorkspace" })
                            if (mon.TryGetProperty(key, out var ws) && ws.TryGetProperty("id", out var wid) && wid.TryGetInt64(out var id) && id != 0)
                                activeWorkspaces.Add(id);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }
        try
        {
            if (!string.IsNullOrWhiteSpace(activeJson) && activeJson.TrimStart().StartsWith('{'))
            {
                using var a = JsonDocument.Parse(activeJson);
                activeAddress = WaylandOutputParsers.Str(a.RootElement, "address");
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { }

        try
        {
            using var doc = JsonDocument.Parse(clientsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var c in doc.RootElement.EnumerateArray())
            {
                var address = WaylandOutputParsers.Str(c, "address") ?? "";
                if (!TryParseAddress(address, out var handle)) continue;
                bool mapped = !c.TryGetProperty("mapped", out var mp) || mp.ValueKind != JsonValueKind.False;
                bool hidden = WaylandOutputParsers.Bool(c, "hidden");
                long ws = c.TryGetProperty("workspace", out var w) && w.TryGetProperty("id", out var wid) && wid.TryGetInt64(out var x) ? x : 0;
                double ax = 0, ay = 0, sw = 0, sh = 0;
                if (c.TryGetProperty("at", out var at) && at.ValueKind == JsonValueKind.Array && at.GetArrayLength() == 2) { ax = at[0].GetDouble(); ay = at[1].GetDouble(); }
                if (c.TryGetProperty("size", out var size) && size.ValueKind == JsonValueKind.Array && size.GetArrayLength() == 2) { sw = size[0].GetDouble(); sh = size[1].GetDouble(); }
                bool visible = mapped && !hidden && (activeWorkspaces.Count == 0 || activeWorkspaces.Contains(ws));
                int focusHistory = (int)WaylandOutputParsers.NumOr(c, "focusHistoryID", 1000);
                bool focused = activeAddress != null ? string.Equals(activeAddress, address, StringComparison.OrdinalIgnoreCase) : focusHistory == 0;
                list.Add(new WaylandWindow(handle, WaylandOutputParsers.Str(c, "title") ?? "", WaylandOutputParsers.Str(c, "class") ?? "",
                    (int)WaylandOutputParsers.Num(c, "pid"), ax, ay, sw, sh, focused, visible, hidden,
                    WaylandOutputParsers.Bool(c, "floating"), WaylandOutputParsers.NumOr(c, "fullscreen", 0) > 0, focusHistory));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            list.Clear();
        }
        return Order(list);
    }

    public static bool TryParseAddress(string address, out nint handle)
    {
        handle = 0;
        var hex = address.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? address[2..] : address;
        if (!long.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) || v == 0) return false;
        handle = (nint)v;
        return true;
    }

    public static string FormatAddress(nint handle) => "0x" + ((long)handle).ToString("x", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------------------------------- KDE (kdotool)

    /// <summary>The KWin script DeskPilot runs through "kdotool kwinscript": every window as JSON, bottom to top.</summary>
    public const string KwinListScript =
        "var l=workspace.windowList?workspace.windowList():workspace.clientList();var o=[];" +
        "for(var i=0;i<l.length;i++){var w=l[i];if(!(w.normalWindow||w.dialog))continue;var g=w.frameGeometry;" +
        "o.push({id:String(w.internalId),title:String(w.caption),cls:String(w.resourceClass),pid:w.pid,x:g.x,y:g.y,w:g.width,h:g.height," +
        "min:!!w.minimized,active:!!w.active,current:w.desktops?(w.desktops.length==0||w.desktops.indexOf(workspace.currentDesktop)>=0):true});}" +
        "output_result(JSON.stringify(o));";

    /// <summary>Parses the script's JSON (the list comes bottom to top, so the last one is in front).</summary>
    public static List<WaylandWindow> ParseKwinJson(string text, Func<string, nint> handleFor)
    {
        var list = new List<WaylandWindow>();
        int start = text.IndexOf('['), end = text.LastIndexOf(']');
        if (start < 0 || end <= start) return list;
        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var items = doc.RootElement.EnumerateArray().ToList();
            for (int i = 0; i < items.Count; i++)
            {
                var w = items[i];
                var id = WaylandOutputParsers.Str(w, "id");
                if (string.IsNullOrEmpty(id)) continue;
                bool min = WaylandOutputParsers.Bool(w, "min");
                bool current = !w.TryGetProperty("current", out var cur) || cur.ValueKind != JsonValueKind.False;
                list.Add(new WaylandWindow(handleFor(id), WaylandOutputParsers.Str(w, "title") ?? "", WaylandOutputParsers.Str(w, "cls") ?? "",
                    (int)WaylandOutputParsers.Num(w, "pid"), WaylandOutputParsers.Num(w, "x"), WaylandOutputParsers.Num(w, "y"),
                    WaylandOutputParsers.Num(w, "w"), WaylandOutputParsers.Num(w, "h"), WaylandOutputParsers.Bool(w, "active"),
                    !min && current, min, Rank: items.Count - i));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            list.Clear();
        }
        // Stacking order is the z-order here, so keep it rather than the floating/tiling heuristics.
        return list.OrderByDescending(w => w.Visible).ThenBy(w => w.Rank).ToList();
    }

    /// <summary>kdotool getwindowgeometry: "Window {id}", "  Position: x,y", "  Geometry: wxh".</summary>
    public static (double X, double Y, double W, double H)? ParseKdotoolGeometry(string text)
    {
        double x = 0, y = 0, w = 0, h = 0;
        bool pos = false, size = false;
        foreach (var raw in text.Split('\n'))
        {
            var t = raw.Trim();
            if (t.StartsWith("Position:", StringComparison.Ordinal))
            {
                var p = t["Position:".Length..].Trim().Split(' ')[0].Split(',');
                pos = p.Length == 2 && TryD(p[0], out x) && TryD(p[1], out y);
            }
            else if (t.StartsWith("Geometry:", StringComparison.Ordinal))
            {
                var s = t["Geometry:".Length..].Trim().Split('x');
                size = s.Length == 2 && TryD(s[0], out w) && TryD(s[1], out h);
            }
        }
        return pos && size ? (x, y, w, h) : null;
    }

    // ------------------------------------------------------------------------------------------- GNOME (Window Calls)

    internal sealed record GnomeListEntry(uint Id, string Title, string ClassName, int Pid, bool Focus, bool InCurrentWorkspace, int WindowType);

    /// <summary>org.gnome.Shell.Extensions.Windows.List: windows in stacking order, bottom to top.</summary>
    public static List<GnomeListEntry> ParseGnomeList(string json)
    {
        var list = new List<GnomeListEntry>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var w in doc.RootElement.EnumerateArray())
            {
                double id = WaylandOutputParsers.Num(w, "id");
                if (id <= 0) continue;
                list.Add(new GnomeListEntry((uint)id, WaylandOutputParsers.Str(w, "title") ?? "", WaylandOutputParsers.Str(w, "wm_class") ?? "",
                    (int)WaylandOutputParsers.Num(w, "pid"), WaylandOutputParsers.Bool(w, "focus"),
                    !w.TryGetProperty("in_current_workspace", out var cur) || cur.ValueKind != JsonValueKind.False,
                    (int)WaylandOutputParsers.NumOr(w, "window_type", 0)));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            list.Clear();
        }
        return list;
    }

    /// <summary>Details / GetFrameRect: the frame rectangle and the minimized flag.</summary>
    public static (double X, double Y, double W, double H, bool Minimized, string? Title)? ParseGnomeDetails(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("width", out _)) return null;
            return (WaylandOutputParsers.Num(r, "x"), WaylandOutputParsers.Num(r, "y"), WaylandOutputParsers.Num(r, "width"),
                WaylandOutputParsers.Num(r, "height"), WaylandOutputParsers.Bool(r, "minimized"), WaylandOutputParsers.Str(r, "title"));
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return null;
        }
    }

    private static bool TryD(string s, out double v) => double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
}

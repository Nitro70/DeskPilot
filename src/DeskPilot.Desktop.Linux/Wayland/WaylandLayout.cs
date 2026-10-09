using System.Globalization;
using System.Text.Json;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>One output in the compositor's logical layout (the coordinates Wayland compositors use for windows and the pointer).</summary>
internal sealed record WaylandOutput(string Name, double X, double Y, double Width, double Height, double Scale, bool Primary = false, bool Focused = false);

/// <summary>
/// The output layout and DeskPilot's "physical" coordinate space on Wayland: logical layout coordinates multiplied by
/// one factor, the highest output scale. With a single scale this is exactly the screen's pixel grid; grim captures with
/// that scale so screenshot pixels and input coordinates always agree.
/// </summary>
internal sealed class WaylandLayout
{
    public IReadOnlyList<WaylandOutput> Outputs { get; }
    /// <summary>Physical pixels per logical pixel.</summary>
    public double Factor { get; }
    /// <summary>Which tool or protocol described the outputs (for diagnostics).</summary>
    public string Source { get; }
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public ScreenRect VirtualScreen { get; }
    /// <summary>Bounding box of all outputs in logical coordinates.</summary>
    public (double X, double Y, double Width, double Height) LogicalBox { get; }

    public WaylandLayout(IReadOnlyList<WaylandOutput> outputs, string source)
    {
        if (outputs.Count == 0) throw new ArgumentException("A layout needs at least one output", nameof(outputs));
        var ordered = outputs.OrderBy(o => o.X).ThenBy(o => o.Y).ToList();
        Outputs = ordered;
        Source = source;
        Factor = Math.Max(0.1, ordered.Max(o => o.Scale > 0 ? o.Scale : 1.0));

        double minX = ordered.Min(o => o.X), minY = ordered.Min(o => o.Y);
        double maxX = ordered.Max(o => o.X + o.Width), maxY = ordered.Max(o => o.Y + o.Height);
        LogicalBox = (minX, minY, maxX - minX, maxY - minY);

        int primary = ordered.FindIndex(o => o.Primary);
        if (primary < 0) primary = ordered.FindIndex(o => o.X <= 0 && o.Y <= 0 && o.X + o.Width > 0 && o.Y + o.Height > 0);
        if (primary < 0) primary = 0;

        var monitors = new List<MonitorInfo>();
        for (int i = 0; i < ordered.Count; i++)
        {
            var o = ordered[i];
            var bounds = ToPhysicalRect(o.X, o.Y, o.Width, o.Height);
            monitors.Add(new MonitorInfo(i, o.Name, bounds, bounds, i == primary, o.Scale > 0 ? o.Scale : 1.0));
        }
        Monitors = monitors;
        VirtualScreen = ToPhysicalRect(minX, minY, maxX - minX, maxY - minY);
    }

    /// <summary>A single output the size of a full-desktop screenshot, for desktops that describe no outputs.</summary>
    public static WaylandLayout FromImageSize(int width, int height, string source) =>
        new(new[] { new WaylandOutput("screen", 0, 0, width, height, 1.0, Primary: true) }, source);

    public ScreenRect ToPhysicalRect(double x, double y, double w, double h)
    {
        int x1 = (int)Math.Round(x * Factor), y1 = (int)Math.Round(y * Factor);
        int x2 = (int)Math.Round((x + w) * Factor), y2 = (int)Math.Round((y + h) * Factor);
        return new ScreenRect(x1, y1, x2 - x1, y2 - y1);
    }

    public (double X, double Y) ToLogical(double physicalX, double physicalY) => (physicalX / Factor, physicalY / Factor);

    public ScreenPoint ToPhysical(double logicalX, double logicalY) =>
        new((int)Math.Round(logicalX * Factor), (int)Math.Round(logicalY * Factor));

    /// <summary>Center of the primary monitor, used when the real cursor position cannot be read.</summary>
    public ScreenPoint PrimaryCenter => (Monitors.FirstOrDefault(m => m.IsPrimary) ?? Monitors[0]).Bounds.Center;

    public override string ToString() =>
        $"{Source}: {string.Join(", ", Outputs.Select(o => $"{o.Name} {o.Width}x{o.Height}+{o.X}+{o.Y}@{o.Scale}"))}";
}

/// <summary>Parsers for the output listings of the compositor tools. All return an empty list for unreadable input.</summary>
internal static class WaylandOutputParsers
{
    /// <summary>swaymsg -t get_outputs (rect is logical).</summary>
    public static IReadOnlyList<WaylandOutput> ParseSway(string json)
    {
        var list = new List<WaylandOutput>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (o.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False) continue;
                if (!o.TryGetProperty("rect", out var rect)) continue;
                double w = Num(rect, "width"), h = Num(rect, "height");
                if (w <= 0 || h <= 0) continue;
                list.Add(new WaylandOutput(Str(o, "name") ?? $"output{list.Count}", Num(rect, "x"), Num(rect, "y"), w, h,
                    NumOr(o, "scale", 1.0), Bool(o, "primary"), Bool(o, "focused")));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { list.Clear(); }
        return list;
    }

    /// <summary>hyprctl monitors -j (x/y logical, width/height the mode in pixels).</summary>
    public static IReadOnlyList<WaylandOutput> ParseHyprland(string json)
    {
        var list = new List<WaylandOutput>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in doc.RootElement.EnumerateArray())
            {
                if (Bool(o, "disabled")) continue;
                double scale = NumOr(o, "scale", 1.0);
                if (scale <= 0) scale = 1.0;
                double w = Num(o, "width"), h = Num(o, "height");
                if (NumOr(o, "transform", 0) is 1 or 3 or 5 or 7) (w, h) = (h, w);
                if (w <= 0 || h <= 0) continue;
                list.Add(new WaylandOutput(Str(o, "name") ?? $"output{list.Count}", Num(o, "x"), Num(o, "y"), w / scale, h / scale,
                    scale, false, Bool(o, "focused")));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { list.Clear(); }
        return list;
    }

    /// <summary>kscreen-doctor -j (pos logical, size the mode in pixels, rotation 2 and 8 are quarter turns).</summary>
    public static IReadOnlyList<WaylandOutput> ParseKscreen(string json)
    {
        var list = new List<WaylandOutput>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("outputs", out var outputs) || outputs.ValueKind != JsonValueKind.Array) return list;
            foreach (var o in outputs.EnumerateArray())
            {
                if (o.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.False) continue;
                if (o.TryGetProperty("connected", out var con) && con.ValueKind == JsonValueKind.False) continue;
                if (!o.TryGetProperty("pos", out var pos) || !o.TryGetProperty("size", out var size)) continue;
                double scale = NumOr(o, "scale", 1.0);
                if (scale <= 0) scale = 1.0;
                double w = Num(size, "width"), h = Num(size, "height");
                if (NumOr(o, "rotation", 1) is 2 or 8) (w, h) = (h, w);
                if (w <= 0 || h <= 0) continue;
                bool primary = Bool(o, "primary") || NumOr(o, "priority", 0) == 1;
                list.Add(new WaylandOutput(Str(o, "name") ?? $"output{list.Count}", Num(pos, "x"), Num(pos, "y"), w / scale, h / scale, scale, primary));
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { list.Clear(); }
        return list;
    }

    /// <summary>The text output of wlr-randr.</summary>
    public static IReadOnlyList<WaylandOutput> ParseWlrRandr(string text)
    {
        var list = new List<WaylandOutput>();
        string? name = null;
        bool enabled = true, inModes = false;
        double x = 0, y = 0, scale = 1, mw = 0, mh = 0;
        string transform = "normal";

        void Flush()
        {
            if (name == null) return;
            if (enabled && mw > 0 && mh > 0)
            {
                double w = mw, h = mh;
                if (transform.Contains("90") || transform.Contains("270")) (w, h) = (h, w);
                double s = scale > 0 ? scale : 1;
                list.Add(new WaylandOutput(name, x, y, w / s, h / s, s));
            }
            name = null;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) continue;
            if (!char.IsWhiteSpace(line[0]))
            {
                Flush();
                name = line.Split(' ', 2)[0];
                enabled = true; inModes = false; x = y = 0; scale = 1; mw = mh = 0; transform = "normal";
                continue;
            }
            var t = line.Trim();
            if (t.StartsWith("Enabled:", StringComparison.Ordinal)) { enabled = t.EndsWith("yes", StringComparison.Ordinal); inModes = false; }
            else if (t.StartsWith("Modes:", StringComparison.Ordinal)) inModes = true;
            else if (t.StartsWith("Position:", StringComparison.Ordinal))
            {
                inModes = false;
                var p = t["Position:".Length..].Trim().Split(',');
                if (p.Length == 2) { x = ParseD(p[0]); y = ParseD(p[1]); }
            }
            else if (t.StartsWith("Transform:", StringComparison.Ordinal)) { inModes = false; transform = t["Transform:".Length..].Trim(); }
            else if (t.StartsWith("Scale:", StringComparison.Ordinal)) { inModes = false; scale = ParseD(t["Scale:".Length..].Trim()); }
            else if (inModes && t.Contains(" px", StringComparison.Ordinal))
            {
                var size = t.Split(' ', 2)[0].Split('x');
                if (size.Length == 2 && (t.Contains("current", StringComparison.Ordinal) || mw == 0))
                {
                    mw = ParseD(size[0]);
                    mh = ParseD(size[1]);
                }
            }
            else if (!t.Contains(" px", StringComparison.Ordinal)) inModes = false;
        }
        Flush();
        return list;
    }

    public static IReadOnlyList<WaylandOutput> FromProtocol(IReadOnlyList<WaylandProtocolOutput> outputs) =>
        outputs.Select((o, i) => new WaylandOutput(o.Name ?? $"output{i}", o.LogicalX, o.LogicalY, o.LogicalWidth, o.LogicalHeight, o.Scale)).ToList();

    private static double ParseD(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static double Num(JsonElement e, string name) => NumOr(e, name, 0);

    internal static double NumOr(JsonElement e, string name, double fallback) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : fallback;

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

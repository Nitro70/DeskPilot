using System.Globalization;
using DeskPilot.Core.Abstractions;
using SkiaSharp;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>
/// Screenshots on Wayland, where every desktop has its own route: grim (wlr-screencopy) on sway, Hyprland and other
/// wlroots compositors; spectacle on KDE; the xdg-desktop-portal Screenshot interface on GNOME, COSMIC and the rest;
/// gnome-screenshot as the last resort. The first route that works is remembered.
/// </summary>
public sealed class WaylandScreenCapture : IScreenCapture
{
    internal const string RouteGrim = "grim";
    internal const string RouteSpectacle = "spectacle";
    internal const string RoutePortal = "portal";
    internal const string RouteGnomeScreenshot = "gnome-screenshot";

    private static readonly TimeSpan FirstPortalTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan PortalTimeout = TimeSpan.FromSeconds(20);

    private readonly WaylandContext _ctx;
    private readonly string? _forcedRoute;
    private readonly object _gate = new();
    private string? _route;
    private bool _portalUsed;

    public WaylandScreenCapture(LinuxSessionInfo session) : this(new WaylandContext(session)) { }

    internal WaylandScreenCapture(WaylandContext context, string? route = null)
    {
        _ctx = context;
        _forcedRoute = route;
    }

    /// <summary>The route that last worked (after the first capture).</summary>
    internal string? Route => _route;

    public IReadOnlyList<MonitorInfo> GetMonitors() => Layout().Monitors;

    public ScreenRect GetVirtualScreen() => Layout().VirtualScreen;

    public CapturedFrame Capture(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Source.IsEmpty) throw new ArgumentException("The capture region is empty.", nameof(request));
        lock (_gate)
        {
            var layout = Layout();
            using var bitmap = CaptureRegion(request.Source, layout);
            CursorOverlay? cursor = request.DrawCursor ? CursorFor(request.Source, bitmap.Width, bitmap.Height) : null;
            return FrameEncoder.Encode(bitmap, request, cursor);
        }
    }

    private WaylandLayout Layout()
    {
        var layout = _ctx.TryGetLayout();
        if (layout != null) return layout;
        lock (_gate)
        {
            if (_ctx.CaptureLayout != null) return _ctx.CaptureLayout;
            // Nothing describes the outputs: treat the whole desktop as one monitor the size of a full screenshot.
            using var full = CaptureFull();
            _ctx.CaptureLayout = WaylandLayout.FromImageSize(full.Width, full.Height, "screenshot");
            return _ctx.CaptureLayout;
        }
    }

    private CursorOverlay? CursorFor(ScreenRect source, int bitmapWidth, int bitmapHeight)
    {
        var p = _ctx.QueryCursor() ?? WaylandCursorTracker.Last;
        if (p is not { } c || !source.Contains(c)) return null;
        double sx = bitmapWidth / (double)source.Width, sy = bitmapHeight / (double)source.Height;
        return new CursorOverlay((int)Math.Round((c.X - source.X) * sx), (int)Math.Round((c.Y - source.Y) * sy));
    }

    // ------------------------------------------------------------------------------------------- routes

    internal IReadOnlyList<string> Routes()
    {
        if (_forcedRoute != null) return new[] { _forcedRoute };
        bool screencopy = _ctx.HasGlobal("zwlr_screencopy_manager_v1") || _ctx.HasGlobal("ext_image_copy_capture_manager_v1");
        return RouteOrder(_ctx.Desktop, screencopy, _ctx.Runner.Find("grim") != null);
    }

    internal static IReadOnlyList<string> RouteOrder(WaylandDesktop desktop, bool hasScreencopy, bool hasGrim)
    {
        var list = new List<string>();
        if (hasGrim && (hasScreencopy || desktop is WaylandDesktop.Sway or WaylandDesktop.Hyprland or WaylandDesktop.Wlroots)) list.Add(RouteGrim);
        if (desktop == WaylandDesktop.Kde) list.Add(RouteSpectacle);
        list.Add(RoutePortal);
        list.Add(RouteGnomeScreenshot);
        return list;
    }

    /// <summary>The pixels of a physical region. The returned bitmap covers exactly the region (black outside the screen).</summary>
    private SKBitmap CaptureRegion(ScreenRect source, WaylandLayout layout)
    {
        var errors = new List<string>();
        foreach (var route in OrderedRoutes())
        {
            try
            {
                SKBitmap result;
                if (route == RouteGrim) result = GrimRegion(source, layout);
                else
                {
                    using var full = CaptureFullWith(route);
                    result = CropFull(full, source, layout.VirtualScreen);
                }
                _route = route;
                return result;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or Tmds.DBus.Protocol.DBusExceptionBase)
            {
                errors.Add($"{route}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(CaptureHelp(_ctx) + (errors.Count > 0 ? " Details: " + string.Join("; ", errors) : ""));
    }

    private SKBitmap CaptureFull()
    {
        var errors = new List<string>();
        foreach (var route in OrderedRoutes())
        {
            try
            {
                var bmp = route == RouteGrim ? GrimFull() : CaptureFullWith(route);
                _route = route;
                return bmp;
            }
            catch (Exception ex) when (ex is InvalidOperationException or TimeoutException or IOException or UnauthorizedAccessException or Tmds.DBus.Protocol.DBusExceptionBase)
            {
                errors.Add($"{route}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(CaptureHelp(_ctx) + (errors.Count > 0 ? " Details: " + string.Join("; ", errors) : ""));
    }

    /// <summary>The remembered route first, then the rest in preference order.</summary>
    private IEnumerable<string> OrderedRoutes()
    {
        var routes = Routes();
        if (_route != null && routes.Contains(_route)) yield return _route;
        foreach (var r in routes)
            if (r != _route) yield return r;
    }

    private SKBitmap CaptureFullWith(string route) => route switch
    {
        RouteSpectacle => Spectacle(),
        RoutePortal => Portal(),
        RouteGnomeScreenshot => GnomeScreenshot(),
        RouteGrim => GrimFull(),
        _ => throw new InvalidOperationException($"Unknown capture route '{route}'."),
    };

    // ------------------------------------------------------------------------------------------- grim

    /// <summary>grim arguments for a physical region: logical geometry widened to whole pixels, captured at the layout's factor.</summary>
    internal static (List<string> Args, ScreenRect Captured) GrimArgs(ScreenRect source, WaylandLayout layout)
    {
        double f = layout.Factor;
        int gx = (int)Math.Floor(source.X / f), gy = (int)Math.Floor(source.Y / f);
        int gr = (int)Math.Ceiling(source.Right / f), gb = (int)Math.Ceiling(source.Bottom / f);
        int gw = Math.Max(1, gr - gx), gh = Math.Max(1, gb - gy);
        var args = new List<string> { "-g", $"{gx},{gy} {gw}x{gh}" };
        if (Math.Abs(f - 1.0) > 0.0001)
        {
            args.Add("-s");
            args.Add(f.ToString("0.####", CultureInfo.InvariantCulture));
        }
        args.AddRange(new[] { "-t", "ppm", "-" });
        var captured = new ScreenRect((int)Math.Round(gx * f), (int)Math.Round(gy * f), (int)Math.Round(gw * f), (int)Math.Round(gh * f));
        return (args, captured);
    }

    private SKBitmap GrimRegion(ScreenRect source, WaylandLayout layout)
    {
        var (args, captured) = GrimArgs(source, layout);
        var r = _ctx.Runner.Run("grim", args, 15000);
        if (!r.Ok) throw new InvalidOperationException(r.Describe("grim"));
        using var image = DecodeImage(r.StdOut);
        // The image covers 'captured' at the layout factor; take exactly the requested region out of it.
        double sx = image.Width / (double)Math.Max(1, captured.Width), sy = image.Height / (double)Math.Max(1, captured.Height);
        var rect = new SKRect((float)((source.X - captured.X) * sx), (float)((source.Y - captured.Y) * sy),
            (float)((source.Right - captured.X) * sx), (float)((source.Bottom - captured.Y) * sy));
        return Crop(image, rect, source.Width, source.Height);
    }

    private SKBitmap GrimFull()
    {
        var r = _ctx.Runner.Run("grim", new[] { "-t", "ppm", "-" }, 15000);
        if (!r.Ok) throw new InvalidOperationException(r.Describe("grim"));
        return DecodeImage(r.StdOut);
    }

    // ------------------------------------------------------------------------------------------- full-desktop routes

    private SKBitmap Spectacle()
    {
        var file = TempPng();
        try
        {
            var r = _ctx.Runner.Run("spectacle", new[] { "-b", "-n", "-f", "-o", file }, 20000);
            if (!r.Ok || !File.Exists(file)) throw new InvalidOperationException(r.Ok ? "spectacle wrote no file" : r.Describe("spectacle"));
            return DecodeImage(File.ReadAllBytes(file));
        }
        finally
        {
            TryDelete(file);
        }
    }

    private SKBitmap Portal()
    {
        var timeout = _portalUsed ? PortalTimeout : FirstPortalTimeout;
        var bytes = InputRoutes.Sync(() => PortalScreenshot.TakeAsync(_ctx.Portal, timeout, CancellationToken.None));
        _portalUsed = true;
        return DecodeImage(bytes);
    }

    private SKBitmap GnomeScreenshot()
    {
        var file = TempPng();
        try
        {
            var r = _ctx.Runner.Run("gnome-screenshot", new[] { "-f", file }, 20000);
            if (!r.Ok || !File.Exists(file)) throw new InvalidOperationException(r.Ok ? "gnome-screenshot wrote no file" : r.Describe("gnome-screenshot"));
            return DecodeImage(File.ReadAllBytes(file));
        }
        finally
        {
            TryDelete(file);
        }
    }

    private static string TempPng()
    {
        // Under the user's private runtime directory when there is one, so the image is never world-readable.
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) dir = Path.GetTempPath();
        return Path.Combine(dir, $"deskpilot-{Guid.NewGuid():N}.png");
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A region of a full-desktop image; the image covers the virtual screen, possibly at another resolution.</summary>
    internal static SKBitmap CropFull(SKBitmap full, ScreenRect source, ScreenRect virtualScreen)
    {
        double sx = full.Width / (double)Math.Max(1, virtualScreen.Width), sy = full.Height / (double)Math.Max(1, virtualScreen.Height);
        var rect = new SKRect((float)((source.X - virtualScreen.X) * sx), (float)((source.Y - virtualScreen.Y) * sy),
            (float)((source.Right - virtualScreen.X) * sx), (float)((source.Bottom - virtualScreen.Y) * sy));
        return Crop(full, rect, source.Width, source.Height);
    }

    /// <summary>Copies rect of image (may extend past its edges) into a new width x height bitmap.</summary>
    internal static SKBitmap Crop(SKBitmap image, SKRect rect, int width, int height)
    {
        int w = Math.Max(1, width), h = Math.Max(1, height);
        // Same size and aligned: a plain pixel copy keeps the capture exact.
        if (Math.Abs(rect.Width - w) < 0.01 && Math.Abs(rect.Height - h) < 0.01 && rect.Left == Math.Floor(rect.Left) && rect.Top == Math.Floor(rect.Top) &&
            rect.Left >= 0 && rect.Top >= 0 && rect.Right <= image.Width && rect.Bottom <= image.Height)
        {
            var subset = new SKBitmap();
            if (image.ExtractSubset(subset, SKRectI.Create((int)rect.Left, (int)rect.Top, w, h)))
            {
                var copy = subset.Copy();
                subset.Dispose();
                if (copy != null) return copy;
            }
            subset.Dispose();
        }

        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bmp);
        canvas.Clear(SKColors.Black);
        using var img = SKImage.FromBitmap(image);
        canvas.DrawImage(img, rect, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
        canvas.Flush();
        return bmp;
    }

    /// <summary>PNG, JPEG (via SkiaSharp) or binary PPM (grim -t ppm, which is much faster than PNG to produce).</summary>
    internal static SKBitmap DecodeImage(byte[] data)
    {
        if (data.Length >= 2 && data[0] == (byte)'P' && data[1] == (byte)'6') return DecodePpm(data);
        return SKBitmap.Decode(data) ?? throw new InvalidOperationException("The screenshot could not be decoded.");
    }

    internal static unsafe SKBitmap DecodePpm(byte[] data)
    {
        int pos = 2;
        int Next()
        {
            while (true)
            {
                while (pos < data.Length && IsSpace(data[pos])) pos++;
                if (pos < data.Length && data[pos] == (byte)'#')
                {
                    while (pos < data.Length && data[pos] != (byte)'\n') pos++;
                    continue;
                }
                break;
            }
            int start = pos;
            int v = 0;
            while (pos < data.Length && data[pos] >= (byte)'0' && data[pos] <= (byte)'9') v = checked(v * 10 + (data[pos++] - '0'));
            if (pos == start) throw new InvalidOperationException("Malformed PPM header.");
            return v;
        }

        int width = Next(), height = Next(), max = Next();
        pos++; // the single whitespace byte before the pixels
        if (width <= 0 || height <= 0 || max <= 0 || max > 255) throw new InvalidOperationException("Unsupported PPM image.");
        long needed = (long)width * height * 3;
        if (data.Length - pos < needed) throw new InvalidOperationException("The PPM image is truncated.");

        var bmp = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Opaque));
        byte* dst = (byte*)bmp.GetPixels();
        int rowBytes = bmp.RowBytes;
        fixed (byte* src0 = &data[pos])
        {
            byte* src = src0;
            for (int y = 0; y < height; y++)
            {
                byte* row = dst + (long)y * rowBytes;
                for (int x = 0; x < width; x++)
                {
                    row[0] = src[0];
                    row[1] = src[1];
                    row[2] = src[2];
                    row[3] = 255;
                    row += 4;
                    src += 3;
                }
            }
        }
        bmp.NotifyPixelsChanged();
        return bmp;
    }

    private static bool IsSpace(byte b) => b is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t';

    internal static string CaptureHelp(WaylandContext ctx) =>
        $"Wayland screenshots need grim (sway/Hyprland and other wlroots compositors) or the xdg-desktop-portal screenshot permission (GNOME/KDE), and none worked on {ctx.DesktopName}. " +
        (ctx.IsWlrootsFamily
            ? "Install grim: sudo apt install grim / sudo dnf install grim / sudo pacman -S grim."
            : "Install xdg-desktop-portal and your desktop's backend (xdg-desktop-portal-gnome or xdg-desktop-portal-kde) and allow DeskPilot to take screenshots when asked.");
}

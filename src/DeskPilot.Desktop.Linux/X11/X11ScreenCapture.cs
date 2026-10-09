using System.Globalization;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.X11.Interop;
using SkiaSharp;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>
/// X11 screen capture: XGetImage of the root window into an SKBitmap, the cursor from XFixes, monitors from
/// RandR 1.5 (XRRGetMonitors), then the shared <see cref="FrameEncoder"/> for scaling, grid and encoding.
/// </summary>
public sealed unsafe class X11ScreenCapture : IScreenCapture, IDisposable
{
    private static readonly nuint AllPlanes = nuint.MaxValue;

    private readonly XConnection _conn = new();
    private readonly XAtomCache _atoms = new();
    private int _extensionsGeneration = -1;
    private bool _hasRandr15;
    private bool _hasXfixes;

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        using var lease = _conn.Acquire();
        var root = RootGeometry(lease);
        double scale = ReadScale(lease);
        var workArea = ReadWorkArea(lease);
        var monitors = new List<MonitorInfo>();

        CheckExtensions(lease);
        if (_hasRandr15)
        {
            int n = 0;
            var list = Xrandr.XRRGetMonitors(lease.Display, lease.Root, X.True, &n);
            if (list != null)
            {
                try
                {
                    for (int i = 0; i < n; i++)
                    {
                        var m = list[i];
                        if (m.width <= 0 || m.height <= 0) continue;
                        var bounds = new ScreenRect(m.x, m.y, m.width, m.height);
                        var name = XProps.AtomName(lease.Display, m.name);
                        monitors.Add(new MonitorInfo(monitors.Count, string.IsNullOrEmpty(name) ? $"monitor{i}" : name,
                            bounds, WorkAreaFor(bounds, workArea), m.primary != 0, scale));
                    }
                }
                finally
                {
                    Xrandr.XRRFreeMonitors(list);
                }
            }
            lease.Sync();
        }

        if (monitors.Count == 0)
            return new[] { new MonitorInfo(0, "screen" + lease.Screen.ToString(CultureInfo.InvariantCulture), root, WorkAreaFor(root, workArea), true, scale) };

        // Exactly one primary: RandR may report none (no primary output set), then the one at the origin is it.
        if (!monitors.Any(m => m.IsPrimary))
        {
            int p = monitors.FindIndex(m => m.Bounds.X == 0 && m.Bounds.Y == 0);
            monitors[p < 0 ? 0 : p] = monitors[p < 0 ? 0 : p] with { IsPrimary = true };
        }
        return monitors;
    }

    public ScreenRect GetVirtualScreen()
    {
        using var lease = _conn.Acquire();
        return RootGeometry(lease);
    }

    public CapturedFrame Capture(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var src = request.Source;
        if (src.IsEmpty) throw new ArgumentException($"Capture source {src} is empty.", nameof(request));
        var normalized = request with
        {
            TargetWidth = request.TargetWidth > 0 ? request.TargetWidth : src.Width,
            TargetHeight = request.TargetHeight > 0 ? request.TargetHeight : src.Height,
        };

        using var bitmap = new SKBitmap(new SKImageInfo(src.Width, src.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
        CursorOverlay? cursor = null;
        try
        {
            using (var lease = _conn.Acquire())
            {
                GrabInto(lease, src, bitmap);
                if (request.DrawCursor) cursor = ReadCursor(lease, src);
            }
            return FrameEncoder.Encode(bitmap, normalized, cursor);
        }
        finally
        {
            cursor?.Image?.Dispose();
        }
    }

    /// <summary>Copies the part of <paramref name="src"/> that lies on the root window into the bitmap; the rest stays black.</summary>
    private static void GrabInto(in XConnection.Lease lease, ScreenRect src, SKBitmap bitmap)
    {
        var dstPixels = new Span<uint>((void*)bitmap.GetPixels(), src.Width * src.Height);
        var visible = src.Intersect(RootGeometry(lease));
        if (visible.IsEmpty)
        {
            dstPixels.Fill(0xff000000u);
            return;
        }
        if (visible != src) dstPixels.Fill(0xff000000u);

        var image = Xlib.XGetImage(lease.Display, lease.Root, visible.X, visible.Y, (uint)visible.Width, (uint)visible.Height, AllPlanes, X.ZPixmap);
        if (image == null)
        {
            var err = lease.Sync();
            throw new InvalidOperationException($"XGetImage failed for {visible}{(err is { } e ? ": " + e : "")}.");
        }
        try
        {
            var data = new ReadOnlySpan<byte>(image->data, image->bytes_per_line * image->height);
            int offset = (visible.Y - src.Y) * src.Width + (visible.X - src.X);
            X11Pixels.ToBgra(data, image->width, image->height, image->bytes_per_line, image->bits_per_pixel,
                image->byte_order == X.MSBFirst, (uint)image->red_mask, (uint)image->green_mask, (uint)image->blue_mask,
                dstPixels[offset..], src.Width);
        }
        finally
        {
            if (image->destroy_image != null) image->destroy_image(image);
            else Xlib.XFree(image);
        }
        bitmap.NotifyPixelsChanged();
    }

    /// <summary>The cursor image and its hotspot position relative to the capture, or null when it is outside it.</summary>
    private CursorOverlay? ReadCursor(in XConnection.Lease lease, ScreenRect src)
    {
        CheckExtensions(lease);
        if (_hasXfixes)
        {
            var ci = Xfixes.XFixesGetCursorImage(lease.Display);
            if (ci != null)
            {
                try
                {
                    if (!src.Contains(ci->x, ci->y)) return null;
                    SKBitmap? image = null;
                    if (ci->width > 0 && ci->height > 0 && ci->pixels != null)
                    {
                        image = new SKBitmap(new SKImageInfo(ci->width, ci->height, SKColorType.Bgra8888, SKAlphaType.Premul));
                        var dst = new Span<uint>((void*)image.GetPixels(), ci->width * ci->height);
                        X11Pixels.CursorToBgra(new ReadOnlySpan<nuint>(ci->pixels, ci->width * ci->height), dst);
                        image.NotifyPixelsChanged();
                    }
                    return new CursorOverlay(ci->x - src.X, ci->y - src.Y, image, ci->xhot, ci->yhot);
                }
                finally
                {
                    Xlib.XFree(ci);
                }
            }
            lease.Sync();
        }

        // No XFixes: a generic arrow at the pointer position.
        nuint r, c;
        int rx, ry, wx, wy;
        uint mask;
        if (Xlib.XQueryPointer(lease.Display, lease.Root, &r, &c, &rx, &ry, &wx, &wy, &mask) == 0) return null;
        return src.Contains(rx, ry) ? new CursorOverlay(rx - src.X, ry - src.Y) : null;
    }

    /// <summary>Test hook: the cursor as it would be drawn into a full-screen capture.</summary>
    internal CursorOverlay? GetCursorForTests()
    {
        using var lease = _conn.Acquire();
        return ReadCursor(lease, RootGeometry(lease));
    }

    private void CheckExtensions(in XConnection.Lease lease)
    {
        if (_extensionsGeneration == lease.Generation) return;
        _extensionsGeneration = lease.Generation;
        _hasRandr15 = false;
        _hasXfixes = false;
        int a, b;
        if (X11Native.IsAvailable(X11Library.Xrandr) && Xrandr.XRRQueryExtension(lease.Display, &a, &b) != 0)
        {
            int major = 0, minor = 0;
            if (Xrandr.XRRQueryVersion(lease.Display, &major, &minor) != 0)
                _hasRandr15 = major > 1 || (major == 1 && minor >= 5);
        }
        if (X11Native.IsAvailable(X11Library.Xfixes) && Xfixes.XFixesQueryExtension(lease.Display, &a, &b) != 0)
            _hasXfixes = true;
        lease.Sync();
    }

    private static ScreenRect RootGeometry(in XConnection.Lease lease)
    {
        XWindowAttributes attrs;
        if (Xlib.XGetWindowAttributes(lease.Display, lease.Root, &attrs) != 0 && attrs.width > 0 && attrs.height > 0)
            return new ScreenRect(0, 0, attrs.width, attrs.height);
        return new ScreenRect(0, 0, Xlib.XDisplayWidth(lease.Display, lease.Screen), Xlib.XDisplayHeight(lease.Display, lease.Screen));
    }

    /// <summary>DPI scale from the Xft.dpi resource (what GTK, Qt and most desktops set for HiDPI), else 1.0.</summary>
    private double ReadScale(in XConnection.Lease lease)
    {
        // The live RESOURCE_MANAGER property of the root window. XResourceManagerString holds the same text, but
        // Xlib copies it once when the connection opens and would miss a later `xrdb` change.
        var resources = XProps.GetText(lease.Display, lease.Root, _atoms.Get(lease, "RESOURCE_MANAGER"), X.XA_STRING, X.None);
        lease.Sync();
        var dpi = ParseXftDpi(resources);
        return dpi is > 0 ? Math.Round(dpi.Value / 96.0, 3) : 1.0;
    }

    /// <summary>The Xft.dpi value from X resources text ("Xft.dpi:\t144"), or null.</summary>
    internal static double? ParseXftDpi(string? resources)
    {
        if (string.IsNullOrEmpty(resources)) return null;
        foreach (var raw in resources.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Xft.dpi", StringComparison.Ordinal)) continue;
            int colon = line.IndexOf(':');
            if (colon < 0 || line[..colon].Trim() != "Xft.dpi") continue;
            if (double.TryParse(line[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi is > 0 and < 2000)
                return dpi;
        }
        return null;
    }

    /// <summary>_NET_WORKAREA of the current desktop (screen minus panels), or null when the window manager sets none.</summary>
    private ScreenRect? ReadWorkArea(in XConnection.Lease lease)
    {
        var areas = XProps.GetLongs(lease.Display, lease.Root, _atoms.Get(lease, "_NET_WORKAREA"), X.XA_CARDINAL);
        var current = XProps.GetLong(lease.Display, lease.Root, _atoms.Get(lease, "_NET_CURRENT_DESKTOP"), X.XA_CARDINAL);
        lease.Sync();
        if (areas == null || areas.Length < 4) return null;
        int desk = (int)(current ?? 0);
        if ((desk + 1) * 4 > areas.Length) desk = 0;
        var r = new ScreenRect((int)(long)areas[desk * 4], (int)(long)areas[desk * 4 + 1], (int)areas[desk * 4 + 2], (int)areas[desk * 4 + 3]);
        return r.IsEmpty ? null : r;
    }

    /// <summary>The monitor's part of the work area; the whole monitor when the work area misses it.</summary>
    internal static ScreenRect WorkAreaFor(ScreenRect monitor, ScreenRect? workArea)
    {
        if (workArea is not { } wa) return monitor;
        var r = monitor.Intersect(wa);
        return r.IsEmpty ? monitor : r;
    }

    public void Dispose() => _conn.Dispose();
}

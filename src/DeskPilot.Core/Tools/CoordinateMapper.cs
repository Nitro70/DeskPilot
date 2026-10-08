using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Tools;

/// <summary>Maps between the model's coordinate space (screenshot pixels or 0-1000) and physical screen pixels.</summary>
public sealed class CoordinateMapper
{
    /// <summary>Size of the normalized coordinate space on both axes.</summary>
    public const double NormalizedRange = 1000.0;

    public CoordinateMapper(ScreenRect source, int imageWidth, int imageHeight, CoordinateMode mode)
    {
        Source = source; ImageWidth = imageWidth; ImageHeight = imageHeight; Mode = mode;
    }

    public ScreenRect Source { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public CoordinateMode Mode { get; }

    /// <summary>Width of the model's coordinate space: the image width, or 1000 in normalized mode.</summary>
    public double ModelWidth => Mode == CoordinateMode.Normalized1000 ? NormalizedRange : Math.Max(1, ImageWidth);

    /// <summary>Height of the model's coordinate space: the image height, or 1000 in normalized mode.</summary>
    public double ModelHeight => Mode == CoordinateMode.Normalized1000 ? NormalizedRange : Math.Max(1, ImageHeight);

    /// <summary>Model coordinates -> physical screen point (clamped inside Source).</summary>
    public ScreenPoint ToScreen(double x, double y)
    {
        var sx = Source.X + Finite(x) * Source.Width / ModelWidth;
        var sy = Source.Y + Finite(y) * Source.Height / ModelHeight;
        return new ScreenPoint(
            ClampInt(RoundToInt(sx), Source.X, Source.Right - 1),
            ClampInt(RoundToInt(sy), Source.Y, Source.Bottom - 1));
    }

    /// <summary>Model-space rectangle -> physical rectangle, clipped to Source (empty when it does not overlap).</summary>
    public ScreenRect ToScreenRect(double x, double y, double width, double height)
    {
        int x1 = RoundToInt(Source.X + Finite(x) * Source.Width / ModelWidth);
        int y1 = RoundToInt(Source.Y + Finite(y) * Source.Height / ModelHeight);
        int x2 = RoundToInt(Source.X + (Finite(x) + Finite(width)) * Source.Width / ModelWidth);
        int y2 = RoundToInt(Source.Y + (Finite(y) + Finite(height)) * Source.Height / ModelHeight);
        if (x2 <= x1 || y2 <= y1) return default;
        return new ScreenRect(x1, y1, x2 - x1, y2 - y1).Intersect(Source);
    }

    /// <summary>Physical screen point -> model coordinates.</summary>
    public (double X, double Y) FromScreen(ScreenPoint p)
    {
        double x = Source.Width <= 0 ? 0 : (double)(p.X - Source.X) * ModelWidth / Source.Width;
        double y = Source.Height <= 0 ? 0 : (double)(p.Y - Source.Y) * ModelHeight / Source.Height;
        return (x, y);
    }

    /// <summary>Physical rect -> model-space rect (x, y, width, height).</summary>
    public (double X, double Y, double W, double H) FromScreen(ScreenRect r)
    {
        var (x, y) = FromScreen(new ScreenPoint(r.X, r.Y));
        double w = Source.Width <= 0 ? 0 : (double)r.Width * ModelWidth / Source.Width;
        double h = Source.Height <= 0 ? 0 : (double)r.Height * ModelHeight / Source.Height;
        return (x, y, w, h);
    }

    /// <summary>True when a model coordinate lies inside the model space (edges included).</summary>
    public bool IsInModelSpace(double x, double y) =>
        double.IsFinite(x) && double.IsFinite(y) && x >= 0 && y >= 0 && x <= ModelWidth && y <= ModelHeight;

    /// <summary>Largest size with the source aspect ratio that fits in maxW x maxH (never upscales).</summary>
    public static (int Width, int Height) FitWithin(int srcWidth, int srcHeight, int maxWidth, int maxHeight)
    {
        if (srcWidth <= 0 || srcHeight <= 0) return (1, 1);
        double scale = 1.0;
        // A non-positive limit (hand-edited settings) means "no limit" on that axis.
        if (maxWidth > 0) scale = Math.Min(scale, (double)maxWidth / srcWidth);
        if (maxHeight > 0) scale = Math.Min(scale, (double)maxHeight / srcHeight);

        int w = (int)Math.Round(srcWidth * scale, MidpointRounding.AwayFromZero);
        int h = (int)Math.Round(srcHeight * scale, MidpointRounding.AwayFromZero);
        if (maxWidth > 0) w = Math.Min(w, maxWidth);
        if (maxHeight > 0) h = Math.Min(h, maxHeight);
        return (Math.Max(1, w), Math.Max(1, h));
    }

    /// <summary>The source rectangle for the configured monitor selection.</summary>
    public static ScreenRect ResolveSource(IScreenCapture screen, ScreenSettings settings) => ResolveSourceDescribed(screen, settings).Rect;

    /// <summary>The source rectangle plus a short phrase for it ("the primary monitor", "all monitors", "monitor 2").</summary>
    public static (ScreenRect Rect, string Description) ResolveSourceDescribed(IScreenCapture screen, ScreenSettings settings)
    {
        var monitors = screen.GetMonitors() ?? Array.Empty<MonitorInfo>();
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();

        switch (settings.Monitor)
        {
            case MonitorSelection.AllMonitors:
            {
                var virtualScreen = screen.GetVirtualScreen();
                if (!virtualScreen.IsEmpty) return (virtualScreen, "all monitors");
                break;
            }
            case MonitorSelection.Specific:
            {
                int index = settings.MonitorIndex;
                if (index >= 0 && index < monitors.Count && !monitors[index].Bounds.IsEmpty)
                {
                    var m = monitors[index];
                    return (m.Bounds, m.IsPrimary ? $"monitor {index + 1} (the primary monitor)" : $"monitor {index + 1}");
                }
                break;
            }
        }

        if (primary != null && !primary.Bounds.IsEmpty) return (primary.Bounds, "the primary monitor");
        var fallback = screen.GetVirtualScreen();
        return (fallback.IsEmpty ? new ScreenRect(0, 0, 1, 1) : fallback, "the screen");
    }

    public static CoordinateMapper ForSettings(IScreenCapture screen, ScreenSettings settings)
    {
        var source = ResolveSource(screen, settings);
        var (w, h) = FitWithin(source.Width, source.Height, settings.MaxImageWidth, settings.MaxImageHeight);
        return new CoordinateMapper(source, w, h, settings.Coordinates);
    }

    private static double Finite(double v) => double.IsFinite(v) ? v : 0;

    private static int RoundToInt(double v)
    {
        var r = Math.Round(v, MidpointRounding.AwayFromZero);
        if (r >= int.MaxValue) return int.MaxValue;
        if (r <= int.MinValue) return int.MinValue;
        return (int)r;
    }

    private static int ClampInt(int v, int min, int max)
    {
        if (max < min) return min;
        return v < min ? min : v > max ? max : v;
    }
}

using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Tools;

// STUB: owned by the Tools/Safety module agent.
/// <summary>Maps between the model's coordinate space (screenshot pixels or 0-1000) and physical screen pixels.</summary>
public sealed class CoordinateMapper
{
    public CoordinateMapper(ScreenRect source, int imageWidth, int imageHeight, CoordinateMode mode)
    {
        Source = source; ImageWidth = imageWidth; ImageHeight = imageHeight; Mode = mode;
    }

    public ScreenRect Source { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
    public CoordinateMode Mode { get; }

    /// <summary>Model coordinates -> physical screen point (clamped inside Source).</summary>
    public ScreenPoint ToScreen(double x, double y) => throw new NotImplementedException("STUB");
    /// <summary>Physical screen point -> model coordinates.</summary>
    public (double X, double Y) FromScreen(ScreenPoint p) => throw new NotImplementedException("STUB");
    /// <summary>Physical rect -> model-space rect (x, y, width, height).</summary>
    public (double X, double Y, double W, double H) FromScreen(ScreenRect r) => throw new NotImplementedException("STUB");
    /// <summary>Largest size with the source aspect ratio that fits in maxW x maxH (never upscales).</summary>
    public static (int Width, int Height) FitWithin(int srcWidth, int srcHeight, int maxWidth, int maxHeight) => throw new NotImplementedException("STUB");
    /// <summary>The source rectangle for the configured monitor selection.</summary>
    public static ScreenRect ResolveSource(IScreenCapture screen, ScreenSettings settings) => throw new NotImplementedException("STUB");
    public static CoordinateMapper ForSettings(IScreenCapture screen, ScreenSettings settings) => throw new NotImplementedException("STUB");
}

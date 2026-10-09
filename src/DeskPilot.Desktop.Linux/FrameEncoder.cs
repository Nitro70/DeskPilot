using DeskPilot.Core.Abstractions;
using SkiaSharp;

namespace DeskPilot.Desktop.Linux;

/// <summary>A cursor to draw into a capture. X/Y are the hotspot position in Source-relative physical pixels.</summary>
public sealed record CursorOverlay(int X, int Y, SKBitmap? Image = null, int HotspotX = 0, int HotspotY = 0);

// STUB: owned by the X11 agent (the Wayland capture uses it too).
/// <summary>Turns a raw capture into the CapturedFrame the tools expect: scale, cursor, grid, encode.</summary>
public static class FrameEncoder
{
    /// <param name="source">Pixels of request.Source at physical resolution (any color type).</param>
    /// <param name="cursor">Cursor to draw when request.DrawCursor; a null Image draws a generic arrow.</param>
    public static CapturedFrame Encode(SKBitmap source, CaptureRequest request, CursorOverlay? cursor = null) => throw new NotImplementedException("STUB");
}

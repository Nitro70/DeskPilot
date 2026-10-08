using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Services;

/// <summary>Pure geometry for the overlay (physical pixels), kept separate so it can be unit-tested.</summary>
public static class OverlayPlacement
{
    /// <summary>Top-left position of a width x height window in a corner of the work area.</summary>
    public static ScreenPoint Compute(ScreenRect workArea, int width, int height, OverlayCorner corner, int margin)
    {
        int left = workArea.X + margin;
        int right = Math.Max(left, workArea.Right - margin - width);
        int center = Math.Max(left, workArea.X + (workArea.Width - width) / 2);
        int top = workArea.Y + margin;
        int bottom = Math.Max(top, workArea.Bottom - margin - height);
        return corner switch
        {
            OverlayCorner.TopLeft => new ScreenPoint(left, top),
            OverlayCorner.TopCenter => new ScreenPoint(center, top),
            OverlayCorner.TopRight => new ScreenPoint(right, top),
            OverlayCorner.BottomLeft => new ScreenPoint(left, bottom),
            OverlayCorner.BottomCenter => new ScreenPoint(center, bottom),
            _ => new ScreenPoint(right, bottom),
        };
    }

    /// <summary>True when an input action at <paramref name="target"/> would land on (or right next to) the overlay.</summary>
    public static bool ShouldHide(ScreenRect overlayBounds, ScreenPoint? target, int margin = 8)
    {
        if (target is not { } p || overlayBounds.IsEmpty) return false;
        var grown = new ScreenRect(overlayBounds.X - margin, overlayBounds.Y - margin, overlayBounds.Width + 2 * margin, overlayBounds.Height + 2 * margin);
        return grown.Contains(p);
    }
}

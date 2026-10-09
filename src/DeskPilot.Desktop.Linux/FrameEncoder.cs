using DeskPilot.Core.Abstractions;
using SkiaSharp;

namespace DeskPilot.Desktop.Linux;

/// <summary>A cursor to draw into a capture. X/Y are the hotspot position in Source-relative physical pixels.</summary>
public sealed record CursorOverlay(int X, int Y, SKBitmap? Image = null, int HotspotX = 0, int HotspotY = 0);

/// <summary>
/// Turns a raw capture into the CapturedFrame the tools expect: scale to the target size, draw the cursor and the
/// optional coordinate grid, encode JPEG or PNG. Same output semantics as the Windows capture.
/// </summary>
public static class FrameEncoder
{
    /// <param name="source">Pixels of request.Source at physical resolution (any color type).</param>
    /// <param name="cursor">Cursor to draw when request.DrawCursor; a null Image draws a generic arrow.</param>
    public static CapturedFrame Encode(SKBitmap source, CaptureRequest request, CursorOverlay? cursor = null)
    {
        int tw = Math.Max(1, request.TargetWidth), th = Math.Max(1, request.TargetHeight);
        var info = new SKImageInfo(tw, th, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Could not create a drawing surface");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Black);

        using (var image = SKImage.FromBitmap(source))
        {
            // Mitchell cubic for downscaling keeps small text readable; linear is enough when enlarging.
            var sampling = tw < source.Width || th < source.Height
                ? new SKSamplingOptions(SKCubicResampler.Mitchell)
                : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
            canvas.DrawImage(image, new SKRect(0, 0, source.Width, source.Height), new SKRect(0, 0, tw, th), sampling, null);
        }

        double sx = tw / (double)Math.Max(1, source.Width), sy = th / (double)Math.Max(1, source.Height);
        if (request.DrawCursor && cursor != null) DrawCursor(canvas, cursor, sx, sy);
        if (request.GridSpacing > 0) DrawGrid(canvas, tw, th, request.GridSpacing);
        canvas.Flush();

        using var snapshot = surface.Snapshot();
        bool png = request.Format == ImageFormatKind.Png;
        using var data = snapshot.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, Math.Clamp(request.JpegQuality, 1, 100))
            ?? throw new InvalidOperationException("Image encoding failed");
        return new CapturedFrame(data.ToArray(), png ? "image/png" : "image/jpeg", tw, th, request.Source);
    }

    /// <summary>Grid line positions (output pixels) for a given size and spacing, excluding 0 and the edge.</summary>
    internal static IReadOnlyList<int> GridPositions(int length, int spacing)
    {
        var list = new List<int>();
        if (spacing <= 0) return list;
        for (int v = spacing; v < length; v += spacing) list.Add(v);
        return list;
    }

    private static void DrawCursor(SKCanvas canvas, CursorOverlay cursor, double sx, double sy)
    {
        if (cursor.Image != null)
        {
            using var img = SKImage.FromBitmap(cursor.Image);
            float x = (float)((cursor.X - cursor.HotspotX) * sx), y = (float)((cursor.Y - cursor.HotspotY) * sy);
            float w = (float)Math.Max(4, cursor.Image.Width * sx), h = (float)Math.Max(4, cursor.Image.Height * sy);
            canvas.DrawImage(img, new SKRect(0, 0, cursor.Image.Width, cursor.Image.Height), new SKRect(x, y, x + w, y + h),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
            return;
        }

        // Generic arrow, tip at the hotspot; never smaller than 12 px so it stays visible after downscaling.
        float s = (float)Math.Max(12.0 / 20.0, Math.Min(sx, sy));
        float ox = (float)(cursor.X * sx), oy = (float)(cursor.Y * sy);
        using var path = new SKPath();
        path.MoveTo(ox, oy);
        path.LineTo(ox, oy + 17 * s);
        path.LineTo(ox + 4.5f * s, oy + 13 * s);
        path.LineTo(ox + 7.5f * s, oy + 19.5f * s);
        path.LineTo(ox + 10 * s, oy + 18.5f * s);
        path.LineTo(ox + 7 * s, oy + 12 * s);
        path.LineTo(ox + 12.5f * s, oy + 12 * s);
        path.Close();
        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
        using var stroke = new SKPaint { Color = SKColors.Black, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(1f, 1.2f * s) };
        canvas.DrawPath(path, fill);
        canvas.DrawPath(path, stroke);
    }

    private static void DrawGrid(SKCanvas canvas, int width, int height, int spacing)
    {
        // Dark and light line pairs and yellow-on-dark labels read on both light and dark content.
        using var dark = new SKPaint { Color = new SKColor(0, 0, 0, 110), StrokeWidth = 1, IsAntialias = false };
        using var light = new SKPaint { Color = new SKColor(255, 255, 255, 110), StrokeWidth = 1, IsAntialias = false };
        foreach (var x in GridPositions(width, spacing))
        {
            canvas.DrawLine(x, 0, x, height, dark);
            canvas.DrawLine(x + 1, 0, x + 1, height, light);
        }
        foreach (var y in GridPositions(height, spacing))
        {
            canvas.DrawLine(0, y, width, y, dark);
            canvas.DrawLine(0, y + 1, width, y + 1, light);
        }

        SKTypeface? typeface = null;
        try { typeface = SKTypeface.Default; }
        catch (Exception) { /* no fonts: lines only */ }
        if (typeface == null) return;

        using var font = new SKFont(typeface, 11);
        using var text = new SKPaint { Color = new SKColor(255, 230, 0), IsAntialias = true };
        using var box = new SKPaint { Color = new SKColor(0, 0, 0, 170) };
        void Label(string s, float x, float y)
        {
            float w = font.MeasureText(s) + 4;
            canvas.DrawRect(x, y, w, 14, box);
            canvas.DrawText(s, x + 2, y + 11, SKTextAlign.Left, font, text);
        }
        foreach (var x in GridPositions(width, spacing)) Label(x.ToString(System.Globalization.CultureInfo.InvariantCulture), x + 2, 1);
        foreach (var y in GridPositions(height, spacing)) Label(y.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, y + 2);
    }
}

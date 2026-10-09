using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;
using static DeskPilot.Desktop.Windows.NativeMethods;

namespace DeskPilot.Desktop.Windows;

/// <summary>
/// GDI screen capture: BitBlt from the screen DC into a DIB section, optional cursor, high-quality
/// downscale, optional coordinate grid, JPEG/PNG encode. Everything happens in memory.
/// </summary>
public sealed class WindowsScreenCapture : IScreenCapture
{
    private static readonly Lazy<ImageCodecInfo?> JpegCodec = new(() =>
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid));

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        using var dpi = DpiScope.PerMonitorV2();
        var handles = new List<nint>();
        MonitorEnumProc proc = (h, _, _, _) => { handles.Add(h); return true; };
        EnumDisplayMonitors(0, 0, proc, 0);
        GC.KeepAlive(proc);

        var result = new List<MonitorInfo>(handles.Count);
        foreach (var h in handles)
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (!GetMonitorInfo(h, ref info)) continue;
            double scale = 1.0;
            try
            {
                if (GetDpiForMonitor(h, MDT_EFFECTIVE_DPI, out var dx, out _) == 0 && dx > 0) scale = dx / 96.0;
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
            result.Add(new MonitorInfo(
                result.Count,
                info.szDevice ?? "",
                info.rcMonitor.ToScreenRect(),
                info.rcWork.ToScreenRect(),
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0,
                scale));
        }
        return result;
    }

    public ScreenRect GetVirtualScreen()
    {
        using var dpi = DpiScope.PerMonitorV2();
        var r = new ScreenRect(
            GetSystemMetrics(SM_XVIRTUALSCREEN),
            GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN),
            GetSystemMetrics(SM_CYVIRTUALSCREEN));
        if (!r.IsEmpty) return r;
        // Should not happen, but a union of the monitors is the same thing by definition.
        return Union(GetMonitors().Select(m => m.Bounds));
    }

    internal static ScreenRect Union(IEnumerable<ScreenRect> rects)
    {
        int? x1 = null, y1 = null, x2 = null, y2 = null;
        foreach (var r in rects)
        {
            x1 = Math.Min(x1 ?? r.X, r.X);
            y1 = Math.Min(y1 ?? r.Y, r.Y);
            x2 = Math.Max(x2 ?? r.Right, r.Right);
            y2 = Math.Max(y2 ?? r.Bottom, r.Bottom);
        }
        return x1 is null ? default : new ScreenRect(x1.Value, y1!.Value, x2!.Value - x1.Value, y2!.Value - y1.Value);
    }

    public CapturedFrame Capture(CaptureRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var src = request.Source;
        if (src.IsEmpty) throw new ArgumentException($"Capture source {src} is empty.", nameof(request));
        int outW = request.TargetWidth > 0 ? request.TargetWidth : src.Width;
        int outH = request.TargetHeight > 0 ? request.TargetHeight : src.Height;

        using var dpi = DpiScope.PerMonitorV2();

        nint screenDc = 0, memDc = 0, dib = 0, oldObj = 0;
        try
        {
            screenDc = GetDC(0);
            if (screenDc == 0) throw new InvalidOperationException("Could not get the screen device context.");
            memDc = CreateCompatibleDC(screenDc);
            if (memDc == 0) throw new InvalidOperationException("CreateCompatibleDC failed.");

            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = src.Width,
                biHeight = -src.Height, // top-down so the bits match a GDI+ bitmap's row order
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };
            dib = CreateDIBSection(screenDc, ref header, DIB_RGB_COLORS, out var bits, 0, 0);
            if (dib == 0 || bits == 0) throw new InvalidOperationException($"CreateDIBSection failed for {src.Width}x{src.Height}.");
            oldObj = SelectObject(memDc, dib);

            if (!BitBlt(memDc, 0, 0, src.Width, src.Height, screenDc, src.X, src.Y, SRCCOPY | CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastWin32Error()}). The desktop may be locked or a secure prompt is showing.");

            if (request.DrawCursor) DrawCursor(memDc, src);
            GdiFlush();

            using var sourceBitmap = new Bitmap(src.Width, src.Height, src.Width * 4, PixelFormat.Format32bppRgb, bits);
            Bitmap output = sourceBitmap;
            try
            {
                if (outW != src.Width || outH != src.Height) output = Scale(sourceBitmap, outW, outH);
                if (request.GridSpacing > 0) DrawGrid(output, request.GridSpacing);
                var data = Encode(output, request.Format, request.JpegQuality);
                return new CapturedFrame(data, request.Format == ImageFormatKind.Png ? "image/png" : "image/jpeg", outW, outH, src);
            }
            finally
            {
                if (!ReferenceEquals(output, sourceBitmap)) output.Dispose();
            }
        }
        finally
        {
            if (memDc != 0 && oldObj != 0) SelectObject(memDc, oldObj);
            if (dib != 0) DeleteObject(dib);
            if (memDc != 0) DeleteDC(memDc);
            if (screenDc != 0) ReleaseDC(0, screenDc);
        }
    }

    private static void DrawCursor(nint memDc, ScreenRect src)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.flags & CURSOR_SHOWING) == 0 || ci.hCursor == 0) return;
        if (!src.Contains(ci.ptScreenPos.X, ci.ptScreenPos.Y)) return;

        int hotX = 0, hotY = 0;
        if (GetIconInfo(ci.hCursor, out var ii))
        {
            hotX = ii.xHotspot;
            hotY = ii.yHotspot;
            if (ii.hbmMask != 0) DeleteObject(ii.hbmMask);
            if (ii.hbmColor != 0) DeleteObject(ii.hbmColor);
        }
        var (x, y) = CursorDrawPosition(new ScreenPoint(ci.ptScreenPos.X, ci.ptScreenPos.Y), hotX, hotY, src);
        // Drawn at its real size into the full-resolution source, so the downscale shrinks it exactly like the screen.
        DrawIconEx(memDc, x, y, ci.hCursor, 0, 0, 0, 0, DI_NORMAL);
    }

    /// <summary>Top-left corner (in source-bitmap pixels) at which to draw a cursor whose hotspot sits on <paramref name="cursor"/>.</summary>
    internal static (int X, int Y) CursorDrawPosition(ScreenPoint cursor, int hotspotX, int hotspotY, ScreenRect source) =>
        (cursor.X - source.X - hotspotX, cursor.Y - source.Y - hotspotY);

    private static Bitmap Scale(Bitmap source, int width, int height)
    {
        var output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var g = Graphics.FromImage(output);
            g.CompositingMode = CompositingMode.SourceCopy;
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.SmoothingMode = SmoothingMode.None;
            using var attributes = new ImageAttributes();
            // Without this the bicubic filter samples outside the image and darkens the edges.
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, attributes);
        }
        catch
        {
            output.Dispose();
            throw;
        }
        return output;
    }

    /// <summary>Positions (in output pixels) of the grid lines: spacing, 2*spacing, ... strictly inside the image.</summary>
    internal static IReadOnlyList<int> GridPositions(int size, int spacing)
    {
        var list = new List<int>();
        if (spacing <= 0 || size <= 0) return list;
        for (int p = spacing; p < size; p += spacing) list.Add(p);
        return list;
    }

    internal static void DrawGrid(Bitmap image, int spacing)
    {
        var xs = GridPositions(image.Width, spacing);
        var ys = GridPositions(image.Height, spacing);
        if (xs.Count == 0 && ys.Count == 0) return;

        using var g = Graphics.FromImage(image);
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // A dark line with a light line beside it stays visible on both light and dark content.
        using var dark = new Pen(Color.FromArgb(110, 0, 0, 0), 1);
        using var light = new Pen(Color.FromArgb(110, 255, 255, 255), 1);
        foreach (var x in xs)
        {
            g.DrawLine(dark, x, 0, x, image.Height - 1);
            if (x + 1 < image.Width) g.DrawLine(light, x + 1, 0, x + 1, image.Height - 1);
        }
        foreach (var y in ys)
        {
            g.DrawLine(dark, 0, y, image.Width - 1, y);
            if (y + 1 < image.Height) g.DrawLine(light, 0, y + 1, image.Width - 1, y + 1);
        }

        float fontPx = Math.Clamp(spacing / 6f, 9f, 13f);
        using var font = new Font(FontFamily.GenericSansSerif, fontPx, FontStyle.Bold, GraphicsUnit.Pixel);
        using var back = new SolidBrush(Color.FromArgb(170, 0, 0, 0));
        using var fore = new SolidBrush(Color.FromArgb(255, 255, 255, 120));

        void Label(string text, float x, float y)
        {
            var size = g.MeasureString(text, font, PointF.Empty, StringFormat.GenericTypographic);
            var rect = new RectangleF(x, y, size.Width + 4, size.Height + 2);
            if (rect.Right > image.Width) rect.X = image.Width - rect.Width;
            if (rect.Bottom > image.Height) rect.Y = image.Height - rect.Height;
            g.FillRectangle(back, rect);
            g.DrawString(text, font, fore, rect.X + 2, rect.Y + 1, StringFormat.GenericTypographic);
        }

        foreach (var x in xs) Label(x.ToString(System.Globalization.CultureInfo.InvariantCulture), x + 2, 1);
        foreach (var y in ys) Label(y.ToString(System.Globalization.CultureInfo.InvariantCulture), 1, y + 2);
    }

    private static byte[] Encode(Bitmap image, ImageFormatKind format, int jpegQuality)
    {
        using var ms = new MemoryStream();
        if (format == ImageFormatKind.Png)
        {
            image.Save(ms, ImageFormat.Png);
        }
        else
        {
            var codec = JpegCodec.Value;
            if (codec == null)
            {
                image.Save(ms, ImageFormat.Jpeg);
            }
            else
            {
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 1, 100));
                image.Save(ms, codec, parameters);
            }
        }
        return ms.ToArray();
    }
}

using System.Numerics;
using System.Runtime.InteropServices;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>ZPixmap image rows to opaque BGRA pixels (SkiaSharp Bgra8888 on a little-endian machine). Pure, so it is tested anywhere.</summary>
internal static class X11Pixels
{
    /// <summary>
    /// Converts <paramref name="height"/> rows of a ZPixmap image into <paramref name="dst"/> (one uint per pixel,
    /// <paramref name="dstStride"/> pixels per row, value 0xAARRGGBB with alpha 255). Handles 32, 24 and 16 bits
    /// per pixel, either byte order and any channel masks (including 10-bit depth 30).
    /// </summary>
    public static void ToBgra(ReadOnlySpan<byte> src, int width, int height, int bytesPerLine, int bitsPerPixel, bool msbFirst,
        uint redMask, uint greenMask, uint blueMask, Span<uint> dst, int dstStride)
    {
        if (width <= 0 || height <= 0) return;
        int bpp = bitsPerPixel / 8;
        if (bitsPerPixel is not (32 or 24 or 16)) throw new NotSupportedException($"{bitsPerPixel} bits per pixel screens are not supported.");
        if (bytesPerLine < width * bpp) throw new ArgumentException("bytes_per_line is smaller than a row.", nameof(bytesPerLine));

        bool fast = bitsPerPixel == 32 && !msbFirst && BitConverter.IsLittleEndian &&
                    redMask == 0xff0000 && greenMask == 0xff00 && blueMask == 0xff;
        if (fast)
        {
            for (int y = 0; y < height; y++)
            {
                var row = MemoryMarshal.Cast<byte, uint>(src.Slice(y * bytesPerLine, width * 4));
                var outRow = dst.Slice(y * dstStride, width);
                row.CopyTo(outRow);
                SetOpaque(outRow);
            }
            return;
        }

        var r = Channel.From(redMask);
        var g = Channel.From(greenMask);
        var b = Channel.From(blueMask);
        for (int y = 0; y < height; y++)
        {
            var row = src.Slice(y * bytesPerLine, width * bpp);
            var outRow = dst.Slice(y * dstStride, width);
            for (int x = 0; x < width; x++)
            {
                uint v = Read(row.Slice(x * bpp, bpp), msbFirst);
                outRow[x] = 0xff000000u | (r.Extract(v) << 16) | (g.Extract(v) << 8) | b.Extract(v);
            }
        }
    }

    private static void SetOpaque(Span<uint> pixels)
    {
        int i = 0;
        if (Vector.IsHardwareAccelerated && pixels.Length >= Vector<uint>.Count)
        {
            var alpha = new Vector<uint>(0xff000000u);
            var vectors = MemoryMarshal.Cast<uint, Vector<uint>>(pixels);
            for (int v = 0; v < vectors.Length; v++) vectors[v] |= alpha;
            i = vectors.Length * Vector<uint>.Count;
        }
        for (; i < pixels.Length; i++) pixels[i] |= 0xff000000u;
    }

    private static uint Read(ReadOnlySpan<byte> p, bool msbFirst)
    {
        uint v = 0;
        if (msbFirst)
            for (int i = 0; i < p.Length; i++) v = (v << 8) | p[i];
        else
            for (int i = p.Length - 1; i >= 0; i--) v = (v << 8) | p[i];
        return v;
    }

    private readonly record struct Channel(uint Mask, int Shift, int Bits)
    {
        public static Channel From(uint mask)
        {
            if (mask == 0) return new Channel(0, 0, 0);
            int shift = BitOperations.TrailingZeroCount(mask);
            int bits = BitOperations.PopCount(mask);
            return new Channel(mask, shift, bits);
        }

        public uint Extract(uint v)
        {
            if (Bits == 0) return 0;
            uint c = (v & Mask) >> Shift;
            if (Bits == 8) return c;
            if (Bits > 8) return c >> (Bits - 8);
            uint max = (1u << Bits) - 1;
            return (c * 255 + max / 2) / max;
        }
    }

    /// <summary>XFixes cursor pixels (one ARGB value per unsigned long, premultiplied) to BGRA uints.</summary>
    public static void CursorToBgra(ReadOnlySpan<nuint> argb, Span<uint> dst)
    {
        for (int i = 0; i < argb.Length && i < dst.Length; i++) dst[i] = (uint)(argb[i] & 0xffffffff);
    }
}

namespace DeskPilot.Core.Abstractions;

/// <summary>A point in physical pixels on the Windows virtual desktop.</summary>
public readonly record struct ScreenPoint(int X, int Y)
{
    public override string ToString() => $"({X}, {Y})";
}

/// <summary>A rectangle in physical pixels on the Windows virtual desktop.</summary>
public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public ScreenPoint Center => new(X + Width / 2, Y + Height / 2);

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
    public bool Contains(ScreenPoint p) => Contains(p.X, p.Y);

    public ScreenRect Intersect(ScreenRect other)
    {
        int x1 = Math.Max(X, other.X), y1 = Math.Max(Y, other.Y);
        int x2 = Math.Min(Right, other.Right), y2 = Math.Min(Bottom, other.Bottom);
        return x2 <= x1 || y2 <= y1 ? default : new ScreenRect(x1, y1, x2 - x1, y2 - y1);
    }

    public override string ToString() => $"[{X},{Y} {Width}x{Height}]";
}

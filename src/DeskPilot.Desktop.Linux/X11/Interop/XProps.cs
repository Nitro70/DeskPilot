using System.Text;

namespace DeskPilot.Desktop.Linux.X11.Interop;

/// <summary>Window property and atom helpers. Every method expects the caller to hold the connection's lock.</summary>
internal static unsafe class XProps
{
    /// <summary>A format-32 property (CARDINAL, WINDOW, ATOM...) as unsigned longs, or null when missing or of another type.</summary>
    public static nuint[]? GetLongs(nint display, nuint window, nuint property, nuint type, int maxItems = 8192)
    {
        nuint actualType, nitems, bytesAfter;
        int actualFormat;
        byte* data = null;
        int rc = Xlib.XGetWindowProperty(display, window, property, 0, maxItems, X.False, type,
            &actualType, &actualFormat, &nitems, &bytesAfter, &data);
        try
        {
            if (rc != X.Success || data == null || actualType == X.None || actualFormat != 32) return null;
            if (type != X.AnyPropertyType && actualType != type) return null;
            var result = new nuint[(int)nitems];
            var src = (nuint*)data;
            for (int i = 0; i < result.Length; i++) result[i] = src[i];
            return result;
        }
        finally
        {
            if (data != null) Xlib.XFree(data);
        }
    }

    /// <summary>The first item of a format-32 property, or null.</summary>
    public static nuint? GetLong(nint display, nuint window, nuint property, nuint type)
    {
        var values = GetLongs(display, window, property, type, 1);
        return values is { Length: > 0 } ? values[0] : null;
    }

    /// <summary>A format-8 property's raw bytes and its actual type, or null when missing.</summary>
    public static byte[]? GetBytes(nint display, nuint window, nuint property, nuint type, out nuint actualType, int maxBytes = 1 << 20)
    {
        nuint nitems, bytesAfter;
        int actualFormat;
        byte* data = null;
        nuint at;
        // long_length counts 32-bit units.
        int rc = Xlib.XGetWindowProperty(display, window, property, 0, (maxBytes + 3) / 4, X.False, type,
            &at, &actualFormat, &nitems, &bytesAfter, &data);
        actualType = at;
        try
        {
            if (rc != X.Success || data == null || at == X.None || actualFormat != 8) return null;
            if (type != X.AnyPropertyType && at != type) return null;
            return new ReadOnlySpan<byte>(data, (int)nitems).ToArray();
        }
        finally
        {
            if (data != null) Xlib.XFree(data);
        }
    }

    /// <summary>A text property decoded as UTF-8 (UTF8_STRING) or Latin-1 (STRING and anything else), or null.</summary>
    public static string? GetText(nint display, nuint window, nuint property, nuint type, nuint utf8Atom)
    {
        var bytes = GetBytes(display, window, property, type, out var actual);
        if (bytes == null) return null;
        int len = Array.IndexOf(bytes, (byte)0);
        if (len < 0) len = bytes.Length;
        return actual == utf8Atom || actual != X.XA_STRING
            ? Encoding.UTF8.GetString(bytes, 0, len)
            : Encoding.Latin1.GetString(bytes, 0, len);
    }

    public static nuint Intern(nint display, string name, bool onlyIfExists = false)
    {
        var bytes = Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* p = bytes) return Xlib.XInternAtom(display, p, onlyIfExists ? X.True : X.False);
    }

    public static string? AtomName(nint display, nuint atom)
    {
        if (atom == X.None) return null;
        var p = Xlib.XGetAtomName(display, atom);
        if (p == null) return null;
        try { return X11Native.FromCString(p); }
        finally { Xlib.XFree(p); }
    }
}

/// <summary>Interned atoms of one connection, re-interned when the connection is reopened.</summary>
internal sealed class XAtomCache
{
    private readonly Dictionary<string, nuint> _atoms = new(StringComparer.Ordinal);
    private int _generation = -1;

    public nuint Get(in XConnection.Lease lease, string name)
    {
        if (_generation != lease.Generation)
        {
            _atoms.Clear();
            _generation = lease.Generation;
        }
        if (!_atoms.TryGetValue(name, out var atom))
        {
            atom = XProps.Intern(lease.Display, name);
            _atoms[name] = atom;
        }
        return atom;
    }
}

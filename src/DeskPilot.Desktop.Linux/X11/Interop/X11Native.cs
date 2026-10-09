using System.Runtime.InteropServices;

namespace DeskPilot.Desktop.Linux.X11.Interop;

internal enum X11Library { X11, Xtst, Xrandr, Xfixes }

/// <summary>
/// Loads the X libraries on first use with a readable error when one is missing, and runs XInitThreads once
/// before DeskPilot makes any other Xlib call (each class then guards its own connection with a lock).
/// </summary>
internal static class X11Native
{
    private static readonly object Gate = new();
    private static readonly Dictionary<X11Library, bool> Loaded = new();
    private static bool _initialized;

    public static string FileName(X11Library library) => library switch
    {
        X11Library.X11 => Xlib.Lib,
        X11Library.Xtst => Xtst.Lib,
        X11Library.Xrandr => Xrandr.Lib,
        X11Library.Xfixes => Xfixes.Lib,
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    public static string MissingMessage(X11Library library) => library switch
    {
        X11Library.X11 => "libX11 is missing: install libx11-6 (Debian/Ubuntu) or libX11 (Fedora)",
        X11Library.Xtst => "libXtst is missing: install libxtst6 (Debian/Ubuntu) or libXtst (Fedora)",
        X11Library.Xrandr => "libXrandr is missing: install libxrandr2 (Debian/Ubuntu) or libXrandr (Fedora)",
        X11Library.Xfixes => "libXfixes is missing: install libxfixes3 (Debian/Ubuntu) or libXfixes (Fedora)",
        _ => throw new ArgumentOutOfRangeException(nameof(library)),
    };

    public static bool IsAvailable(X11Library library)
    {
        lock (Gate) return IsAvailableUnlocked(library);
    }

    public static void Require(X11Library library)
    {
        if (!IsAvailable(library)) throw new InvalidOperationException(MissingMessage(library));
    }

    /// <summary>Loads libX11, calls XInitThreads and installs the recording error handler. Safe to call often.</summary>
    public static void EnsureInitialized()
    {
        lock (Gate)
        {
            if (!_initialized)
            {
                if (!IsAvailableUnlocked(X11Library.X11)) throw new InvalidOperationException(MissingMessage(X11Library.X11));
                Xlib.XInitThreads();
                _initialized = true;
            }
        }
        XErrorTrap.Install();
    }

    private static bool IsAvailableUnlocked(X11Library library)
    {
        if (Loaded.TryGetValue(library, out var ok)) return ok;
        ok = OperatingSystem.IsLinux() && NativeLibrary.TryLoad(FileName(library), out _);
        Loaded[library] = ok;
        return ok;
    }

    /// <summary>A NUL-terminated C string (UTF-8) to a managed string; null for a null pointer.</summary>
    public static unsafe string? FromCString(byte* s) =>
        s == null ? null : Marshal.PtrToStringUTF8((nint)s);
}

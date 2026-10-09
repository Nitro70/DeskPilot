using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace DeskPilot.Desktop.Linux.X11.Interop;

/// <summary>An X protocol error recorded for one of DeskPilot's connections.</summary>
internal readonly record struct XErrorInfo(byte ErrorCode, byte RequestCode, byte MinorCode, nuint ResourceId, int Count)
{
    public bool IsBadAccess => ErrorCode == X.BadAccess;
    public override string ToString() => $"X error {ErrorCode} (request {RequestCode}.{MinorCode}, resource 0x{(ulong)ResourceId:x})";
}

/// <summary>
/// Xlib's default error handler prints and exits the process, for example on BadWindow when a window vanished
/// between listing and querying it. This handler records errors for DeskPilot's own connections instead and
/// passes errors of other connections (for example the UI toolkit's) on to the handler that was there before.
/// The handler is a static UnmanagedCallersOnly function, so there is no delegate that could be collected.
/// </summary>
internal static unsafe class XErrorTrap
{
    private sealed class Slot
    {
        public int Count;
        public XErrorInfo First;
    }

    private static readonly ConcurrentDictionary<nint, Slot> Displays = new();
    private static readonly ConcurrentDictionary<nint, bool> DeadDisplays = new();
    private static readonly object Gate = new();
    private static nint _previous;
    private static bool _ioExitHandlerChecked;
    private static nint _setIoErrorExitHandler;

    [ThreadStatic] private static bool _inHandler;

    /// <summary>Installs (or re-installs, when another library replaced it) the recording handler.</summary>
    public static void Install()
    {
        lock (Gate)
        {
            delegate* unmanaged[Cdecl]<nint, XErrorEvent*, int> mine = &OnError;
            nint previous = Xlib.XSetErrorHandler((nint)mine);
            if (previous != (nint)mine) _previous = previous;
        }
    }

    public static void Register(nint display)
    {
        Displays[display] = new Slot();
        DeadDisplays.TryRemove(display, out _);
        InstallIoExitHandler(display);
    }

    public static void Unregister(nint display)
    {
        Displays.TryRemove(display, out _);
        DeadDisplays.TryRemove(display, out _);
    }

    /// <summary>True once Xlib reported a fatal I/O error (the X server went away) on this connection.</summary>
    public static bool IsDead(nint display) => DeadDisplays.ContainsKey(display);

    /// <summary>Forgets errors recorded so far for the connection.</summary>
    public static void Reset(nint display)
    {
        if (Displays.TryGetValue(display, out var slot)) slot.Count = 0;
    }

    /// <summary>Waits for the server to process every request sent so far and returns the first error since the last reset.</summary>
    public static XErrorInfo? Sync(nint display)
    {
        Xlib.XSync(display, X.False);
        return Take(display);
    }

    /// <summary>The first error recorded since the last reset (without a round trip), and resets.</summary>
    public static XErrorInfo? Take(nint display)
    {
        if (!Displays.TryGetValue(display, out var slot) || slot.Count == 0) return null;
        var info = slot.First with { Count = slot.Count };
        slot.Count = 0;
        return info;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnError(nint display, XErrorEvent* ev)
    {
        // Never let an exception escape into native code.
        try
        {
            if (Displays.TryGetValue(display, out var slot))
            {
                if (slot.Count == 0)
                    slot.First = new XErrorInfo(ev->error_code, ev->request_code, ev->minor_code, ev->resourceid, 1);
                slot.Count++;
                return 0;
            }
            var previous = _previous;
            if (previous != 0 && !_inHandler)
            {
                _inHandler = true;
                try { return ((delegate* unmanaged[Cdecl]<nint, XErrorEvent*, int>)previous)(display, ev); }
                finally { _inHandler = false; }
            }
        }
        catch (Exception) { }
        return 0;
    }

    /// <summary>
    /// libX11 1.7+ lets a connection survive a fatal I/O error (the server went away) instead of exiting the
    /// process; the connection is then marked dead and reopened on next use. Older libX11 keeps its default.
    /// </summary>
    private static void InstallIoExitHandler(nint display)
    {
        lock (Gate)
        {
            if (!_ioExitHandlerChecked)
            {
                _ioExitHandlerChecked = true;
                if (NativeLibrary.TryLoad(Xlib.Lib, out var lib) && NativeLibrary.TryGetExport(lib, "XSetIOErrorExitHandler", out var fn))
                    _setIoErrorExitHandler = fn;
            }
        }
        if (_setIoErrorExitHandler == 0) return;
        delegate* unmanaged[Cdecl]<nint, nint, void> handler = &OnIoErrorExit;
        ((delegate* unmanaged[Cdecl]<nint, nint, nint, void>)_setIoErrorExitHandler)(display, (nint)handler, 0);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnIoErrorExit(nint display, nint userData)
    {
        try { DeadDisplays[display] = true; }
        catch (Exception) { }
    }
}

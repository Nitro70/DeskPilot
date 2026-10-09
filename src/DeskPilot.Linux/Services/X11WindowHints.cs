using System.Runtime.InteropServices;
using Avalonia.Controls;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Linux.Services;

/// <summary>
/// Marks an X11 window as one that never takes the keyboard focus (WM_HINTS input = False, the "No Input" model),
/// the X11 counterpart of WS_EX_NOACTIVATE: clicking the overlay's Stop button then leaves the focus where it was.
/// Avalonia's ShowActivated=False only covers showing the window, not clicking it. Under Wayland the overlay is an
/// Xwayland window, so the same hint applies. Best effort: anything unexpected leaves the window as it is.
/// </summary>
internal static class X11WindowHints
{
    private const string LibX11 = "libX11.so.6";
    private const long InputHint = 1L << 0;

    private static nint _display;
    private static bool _unavailable;

    [StructLayout(LayoutKind.Sequential)]
    private struct XWMHints
    {
        public nint Flags;          // long
        public int Input;           // Bool
        public int InitialState;
        public nint IconPixmap;
        public nint IconWindow;
        public int IconX;
        public int IconY;
        public nint IconMask;
        public nint WindowGroup;
    }

    [DllImport(LibX11)] private static extern nint XOpenDisplay(nint name);
    [DllImport(LibX11)] private static extern nint XGetWMHints(nint display, nint window);
    [DllImport(LibX11)] private static extern int XSetWMHints(nint display, nint window, ref XWMHints hints);
    [DllImport(LibX11)] private static extern int XFree(nint data);
    [DllImport(LibX11)] private static extern int XFlush(nint display);

    /// <summary>Sets input = False on the window's WM_HINTS (keeping its other hints). True when it was applied.</summary>
    public static bool TrySetNoInput(Window window)
    {
        if (!OperatingSystem.IsLinux() || _unavailable) return false;
        var handle = window.TryGetPlatformHandle();
        if (handle == null || handle.Handle == 0 || !string.Equals(handle.HandleDescriptor, "XID", StringComparison.Ordinal)) return false;
        try
        {
            if (_display == 0)
            {
                // Our own connection: the hint is a property on the server, so any client may set it.
                _display = XOpenDisplay(0);
                if (_display == 0)
                {
                    _unavailable = true;
                    return false;
                }
            }

            var hints = new XWMHints();
            var current = XGetWMHints(_display, handle.Handle);
            if (current != 0)
            {
                hints = Marshal.PtrToStructure<XWMHints>(current);
                XFree(current);
            }
            hints.Flags = (nint)((long)hints.Flags | InputHint);
            hints.Input = 0;
            XSetWMHints(_display, handle.Handle, ref hints);
            XFlush(_display);
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _unavailable = true;
            Log.Warn($"X11 window hints unavailable: {ex.Message}");
            return false;
        }
    }
}

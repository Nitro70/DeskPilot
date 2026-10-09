using System.Runtime.InteropServices;

namespace DeskPilot.Desktop.Linux.X11.Interop;

// Raw Xlib and extension entry points. Every signature uses blittable types only: Display* and other pointers
// are nint, XIDs (Window, Atom, KeySym, Time, Drawable) are nuint (C "unsigned long"), C "long" is nint.
// Nothing here is called before X11Native has loaded the library (with a readable error) and run XInitThreads.

internal static unsafe class Xlib
{
    public const string Lib = "libX11.so.6";

    [DllImport(Lib)] public static extern int XInitThreads();
    [DllImport(Lib)] public static extern nint XOpenDisplay(byte* displayName);
    [DllImport(Lib)] public static extern int XCloseDisplay(nint display);
    [DllImport(Lib)] public static extern byte* XDisplayString(nint display);
    [DllImport(Lib)] public static extern int XDefaultScreen(nint display);
    [DllImport(Lib)] public static extern nuint XRootWindow(nint display, int screen);
    [DllImport(Lib)] public static extern int XDisplayWidth(nint display, int screen);
    [DllImport(Lib)] public static extern int XDisplayHeight(nint display, int screen);
    [DllImport(Lib)] public static extern int XFlush(nint display);
    [DllImport(Lib)] public static extern int XSync(nint display, int discard);
    [DllImport(Lib)] public static extern int XFree(void* data);
    [DllImport(Lib)] public static extern nint XSetErrorHandler(nint handler);

    [DllImport(Lib)] public static extern nuint XInternAtom(nint display, byte* name, int onlyIfExists);
    [DllImport(Lib)] public static extern byte* XGetAtomName(nint display, nuint atom);

    [DllImport(Lib)]
    public static extern int XGetWindowProperty(nint display, nuint window, nuint property, nint longOffset, nint longLength,
        int delete, nuint reqType, nuint* actualType, int* actualFormat, nuint* nitems, nuint* bytesAfter, byte** prop);

    [DllImport(Lib)] public static extern int XGetWindowAttributes(nint display, nuint window, XWindowAttributes* attributes);
    [DllImport(Lib)]
    public static extern int XTranslateCoordinates(nint display, nuint srcWindow, nuint destWindow, int srcX, int srcY,
        int* destX, int* destY, nuint* child);
    [DllImport(Lib)]
    public static extern int XQueryTree(nint display, nuint window, nuint* root, nuint* parent, nuint** children, uint* nchildren);
    [DllImport(Lib)]
    public static extern int XQueryPointer(nint display, nuint window, nuint* root, nuint* child, int* rootX, int* rootY,
        int* winX, int* winY, uint* mask);
    [DllImport(Lib)]
    public static extern int XWarpPointer(nint display, nuint srcWindow, nuint destWindow, int srcX, int srcY, uint srcWidth,
        uint srcHeight, int destX, int destY);
    [DllImport(Lib)]
    public static extern XImage* XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);

    [DllImport(Lib)] public static extern int XSendEvent(nint display, nuint window, int propagate, nint eventMask, XEvent* ev);
    [DllImport(Lib)] public static extern int XRaiseWindow(nint display, nuint window);
    [DllImport(Lib)] public static extern int XMapRaised(nint display, nuint window);
    [DllImport(Lib)] public static extern int XSetInputFocus(nint display, nuint focus, int revertTo, nuint time);
    [DllImport(Lib)] public static extern int XGetInputFocus(nint display, nuint* focus, int* revertTo);

    [DllImport(Lib)] public static extern byte XKeysymToKeycode(nint display, nuint keysym);
    [DllImport(Lib)] public static extern int XDisplayKeycodes(nint display, int* minKeycode, int* maxKeycode);
    // first_keycode is a KeyCode (or an unsigned int with wide prototypes); a zero-extended uint suits both.
    [DllImport(Lib)] public static extern nuint* XGetKeyboardMapping(nint display, uint firstKeycode, int keycodeCount, int* keysymsPerKeycode);
    [DllImport(Lib)] public static extern int XChangeKeyboardMapping(nint display, int firstKeycode, int keysymsPerKeycode, nuint* keysyms, int numCodes);
    [DllImport(Lib)] public static extern XModifierKeymap* XGetModifierMapping(nint display);
    [DllImport(Lib)] public static extern int XFreeModifiermap(XModifierKeymap* map);
    [DllImport(Lib)] public static extern int XQueryKeymap(nint display, byte* keysReturn32);
    [DllImport(Lib)] public static extern int XGetPointerMapping(nint display, byte* map, int nmap);
    [DllImport(Lib)] public static extern int XkbGetState(nint display, uint deviceSpec, XkbStateRec* state);

    [DllImport(Lib)]
    public static extern int XGrabKey(nint display, int keycode, uint modifiers, nuint grabWindow, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(Lib)] public static extern int XUngrabKey(nint display, int keycode, uint modifiers, nuint grabWindow);
    [DllImport(Lib)] public static extern int XPending(nint display);
    [DllImport(Lib)] public static extern int XNextEvent(nint display, XEvent* ev);
}

internal static unsafe class Xtst
{
    public const string Lib = "libXtst.so.6";

    [DllImport(Lib)] public static extern int XTestQueryExtension(nint display, int* eventBase, int* errorBase, int* major, int* minor);
    [DllImport(Lib)] public static extern int XTestFakeKeyEvent(nint display, uint keycode, int isPress, nuint delay);
    [DllImport(Lib)] public static extern int XTestFakeButtonEvent(nint display, uint button, int isPress, nuint delay);
    [DllImport(Lib)] public static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
}

internal static unsafe class Xrandr
{
    public const string Lib = "libXrandr.so.2";

    [DllImport(Lib)] public static extern int XRRQueryExtension(nint display, int* eventBase, int* errorBase);
    [DllImport(Lib)] public static extern int XRRQueryVersion(nint display, int* major, int* minor);
    [DllImport(Lib)] public static extern XRRMonitorInfo* XRRGetMonitors(nint display, nuint window, int getActive, int* nmonitors);
    [DllImport(Lib)] public static extern void XRRFreeMonitors(XRRMonitorInfo* monitors);
}

internal static unsafe class Xfixes
{
    public const string Lib = "libXfixes.so.3";

    [DllImport(Lib)] public static extern int XFixesQueryExtension(nint display, int* eventBase, int* errorBase);
    [DllImport(Lib)] public static extern XFixesCursorImage* XFixesGetCursorImage(nint display);
}

internal static class X
{
    public const int Success = 0;
    public const int False = 0, True = 1;
    public const nuint None = 0;
    public const nuint CurrentTime = 0;
    public const nuint AnyPropertyType = 0;

    // Predefined atoms.
    public const nuint XA_ATOM = 4, XA_CARDINAL = 6, XA_STRING = 31, XA_WINDOW = 33, XA_WM_NAME = 39, XA_WM_CLASS = 67;

    public const int ZPixmap = 2;
    public const int LSBFirst = 0, MSBFirst = 1;
    public const int IsUnmapped = 0, IsUnviewable = 1, IsViewable = 2;
    public const int InputOutput = 1, InputOnly = 2;

    public const int KeyPress = 2, KeyRelease = 3, ClientMessage = 33, MappingNotify = 34;
    public const int GrabModeAsync = 1;
    public const int RevertToParent = 2;

    public const nint SubstructureNotifyMask = 1 << 19, SubstructureRedirectMask = 1 << 20;

    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod3Mask = 32, Mod4Mask = 64, Mod5Mask = 128;
    public const uint Button1Mask = 1 << 8, Button2Mask = 1 << 9, Button3Mask = 1 << 10, Button4Mask = 1 << 11, Button5Mask = 1 << 12;

    public const uint XkbUseCoreKbd = 0x0100;

    // Error codes.
    public const byte BadRequest = 1, BadValue = 2, BadWindow = 3, BadAtom = 5, BadMatch = 8, BadDrawable = 9, BadAccess = 10;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XImage
{
    public int width, height;
    public int xoffset;
    public int format;
    public byte* data;
    public int byte_order;
    public int bitmap_unit;
    public int bitmap_bit_order;
    public int bitmap_pad;
    public int depth;
    public int bytes_per_line;
    public int bits_per_pixel;
    public nuint red_mask;
    public nuint green_mask;
    public nuint blue_mask;
    public nint obdata;
    // struct funcs: create_image, destroy_image, get_pixel, put_pixel, sub_image, add_pixel.
    public nint create_image;
    public delegate* unmanaged[Cdecl]<XImage*, int> destroy_image;
    public nint get_pixel;
    public nint put_pixel;
    public nint sub_image;
    public nint add_pixel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XWindowAttributes
{
    public int x, y;
    public int width, height;
    public int border_width;
    public int depth;
    public nint visual;
    public nuint root;
    public int c_class;
    public int bit_gravity;
    public int win_gravity;
    public int backing_store;
    public nuint backing_planes;
    public nuint backing_pixel;
    public int save_under;
    public nuint colormap;
    public int map_installed;
    public int map_state;
    public nint all_event_masks;
    public nint your_event_mask;
    public nint do_not_propagate_mask;
    public int override_redirect;
    public nint screen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XErrorEvent
{
    public int type;
    public nint display;
    public nuint resourceid;
    public nuint serial;
    public byte error_code;
    public byte request_code;
    public byte minor_code;
}

/// <summary>The XEvent union: 24 longs.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XEvent
{
    public fixed long pad[24];

    public int Type
    {
        get { fixed (long* p = pad) return *(int*)p; }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct XKeyEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public nuint root;
    public nuint subwindow;
    public nuint time;
    public int x, y;
    public int x_root, y_root;
    public uint state;
    public uint keycode;
    public int same_screen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XClientMessageEvent
{
    public int type;
    public nuint serial;
    public int send_event;
    public nint display;
    public nuint window;
    public nuint message_type;
    public int format;
    public nint l0, l1, l2, l3, l4;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XModifierKeymap
{
    public int max_keypermod;
    public byte* modifiermap;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XkbStateRec
{
    public byte group;
    public byte locked_group;
    public ushort base_group;
    public ushort latched_group;
    public byte mods;
    public byte base_mods;
    public byte latched_mods;
    public byte locked_mods;
    public byte compat_state;
    public byte grab_mods;
    public byte compat_grab_mods;
    public byte lookup_mods;
    public byte compat_lookup_mods;
    public ushort ptr_buttons;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XRRMonitorInfo
{
    public nuint name;
    public int primary;
    public int automatic;
    public int noutput;
    public int x;
    public int y;
    public int width;
    public int height;
    public int mwidth;
    public int mheight;
    public nuint* outputs;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct XFixesCursorImage
{
    public short x, y;
    public ushort width, height;
    public ushort xhot, yhot;
    public nuint cursor_serial;
    public nuint* pixels;
    public nuint atom;
    public byte* name;
}

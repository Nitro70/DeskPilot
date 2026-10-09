using System.Runtime.InteropServices;

namespace DeskPilot.Desktop.Windows;

/// <summary>Win32 declarations used by the desktop layer. Kept in one place so struct layouts are easy to audit.</summary>
internal static unsafe class NativeMethods
{
    // ---- DPI ----
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE = -3;
    public static readonly nint DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4;

    [DllImport("user32.dll")]
    public static extern nint SetThreadDpiAwarenessContext(nint dpiContext);

    [DllImport("user32.dll")]
    public static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    public static extern bool AreDpiAwarenessContextsEqual(nint a, nint b);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    public const int MDT_EFFECTIVE_DPI = 0;

    // ---- Monitors / metrics ----
    public delegate bool MonitorEnumProc(nint hMonitor, nint hdc, nint lprcMonitor, nint data);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(nint hdc, nint lprcClip, MonitorEnumProc lpfnEnum, nint dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFOEX lpmi);

    public const uint MONITORINFOF_PRIMARY = 1;

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int nIndex);

    public const int SM_XVIRTUALSCREEN = 76;
    public const int SM_YVIRTUALSCREEN = 77;
    public const int SM_CXVIRTUALSCREEN = 78;
    public const int SM_CYVIRTUALSCREEN = 79;
    public const int SM_SWAPBUTTON = 23;

    // ---- GDI capture ----
    [DllImport("user32.dll")]
    public static extern nint GetDC(nint hWnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(nint hWnd, nint hDC);

    [DllImport("gdi32.dll")]
    public static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(nint hdc);

    [DllImport("gdi32.dll")]
    public static extern nint CreateDIBSection(nint hdc, ref BITMAPINFOHEADER pbmi, uint usage, out nint ppvBits, nint hSection, uint offset);

    [DllImport("gdi32.dll")]
    public static extern nint SelectObject(nint hdc, nint h);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(nint ho);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern bool BitBlt(nint hdc, int x, int y, int cx, int cy, nint hdcSrc, int x1, int y1, uint rop);

    [DllImport("gdi32.dll")]
    public static extern bool GdiFlush();

    public const uint SRCCOPY = 0x00CC0020;
    public const uint CAPTUREBLT = 0x40000000;
    public const uint DIB_RGB_COLORS = 0;
    public const uint BI_RGB = 0;

    [DllImport("user32.dll")]
    public static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    public static extern bool GetIconInfo(nint hIcon, out ICONINFO piconinfo);

    [DllImport("user32.dll")]
    public static extern bool DrawIconEx(nint hdc, int xLeft, int yTop, nint hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, nint hbrFlickerFreeDraw, uint diFlags);

    public const int CURSOR_SHOWING = 0x00000001;
    public const uint DI_NORMAL = 0x0003;

    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(nint hProcess, uint uiFlags);

    public const uint GR_GDIOBJECTS = 0;
    public const uint GR_USEROBJECTS = 1;

    // ---- Input ----
    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint SendInput(uint cInputs, [In] INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "VkKeyScanExW")]
    public static extern short VkKeyScanEx(char ch, nint dwhkl);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW")]
    public static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, nint dwhkl);

    [DllImport("user32.dll")]
    public static extern nint GetKeyboardLayout(uint idThread);

    public const uint MAPVK_VK_TO_VSC = 0;

    public const uint INPUT_MOUSE = 0;
    public const uint INPUT_KEYBOARD = 1;

    public const uint MOUSEEVENTF_MOVE = 0x0001;
    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    public const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    public const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    public const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
    public const uint MOUSEEVENTF_WHEEL = 0x0800;
    public const uint MOUSEEVENTF_HWHEEL = 0x1000;
    public const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
    public const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    public const uint KEYEVENTF_KEYUP = 0x0002;
    public const uint KEYEVENTF_UNICODE = 0x0004;

    public const int WHEEL_DELTA = 120;

    // ---- Windows ----
    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")]
    public static extern int GetWindowTextLength(nint hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(nint hWnd, char* lpString, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static extern int GetClassName(nint hWnd, char* lpClassName, int nMaxCount);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_APPWINDOW = 0x00040000;

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(nint hWnd, int nCmdShow);

    public const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern void SwitchToThisWindow(nint hWnd, bool fUnknown);

    [DllImport("user32.dll")]
    public static extern nint WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    public static extern nint GetAncestor(nint hwnd, uint gaFlags);

    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "PeekMessageW")]
    public static extern bool PeekMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    public const uint PM_NOREMOVE = 0;

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(nint hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_CLOAKED = 14;

    // ---- Processes / tokens ----
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    public static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    public static extern nint GetCurrentProcess();

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(nint processHandle, uint desiredAccess, out nint tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool GetTokenInformation(nint tokenHandle, int tokenInformationClass, out int tokenInformation, int tokenInformationLength, out int returnLength);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint TOKEN_QUERY = 0x0008;
    public const int TokenElevation = 20;
    public const int ERROR_ACCESS_DENIED = 5;

    // ---- Structs ----
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly DeskPilot.Core.Abstractions.ScreenRect ToScreenRect() => new(Left, Top, Right - Left, Bottom - Top);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public nint hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public int fIcon;
        public int xHotspot;
        public int yHotspot;
        public nint hbmMask;
        public nint hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    // INPUT must be 40 bytes on x64 (4-byte type, 4 bytes padding, 32-byte union) or SendInput rejects it.
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }
}

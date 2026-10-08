using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeskPilot.Services;

/// <summary>Small Win32 helpers shared by every DeskPilot window.</summary>
public static class NativeUi
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const uint WDA_NONE = 0x0;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);

    /// <summary>Dark title bar on Windows 10 2004+ / 11. Call from SourceInitialized or later.</summary>
    public static void ApplyDarkTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        int on = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    /// <summary>
    /// Hides the window from screenshots and screen recording (Windows 10 2004+), so the agent never
    /// sees DeskPilot's own UI. The user still sees it normally. Call from SourceInitialized or later.
    /// </summary>
    public static bool ExcludeFromCapture(Window window, bool exclude = true)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return false;
        return SetWindowDisplayAffinity(hwnd, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    /// <summary>Applies both helpers once the window has a handle.</summary>
    public static void Prepare(Window window, bool excludeFromCapture = true)
    {
        void Apply()
        {
            ApplyDarkTitleBar(window);
            if (excludeFromCapture) ExcludeFromCapture(window);
        }

        if (new WindowInteropHelper(window).Handle != 0) Apply();
        else window.SourceInitialized += (_, _) => Apply();
    }
}

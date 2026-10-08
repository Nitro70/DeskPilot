using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;
using static DeskPilot.Core.Desktop.NativeMethods;

namespace DeskPilot.Core.Desktop;

/// <summary>
/// The thin layer between <see cref="WindowsInputSimulator"/> and Win32. Tests substitute a recording fake
/// so the simulator's sequencing logic runs without touching the real mouse or keyboard.
/// </summary>
internal interface IInputBackend
{
    /// <summary>Sends the inputs; returns how many the system accepted.</summary>
    int Send(INPUT[] inputs);
    ScreenPoint GetCursorPos();
    bool SetCursorPos(int x, int y);
    ScreenRect GetVirtualScreen();
    int DoubleClickTimeMs { get; }
    bool ButtonsSwapped { get; }
    /// <summary>VkKeyScanEx on the keyboard layout of the window that will receive the keys.</summary>
    short VkKeyScan(char c);
    ushort ScanCode(ushort vk);
    void Sleep(int ms);
    /// <summary>Puts the calling thread in physical-pixel (Per-Monitor-V2) coordinates until disposed.</summary>
    IDisposable EnterDpiScope();
}

internal sealed class Win32InputBackend : IInputBackend
{
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();

    public int Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return 0;
        return (int)SendInput((uint)inputs.Length, inputs, InputSize);
    }

    public ScreenPoint GetCursorPos() =>
        NativeMethods.GetCursorPos(out var p) ? new ScreenPoint(p.X, p.Y) : default;

    public bool SetCursorPos(int x, int y) => NativeMethods.SetCursorPos(x, y);

    public ScreenRect GetVirtualScreen() => new(
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public int DoubleClickTimeMs
    {
        get
        {
            var t = (int)GetDoubleClickTime();
            return t > 0 ? t : 500;
        }
    }

    public bool ButtonsSwapped => GetSystemMetrics(SM_SWAPBUTTON) != 0;

    public short VkKeyScan(char c) => VkKeyScanEx(c, TargetLayout());

    public ushort ScanCode(ushort vk) => (ushort)(MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC, TargetLayout()) & 0xFF);

    public void Sleep(int ms)
    {
        if (ms > 0) Thread.Sleep(ms);
    }

    public IDisposable EnterDpiScope() => DpiScope.PerMonitorV2();

    private static nint TargetLayout()
    {
        // Keys are interpreted by the foreground window's thread, with that thread's layout.
        var fg = GetForegroundWindow();
        if (fg != 0)
        {
            var thread = GetWindowThreadProcessId(fg, out _);
            if (thread != 0)
            {
                var hkl = GetKeyboardLayout(thread);
                if (hkl != 0) return hkl;
            }
        }
        return GetKeyboardLayout(0);
    }
}

using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.X11;

// STUB: owned by the X11 agent.
public sealed class X11WindowManager : IWindowManager
{
    public IReadOnlyList<WindowInfo> ListWindows() => throw new NotImplementedException("STUB");
    public WindowInfo? GetForegroundWindow() => throw new NotImplementedException("STUB");
    public WindowInfo? GetWindowAt(int x, int y) => throw new NotImplementedException("STUB");
    public bool FocusWindow(nint handle) => throw new NotImplementedException("STUB");
    public bool IsCurrentProcessElevated => throw new NotImplementedException("STUB");
    public bool IsUacPromptActive() => throw new NotImplementedException("STUB");
}

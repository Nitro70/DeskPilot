using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

// STUB: owned by the Wayland/session agent.
public sealed class WaylandWindowManager : IWindowManager
{
    public WaylandWindowManager(LinuxSessionInfo session) { }

    public IReadOnlyList<WindowInfo> ListWindows() => throw new NotImplementedException("STUB");
    public WindowInfo? GetForegroundWindow() => throw new NotImplementedException("STUB");
    public WindowInfo? GetWindowAt(int x, int y) => throw new NotImplementedException("STUB");
    public bool FocusWindow(nint handle) => throw new NotImplementedException("STUB");
    public bool IsCurrentProcessElevated => throw new NotImplementedException("STUB");
    public bool IsUacPromptActive() => throw new NotImplementedException("STUB");
}

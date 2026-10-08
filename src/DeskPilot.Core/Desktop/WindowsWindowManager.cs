using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class WindowsWindowManager : IWindowManager
{
    public IReadOnlyList<WindowInfo> ListWindows() => throw new NotImplementedException("STUB");
    public WindowInfo? GetForegroundWindow() => throw new NotImplementedException("STUB");
    public WindowInfo? GetWindowAt(int x, int y) => throw new NotImplementedException("STUB");
    public bool FocusWindow(nint handle) => throw new NotImplementedException("STUB");
    public bool IsCurrentProcessElevated => throw new NotImplementedException("STUB");
    public bool IsUacPromptActive() => throw new NotImplementedException("STUB");
}

using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class WindowsScreenCapture : IScreenCapture
{
    public IReadOnlyList<MonitorInfo> GetMonitors() => throw new NotImplementedException("STUB");
    public ScreenRect GetVirtualScreen() => throw new NotImplementedException("STUB");
    public CapturedFrame Capture(CaptureRequest request) => throw new NotImplementedException("STUB");
}

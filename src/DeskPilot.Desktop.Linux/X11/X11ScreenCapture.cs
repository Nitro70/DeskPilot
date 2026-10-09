using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.X11;

// STUB: owned by the X11 agent.
public sealed class X11ScreenCapture : IScreenCapture
{
    public IReadOnlyList<MonitorInfo> GetMonitors() => throw new NotImplementedException("STUB");
    public ScreenRect GetVirtualScreen() => throw new NotImplementedException("STUB");
    public CapturedFrame Capture(CaptureRequest request) => throw new NotImplementedException("STUB");
}

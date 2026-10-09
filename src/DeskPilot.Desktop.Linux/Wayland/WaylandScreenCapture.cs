using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

// STUB: owned by the Wayland/session agent.
public sealed class WaylandScreenCapture : IScreenCapture
{
    public WaylandScreenCapture(LinuxSessionInfo session) { }

    public IReadOnlyList<MonitorInfo> GetMonitors() => throw new NotImplementedException("STUB");
    public ScreenRect GetVirtualScreen() => throw new NotImplementedException("STUB");
    public CapturedFrame Capture(CaptureRequest request) => throw new NotImplementedException("STUB");
}

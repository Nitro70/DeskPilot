using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux;

// STUB: owned by the Wayland/session agent.
public static class LinuxDesktopFactory
{
    /// <summary>
    /// The desktop services for the current session: X11 implementations under X11, Wayland implementations
    /// under Wayland, plus the session-independent services (AT-SPI, launcher, clipboard, shell).
    /// </summary>
    public static DesktopServices CreateDefault() => CreateFor(LinuxSession.Detect());

    public static DesktopServices CreateFor(LinuxSessionInfo session) => throw new NotImplementedException("STUB");

    /// <summary>Human-readable notes about missing helper tools for this session (shown in the UI), empty when all is well.</summary>
    public static IReadOnlyList<string> DescribeMissingTools(LinuxSessionInfo session) => throw new NotImplementedException("STUB");
}

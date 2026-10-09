using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

// STUB: owned by the Linux services agent.
public sealed class LinuxAppLauncher : IAppLauncher
{
    public LaunchResult Launch(string target, string? arguments, bool allowElevation) => throw new NotImplementedException("STUB");
}

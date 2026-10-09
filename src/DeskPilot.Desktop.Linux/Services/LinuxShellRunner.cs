using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

// STUB: owned by the Linux services agent.
public sealed class LinuxShellRunner : IShellRunner
{
    public Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct) => throw new NotImplementedException("STUB");
}

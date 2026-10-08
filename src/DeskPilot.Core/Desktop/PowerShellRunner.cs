using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class PowerShellRunner : IShellRunner
{
    public Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct) => throw new NotImplementedException("STUB");
}

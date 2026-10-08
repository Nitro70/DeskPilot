using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Cli;

// STUB: owned by the Claude CLI module agent.
public static class ClaudeCliProbe
{
    /// <summary>Finds the claude CLI and reports its version and login state (`claude auth status`). Never throws.</summary>
    public static Task<CliToolStatus> ProbeAsync(string? configuredPath, CancellationToken ct) => throw new NotImplementedException("STUB");
}

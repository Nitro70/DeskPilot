using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Agent;

// STUB: owned by the Session module agent.
public sealed class EnvironmentDetector : IEnvironmentDetector
{
    public EnvironmentDetector(IScreenCapture? screen = null, IWindowManager? windows = null, HttpClient? http = null) { }
    public Task<EnvironmentReport> DetectAsync(CancellationToken ct) => throw new NotImplementedException("STUB");
}

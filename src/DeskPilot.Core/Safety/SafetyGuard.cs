using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Safety;

// STUB: owned by the Tools/Safety module agent.
public sealed class SafetyGuard : ISafetyGuard
{
    public SafetyGuard(Func<AppSettings> settings, IWindowManager windows, IUiInspector? ui = null) { }
    public Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct) => throw new NotImplementedException("STUB");
}

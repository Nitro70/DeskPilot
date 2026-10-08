using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Tools;

// STUB: owned by the Tools/Safety module agent.
public sealed class ComputerToolHost : IToolHost
{
    public ComputerToolHost(DesktopServices desktop, ISafetyGuard safety, Func<AppSettings> settings, AgentRunControl control,
        IUserConfirmation? confirmation = null, IInputActionObserver? observer = null) { }

    /// <summary>The coordinate mapping screenshots currently use (depends on monitor selection and size limits).</summary>
    public CoordinateMapper CurrentMapper() => throw new NotImplementedException("STUB");

    public IReadOnlyList<ToolSpec> GetTools() => throw new NotImplementedException("STUB");
    public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) => throw new NotImplementedException("STUB");
}

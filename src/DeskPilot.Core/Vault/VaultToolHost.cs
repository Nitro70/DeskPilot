using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Vault;

// STUB: owned by the Vault module agent.
public sealed class VaultToolHost : IToolHost
{
    public VaultToolHost(Func<AppSettings> settings) { }
    /// <summary>True when a vault folder is configured, enabled and exists.</summary>
    public bool IsAvailable => throw new NotImplementedException("STUB");
    public IReadOnlyList<ToolSpec> GetTools() => throw new NotImplementedException("STUB");
    public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) => throw new NotImplementedException("STUB");
}

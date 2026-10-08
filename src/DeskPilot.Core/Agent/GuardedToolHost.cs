using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Core.Agent;

/// <summary>
/// Keeps one broken tool host from taking the whole tool list down: CompositeToolHost calls GetTools()
/// on every host for every call, so a host that throws there would break every tool and the prompt.
/// </summary>
internal sealed class GuardedToolHost : IToolHost
{
    private readonly IToolHost _inner;
    private readonly string _label;
    private int _reported;

    public GuardedToolHost(IToolHost inner, string label)
    {
        _inner = inner;
        _label = label;
    }

    public IReadOnlyList<ToolSpec> GetTools()
    {
        try
        {
            return _inner.GetTools() ?? Array.Empty<ToolSpec>();
        }
        catch (Exception ex)
        {
            // GetTools runs for every tool call; log the first failure only.
            if (Interlocked.Exchange(ref _reported, 1) == 0) Log.Error($"{_label} tools are unavailable", ex);
            return Array.Empty<ToolSpec>();
        }
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        try
        {
            return await _inner.ExecuteAsync(name, arguments, ct).ConfigureAwait(false)
                ?? ToolResult.Error($"Tool '{name}' returned no result.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"{_label} tool '{name}' failed", ex);
            return ToolResult.Error($"Tool '{name}' failed: {ex.GetType().Name}: {ex.Message}");
        }
    }
}

/// <summary>Stands in for a tool host that could not be created.</summary>
internal sealed class UnavailableToolHost : IToolHost
{
    private readonly string _reason;

    public UnavailableToolHost(string reason) => _reason = reason;

    public IReadOnlyList<ToolSpec> GetTools() => Array.Empty<ToolSpec>();

    public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) =>
        Task.FromResult(ToolResult.Error($"Tool '{name}' is unavailable: {_reason}"));
}

/// <summary>Used only if the real safety guard cannot be created: computer actions must never run unguarded.</summary>
internal sealed class DenyAllSafetyGuard : ISafetyGuard
{
    private readonly string _reason;

    public DenyAllSafetyGuard(string reason) => _reason = reason;

    public Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct) =>
        Task.FromResult(SafetyVerdict.Denied(_reason));
}

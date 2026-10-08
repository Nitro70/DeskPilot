using System.Diagnostics;
using System.Text.Json;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Runtime;

/// <summary>Merges several tool hosts. The first host that lists a tool name owns it.</summary>
public sealed class CompositeToolHost : IToolHost
{
    private readonly IToolHost[] _hosts;

    public CompositeToolHost(params IToolHost[] hosts) => _hosts = hosts;

    public IReadOnlyList<ToolSpec> GetTools()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<ToolSpec>();
        foreach (var h in _hosts)
            foreach (var t in h.GetTools())
                if (seen.Add(t.Name)) list.Add(t);
        return list;
    }

    public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        foreach (var h in _hosts)
            if (h.GetTools().Any(t => t.Name == name))
                return h.ExecuteAsync(name, arguments, ct);
        return Task.FromResult(ToolResult.Error($"Unknown tool '{name}'. Available tools: {string.Join(", ", GetTools().Select(t => t.Name))}"));
    }
}

/// <summary>
/// Wraps the real tools: enforces the stop signal and the per-turn step limit, reports every call and
/// result as events, and turns unexpected exceptions into error results. Both the MCP server (CLI/ACP
/// backends) and the HTTP agent loop execute tools through this wrapper, so the UI sees the same log.
/// </summary>
public sealed class ObservedToolHost : IToolHost
{
    private readonly IToolHost _inner;
    private readonly AgentRunControl _control;
    private readonly Func<int> _maxSteps;
    private readonly Action<AgentEvent> _emit;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private int _callCounter;

    public ObservedToolHost(IToolHost inner, AgentRunControl control, Func<int> maxSteps, Action<AgentEvent> emit)
    {
        _inner = inner;
        _control = control;
        _maxSteps = maxSteps;
        _emit = emit;
    }

    public IReadOnlyList<ToolSpec> GetTools() => _inner.GetTools();

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        var callId = "call" + Interlocked.Increment(ref _callCounter);
        var argsJson = arguments.ValueKind == JsonValueKind.Undefined ? "{}" : arguments.GetRawText();
        _emit(new ToolCallEvent(callId, name, argsJson, ToolSummaries.Describe(name, arguments)));

        var sw = Stopwatch.StartNew();
        ToolResult result;
        if (_control.IsStopRequested)
        {
            result = ToolResult.Error($"STOPPED: {_control.StopReason ?? "the user stopped the task"}. Do not call any more tools. End your turn now with a one-line summary of where you got to.");
        }
        else
        {
            var step = _control.RegisterStep();
            var max = _maxSteps();
            if (step > max + 3)
            {
                _control.RequestStop($"Step limit ({max}) reached");
                result = ToolResult.Error($"STOPPED: step limit of {max} actions reached. End your turn now and tell the user what is left.");
            }
            else if (step > max)
            {
                result = ToolResult.Error($"Step limit of {max} actions reached for this request. Do not call more tools; end your turn and summarize what is done and what is left.");
            }
            else
            {
                // Desktop actions must never interleave, even if a model issues parallel tool calls.
                await _serial.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _control.Token);
                    result = await _inner.ExecuteAsync(name, arguments, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_control.IsStopRequested && !ct.IsCancellationRequested)
                {
                    result = ToolResult.Error($"STOPPED: {_control.StopReason ?? "the user stopped the task"}. End your turn now.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    result = ToolResult.Error($"Tool '{name}' failed: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    _serial.Release();
                }
            }
        }

        sw.Stop();
        _emit(new ToolResultEvent(callId, name, result.IsError, result.Text, result.Images.Count > 0 ? result.Images[^1] : null, sw.Elapsed));
        return result;
    }
}

/// <summary>Short human-readable descriptions of tool calls for the log.</summary>
public static class ToolSummaries
{
    public static string Describe(string name, JsonElement args)
    {
        try
        {
            string S(string p) => args.ValueKind == JsonValueKind.Object && args.TryGetProperty(p, out var v)
                ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText())
                : "";
            static string Clip(string s, int n = 60) => s.Length <= n ? s : s[..n] + "…";

            return name switch
            {
                "screenshot" => "Take a screenshot",
                "zoom" => $"Zoom into ({S("x")}, {S("y")}) {S("width")}x{S("height")}",
                "click" => $"{(S("button") is { Length: > 0 } b ? b : "left")} click{(S("clicks") is "2" ? " x2" : S("clicks") is "3" ? " x3" : "")} at ({S("x")}, {S("y")})",
                "move_mouse" => $"Move mouse to ({S("x")}, {S("y")})",
                "drag" => $"Drag ({S("from_x")}, {S("from_y")}) → ({S("to_x")}, {S("to_y")})",
                "scroll" => $"Scroll {S("direction")} {S("amount")}",
                "type_text" => $"Type \"{Clip(S("text"))}\"",
                "press_keys" => $"Press {S("keys")}",
                "wait" => $"Wait {S("seconds")}s",
                "list_windows" => "List windows",
                "focus_window" => $"Focus window \"{S("title")}\"",
                "launch" => $"Open {Clip(S("target"))}",
                "ui_elements" => "Read UI elements",
                "get_clipboard" => "Read clipboard",
                "set_clipboard" => "Set clipboard",
                "run_command" => $"Run: {Clip(S("command"))}",
                "vault_search" => $"Search vault: \"{Clip(S("query"))}\"",
                "vault_read" => $"Read note {Clip(S("path"))}",
                "vault_list" => $"List vault {Clip(S("folder"))}",
                "vault_append" => $"Write to note {Clip(S("path"))}",
                _ => $"{name} {Clip(args.ValueKind == JsonValueKind.Undefined ? "" : args.GetRawText(), 80)}",
            };
        }
        catch (Exception)
        {
            return name;
        }
    }
}

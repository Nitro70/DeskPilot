using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Cli;

/// <summary>
/// Runs the Claude Code CLI (`claude -p` in stream-json mode) as a long-lived process that keeps the
/// conversation between turns. The CLI runs the agent loop itself and reaches DeskPilot's tools through
/// the MCP bridge, so tool calls are reported by ObservedToolHost, not here.
/// </summary>
public sealed class ClaudeCliBackend : IAgentBackend
{
    private const string RestartedMessage = "Claude Code was restarted, so the conversation context was reset.";

    private readonly Func<string?, string?> _locate;
    private readonly Func<ClaudeLaunchSpec, Action<string>, IClaudeProcess> _startProcess;
    private readonly string? _tempDirectory;
    private readonly SemaphoreSlim _turnGate = new(1, 1);
    private readonly object _gate = new();

    private AgentBackendContext? _context;
    private string? _exePath;
    private List<string>? _args;
    private string? _mcpServerName;
    private string? _promptFile;
    private string? _mcpConfigFile;
    private Slot? _slot;
    private volatile string? _model;
    private int _requestCounter;
    private int _disposed;

    public ClaudeCliBackend()
        : this(ClaudeCliCommand.ResolveExecutable, (spec, onLine) => ClaudeCliProcess.Start(spec, onLine))
    {
    }

    /// <param name="locateExecutable">Configured CLI path -> resolved executable, or null when not installed.</param>
    /// <param name="startProcess">Starts one CLI process; lines from its stdout go to the callback.</param>
    /// <param name="tempDirectory">Where the prompt and MCP config files go (default AppPaths.TempDirectory).</param>
    internal ClaudeCliBackend(Func<string?, string?> locateExecutable, Func<ClaudeLaunchSpec, Action<string>, IClaudeProcess> startProcess, string? tempDirectory = null)
    {
        _locate = locateExecutable;
        _startProcess = startProcess;
        _tempDirectory = tempDirectory;
    }

    /// <summary>How long a turn may take to end after the interrupt request before the process is killed.</summary>
    internal TimeSpan InterruptGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How long a stopping process may take to exit after stdin closes before it is killed.</summary>
    internal TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(2);

    internal string? SystemPromptFile => _promptFile;
    internal string? McpConfigFile => _mcpConfigFile;
    internal IReadOnlyList<string>? Arguments => _args;
    internal string? ExecutablePath => _exePath;

    public bool UsesMcp => true;

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    public Task StartAsync(AgentBackendContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_context != null) throw new InvalidOperationException("The Claude Code backend was already started.");
        }

        var exe = _locate(context.Profile.CliPath) ?? throw new InvalidOperationException(ClaudeCliCommand.NotFoundMessage);

        var tempDir = _tempDirectory ?? AppPaths.TempDirectory;
        Directory.CreateDirectory(tempDir);
        var id = Guid.NewGuid().ToString("N");
        var promptFile = Path.Combine(tempDir, $"claude-system-prompt-{id}.md");
        var mcpFile = context.Mcp != null ? Path.Combine(tempDir, $"claude-mcp-{id}.json") : null;

        try
        {
            File.WriteAllText(promptFile, context.SystemPrompt ?? "", ClaudeCliCommand.FileEncoding);
            if (context.Mcp != null)
                File.WriteAllText(mcpFile!, ClaudeCliCommand.BuildMcpConfigJson(context.Mcp), ClaudeCliCommand.FileEncoding);

            lock (_gate)
            {
                _context = context;
                _exePath = exe;
                _promptFile = promptFile;
                _mcpConfigFile = mcpFile;
                _mcpServerName = context.Mcp?.ServerName;
                _args = ClaudeCliCommand.BuildArguments(context.Profile, promptFile, mcpFile, context.Mcp?.ServerName);
                _slot = StartSlot();
            }
            Log.Info($"Claude CLI backend started with model {ClaudeCliCommand.ModelOrDefault(context.Profile.Model)}" +
                     (context.Mcp == null ? " (no MCP server)" : ""));
        }
        catch
        {
            lock (_gate)
            {
                _context = null;
                _slot = null;
                _promptFile = null;
                _mcpConfigFile = null;
            }
            TryDelete(promptFile);
            TryDelete(mcpFile);
            throw;
        }
        return Task.CompletedTask;
    }

    public async Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var ctx = _context ?? throw new InvalidOperationException("Call StartAsync before RunTurnAsync.");
        var sw = Stopwatch.StartNew();
        var control = ctx.Control;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct, control.Token);

        try
        {
            await _turnGate.WaitAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Cancelled(null, null, sw, ct);
        }

        try
        {
            if (IsDisposed) return Failed("The Claude Code backend was closed.", sw);
            if (cancel.IsCancellationRequested) return Cancelled(null, null, sw, ct);

            Slot slot;
            try
            {
                slot = await EnsureProcessAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                Log.Error("Could not start the Claude CLI", ex);
                return Failed(ex.Message, sw);
            }

            var state = new TurnState();
            slot.Turn = state;
            try
            {
                return await RunOnSlotAsync(slot, state, turn, cancel.Token, ct, sw).ConfigureAwait(false);
            }
            finally
            {
                slot.Turn = null;
                state.Dispose();
            }
        }
        finally
        {
            _turnGate.Release();
        }
    }

    private async Task<TurnResult> RunOnSlotAsync(Slot slot, TurnState state, UserTurn turn, CancellationToken cancel, CancellationToken callerCt, Stopwatch sw)
    {
        try
        {
            await slot.Process.WriteLineAsync(ClaudeCliCommand.BuildUserMessage(turn), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone; the wait below sees Exited and reports stderr.
            Log.Warn($"Could not send the message to Claude Code: {ex.Message}");
        }

        var stop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancel.Register(() => stop.TrySetResult()))
        using (state.Interrupt.Token.Register(() => stop.TrySetResult()))
        {
            await Task.WhenAny(state.Result.Task, slot.Process.Exited, stop.Task).ConfigureAwait(false);
        }

        // The reader delivers the result line before it reports end of output, so check the result first.
        if (state.Result.Task.IsCompleted) return MapResult(state, state.Result.Task.Result, sw, callerCt);
        if (slot.Process.Exited.IsCompleted) return ExitedResult(slot, state, sw, callerCt);

        await SendInterruptAsync(slot, state).ConfigureAwait(false);
        await Task.WhenAny(state.Result.Task, slot.Process.Exited, Task.Delay(InterruptGrace)).ConfigureAwait(false);

        if (state.Result.Task.IsCompleted)
        {
            var r = state.Result.Task.Result;
            return Cancelled(NullIfBlank(r.Text), r, sw, callerCt);
        }

        slot.Dead = true;
        if (!slot.Process.Exited.IsCompleted)
        {
            Log.Warn("Claude Code did not end the turn after an interrupt; killing it (it restarts on the next turn).");
            slot.Process.Kill();
        }
        return Cancelled(null, null, sw, callerCt);
    }

    public async Task InterruptAsync()
    {
        Slot? slot;
        lock (_gate) slot = _slot;
        var state = slot?.Turn;
        if (slot == null || state == null) return;
        await SendInterruptAsync(slot, state).ConfigureAwait(false);
        try { state.Interrupt.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task ResetConversationAsync(CancellationToken ct)
    {
        Slot? slot;
        lock (_gate)
        {
            slot = _slot;
            _slot = null;
        }
        if (slot == null) return;
        slot.StopRequested = true;
        await StopSlotAsync(slot).ConfigureAwait(false);
        Log.Info("Claude CLI conversation reset");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        Slot? slot;
        lock (_gate)
        {
            slot = _slot;
            _slot = null;
        }
        if (slot != null)
        {
            slot.StopRequested = true;
            await StopSlotAsync(slot).ConfigureAwait(false);
        }
        TryDelete(_promptFile);
        TryDelete(_mcpConfigFile);
    }

    // ------------------------------------------------------------------ process lifetime

    private Slot StartSlot()
    {
        var ctx = _context!;
        var slot = new Slot();
        var spec = new ClaudeLaunchSpec(_exePath!, _args!, ctx.WorkingDirectory, ctx.Profile);
        slot.Process = _startProcess(spec, line => OnStdoutLine(slot, line));
        return slot;
    }

    /// <summary>Returns the running process, starting a new one when the previous one ended.</summary>
    private async Task<Slot> EnsureProcessAsync()
    {
        Slot? old;
        lock (_gate)
        {
            old = _slot;
            if (old != null && !old.Dead && !old.Process.HasExited) return old;
        }

        if (old != null) await StopSlotAsync(old).ConfigureAwait(false);

        Slot fresh;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, this);
            fresh = StartSlot();
            _slot = fresh;
        }
        // A process that ended on its own (crash, kill after a stuck interrupt) took the conversation with it.
        // After ResetConversationAsync there is no old slot and nothing to announce.
        if (old != null) Emit(new StatusEvent(RestartedMessage, StatusLevel.Warning));
        return fresh;
    }

    private async Task StopSlotAsync(Slot slot)
    {
        if (Interlocked.Exchange(ref slot.Stopping, 1) == 1) return;
        try
        {
            slot.Process.CloseInput();
            await Task.WhenAny(slot.Process.Exited, Task.Delay(ShutdownGrace)).ConfigureAwait(false);
            slot.Process.Kill();
            await slot.Process.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the Claude CLI failed", ex);
        }
    }

    // ------------------------------------------------------------------ stdout handling (reader thread)

    private void OnStdoutLine(Slot slot, string line)
    {
        var parsed = ClaudeStreamParser.Parse(line, _mcpServerName);
        var state = slot.Turn;
        switch (parsed.Kind)
        {
            case ClaudeLineKind.Garbage:
                if (Interlocked.Increment(ref slot.GarbageLogged) <= 20)
                    Log.Warn("Unrecognized Claude CLI output: " + ClaudeStreamParser.Clip(line.Trim(), 200));
                return;
            case ClaudeLineKind.Init:
                if (!string.IsNullOrEmpty(parsed.Model)) _model = parsed.Model;
                if (parsed.McpStatus is { } status && status != "connected")
                    Log.Warn($"DeskPilot MCP server status in Claude Code: {status}");
                break;
            case ClaudeLineKind.Assistant:
                if (!string.IsNullOrEmpty(parsed.Model)) _model = parsed.Model;
                if (state != null)
                {
                    if (parsed.AssistantError != null) state.AssistantError = parsed.AssistantError;
                    else if (parsed.AssistantText != null) state.LastAssistantText = parsed.AssistantText;
                }
                break;
            case ClaudeLineKind.ControlResponse:
                if (string.Equals(parsed.Subtype, "error", StringComparison.OrdinalIgnoreCase))
                    Log.Warn($"Claude CLI rejected control request {parsed.RequestId}: {parsed.ControlError}");
                break;
            case ClaudeLineKind.ControlRequest:
                _ = AnswerControlRequestAsync(slot, parsed);
                break;
            case ClaudeLineKind.Result:
                if (state == null) Log.Info("Claude CLI sent a result with no turn waiting for it");
                else state.Result.TrySetResult(parsed.Result!);
                break;
        }

        foreach (var e in parsed.Events) Emit(e);
    }

    /// <summary>
    /// DeskPilot does not ask the CLI to route permission prompts to it, but if a request still arrives,
    /// answer it so the CLI never waits forever: allow only DeskPilot's own tools, refuse anything else.
    /// </summary>
    private async Task AnswerControlRequestAsync(Slot slot, ClaudeStreamLine request)
    {
        if (string.IsNullOrEmpty(request.RequestId)) return;
        string response;
        if (string.Equals(request.Subtype, "can_use_tool", StringComparison.Ordinal))
        {
            var allowed = _mcpServerName != null && request.ToolName != null &&
                          request.ToolName.StartsWith(ClaudeCliCommand.AllowedToolsFor(_mcpServerName) + "__", StringComparison.Ordinal);
            response = ClaudeCliCommand.BuildControlSuccess(request.RequestId, w =>
            {
                w.WriteStartObject();
                if (allowed)
                {
                    w.WriteString("behavior", "allow");
                    w.WritePropertyName("updatedInput");
                    using var input = JsonDocument.Parse(request.ToolInputJson ?? "{}");
                    input.RootElement.WriteTo(w);
                }
                else
                {
                    w.WriteString("behavior", "deny");
                    w.WriteString("message", "DeskPilot only allows its own tools.");
                }
                w.WriteEndObject();
            });
        }
        else
        {
            response = ClaudeCliCommand.BuildControlError(request.RequestId, $"DeskPilot does not handle '{request.Subtype}' requests.");
        }

        try
        {
            await slot.Process.WriteLineAsync(response, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"Could not answer a Claude CLI control request: {ex.Message}");
        }
    }

    private async Task SendInterruptAsync(Slot slot, TurnState state)
    {
        if (Interlocked.Exchange(ref state.InterruptSent, 1) == 1) return;
        var id = "deskpilot-interrupt-" + Interlocked.Increment(ref _requestCounter).ToString(CultureInfo.InvariantCulture) +
                 "-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await slot.Process.WriteLineAsync(ClaudeCliCommand.BuildInterruptRequest(id), CancellationToken.None).ConfigureAwait(false);
            Log.Info($"Claude CLI interrupt sent ({id})");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Log.Warn($"Could not send the interrupt to Claude Code: {ex.Message}");
        }
    }

    private void Emit(AgentEvent e)
    {
        try { _context?.Emit(e); }
        catch (Exception ex) { Log.Error("Agent event handler failed", ex); }
    }

    // ------------------------------------------------------------------ results

    private TurnResult MapResult(TurnState state, ClaudeResultInfo r, Stopwatch sw, CancellationToken callerCt)
    {
        if (state.InterruptRequested || r.IsAborted) return Cancelled(NullIfBlank(r.Text), r, sw, callerCt);
        var stats = Stats(r, sw);
        if (r.IsSuccess) return new TurnResult(TurnOutcome.Completed, r.Text ?? state.LastAssistantText ?? "", stats, null);
        var error = ClaudeStreamParser.DescribeFailure(r, state.AssistantError);
        Log.Warn($"Claude CLI turn failed ({r.Subtype}): {error}");
        return new TurnResult(TurnOutcome.Failed, null, stats, error);
    }

    private TurnResult ExitedResult(Slot slot, TurnState state, Stopwatch sw, CancellationToken callerCt)
    {
        slot.Dead = true;
        if (slot.StopRequested || IsDisposed || state.InterruptRequested) return Cancelled(null, null, sw, callerCt);

        var tail = string.Join("\n", slot.Process.StderrTail.TakeLast(10)).Trim();
        string error;
        if (string.Equals(state.AssistantError, "authentication_failed", StringComparison.OrdinalIgnoreCase) || ClaudeStreamParser.IsAuthFailure(tail))
        {
            error = ClaudeCliCommand.NotLoggedInMessage;
        }
        else
        {
            var code = slot.Process.ExitCode is { } c ? c.ToString(CultureInfo.InvariantCulture) : "unknown";
            error = $"Claude Code exited unexpectedly (exit code {code})" + (tail.Length > 0 ? ": " + ClaudeStreamParser.Clip(tail) : ".");
        }
        Log.Warn(error);
        return Failed(error, sw);
    }

    private TurnResult Cancelled(string? finalText, ClaudeResultInfo? r, Stopwatch sw, CancellationToken callerCt)
    {
        var control = _context?.Control;
        // The step limit stops the turn through the shared stop signal; report it as such.
        var stepLimit = !callerCt.IsCancellationRequested && control is { IsStopRequested: true } &&
                        control.StopReason?.StartsWith("Step limit", StringComparison.OrdinalIgnoreCase) == true;
        return new TurnResult(stepLimit ? TurnOutcome.StepLimit : TurnOutcome.Cancelled, finalText, Stats(r, sw), null);
    }

    private TurnResult Failed(string error, Stopwatch sw) => new(TurnOutcome.Failed, null, Stats(null, sw), error);

    private TurnStats Stats(ClaudeResultInfo? r, Stopwatch sw)
    {
        var model = r?.Model ?? _model ?? (_context != null ? ClaudeCliCommand.ModelOrDefault(_context.Profile.Model) : null);
        return new TurnStats(
            ClampToInt(r?.InputTokens ?? 0),
            ClampToInt(r?.OutputTokens ?? 0),
            _context?.Control.Steps ?? 0,
            r?.CostUsd,
            sw.Elapsed,
            model);
    }

    private static int ClampToInt(long v) => (int)Math.Clamp(v, 0, int.MaxValue);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Could not delete temp file: {ex.Message}"); }
    }

    // ------------------------------------------------------------------ state

    /// <summary>One CLI process and the turn currently running on it.</summary>
    private sealed class Slot
    {
        public IClaudeProcess Process = null!;
        public volatile TurnState? Turn;
        /// <summary>Reset or dispose asked it to stop, so its exit is expected.</summary>
        public volatile bool StopRequested;
        /// <summary>Exited or was killed; the next turn needs a new process.</summary>
        public volatile bool Dead;
        public int Stopping;
        public int GarbageLogged;
    }

    private sealed class TurnState : IDisposable
    {
        public readonly TaskCompletionSource<ClaudeResultInfo> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly CancellationTokenSource Interrupt = new();
        public int InterruptSent;
        public volatile string? AssistantError;
        public volatile string? LastAssistantText;

        public bool InterruptRequested => Volatile.Read(ref InterruptSent) != 0;

        public void Dispose() => Interrupt.Dispose();
    }
}

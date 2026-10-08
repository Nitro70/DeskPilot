using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Runtime;

/// <summary>
/// Shared per-turn control state: the stop signal and the step counter. One instance lives for the
/// whole session; BeginTurn() resets it at the start of each user turn.
/// </summary>
public sealed class AgentRunControl
{
    private readonly object _gate = new();
    private CancellationTokenSource _cts = new();
    private int _steps;

    public CancellationToken Token { get { lock (_gate) return _cts.Token; } }
    public bool IsStopRequested { get; private set; }
    public string? StopReason { get; private set; }
    public int Steps => Volatile.Read(ref _steps);
    /// <summary>"Allow all" was chosen in a confirmation dialog during this turn.</summary>
    public bool AllowAllThisTurn { get; set; }

    /// <summary>Raised once per turn when a stop is requested (from the UI, hotkey, failsafe or step limit).</summary>
    public event Action<string>? StopRequested;

    public void BeginTurn()
    {
        lock (_gate)
        {
            if (_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts = new CancellationTokenSource();
            }
            IsStopRequested = false;
            StopReason = null;
            AllowAllThisTurn = false;
            Volatile.Write(ref _steps, 0);
        }
    }

    public void RequestStop(string reason)
    {
        bool raise;
        lock (_gate)
        {
            raise = !IsStopRequested;
            IsStopRequested = true;
            StopReason ??= reason;
        }
        if (!raise) return;
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
        StopRequested?.Invoke(reason);
    }

    /// <summary>Counts a tool call and returns the new total.</summary>
    public int RegisterStep() => Interlocked.Increment(ref _steps);
}

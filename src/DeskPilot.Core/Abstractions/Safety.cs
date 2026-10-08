namespace DeskPilot.Core.Abstractions;

public enum ActionRisk
{
    /// <summary>Observing only: screenshots, listing windows, reading the vault.</summary>
    None,
    /// <summary>Moving/scrolling.</summary>
    Low,
    /// <summary>Clicking, typing, focusing windows, clipboard.</summary>
    Medium,
    /// <summary>Launching programs, running commands, Enter/Delete/Alt+F4-style keys, writing to the vault.</summary>
    High,
}

/// <summary>An action the model wants to take, described for the safety guard and the confirmation dialog.</summary>
public sealed record ProposedAction(
    string Tool,
    string Summary,                 // human readable, e.g. "Click left at (512, 300) on 'Notepad'"
    ActionRisk Risk,
    ScreenPoint? Target = null,     // physical screen point for pointer actions
    string? Text = null,            // typed text
    KeyCombo? Keys = null,          // pressed keys
    string? LaunchTarget = null,    // app/file/URL to open
    string? Command = null);        // shell command

public enum SafetyDecision { Allow, Deny, NeedsConfirmation }

public sealed record SafetyVerdict(SafetyDecision Decision, string Reason)
{
    public static readonly SafetyVerdict Allowed = new(SafetyDecision.Allow, "");
    public static SafetyVerdict Denied(string reason) => new(SafetyDecision.Deny, reason);
    public static SafetyVerdict Confirm(string reason) => new(SafetyDecision.NeedsConfirmation, reason);
}

public interface ISafetyGuard
{
    /// <summary>Decides whether an action may run. Must be fast (it is called before every action).</summary>
    Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct);
}

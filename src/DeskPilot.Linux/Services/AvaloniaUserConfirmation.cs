using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux.Services;

/// <summary>
/// IUserConfirmation backed by <see cref="ConfirmWindow"/>: a topmost dialog with Deny as the default.
/// Cancelling the token (stop, step limit, shutdown) closes it as Deny. Afterwards the window the agent was
/// working in gets the focus back (through the desktop layer, when one is given).
/// </summary>
public sealed class AvaloniaUserConfirmation : IUserConfirmation
{
    private readonly IWindowManager? _windows;

    public AvaloniaUserConfirmation(IWindowManager? windows = null) => _windows = windows;

    /// <summary>Raised on the UI thread with each dialog right after it opens (lets tests answer it).</summary>
    public event Action<ConfirmWindow>? DialogOpened;

    public async Task<ConfirmationChoice> ConfirmAsync(ProposedAction action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return ConfirmationChoice.Deny;

        var previous = ForegroundWindow();
        var result = new TaskCompletionSource<ConfirmationChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmWindow? window = null;

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                var w = new ConfirmWindow(action);
                w.Closed += (_, _) => result.TrySetResult(w.Choice);
                window = w;
                w.Show();
                w.Activate();
                DialogOpened?.Invoke(w);
            }
            catch (Exception ex)
            {
                Log.Error("Confirmation dialog failed", ex);
                result.TrySetResult(ConfirmationChoice.Deny);
            }
        });

        ConfirmationChoice choice;
        using (ct.Register(() =>
        {
            result.TrySetResult(ConfirmationChoice.Deny);
            Dispatcher.UIThread.Post(() =>
            {
                if (window is { IsVisible: true } w) w.CloseWith(ConfirmationChoice.Deny);
            });
        }))
        {
            choice = await result.Task.ConfigureAwait(false);
        }

        RestoreFocus(previous);
        return choice;
    }

    private WindowInfo? ForegroundWindow()
    {
        if (_windows == null) return null;
        try
        {
            var w = _windows.GetForegroundWindow();
            // Our own windows (main window, overlay) are not where the agent works.
            return w is { ProcessId: var pid } && pid != Environment.ProcessId ? w : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Foreground window unknown before confirmation: {ex.Message}");
            return null;
        }
    }

    private void RestoreFocus(WindowInfo? previous)
    {
        if (previous == null || _windows == null) return;
        try { _windows.FocusWindow(previous.Handle); }
        catch (Exception ex) { Log.Warn($"Focus not handed back after confirmation: {ex.Message}"); }
    }
}

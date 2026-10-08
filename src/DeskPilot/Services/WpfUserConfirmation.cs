using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Views;

namespace DeskPilot.Services;

/// <summary>
/// IUserConfirmation backed by <see cref="ConfirmWindow"/>: a topmost, capture-excluded dialog.
/// Cancelling the token (stop, step limit, shutdown) closes it as Deny.
/// </summary>
public sealed class WpfUserConfirmation : IUserConfirmation
{
    private readonly Dispatcher _dispatcher;

    public WpfUserConfirmation(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public async Task<ConfirmationChoice> ConfirmAsync(ProposedAction action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested || _dispatcher.HasShutdownStarted) return ConfirmationChoice.Deny;

        var result = new TaskCompletionSource<ConfirmationChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        ConfirmWindow? window = null;

        await _dispatcher.InvokeAsync(() =>
        {
            try
            {
                var previousForeground = Win32.GetForegroundWindow();
                var w = new ConfirmWindow(action);
                NativeUi.Prepare(w);
                w.Closed += (_, _) =>
                {
                    result.TrySetResult(w.Choice);
                    // Hand focus back to the window the agent was working in.
                    if (previousForeground != 0 && Win32.IsWindow(previousForeground)) Win32.SetForegroundWindow(previousForeground);
                };
                window = w;
                w.Show();
                w.Activate();
            }
            catch (Exception ex)
            {
                Log.Error("Confirmation dialog failed", ex);
                result.TrySetResult(ConfirmationChoice.Deny);
            }
        });

        using var registration = ct.Register(() =>
        {
            if (_dispatcher.HasShutdownStarted)
            {
                result.TrySetResult(ConfirmationChoice.Deny);
                return;
            }
            _dispatcher.BeginInvoke(() =>
            {
                if (window is { IsLoaded: true }) window.CloseWith(ConfirmationChoice.Deny);
                result.TrySetResult(ConfirmationChoice.Deny);
            });
        });

        return await result.Task.ConfigureAwait(false);
    }
}

using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux.Services;

/// <summary>
/// IUserConfirmation backed by <see cref="ConfirmWindow"/>: a topmost dialog with Deny as the default.
/// Cancelling the token (stop, step limit, shutdown) closes it as Deny.
/// </summary>
public sealed class AvaloniaUserConfirmation : IUserConfirmation
{
    /// <summary>Raised on the UI thread with each dialog right after it opens (lets tests answer it).</summary>
    public event Action<ConfirmWindow>? DialogOpened;

    public async Task<ConfirmationChoice> ConfirmAsync(ProposedAction action, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return ConfirmationChoice.Deny;

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

        using var registration = ct.Register(() =>
        {
            result.TrySetResult(ConfirmationChoice.Deny);
            Dispatcher.UIThread.Post(() =>
            {
                if (window is { IsVisible: true } w) w.CloseWith(ConfirmationChoice.Deny);
            });
        });

        return await result.Task.ConfigureAwait(false);
    }
}

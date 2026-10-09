using Avalonia.Threading;

namespace DeskPilot.Linux.Services;

/// <summary>Posts work to the UI thread. Abstracted so view models can be tested without a dispatcher.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues the action on the UI thread (never runs it inline).</summary>
    void Post(Action action);
}

/// <summary>Posts through Avalonia's UI thread dispatcher.</summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public static readonly AvaloniaUiDispatcher Instance = new();

    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

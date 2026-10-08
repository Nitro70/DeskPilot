using System.Windows.Threading;

namespace DeskPilot.Services;

/// <summary>Posts work to the UI thread. Abstracted so view models can be tested without a dispatcher.</summary>
public interface IUiDispatcher
{
    /// <summary>Queues the action on the UI thread (never runs it inline).</summary>
    void Post(Action action);
}

public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(action);
    }
}

using System.Runtime.ExceptionServices;

namespace DeskPilot.Desktop.Windows;

/// <summary>Runs a function on a fresh STA thread (clipboard and shell COM objects need one) with a timeout.</summary>
internal static class StaThread
{
    public static T Run<T>(Func<T> func, TimeSpan timeout, string what)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "DeskPilot STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout)) throw new TimeoutException($"Timed out after {timeout.TotalSeconds:0.#} s while trying to {what}.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    public static void Run(Action action, TimeSpan timeout, string what) =>
        Run<object?>(() => { action(); return null; }, timeout, what);
}

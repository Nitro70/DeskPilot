namespace DeskPilot.Core.Desktop;

/// <summary>
/// Switches the calling thread to Per-Monitor-V2 DPI awareness for the lifetime of the scope and restores
/// the previous context afterwards. Inside the scope every geometry API (cursor, window rects, monitor
/// bounds, BitBlt from the screen DC, SendInput) works in physical pixels, whatever the process manifest
/// says. The app itself is PMv2 already, but the test host and the MCP bridge process may not be.
/// </summary>
internal readonly struct DpiScope : IDisposable
{
    private readonly nint _previous;

    private DpiScope(nint previous) => _previous = previous;

    public static DpiScope PerMonitorV2()
    {
        try
        {
            var previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
            if (previous == 0)
            {
                // PMv2 needs Windows 10 1703; plain per-monitor awareness still gives physical pixels.
                previous = NativeMethods.SetThreadDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE);
            }
            return new DpiScope(previous);
        }
        catch (EntryPointNotFoundException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        if (_previous == 0) return;
        try { NativeMethods.SetThreadDpiAwarenessContext(_previous); }
        catch (EntryPointNotFoundException) { }
    }

    /// <summary>True when the calling thread currently runs Per-Monitor-V2 aware.</summary>
    internal static bool IsThreadPerMonitorV2()
    {
        try
        {
            return NativeMethods.AreDpiAwarenessContextsEqual(NativeMethods.GetThreadDpiAwarenessContext(), NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static nint CurrentContext()
    {
        try { return NativeMethods.GetThreadDpiAwarenessContext(); }
        catch (EntryPointNotFoundException) { return 0; }
    }
}

namespace DeskPilot.Desktop.Linux.X11.Interop;

/// <summary>
/// One Xlib Display* connection, opened lazily and guarded by a lock: Xlib connections must not be used by two
/// threads at once. A connection that died (X server restarted) is closed and reopened on next use.
/// </summary>
internal sealed unsafe class XConnection : IDisposable
{
    private readonly object _gate = new();
    private nint _display;
    private bool _disposed;

    /// <summary>Default screen number and its root window of the current connection.</summary>
    public int Screen { get; private set; }
    public nuint Root { get; private set; }

    /// <summary>Increments every time a new connection is opened, so per-connection caches (atoms) can refresh.</summary>
    public int Generation { get; private set; }

    /// <summary>Locks the connection (opening it when needed) until the lease is disposed.</summary>
    public Lease Acquire()
    {
        Monitor.Enter(_gate);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_display != 0 && XErrorTrap.IsDead(_display)) CloseUnlocked();
            if (_display == 0) OpenUnlocked();
            XErrorTrap.Reset(_display);
            return new Lease(this, _display);
        }
        catch
        {
            Monitor.Exit(_gate);
            throw;
        }
    }

    private void OpenUnlocked()
    {
        X11Native.EnsureInitialized();
        var name = Environment.GetEnvironmentVariable("DISPLAY");
        var d = Xlib.XOpenDisplay(null);
        if (d == 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(name)
                ? "Cannot open the X display: DISPLAY is not set. DeskPilot's X11 mode needs a running X11 session."
                : $"Cannot open the X display '{name}'. Is the X server running and does this user have access to it?");
        }
        XErrorTrap.Register(d);
        _display = d;
        Screen = Xlib.XDefaultScreen(d);
        Root = Xlib.XRootWindow(d, Screen);
        Generation++;
    }

    private void CloseUnlocked()
    {
        var d = _display;
        _display = 0;
        Root = 0;
        if (d == 0) return;
        try { Xlib.XCloseDisplay(d); }
        catch (Exception) { /* a dead connection may fail to close; it is gone either way */ }
        XErrorTrap.Unregister(d);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            CloseUnlocked();
        }
    }

    public readonly ref struct Lease
    {
        private readonly XConnection _owner;
        public nint Display { get; }
        public nuint Root => _owner.Root;
        public int Screen => _owner.Screen;
        public int Generation => _owner.Generation;

        internal Lease(XConnection owner, nint display)
        {
            _owner = owner;
            Display = display;
        }

        /// <summary>Round trip, then the first X error since the lease began or the last check.</summary>
        public XErrorInfo? Sync() => XErrorTrap.Sync(Display);

        public void ResetErrors() => XErrorTrap.Reset(Display);

        public void Dispose() => Monitor.Exit(_owner._gate);
    }
}

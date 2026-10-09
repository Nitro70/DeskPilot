namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>
/// A pointer device created through the wlroots virtual-pointer protocol (zwlr_virtual_pointer_v1), kept alive for
/// the whole session. sway, Hyprland, river, wayfire, labwc and other wlroots compositors support it. Because the
/// device stays, the seat keeps its pointer capability, so clicks reach apps even on machines with no mouse attached.
/// </summary>
internal sealed class VirtualPointerDevice : IDisposable
{
    public const string ManagerInterface = "zwlr_virtual_pointer_manager_v1";
    public const uint BtnLeft = 0x110, BtnRight = 0x111, BtnMiddle = 0x112;

    private const ushort OpMotionAbsolute = 1, OpButton = 2, OpFrame = 4, OpAxisSource = 5, OpAxisDiscrete = 7;
    // Absolute coordinates are sent in 1/16 logical pixels so fractional output scales stay exact.
    internal const double SubPixel = 16.0;

    private readonly string _socketPath;
    private readonly object _gate = new();
    private WaylandConnection? _connection;
    private uint _pointer;

    public VirtualPointerDevice(string socketPath)
    {
        _socketPath = socketPath;
        lock (_gate) Connect();
    }

    private void Connect()
    {
        _connection?.Dispose();
        _connection = null;
        var c = WaylandConnection.Connect(_socketPath);
        try
        {
            var manager = c.FindGlobal(ManagerInterface)
                          ?? throw new WaylandProtocolException("The compositor does not offer the virtual pointer protocol.");
            uint mgr = c.Bind(manager, 2);
            uint pointer = c.NewId();
            c.Send(mgr, 0, new WireWriter().UInt(0).UInt(pointer)); // create_virtual_pointer(seat: default, id)
            c.Roundtrip();
            _connection = c;
            _pointer = pointer;
        }
        catch
        {
            c.Dispose();
            throw;
        }
        // Clients bind their wl_pointer when the seat announces the new capability; give them a moment so the first
        // event is not lost on a seat that had no pointer before.
        Thread.Sleep(80);
    }

    /// <summary>Absolute motion within the layout box: x and y in logical pixels relative to the box origin.</summary>
    public void MoveAbsolute(double x, double y, double boxWidth, double boxHeight)
    {
        var (ux, uy, ex, ey) = AbsoluteArgs(x, y, boxWidth, boxHeight);
        Run(c =>
        {
            c.Send(_pointer, OpMotionAbsolute, new WireWriter().UInt(Now()).UInt(ux).UInt(uy).UInt(ex).UInt(ey));
            c.Send(_pointer, OpFrame, new WireWriter());
        });
    }

    internal static (uint X, uint Y, uint XExtent, uint YExtent) AbsoluteArgs(double x, double y, double boxWidth, double boxHeight)
    {
        uint ex = (uint)Math.Max(1, Math.Round(boxWidth * SubPixel)), ey = (uint)Math.Max(1, Math.Round(boxHeight * SubPixel));
        uint ux = (uint)Math.Clamp(Math.Round(x * SubPixel), 0, ex - 1);
        uint uy = (uint)Math.Clamp(Math.Round(y * SubPixel), 0, ey - 1);
        return (ux, uy, ex, ey);
    }

    public void Button(uint code, bool pressed) => Run(c =>
    {
        c.Send(_pointer, OpButton, new WireWriter().UInt(Now()).UInt(code).UInt(pressed ? 1u : 0u));
        c.Send(_pointer, OpFrame, new WireWriter());
    });

    /// <summary>One wheel notch: axis 0 = vertical, 1 = horizontal; positive scrolls down / right.</summary>
    public void Notch(uint axis, int direction) => Run(c =>
    {
        int d = Math.Sign(direction);
        c.Send(_pointer, OpAxisSource, new WireWriter().UInt(0)); // wheel
        c.Send(_pointer, OpAxisDiscrete, new WireWriter().UInt(Now()).UInt(axis).Fixed(15.0 * d).Int(d));
        c.Send(_pointer, OpFrame, new WireWriter());
    });

    private void Run(Action<WaylandConnection> send)
    {
        lock (_gate)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (_connection == null || _connection.IsBroken) Connect();
                    send(_connection!);
                    // The roundtrip makes sure the compositor has applied the event before DeskPilot's next action
                    // (which may come from another process, e.g. wtype).
                    _connection!.Roundtrip();
                    return;
                }
                catch (WaylandProtocolException) when (attempt == 0)
                {
                    _connection?.Dispose();
                    _connection = null;
                }
            }
        }
    }

    private static uint Now() => (uint)(Environment.TickCount64 & 0xffffffff);

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}

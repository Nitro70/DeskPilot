using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>A global the compositor advertises (an interface it supports).</summary>
internal readonly record struct WaylandGlobal(uint Name, string Interface, uint Version);

internal sealed class WaylandProtocolException : Exception
{
    public WaylandProtocolException(string message) : base(message) { }
}

/// <summary>
/// A minimal Wayland client that speaks the wire protocol directly over the compositor's socket. DeskPilot only
/// needs a few requests (the registry, outputs, the wlroots virtual pointer) and none of them pass file
/// descriptors, so this avoids a native libwayland dependency.
/// </summary>
internal sealed class WaylandConnection : IDisposable
{
    private const uint DisplayId = 1;

    private readonly Socket _socket;
    private readonly object _gate = new();
    private readonly Dictionary<uint, Action<ushort, byte[]>> _listeners = new();
    private readonly Dictionary<uint, WaylandGlobal> _globals = new();
    private byte[] _buffer = new byte[16 * 1024];
    private int _length;
    private uint _nextId = 2;
    private uint _registry;
    private string? _fatal;

    private WaylandConnection(Socket socket) => _socket = socket;

    /// <summary>The compositor socket: WAYLAND_DISPLAY when absolute, else XDG_RUNTIME_DIR/WAYLAND_DISPLAY (default wayland-0).</summary>
    public static string? ResolveSocketPath(string? waylandDisplay, string? runtimeDir)
    {
        var name = string.IsNullOrWhiteSpace(waylandDisplay) ? "wayland-0" : waylandDisplay.Trim();
        if (name.StartsWith('/')) return name;
        if (string.IsNullOrWhiteSpace(runtimeDir)) return null;
        return Path.Combine(runtimeDir.Trim(), name);
    }

    public static string? ResolveSocketPath(LinuxSessionInfo session) =>
        ResolveSocketPath(session.WaylandDisplay ?? Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"),
            session.RuntimeDir ?? Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"));

    /// <summary>Connects and reads the registry. Throws when the compositor cannot be reached.</summary>
    public static WaylandConnection Connect(string socketPath, int timeoutMs = 3000)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        }
        catch (SocketException ex)
        {
            socket.Dispose();
            throw new WaylandProtocolException($"Could not connect to the Wayland compositor at {socketPath}: {ex.Message}");
        }

        var c = new WaylandConnection(socket);
        try
        {
            c._registry = c.NewId();
            c.Send(DisplayId, 1, new WireWriter().UInt(c._registry)); // wl_display.get_registry
            c.Roundtrip(timeoutMs);
            return c;
        }
        catch
        {
            c.Dispose();
            throw;
        }
    }

    public IReadOnlyList<WaylandGlobal> Globals
    {
        get { lock (_gate) return _globals.Values.ToList(); }
    }

    public WaylandGlobal? FindGlobal(string iface)
    {
        lock (_gate)
        {
            foreach (var g in _globals.Values)
                if (g.Interface == iface) return g;
            return null;
        }
    }

    public bool IsBroken
    {
        get { lock (_gate) return _fatal != null; }
    }

    public uint NewId()
    {
        lock (_gate) return _nextId++;
    }

    /// <summary>Receives the events of one object (opcode + raw argument bytes).</summary>
    public void Listen(uint objectId, Action<ushort, byte[]> listener)
    {
        lock (_gate) _listeners[objectId] = listener;
    }

    public void Forget(uint objectId)
    {
        lock (_gate) _listeners.Remove(objectId);
    }

    /// <summary>wl_registry.bind with an untyped new_id; returns the new object id.</summary>
    public uint Bind(WaylandGlobal global, uint version)
    {
        uint id = NewId();
        Send(_registry, 0, new WireWriter().UInt(global.Name).String(global.Interface).UInt(Math.Min(version, global.Version)).UInt(id));
        return id;
    }

    public void Send(uint objectId, ushort opcode, WireWriter args)
    {
        lock (_gate)
        {
            if (_fatal != null) throw new WaylandProtocolException(_fatal);
            var body = args.ToArray();
            int size = 8 + body.Length;
            if (size > ushort.MaxValue) throw new ArgumentException("Wayland message too large");
            var msg = new byte[size];
            BinaryPrimitives.WriteUInt32LittleEndian(msg, objectId);
            BinaryPrimitives.WriteUInt32LittleEndian(msg.AsSpan(4), ((uint)size << 16) | opcode);
            body.CopyTo(msg, 8);
            try
            {
                int sent = 0;
                while (sent < msg.Length) sent += _socket.Send(msg, sent, msg.Length - sent, SocketFlags.None);
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _fatal = "The Wayland connection was lost: " + ex.Message;
                throw new WaylandProtocolException(_fatal);
            }
        }
    }

    /// <summary>wl_display.sync and dispatch events until the compositor has processed everything sent so far.</summary>
    public void Roundtrip(int timeoutMs = 2000)
    {
        lock (_gate)
        {
            uint callback = _nextId++;
            bool done = false;
            _listeners[callback] = (_, _) => done = true;
            try
            {
                Send(DisplayId, 0, new WireWriter().UInt(callback));
                Dispatch(timeoutMs, () => done);
            }
            finally
            {
                _listeners.Remove(callback);
            }
        }
    }

    private void Dispatch(int timeoutMs, Func<bool> done)
    {
        long deadline = Environment.TickCount64 + Math.Max(1, timeoutMs);
        while (!done())
        {
            if (TryDispatchOne()) continue;
            long remaining = deadline - Environment.TickCount64;
            if (remaining <= 0) throw new TimeoutException("The Wayland compositor did not answer in time.");
            try
            {
                if (!_socket.Poll((int)Math.Min(remaining * 1000, int.MaxValue), SelectMode.SelectRead))
                    throw new TimeoutException("The Wayland compositor did not answer in time.");
                if (_length == _buffer.Length) Array.Resize(ref _buffer, _buffer.Length * 2);
                int n = _socket.Receive(_buffer, _length, _buffer.Length - _length, SocketFlags.None);
                if (n == 0)
                {
                    _fatal = "The Wayland compositor closed the connection.";
                    throw new WaylandProtocolException(_fatal);
                }
                _length += n;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                _fatal = "The Wayland connection was lost: " + ex.Message;
                throw new WaylandProtocolException(_fatal);
            }
        }
    }

    private bool TryDispatchOne()
    {
        if (_length < 8) return false;
        uint objectId = BinaryPrimitives.ReadUInt32LittleEndian(_buffer);
        uint word = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(4));
        int size = (int)(word >> 16);
        ushort opcode = (ushort)(word & 0xffff);
        if (size < 8)
        {
            _fatal = "Malformed message from the Wayland compositor.";
            throw new WaylandProtocolException(_fatal);
        }
        if (_length < size) return false;

        var args = _buffer.AsSpan(8, size - 8).ToArray();
        Buffer.BlockCopy(_buffer, size, _buffer, 0, _length - size);
        _length -= size;
        HandleEvent(objectId, opcode, args);
        return true;
    }

    private void HandleEvent(uint objectId, ushort opcode, byte[] args)
    {
        if (objectId == DisplayId)
        {
            if (opcode == 0) // wl_display.error(object_id, code, message)
            {
                var r = new WireReader(args);
                uint obj = r.UInt();
                uint code = r.UInt();
                string message = r.String() ?? "";
                _fatal = $"Wayland protocol error {code} on object {obj}: {message}";
                throw new WaylandProtocolException(_fatal);
            }
            return; // delete_id: ids are never reused here
        }

        if (objectId == _registry)
        {
            var r = new WireReader(args);
            if (opcode == 0)
            {
                uint name = r.UInt();
                string iface = r.String() ?? "";
                uint version = r.UInt();
                _globals[name] = new WaylandGlobal(name, iface, version);
            }
            else if (opcode == 1)
            {
                _globals.Remove(r.UInt());
            }
            return;
        }

        if (_listeners.TryGetValue(objectId, out var listener)) listener(opcode, args);
    }

    public void Dispose()
    {
        try { _socket.Shutdown(SocketShutdown.Both); } catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        _socket.Dispose();
    }
}

/// <summary>Builds the argument part of a Wayland request (32-bit words, host byte order: little endian on every supported CPU).</summary>
internal sealed class WireWriter
{
    private readonly List<byte> _bytes = new();

    public WireWriter UInt(uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        foreach (var x in b) _bytes.Add(x);
        return this;
    }

    public WireWriter Int(int v) => UInt(unchecked((uint)v));

    /// <summary>24.8 signed fixed point.</summary>
    public WireWriter Fixed(double v) => Int((int)Math.Round(v * 256.0));

    public WireWriter String(string? s)
    {
        if (s == null) return UInt(0);
        var data = Encoding.UTF8.GetBytes(s);
        UInt((uint)data.Length + 1);
        _bytes.AddRange(data);
        _bytes.Add(0);
        while (_bytes.Count % 4 != 0) _bytes.Add(0);
        return this;
    }

    public byte[] ToArray() => _bytes.ToArray();
}

/// <summary>Reads event arguments.</summary>
internal ref struct WireReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _pos;

    public WireReader(ReadOnlySpan<byte> data)
    {
        _data = data;
        _pos = 0;
    }

    public uint UInt()
    {
        if (_pos + 4 > _data.Length) throw new WaylandProtocolException("Truncated Wayland event");
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public int Int() => unchecked((int)UInt());

    public double Fixed() => Int() / 256.0;

    public string? String()
    {
        uint len = UInt();
        if (len == 0) return null;
        if (_pos + len > _data.Length) throw new WaylandProtocolException("Truncated Wayland string");
        var s = Encoding.UTF8.GetString(_data.Slice(_pos, (int)len - 1));
        _pos += (int)((len + 3) & ~3u);
        return s;
    }
}

/// <summary>An output as the Wayland protocol describes it (wl_output plus xdg-output when available).</summary>
internal sealed record WaylandProtocolOutput(string? Name, int LogicalX, int LogicalY, int LogicalWidth, int LogicalHeight, int ModeWidth, int ModeHeight, double Scale);

internal static class WaylandOutputQuery
{
    /// <summary>
    /// Reads every output's logical position and size (xdg-output) and its current mode and scale (wl_output) over a
    /// short-lived connection. Works on any compositor with xdg-output: wlroots, KWin, Mutter, COSMIC.
    /// </summary>
    public static IReadOnlyList<WaylandProtocolOutput> Query(string socketPath, int timeoutMs = 3000)
    {
        using var c = WaylandConnection.Connect(socketPath, timeoutMs);
        var outputs = c.Globals.Where(g => g.Interface == "wl_output").OrderBy(g => g.Name).ToList();
        if (outputs.Count == 0) return Array.Empty<WaylandProtocolOutput>();

        var states = new List<OutputState>();
        foreach (var g in outputs)
        {
            var s = new OutputState { Id = c.Bind(g, 4) };
            c.Listen(s.Id, (op, args) => s.OnOutputEvent(op, args));
            states.Add(s);
        }

        var manager = c.FindGlobal("zxdg_output_manager_v1");
        if (manager is { } m)
        {
            uint mgr = c.Bind(m, 3);
            foreach (var s in states)
            {
                s.XdgId = c.NewId();
                c.Listen(s.XdgId, (op, args) => s.OnXdgEvent(op, args));
                c.Send(mgr, 1, new WireWriter().UInt(s.XdgId).UInt(s.Id)); // get_xdg_output(id, output)
            }
        }
        c.Roundtrip(timeoutMs);
        c.Roundtrip(timeoutMs);
        return states.Select(s => s.ToOutput()).Where(o => o.LogicalWidth > 0 && o.LogicalHeight > 0).ToList();
    }

    /// <summary>Combines wl_output and xdg-output facts. Separate so the arithmetic can be tested without a compositor.</summary>
    internal static WaylandProtocolOutput Combine(string? name, int geometryX, int geometryY, int transform, int modeW, int modeH, int intScale,
        int? xdgX, int? xdgY, int? xdgW, int? xdgH)
    {
        bool rotated = transform is 1 or 3 or 5 or 7;
        int mw = rotated ? modeH : modeW, mh = rotated ? modeW : modeH;
        int scale = Math.Max(1, intScale);
        int lw = xdgW ?? (mw / scale), lh = xdgH ?? (mh / scale);
        double fractional = lw > 0 && mw > 0 ? (double)mw / lw : scale;
        return new WaylandProtocolOutput(name, xdgX ?? geometryX, xdgY ?? geometryY, lw, lh, mw, mh, Math.Round(fractional, 4));
    }

    private sealed class OutputState
    {
        public uint Id;
        public uint XdgId;
        private string? _name;
        private int _gx, _gy, _transform, _mw, _mh, _scale = 1;
        private int? _lx, _ly, _lw, _lh;

        public void OnOutputEvent(ushort opcode, byte[] args)
        {
            var r = new WireReader(args);
            switch (opcode)
            {
                case 0: // geometry(x, y, physical_width, physical_height, subpixel, make, model, transform)
                    _gx = r.Int(); _gy = r.Int(); r.Int(); r.Int(); r.Int(); r.String(); r.String(); _transform = r.Int();
                    break;
                case 1: // mode(flags, width, height, refresh)
                    uint flags = r.UInt();
                    int w = r.Int(), h = r.Int();
                    if ((flags & 1) != 0 || _mw == 0) { _mw = w; _mh = h; }
                    break;
                case 3: _scale = r.Int(); break;
                case 4: _name ??= r.String(); break;
            }
        }

        public void OnXdgEvent(ushort opcode, byte[] args)
        {
            var r = new WireReader(args);
            switch (opcode)
            {
                case 0: _lx = r.Int(); _ly = r.Int(); break;
                case 1: _lw = r.Int(); _lh = r.Int(); break;
                case 3: _name = r.String() ?? _name; break;
            }
        }

        public WaylandProtocolOutput ToOutput() => Combine(_name, _gx, _gy, _transform, _mw, _mh, _scale, _lx, _ly, _lw, _lh);
    }
}

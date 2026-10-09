using DeskPilot.Core.Settings;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>org.freedesktop.portal.Screenshot: a non-interactive full-desktop screenshot saved to a file the portal names.</summary>
internal static class PortalScreenshot
{
    public static PortalCall Build(string token) => new(PortalNames.Screenshot, "Screenshot", new[]
    {
        PortalArg.Str(""), // no parent window
        PortalArg.Options(new Dictionary<string, object>
        {
            ["handle_token"] = token,
            ["interactive"] = false,
            ["modal"] = false,
        }),
    }, token);

    /// <summary>The local file path of the screenshot from a Response.</summary>
    public static string ParseFile(PortalResponse response)
    {
        if (response.Code != 0) throw new InvalidOperationException(PortalNames.DescribeFailure("The portal screenshot", response.Code));
        var uri = response.GetString("uri");
        if (string.IsNullOrEmpty(uri)) throw new InvalidOperationException("The portal screenshot returned no file.");
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || !u.IsFile) throw new InvalidOperationException($"The portal screenshot returned an unexpected location: {uri}");
        return u.LocalPath;
    }

    /// <summary>Takes the screenshot and returns the PNG bytes; the file the portal wrote is deleted at once.</summary>
    public static async Task<byte[]> TakeAsync(IPortalBus bus, TimeSpan timeout, CancellationToken ct)
    {
        var token = PortalNames.NewToken();
        var response = await bus.RequestAsync(Build(token), timeout, ct).ConfigureAwait(false);
        var path = ParseFile(response);
        try
        {
            return await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

/// <summary>A screen-cast stream of a RemoteDesktop session: its PipeWire node and where it sits in the logical layout.</summary>
internal sealed record PortalStream(uint NodeId, int X, int Y, int Width, int Height)
{
    public bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;
}

/// <summary>
/// org.freedesktop.portal.RemoteDesktop with a ScreenCast source, which GNOME, KDE and COSMIC offer for input
/// injection. The user approves once; persist_mode 2 and the saved restore token skip the dialog afterwards.
/// </summary>
internal sealed class PortalRemoteDesktop
{
    public const uint DeviceKeyboard = 1, DevicePointer = 2;
    public const uint SourceMonitor = 1;
    public const uint PersistUntilRevoked = 2;

    private static readonly TimeSpan ApprovalTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    private readonly IPortalBus _bus;
    private readonly string _tokenFile;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string? SessionHandle { get; private set; }
    public IReadOnlyList<PortalStream> Streams { get; private set; } = Array.Empty<PortalStream>();
    public uint Devices { get; private set; }

    public PortalRemoteDesktop(IPortalBus bus, string? tokenFile = null)
    {
        _bus = bus;
        _tokenFile = tokenFile ?? DefaultTokenFile;
    }

    public static string DefaultTokenFile => Path.Combine(AppPaths.LocalRoot, "remote-desktop-token");

    public bool IsStarted => SessionHandle != null;

    // ------------------------------------------------------------- request building (pure, tested)

    public static PortalCall CreateSession(string token, string sessionToken) => new(PortalNames.RemoteDesktop, "CreateSession", new[]
    {
        PortalArg.Options(new Dictionary<string, object> { ["handle_token"] = token, ["session_handle_token"] = sessionToken }),
    }, token);

    public static PortalCall SelectDevices(string session, string token, uint version, string? restoreToken)
    {
        var options = new Dictionary<string, object>
        {
            ["handle_token"] = token,
            ["types"] = DeviceKeyboard | DevicePointer,
        };
        if (version >= 2)
        {
            options["persist_mode"] = PersistUntilRevoked;
            if (!string.IsNullOrEmpty(restoreToken)) options["restore_token"] = restoreToken;
        }
        return new PortalCall(PortalNames.RemoteDesktop, "SelectDevices", new[] { PortalArg.Path(session), PortalArg.Options(options) }, token);
    }

    public static PortalCall SelectSources(string session, string token) => new(PortalNames.ScreenCast, "SelectSources", new[]
    {
        PortalArg.Path(session),
        PortalArg.Options(new Dictionary<string, object>
        {
            ["handle_token"] = token,
            ["types"] = SourceMonitor,
            ["multiple"] = true,
        }),
    }, token);

    public static PortalCall Start(string session, string token) => new(PortalNames.RemoteDesktop, "Start", new[]
    {
        PortalArg.Path(session),
        PortalArg.Str(""),
        PortalArg.Options(new Dictionary<string, object> { ["handle_token"] = token }),
    }, token);

    public static PortalCall PointerMotionAbsolute(string session, uint stream, double x, double y) =>
        Notify("NotifyPointerMotionAbsolute", session, PortalArg.U32(stream), PortalArg.F64(x), PortalArg.F64(y));

    public static PortalCall PointerButton(string session, int evdevButton, bool pressed) =>
        Notify("NotifyPointerButton", session, PortalArg.I32(evdevButton), PortalArg.U32(pressed ? 1u : 0u));

    /// <summary>axis 0 = vertical, 1 = horizontal; positive steps scroll down / right.</summary>
    public static PortalCall PointerAxisDiscrete(string session, uint axis, int steps) =>
        Notify("NotifyPointerAxisDiscrete", session, PortalArg.U32(axis), PortalArg.I32(steps));

    public static PortalCall KeyboardKeysym(string session, int keysym, bool pressed) =>
        Notify("NotifyKeyboardKeysym", session, PortalArg.I32(keysym), PortalArg.U32(pressed ? 1u : 0u));

    private static PortalCall Notify(string method, string session, params PortalArg[] args)
    {
        var all = new List<PortalArg> { PortalArg.Path(session), PortalArg.Options(new Dictionary<string, object>()) };
        all.AddRange(args);
        return new PortalCall(PortalNames.RemoteDesktop, method, all);
    }

    /// <summary>Reads the streams of a Start response: a(ua{sv}) with position (ii) and size (ii).</summary>
    public static IReadOnlyList<PortalStream> ParseStreams(PortalResponse response)
    {
        var list = new List<PortalStream>();
        if (!response.Results.TryGetValue("streams", out var raw) || raw is not object?[] streams) return list;
        foreach (var s in streams)
        {
            if (s is not object?[] { Length: >= 2 } pair || pair[0] is not uint node) continue;
            var props = pair[1] as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
            int x = 0, y = 0, w = 0, h = 0;
            if (props.TryGetValue("position", out var pos) && pos is object?[] { Length: 2 } p)
            {
                PortalValues.TryGetInt(p[0], out x);
                PortalValues.TryGetInt(p[1], out y);
            }
            if (props.TryGetValue("size", out var size) && size is object?[] { Length: 2 } z)
            {
                PortalValues.TryGetInt(z[0], out w);
                PortalValues.TryGetInt(z[1], out h);
            }
            list.Add(new PortalStream(node, x, y, w, h));
        }
        return list;
    }

    /// <summary>The stream under a logical point and the point relative to it; falls back to the nearest stream.</summary>
    public static (PortalStream Stream, double X, double Y)? MapPoint(IReadOnlyList<PortalStream> streams, double x, double y)
    {
        if (streams.Count == 0) return null;
        var hit = streams.FirstOrDefault(s => s.Contains(x, y));
        if (hit == null)
        {
            hit = streams.OrderBy(s => Distance(s, x, y)).First();
        }
        double rx = Math.Clamp(x - hit.X, 0, Math.Max(0, hit.Width - 1));
        double ry = Math.Clamp(y - hit.Y, 0, Math.Max(0, hit.Height - 1));
        return (hit, rx, ry);
    }

    private static double Distance(PortalStream s, double x, double y)
    {
        double dx = x < s.X ? s.X - x : x >= s.X + s.Width ? x - (s.X + s.Width - 1) : 0;
        double dy = y < s.Y ? s.Y - y : y >= s.Y + s.Height ? y - (s.Y + s.Height - 1) : 0;
        return dx * dx + dy * dy;
    }

    // ------------------------------------------------------------- session

    /// <summary>Creates and starts the session once (the first time this shows the portal's approval dialog).</summary>
    public async Task EnsureStartedAsync(CancellationToken ct)
    {
        if (SessionHandle != null) return;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (SessionHandle != null) return;
            uint version = await _bus.GetVersionAsync(PortalNames.RemoteDesktop, ct).ConfigureAwait(false);
            if (version == 0) throw new InvalidOperationException("xdg-desktop-portal on this desktop has no RemoteDesktop interface.");

            var token = PortalNames.NewToken();
            var created = await _bus.RequestAsync(CreateSession(token, PortalNames.NewToken()), StepTimeout, ct).ConfigureAwait(false);
            if (created.Code != 0) throw new InvalidOperationException(PortalNames.DescribeFailure("Creating the remote desktop session", created.Code));
            var session = created.GetString("session_handle") ?? throw new InvalidOperationException("The portal returned no session handle.");

            var restore = LoadRestoreToken();
            var devices = await _bus.RequestAsync(SelectDevices(session, PortalNames.NewToken(), version, restore), StepTimeout, ct).ConfigureAwait(false);
            if (devices.Code != 0) throw new InvalidOperationException(PortalNames.DescribeFailure("Selecting keyboard and pointer", devices.Code));

            // A screen-cast source gives absolute pointer coordinates; without it only relative motion would work.
            var sources = await _bus.RequestAsync(SelectSources(session, PortalNames.NewToken()), StepTimeout, ct).ConfigureAwait(false);
            if (sources.Code != 0) throw new InvalidOperationException(PortalNames.DescribeFailure("Selecting the screens", sources.Code));

            var started = await _bus.RequestAsync(Start(session, PortalNames.NewToken()), ApprovalTimeout, ct).ConfigureAwait(false);
            if (started.Code != 0)
                throw new InvalidOperationException(PortalNames.DescribeFailure("Remote control of the desktop", started.Code) +
                                                    " Approve DeskPilot in the dialog that asks to control the screen.");

            Streams = ParseStreams(started);
            Devices = started.Results.TryGetValue("devices", out var d) && d is uint u ? u : 0;
            var newToken = started.GetString("restore_token");
            if (!string.IsNullOrEmpty(newToken)) SaveRestoreToken(newToken);
            SessionHandle = session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Forgets the session (e.g. after the user revoked it) so the next action starts a new one.</summary>
    public void Reset()
    {
        SessionHandle = null;
        Streams = Array.Empty<PortalStream>();
    }

    public async Task MoveAsync(double logicalX, double logicalY, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        var mapped = MapPoint(Streams, logicalX, logicalY)
                     ?? throw new InvalidOperationException("The remote desktop session has no screen stream, so the pointer cannot be placed absolutely.");
        await _bus.CallAsync(PointerMotionAbsolute(SessionHandle!, mapped.Stream.NodeId, mapped.X, mapped.Y), ct).ConfigureAwait(false);
    }

    public async Task ButtonAsync(int evdevButton, bool pressed, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        await _bus.CallAsync(PointerButton(SessionHandle!, evdevButton, pressed), ct).ConfigureAwait(false);
    }

    public async Task ScrollAsync(uint axis, int steps, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        await _bus.CallAsync(PointerAxisDiscrete(SessionHandle!, axis, steps), ct).ConfigureAwait(false);
    }

    public async Task KeysymAsync(int keysym, bool pressed, CancellationToken ct)
    {
        await EnsureStartedAsync(ct).ConfigureAwait(false);
        await _bus.CallAsync(KeyboardKeysym(SessionHandle!, keysym, pressed), ct).ConfigureAwait(false);
    }

    internal string? LoadRestoreToken()
    {
        try
        {
            return File.Exists(_tokenFile) ? File.ReadAllText(_tokenFile).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal void SaveRestoreToken(string token)
    {
        try
        {
            var dir = Path.GetDirectoryName(_tokenFile);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_tokenFile, token);
            // The token lets this app control the desktop without asking again: keep it private to the user.
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

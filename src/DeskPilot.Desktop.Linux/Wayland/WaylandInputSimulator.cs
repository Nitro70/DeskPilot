using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>
/// Mouse and keyboard on Wayland, which has no universal input-injection API. The route is chosen per compositor at
/// first use: wlroots (sway, Hyprland, river...) get DeskPilot's own virtual pointer plus wtype; sway and Hyprland can
/// fall back to their IPC; GNOME, KDE and COSMIC use the xdg-desktop-portal RemoteDesktop session (approved once);
/// ydotool and dotool are the last resort anywhere. A route that fails before it ever worked (for example a protocol
/// the compositor advertises but refuses) is dropped for the next one. Coordinates are DeskPilot's physical pixels
/// (see <see cref="WaylandLayout"/>).
/// </summary>
public sealed class WaylandInputSimulator : IInputSimulator
{
    internal const int ClickHoldMs = 30;
    internal const int ClickGapMs = 60;
    internal const int SmoothStepMs = 12;

    private readonly WaylandContext _ctx;
    private readonly string? _forcedPointer;
    private readonly string? _forcedKeyboard;
    private readonly object _gate = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();
    private readonly List<string> _pressedKeys = new();
    private readonly HashSet<string> _failedPointerRoutes = new(StringComparer.Ordinal);
    private readonly HashSet<string> _failedKeyboardRoutes = new(StringComparer.Ordinal);
    private readonly List<string> _pointerErrors = new();
    private readonly List<string> _keyboardErrors = new();
    private IPointerBackend? _pointer;
    private IKeyboardBackend? _keyboard;
    private bool _pointerWorked;
    private bool _keyboardWorked;
    private PortalRemoteDesktop? _remoteDesktop;

    public WaylandInputSimulator(LinuxSessionInfo session) : this(new WaylandContext(session)) { }

    internal WaylandInputSimulator(WaylandContext context, string? pointerRoute = null, string? keyboardRoute = null)
    {
        _ctx = context;
        _forcedPointer = pointerRoute;
        _forcedKeyboard = keyboardRoute;
    }

    /// <summary>Tests: fixed backends instead of route selection.</summary>
    internal WaylandInputSimulator(WaylandContext context, IPointerBackend pointer, IKeyboardBackend keyboard) : this(context)
    {
        _pointer = pointer;
        _keyboard = keyboard;
        _pointerWorked = _keyboardWorked = true;
    }

    /// <summary>The routes in use (after first use), e.g. "virtual-pointer" / "wtype".</summary>
    internal string? PointerRoute => _pointer?.Name;
    internal string? KeyboardRoute => _keyboard?.Name;

    // ------------------------------------------------------------------------------------------- pointer

    public void MoveMouse(int x, int y)
    {
        lock (_gate)
        {
            var layout = Layout();
            var (lx, ly) = layout.ToLogical(x, y);
            UsePointer(p => p.Move(lx, ly, layout));
            WaylandCursorTracker.Set(new ScreenPoint(x, y));
        }
    }

    public void MoveMouseSmooth(int x, int y, int durationMs)
    {
        lock (_gate)
        {
            if (durationMs <= 0)
            {
                MoveMouse(x, y);
                return;
            }
            var from = GetCursorPosition();
            int steps = Math.Clamp(durationMs / SmoothStepMs, 2, 120);
            int pause = Math.Max(1, durationMs / steps);
            for (int i = 1; i <= steps; i++)
            {
                double t = i / (double)steps;
                // Ease in and out so apps see a natural drag.
                double e = t * t * (3 - 2 * t);
                MoveMouse((int)Math.Round(from.X + (x - from.X) * e), (int)Math.Round(from.Y + (y - from.Y) * e));
                if (i < steps) Thread.Sleep(pause);
            }
        }
    }

    public void MouseDown(MouseButton button)
    {
        lock (_gate)
        {
            UsePointer(p => p.Button(button, true));
            _pressedButtons.Add(button);
        }
    }

    public void MouseUp(MouseButton button)
    {
        lock (_gate)
        {
            UsePointer(p => p.Button(button, false));
            _pressedButtons.Remove(button);
        }
    }

    public void Click(MouseButton button, int clicks)
    {
        clicks = Math.Clamp(clicks, 1, 3);
        lock (_gate)
        {
            UsePointer(p =>
            {
                for (int i = 0; i < clicks; i++)
                {
                    p.Button(button, true);
                    _pressedButtons.Add(button);
                    Thread.Sleep(ClickHoldMs);
                    p.Button(button, false);
                    _pressedButtons.Remove(button);
                    if (i < clicks - 1) Thread.Sleep(ClickGapMs);
                }
            });
        }
    }

    public void Scroll(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate) UsePointer(p => p.Scroll(dx, dy));
    }

    public ScreenPoint GetCursorPosition()
    {
        var real = _ctx.QueryCursor();
        if (real is { } r) return r;
        if (WaylandCursorTracker.Last is { } last) return last;
        // Unknown: a neutral point well away from the failsafe corner.
        var layout = _ctx.TryGetLayout();
        return layout?.PrimaryCenter ?? new ScreenPoint(200, 200);
    }

    // ------------------------------------------------------------------------------------------- keyboard

    public void TypeText(string text, int delayMsPerChar)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate) UseKeyboard(k => k.Type(text, Math.Max(0, delayMsPerChar)));
    }

    public void PressCombo(KeyCombo combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        if (combo.Key != null && !XkbKeys.TryResolve(combo.Key, out _, out var error)) throw new ArgumentException(error);
        lock (_gate) UseKeyboard(k => k.Combo(combo.Modifiers, combo.Key));
    }

    public void KeyDown(string key)
    {
        var name = Normalize(key);
        lock (_gate)
        {
            UseKeyboard(k => k.KeyDown(name));
            if (!_pressedKeys.Contains(name)) _pressedKeys.Add(name);
        }
    }

    public void KeyUp(string key)
    {
        var name = Normalize(key);
        lock (_gate)
        {
            UseKeyboard(k => k.KeyUp(name));
            _pressedKeys.Remove(name);
        }
    }

    private static string Normalize(string key)
    {
        var name = KeyCombo.NormalizeName(key ?? "");
        if (!XkbKeys.TryResolve(name, out _, out var error)) throw new ArgumentException(error, nameof(key));
        return name;
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            if (_keyboard != null)
            {
                try { _keyboard.ReleaseAll(); } catch (Exception) { }
            }
            _pressedKeys.Clear();
            if (_pointer != null)
            {
                foreach (var b in _pressedButtons.ToList())
                {
                    try { _pointer.Button(b, false); } catch (Exception) { }
                }
            }
            _pressedButtons.Clear();
        }
    }

    internal IReadOnlyCollection<MouseButton> PressedButtons { get { lock (_gate) return _pressedButtons.ToList(); } }
    internal IReadOnlyCollection<string> PressedKeys { get { lock (_gate) return _pressedKeys.ToList(); } }

    // ------------------------------------------------------------------------------------------- route selection

    private WaylandLayout Layout() =>
        _ctx.TryGetLayout() ?? _ctx.CaptureLayout ?? throw new InvalidOperationException(
            $"Could not read the screen layout of {_ctx.DesktopName}. Take a screenshot first, or install wlr-randr (wlroots) or kscreen-doctor (KDE).");

    private static bool IsRouteFailure(Exception ex) => ex is WaylandProtocolException or TimeoutException or IOException or InvalidOperationException;

    private void UsePointer(Action<IPointerBackend> action)
    {
        while (true)
        {
            var p = Pointer();
            try
            {
                action(p);
                _pointerWorked = true;
                return;
            }
            catch (Exception ex) when (!_pointerWorked && IsRouteFailure(ex))
            {
                _failedPointerRoutes.Add(p.Name);
                _pointerErrors.Add($"{p.Name}: {ex.Message}");
                try { p.Dispose(); } catch (Exception) { }
                _pointer = null;
                if (!PointerCandidates().Any(r => !_failedPointerRoutes.Contains(r)))
                    throw new InvalidOperationException(PointerHelp(_ctx) + " Details: " + string.Join("; ", _pointerErrors), ex);
            }
        }
    }

    private void UseKeyboard(Action<IKeyboardBackend> action)
    {
        while (true)
        {
            var k = Keyboard();
            try
            {
                action(k);
                _keyboardWorked = true;
                return;
            }
            catch (Exception ex) when (!_keyboardWorked && IsRouteFailure(ex))
            {
                _failedKeyboardRoutes.Add(k.Name);
                _keyboardErrors.Add($"{k.Name}: {ex.Message}");
                try { k.Dispose(); } catch (Exception) { }
                _keyboard = null;
                if (!KeyboardCandidates().Any(r => !_failedKeyboardRoutes.Contains(r)))
                    throw new InvalidOperationException(KeyboardHelp(_ctx) + " Details: " + string.Join("; ", _keyboardErrors), ex);
            }
        }
    }

    private IPointerBackend Pointer()
    {
        if (_pointer != null) return _pointer;
        var errors = new List<string>(_pointerErrors);
        foreach (var route in PointerCandidates().Where(r => !_failedPointerRoutes.Contains(r)))
        {
            try
            {
                var backend = CreatePointer(route);
                if (backend != null) return _pointer = backend;
            }
            catch (Exception ex) when (IsRouteFailure(ex))
            {
                errors.Add($"{route}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(PointerHelp(_ctx) + (errors.Count > 0 ? " Details: " + string.Join("; ", errors) : ""));
    }

    private IKeyboardBackend Keyboard()
    {
        if (_keyboard != null) return _keyboard;
        var errors = new List<string>(_keyboardErrors);
        foreach (var route in KeyboardCandidates().Where(r => !_failedKeyboardRoutes.Contains(r)))
        {
            try
            {
                var backend = CreateKeyboard(route);
                if (backend != null) return _keyboard = backend;
            }
            catch (Exception ex) when (IsRouteFailure(ex))
            {
                errors.Add($"{route}: {ex.Message}");
            }
        }
        throw new InvalidOperationException(KeyboardHelp(_ctx) + (errors.Count > 0 ? " Details: " + string.Join("; ", errors) : ""));
    }


    /// <summary>Pointer routes in order of preference for this desktop.</summary>
    internal IEnumerable<string> PointerCandidates()
    {
        if (_forcedPointer != null) return new[] { _forcedPointer };
        return PointerOrder(_ctx.Desktop, _ctx.HasGlobal(VirtualPointerDevice.ManagerInterface));
    }

    internal static IReadOnlyList<string> PointerOrder(WaylandDesktop desktop, bool hasVirtualPointer)
    {
        var list = new List<string>();
        if (hasVirtualPointer) list.Add(InputRoutes.VirtualPointer);
        switch (desktop)
        {
            case WaylandDesktop.Sway: list.Add(InputRoutes.Sway); break;
            case WaylandDesktop.Hyprland: list.Add(InputRoutes.Hyprland); break;
        }
        if (hasVirtualPointer) list.Add(InputRoutes.Wlrctl);
        if (desktop is not (WaylandDesktop.Sway or WaylandDesktop.Hyprland or WaylandDesktop.Wlroots)) list.Add(InputRoutes.Portal);
        list.Add(InputRoutes.Ydotool);
        list.Add(InputRoutes.Dotool);
        return list;
    }

    internal IEnumerable<string> KeyboardCandidates()
    {
        if (_forcedKeyboard != null) return new[] { _forcedKeyboard };
        return KeyboardOrder(_ctx.Desktop, _ctx.HasGlobal("zwp_virtual_keyboard_manager_v1"));
    }

    internal static IReadOnlyList<string> KeyboardOrder(WaylandDesktop desktop, bool hasVirtualKeyboard)
    {
        var list = new List<string>();
        if (hasVirtualKeyboard) list.Add(InputRoutes.Wtype);
        if (desktop is not (WaylandDesktop.Sway or WaylandDesktop.Hyprland or WaylandDesktop.Wlroots)) list.Add(InputRoutes.Portal);
        list.Add(InputRoutes.Ydotool);
        list.Add(InputRoutes.Dotool);
        return list;
    }

    private IPointerBackend? CreatePointer(string route)
    {
        var runner = _ctx.Runner;
        switch (route)
        {
            case InputRoutes.VirtualPointer:
                return _ctx.SocketPath == null ? null : new VirtualPointerBackend(_ctx.SocketPath);
            case InputRoutes.Sway:
                return _ctx.Sway is { Available: true } ipc ? new SwayPointerBackend(ipc) : null;
            case InputRoutes.Hyprland:
                if (runner.Find("hyprctl") == null) return null;
                IPointerBackend? buttons = runner.Find("wlrctl") != null && _ctx.HasGlobal(VirtualPointerDevice.ManagerInterface) ? new WlrctlPointerBackend(runner)
                    : runner.Find("ydotool") != null && InputRoutes.YdotoolSocket(_ctx.Env) != null ? new YdotoolPointerBackend(runner) : null;
                return new HyprlandPointerBackend(runner, buttons);
            case InputRoutes.Wlrctl:
                return runner.Find("wlrctl") != null ? new WlrctlPointerBackend(runner) : null;
            case InputRoutes.Portal:
                var rd = RemoteDesktop();
                InputRoutes.Sync(() => rd.EnsureStartedAsync(CancellationToken.None));
                return new PortalPointerBackend(rd);
            case InputRoutes.Ydotool:
                return runner.Find("ydotool") != null && InputRoutes.YdotoolSocket(_ctx.Env) != null ? new YdotoolPointerBackend(runner) : null;
            case InputRoutes.Dotool:
                return runner.Find("dotool") != null ? new DotoolPointerBackend(runner) : null;
            default:
                throw new InvalidOperationException($"Unknown pointer route '{route}'.");
        }
    }

    private IKeyboardBackend? CreateKeyboard(string route)
    {
        var runner = _ctx.Runner;
        switch (route)
        {
            case InputRoutes.Wtype:
                return runner.Find("wtype") != null ? new WtypeKeyboardBackend(runner) : null;
            case InputRoutes.Portal:
                var rd = RemoteDesktop();
                InputRoutes.Sync(() => rd.EnsureStartedAsync(CancellationToken.None));
                return new PortalKeyboardBackend(rd);
            case InputRoutes.Ydotool:
                return runner.Find("ydotool") != null && InputRoutes.YdotoolSocket(_ctx.Env) != null ? new YdotoolKeyboardBackend(runner) : null;
            case InputRoutes.Dotool:
                return runner.Find("dotool") != null ? new DotoolKeyboardBackend(runner) : null;
            default:
                throw new InvalidOperationException($"Unknown keyboard route '{route}'.");
        }
    }

    private PortalRemoteDesktop RemoteDesktop() => _remoteDesktop ??= new PortalRemoteDesktop(_ctx.Portal);

    internal static string PointerHelp(WaylandContext ctx) => ctx.IsWlrootsFamily
        ? $"Wayland mouse control is not available on {ctx.DesktopName}. DeskPilot uses the compositor's virtual-pointer protocol; " +
          "if it is missing, install ydotool and start ydotoold (sudo apt install ydotool / sudo dnf install ydotool / sudo pacman -S ydotool)."
        : $"Wayland mouse control on {ctx.DesktopName} needs xdg-desktop-portal with remote desktop support (approve the \"remote control\" dialog when it appears), " +
          "or ydotool with ydotoold running (sudo apt install ydotool / sudo dnf install ydotool / sudo pacman -S ydotool).";

    internal static string KeyboardHelp(WaylandContext ctx) => ctx.IsWlrootsFamily
        ? $"Wayland keyboard control on {ctx.DesktopName} needs wtype (sudo apt install wtype / sudo dnf install wtype / sudo pacman -S wtype)."
        : $"Wayland keyboard control on {ctx.DesktopName} needs xdg-desktop-portal with remote desktop support (approve the \"remote control\" dialog when it appears), " +
          "or ydotool with ydotoold running (sudo apt install ydotool / sudo dnf install ydotool / sudo pacman -S ydotool).";
}

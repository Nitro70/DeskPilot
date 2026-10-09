using System.Globalization;
using System.Text;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>Moves and clicks the pointer. Coordinates are logical layout pixels (what Wayland compositors use).</summary>
internal interface IPointerBackend : IDisposable
{
    string Name { get; }
    void Move(double logicalX, double logicalY, WaylandLayout layout);
    void Button(MouseButton button, bool pressed);
    /// <summary>One wheel notch per unit; positive dy scrolls down, positive dx right.</summary>
    void Scroll(int dx, int dy);
}

/// <summary>Types text and presses keys.</summary>
internal interface IKeyboardBackend : IDisposable
{
    string Name { get; }
    void Type(string text, int delayMsPerChar);
    /// <summary>Presses modifiers (normalized names: ctrl, alt, shift, win), taps the key, releases in reverse.</summary>
    void Combo(IReadOnlyList<string> modifiers, string? key);
    void KeyDown(string normalizedKey);
    void KeyUp(string normalizedKey);
    void ReleaseAll();
}

internal static class InputRoutes
{
    public const string VirtualPointer = "virtual-pointer";
    public const string Sway = "sway";
    public const string Hyprland = "hyprland";
    public const string Wlrctl = "wlrctl";
    public const string Portal = "portal";
    public const string Ydotool = "ydotool";
    public const string Dotool = "dotool";
    public const string Wtype = "wtype";

    public static int EvdevButton(MouseButton b) => b switch
    {
        MouseButton.Right => (int)VirtualPointerDevice.BtnRight,
        MouseButton.Middle => (int)VirtualPointerDevice.BtnMiddle,
        _ => (int)VirtualPointerDevice.BtnLeft,
    };

    public static string Inv(double v) => Math.Round(v, 2).ToString("0.##", CultureInfo.InvariantCulture);

    public static T Sync<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    public static void Sync(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>ydotoold's socket, which ydotool needs (it talks to the daemon, which owns /dev/uinput).</summary>
    public static string? YdotoolSocket(Func<string, string?> env)
    {
        var configured = env("YDOTOOL_SOCKET");
        if (!string.IsNullOrEmpty(configured)) return File.Exists(configured) ? configured : null;
        var runtime = env("XDG_RUNTIME_DIR");
        foreach (var candidate in new[] { runtime == null ? null : Path.Combine(runtime, ".ydotool_socket"), "/tmp/.ydotool_socket" })
            if (candidate != null && File.Exists(candidate)) return candidate;
        return null;
    }

    public static void Check(CommandResult r, string tool, string what)
    {
        if (!r.Ok) throw new InvalidOperationException($"Could not {what}: {r.Describe(tool)}");
    }
}

// ------------------------------------------------------------------------------------------------ pointer routes

/// <summary>wlroots compositors: our own long-lived virtual pointer (absolute coordinates, press/release, wheel).</summary>
internal sealed class VirtualPointerBackend : IPointerBackend
{
    private readonly VirtualPointerDevice _device;

    public VirtualPointerBackend(string socketPath) => _device = new VirtualPointerDevice(socketPath);

    public string Name => InputRoutes.VirtualPointer;

    public void Move(double lx, double ly, WaylandLayout layout)
    {
        var box = layout.LogicalBox;
        _device.MoveAbsolute(lx - box.X, ly - box.Y, box.Width, box.Height);
    }

    public void Button(MouseButton button, bool pressed) => _device.Button((uint)InputRoutes.EvdevButton(button), pressed);

    public void Scroll(int dx, int dy)
    {
        for (int i = 0; i < Math.Abs(dy); i++) { _device.Notch(0, Math.Sign(dy)); Thread.Sleep(8); }
        for (int i = 0; i < Math.Abs(dx); i++) { _device.Notch(1, Math.Sign(dx)); Thread.Sleep(8); }
    }

    public void Dispose() => _device.Dispose();
}

/// <summary>sway's own seat cursor commands (works whenever the seat has a pointer, e.g. a real mouse).</summary>
internal sealed class SwayPointerBackend : IPointerBackend
{
    private readonly SwayIpc _ipc;

    public SwayPointerBackend(SwayIpc ipc) => _ipc = ipc;

    public string Name => InputRoutes.Sway;

    /// <summary>"seat - cursor set" takes coordinates relative to the layout's origin.</summary>
    internal static string MoveCommand(double lx, double ly, WaylandLayout layout) =>
        $"seat - cursor set {InputRoutes.Inv(lx - layout.LogicalBox.X)} {InputRoutes.Inv(ly - layout.LogicalBox.Y)}";

    /// <summary>X11-style button names, as sway-input(5) documents: button1 left, button2 middle, button3 right.</summary>
    internal static string ButtonCommand(MouseButton b, bool pressed) =>
        $"seat - cursor {(pressed ? "press" : "release")} {b switch { MouseButton.Right => "button3", MouseButton.Middle => "button2", _ => "button1" }}";

    /// <summary>button4-7 are scroll up, down, left, right (one notch each).</summary>
    internal static IEnumerable<string> ScrollCommands(int dx, int dy)
    {
        for (int i = 0; i < Math.Abs(dy); i++) yield return $"seat - cursor press {(dy > 0 ? "button5" : "button4")}";
        for (int i = 0; i < Math.Abs(dx); i++) yield return $"seat - cursor press {(dx > 0 ? "button7" : "button6")}";
    }

    public void Move(double lx, double ly, WaylandLayout layout) => Run(MoveCommand(lx, ly, layout), "move the pointer");

    public void Button(MouseButton button, bool pressed) => Run(ButtonCommand(button, pressed), pressed ? "press the mouse button" : "release the mouse button");

    public void Scroll(int dx, int dy)
    {
        foreach (var cmd in ScrollCommands(dx, dy)) Run(cmd, "scroll");
    }

    private void Run(string command, string what)
    {
        if (!_ipc.Command(command, out var error)) throw new InvalidOperationException($"sway could not {what}: {error}");
    }

    public void Dispose() { }
}

/// <summary>Hyprland without the virtual pointer: hyprctl moves the cursor, wlrctl or ydotool click.</summary>
internal sealed class HyprlandPointerBackend : IPointerBackend
{
    private readonly ICommandRunner _runner;
    private readonly IPointerBackend? _buttons;

    public HyprlandPointerBackend(ICommandRunner runner, IPointerBackend? buttons)
    {
        _runner = runner;
        _buttons = buttons;
    }

    public string Name => InputRoutes.Hyprland;

    public void Move(double lx, double ly, WaylandLayout layout)
    {
        var r = _runner.Run("hyprctl", new[] { "dispatch", "movecursor", ((int)Math.Round(lx)).ToString(CultureInfo.InvariantCulture), ((int)Math.Round(ly)).ToString(CultureInfo.InvariantCulture) }, 3000);
        InputRoutes.Check(r, "hyprctl", "move the pointer");
    }

    public void Button(MouseButton button, bool pressed) => Buttons.Button(button, pressed);

    public void Scroll(int dx, int dy) => Buttons.Scroll(dx, dy);

    private IPointerBackend Buttons => _buttons ?? throw new InvalidOperationException(
        "Hyprland can move the pointer with hyprctl but needs wlrctl or ydotool (with ydotoold running) to click.");

    public void Dispose() => _buttons?.Dispose();
}

/// <summary>wlrctl: relative moves only, so every move first pushes the pointer into the layout's top-left corner.</summary>
internal sealed class WlrctlPointerBackend : IPointerBackend
{
    private readonly ICommandRunner _runner;

    public WlrctlPointerBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Wlrctl;

    public void Move(double lx, double ly, WaylandLayout layout)
    {
        var box = layout.LogicalBox;
        int big = (int)Math.Max(box.Width, box.Height) * 4 + 1000;
        Run("move", (-big).ToString(CultureInfo.InvariantCulture), (-big).ToString(CultureInfo.InvariantCulture));
        Run("move", InputRoutes.Inv(lx - box.X), InputRoutes.Inv(ly - box.Y));
    }

    public void Button(MouseButton button, bool pressed)
    {
        // wlrctl can only click; a click on release keeps simple clicks working, drags need another route.
        if (pressed) return;
        Run("click", button switch { MouseButton.Right => "right", MouseButton.Middle => "middle", _ => "left" });
    }

    public void Scroll(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        Run("scroll", (dy * 15).ToString(CultureInfo.InvariantCulture), (dx * 15).ToString(CultureInfo.InvariantCulture));
    }

    private void Run(params string[] args)
    {
        var all = new List<string> { "pointer" };
        all.AddRange(args);
        InputRoutes.Check(_runner.Run("wlrctl", all, 3000), "wlrctl", "control the pointer");
    }

    public void Dispose() { }
}

/// <summary>GNOME, KDE, COSMIC: xdg-desktop-portal RemoteDesktop (absolute motion through a screen-cast stream).</summary>
internal sealed class PortalPointerBackend : IPointerBackend
{
    private readonly PortalRemoteDesktop _rd;

    public PortalPointerBackend(PortalRemoteDesktop rd) => _rd = rd;

    public string Name => InputRoutes.Portal;

    public void Move(double lx, double ly, WaylandLayout layout) =>
        InputRoutes.Sync(async () =>
        {
            await _rd.EnsureStartedAsync(CancellationToken.None).ConfigureAwait(false);
            var (x, y) = ToStreamSpace(lx, ly, layout, _rd.Streams);
            await _rd.MoveAsync(x, y, CancellationToken.None).ConfigureAwait(false);
        });

    /// <summary>
    /// Streams are in the compositor's logical layout. When DeskPilot's layout came from a screenshot (no output
    /// listing), its coordinates are screenshot pixels: rescale them onto the streams' bounding box.
    /// </summary>
    internal static (double X, double Y) ToStreamSpace(double lx, double ly, WaylandLayout layout, IReadOnlyList<PortalStream> streams)
    {
        if (streams.Count == 0) return (lx, ly);
        double sx = streams.Min(s => s.X), sy = streams.Min(s => s.Y);
        double sw = streams.Max(s => s.X + s.Width) - sx, sh = streams.Max(s => s.Y + s.Height) - sy;
        var box = layout.LogicalBox;
        if (sw <= 0 || sh <= 0 || box.Width <= 0 || box.Height <= 0) return (lx, ly);
        bool same = Math.Abs(sw - box.Width) <= Math.Max(2, box.Width * 0.02) && Math.Abs(sh - box.Height) <= Math.Max(2, box.Height * 0.02);
        if (same) return (lx, ly);
        return (sx + (lx - box.X) * sw / box.Width, sy + (ly - box.Y) * sh / box.Height);
    }

    public void Button(MouseButton button, bool pressed) =>
        InputRoutes.Sync(() => _rd.ButtonAsync(InputRoutes.EvdevButton(button), pressed, CancellationToken.None));

    public void Scroll(int dx, int dy) => InputRoutes.Sync(async () =>
    {
        if (dy != 0) await _rd.ScrollAsync(0, dy, CancellationToken.None).ConfigureAwait(false);
        if (dx != 0) await _rd.ScrollAsync(1, dx, CancellationToken.None).ConfigureAwait(false);
    });

    public void Dispose() { }
}

/// <summary>ydotool (uinput through ydotoold). Absolute moves are emulated by ydotool and can be skewed by pointer acceleration.</summary>
internal sealed class YdotoolPointerBackend : IPointerBackend
{
    private readonly ICommandRunner _runner;

    public YdotoolPointerBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Ydotool;

    public void Move(double lx, double ly, WaylandLayout layout)
    {
        var box = layout.LogicalBox;
        Run("mousemove", "--absolute", "-x", ((int)Math.Round(lx - box.X)).ToString(CultureInfo.InvariantCulture), "-y", ((int)Math.Round(ly - box.Y)).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>ydotool click codes: 0x00 left, 0x01 right, 0x02 middle; 0x40 = down, 0x80 = up.</summary>
    internal static string ButtonCode(MouseButton b, bool pressed)
    {
        int code = b switch { MouseButton.Right => 1, MouseButton.Middle => 2, _ => 0 };
        return "0x" + ((pressed ? 0x40 : 0x80) | code).ToString("X2", CultureInfo.InvariantCulture);
    }

    public void Button(MouseButton button, bool pressed) => Run("click", ButtonCode(button, pressed));

    public void Scroll(int dx, int dy)
    {
        // The wheel axis counts up as positive.
        if (dy != 0) Run("mousemove", "--wheel", "-x", "0", "-y", (-dy).ToString(CultureInfo.InvariantCulture));
        if (dx != 0) Run("mousemove", "--wheel", "-x", dx.ToString(CultureInfo.InvariantCulture), "-y", "0");
    }

    private void Run(params string[] args) => InputRoutes.Check(_runner.Run("ydotool", args, 5000), "ydotool", "control the pointer");

    public void Dispose() { }
}

/// <summary>dotool (uinput, reads commands on stdin). Its absolute moves take fractions of the whole layout.</summary>
internal sealed class DotoolPointerBackend : IPointerBackend
{
    private readonly ICommandRunner _runner;

    public DotoolPointerBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Dotool;

    public void Move(double lx, double ly, WaylandLayout layout)
    {
        var box = layout.LogicalBox;
        double fx = box.Width <= 0 ? 0 : (lx - box.X) / box.Width, fy = box.Height <= 0 ? 0 : (ly - box.Y) / box.Height;
        DotoolKeyboardBackend.Send(_runner, $"mouseto {Math.Clamp(fx, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture)} {Math.Clamp(fy, 0, 1).ToString("0.#####", CultureInfo.InvariantCulture)}");
    }

    public void Button(MouseButton button, bool pressed) =>
        DotoolKeyboardBackend.Send(_runner, $"{(pressed ? "buttondown" : "buttonup")} {button switch { MouseButton.Right => "right", MouseButton.Middle => "middle", _ => "left" }}");

    public void Scroll(int dx, int dy)
    {
        var sb = new StringBuilder();
        if (dy != 0) sb.Append("wheel ").Append((-dy).ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (dx != 0) sb.Append("hwheel ").Append(dx.ToString(CultureInfo.InvariantCulture)).Append('\n');
        if (sb.Length > 0) DotoolKeyboardBackend.Send(_runner, sb.ToString());
    }

    public void Dispose() { }
}

// ------------------------------------------------------------------------------------------------ keyboard routes

/// <summary>
/// wtype (virtual keyboard protocol on wlroots). It builds its own keymap per run, so any Unicode text types. Keys
/// held with KeyDown are kept by a wtype process that sleeps until KeyUp stops it.
/// </summary>
internal sealed class WtypeKeyboardBackend : IKeyboardBackend
{
    private const int HoldMs = 3_600_000;
    private const int HoldSettleMs = 120;

    private readonly ICommandRunner _runner;
    private readonly object _gate = new();
    private readonly List<(string Key, IRunningProcess Process)> _held = new();

    public WtypeKeyboardBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Wtype;

    /// <summary>Arguments to type text. "--" makes wtype treat the rest literally (text may start with '-').</summary>
    internal static List<string>? TypeArgs(string text, int delayMsPerChar)
    {
        var sb = new StringBuilder();
        foreach (var cp in XkbKeys.CodePoints(text))
        {
            if (cp == 0 || (cp < 0x20 && cp != '\n' && cp != '\t')) continue;
            sb.Append(char.ConvertFromUtf32(cp));
        }
        if (sb.Length == 0) return null;
        var args = new List<string>();
        if (delayMsPerChar > 0) { args.Add("-d"); args.Add(delayMsPerChar.ToString(CultureInfo.InvariantCulture)); }
        args.Add("--");
        args.Add(sb.ToString());
        return args;
    }

    /// <summary>Arguments for a combination: -M mod... -k key -m mod... (a modifier-only combo taps the last modifier).</summary>
    internal static List<string> ComboArgs(IReadOnlyList<string> modifiers, string? key)
    {
        var mods = modifiers.ToList();
        string keyName;
        if (key != null) keyName = XkbKeys.Resolve(key).KeysymName;
        else if (mods.Count > 0)
        {
            keyName = XkbKeys.Modifier(mods[^1]).KeysymName;
            mods.RemoveAt(mods.Count - 1);
        }
        else throw new ArgumentException("A key combination needs at least one key.");

        var args = new List<string>();
        foreach (var m in mods) { args.Add("-M"); args.Add(XkbKeys.WtypeModifier(m)); }
        args.Add("-k");
        args.Add(keyName);
        for (int i = mods.Count - 1; i >= 0; i--) { args.Add("-m"); args.Add(XkbKeys.WtypeModifier(mods[i])); }
        return args;
    }

    internal static List<string> HoldArgs(string normalizedKey)
    {
        var k = XkbKeys.Resolve(normalizedKey);
        var args = k.IsModifier
            ? new List<string> { "-M", XkbKeys.WtypeModifier(KeyCombo.NormalizeName(normalizedKey)) }
            : new List<string> { "-P", k.KeysymName };
        args.Add("-s");
        args.Add(HoldMs.ToString(CultureInfo.InvariantCulture));
        return args;
    }

    public void Type(string text, int delayMsPerChar)
    {
        var args = TypeArgs(text, delayMsPerChar);
        if (args == null) return;
        int timeout = 10_000 + text.Length * (Math.Max(0, delayMsPerChar) + 30);
        InputRoutes.Check(_runner.Run("wtype", args, timeout), "wtype", "type");
    }

    public void Combo(IReadOnlyList<string> modifiers, string? key) =>
        InputRoutes.Check(_runner.Run("wtype", ComboArgs(modifiers, key), 5000), "wtype", "press the keys");

    public void KeyDown(string normalizedKey)
    {
        lock (_gate)
        {
            if (_held.Any(h => h.Key == normalizedKey && !h.Process.HasExited)) return;
            var p = _runner.Start("wtype", HoldArgs(normalizedKey))
                    ?? throw new InvalidOperationException("Could not start wtype to hold a key down.");
            _held.Add((normalizedKey, p));
        }
        Thread.Sleep(HoldSettleMs);
    }

    public void KeyUp(string normalizedKey)
    {
        List<(string Key, IRunningProcess Process)> release;
        lock (_gate)
        {
            release = _held.Where(h => h.Key == normalizedKey).ToList();
            _held.RemoveAll(h => h.Key == normalizedKey);
        }
        foreach (var h in release) h.Process.Dispose();
        if (release.Count > 0 && XkbKeys.TryResolve(normalizedKey, out var k, out _) && k.IsModifier)
            ClearModifiers(new[] { KeyCombo.NormalizeName(normalizedKey) });
    }

    public void ReleaseAll()
    {
        List<(string Key, IRunningProcess Process)> all;
        lock (_gate)
        {
            all = _held.ToList();
            _held.Clear();
        }
        foreach (var h in all) h.Process.Dispose();
        if (all.Count > 0) ClearModifiers(new[] { "ctrl", "shift", "alt", "win" });
    }

    /// <summary>
    /// When a held key's wtype exits, the compositor releases its keys; a quick "-m" run also resets the modifier state
    /// apps see when no other keyboard takes over.
    /// </summary>
    private void ClearModifiers(IEnumerable<string> modifiers)
    {
        var args = new List<string>();
        foreach (var m in modifiers) { args.Add("-m"); args.Add(XkbKeys.WtypeModifier(m)); }
        _runner.Run("wtype", args, 3000);
    }

    public void Dispose() => ReleaseAll();
}

/// <summary>xdg-desktop-portal RemoteDesktop keysyms: Unicode text works for any character with a keysym.</summary>
internal sealed class PortalKeyboardBackend : IKeyboardBackend
{
    private readonly PortalRemoteDesktop _rd;
    private readonly object _gate = new();
    private readonly List<int> _held = new();

    public PortalKeyboardBackend(PortalRemoteDesktop rd) => _rd = rd;

    public string Name => InputRoutes.Portal;

    public void Type(string text, int delayMsPerChar) => InputRoutes.Sync(async () =>
    {
        foreach (var cp in XkbKeys.CodePoints(text))
        {
            if (cp == 0 || (cp < 0x20 && cp != '\n' && cp != '\t')) continue;
            int sym = XkbKeys.KeysymForCodePoint(cp);
            await _rd.KeysymAsync(sym, true, CancellationToken.None).ConfigureAwait(false);
            await _rd.KeysymAsync(sym, false, CancellationToken.None).ConfigureAwait(false);
            if (delayMsPerChar > 0) await Task.Delay(delayMsPerChar).ConfigureAwait(false);
        }
    });

    /// <summary>The keysym press/release sequence for a combination.</summary>
    internal static List<(int Keysym, bool Pressed)> ComboSequence(IReadOnlyList<string> modifiers, string? key)
    {
        var seq = new List<(int, bool)>();
        var mods = modifiers.Select(m => XkbKeys.Modifier(m).Keysym).ToList();
        foreach (var m in mods) seq.Add((m, true));
        if (key != null)
        {
            int k = XkbKeys.Resolve(key).Keysym;
            seq.Add((k, true));
            seq.Add((k, false));
        }
        for (int i = mods.Count - 1; i >= 0; i--) seq.Add((mods[i], false));
        return seq;
    }

    public void Combo(IReadOnlyList<string> modifiers, string? key) => InputRoutes.Sync(async () =>
    {
        foreach (var (sym, pressed) in ComboSequence(modifiers, key))
        {
            await _rd.KeysymAsync(sym, pressed, CancellationToken.None).ConfigureAwait(false);
            await Task.Delay(8).ConfigureAwait(false);
        }
    });

    public void KeyDown(string normalizedKey)
    {
        int sym = XkbKeys.Resolve(normalizedKey).Keysym;
        InputRoutes.Sync(() => _rd.KeysymAsync(sym, true, CancellationToken.None));
        lock (_gate) if (!_held.Contains(sym)) _held.Add(sym);
    }

    public void KeyUp(string normalizedKey)
    {
        int sym = XkbKeys.Resolve(normalizedKey).Keysym;
        InputRoutes.Sync(() => _rd.KeysymAsync(sym, false, CancellationToken.None));
        lock (_gate) _held.Remove(sym);
    }

    public void ReleaseAll()
    {
        List<int> held;
        lock (_gate)
        {
            held = _held.ToList();
            _held.Clear();
        }
        for (int i = held.Count - 1; i >= 0; i--)
        {
            try { InputRoutes.Sync(() => _rd.KeysymAsync(held[i], false, CancellationToken.None)); }
            catch (Exception) { }
        }
    }

    public void Dispose() { }
}

/// <summary>ydotool keyboard: evdev key codes (US layout for text, so ASCII only).</summary>
internal sealed class YdotoolKeyboardBackend : IKeyboardBackend
{
    private readonly ICommandRunner _runner;
    private readonly object _gate = new();
    private readonly List<int> _held = new();

    public YdotoolKeyboardBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Ydotool;

    public void Type(string text, int delayMsPerChar)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Any(c => c > 0x7e && c != '\n' && c != '\t'))
            throw new InvalidOperationException("ydotool can only type ASCII text. Install wtype (wlroots) or use xdg-desktop-portal for other characters.");
        var args = new List<string> { "type" };
        if (delayMsPerChar > 0) { args.Add("--key-delay"); args.Add(delayMsPerChar.ToString(CultureInfo.InvariantCulture)); }
        args.Add("--");
        args.Add(t);
        InputRoutes.Check(_runner.Run("ydotool", args, 10_000 + t.Length * (delayMsPerChar + 30)), "ydotool", "type");
    }

    internal static List<string> ComboArgs(IReadOnlyList<string> modifiers, string? key)
    {
        var codes = modifiers.Select(m => XkbKeys.Modifier(m).EvdevCode).ToList();
        if (key != null)
        {
            var k = XkbKeys.Resolve(key);
            if (k.EvdevCode == 0) throw new InvalidOperationException($"ydotool has no key code for '{key}'.");
            codes.Add(k.EvdevCode);
        }
        var args = new List<string> { "key" };
        args.AddRange(codes.Select(c => $"{c}:1"));
        for (int i = codes.Count - 1; i >= 0; i--) args.Add($"{codes[i]}:0");
        return args;
    }

    public void Combo(IReadOnlyList<string> modifiers, string? key) =>
        InputRoutes.Check(_runner.Run("ydotool", ComboArgs(modifiers, key), 5000), "ydotool", "press the keys");

    public void KeyDown(string normalizedKey)
    {
        int code = Code(normalizedKey);
        InputRoutes.Check(_runner.Run("ydotool", new[] { "key", $"{code}:1" }, 5000), "ydotool", "press a key");
        lock (_gate) if (!_held.Contains(code)) _held.Add(code);
    }

    public void KeyUp(string normalizedKey)
    {
        int code = Code(normalizedKey);
        InputRoutes.Check(_runner.Run("ydotool", new[] { "key", $"{code}:0" }, 5000), "ydotool", "release a key");
        lock (_gate) _held.Remove(code);
    }

    private static int Code(string key)
    {
        var k = XkbKeys.Resolve(key);
        return k.EvdevCode != 0 ? k.EvdevCode : throw new InvalidOperationException($"ydotool has no key code for '{key}'.");
    }

    public void ReleaseAll()
    {
        List<int> held;
        lock (_gate)
        {
            held = _held.ToList();
            _held.Clear();
        }
        if (held.Count == 0) return;
        var args = new List<string> { "key" };
        for (int i = held.Count - 1; i >= 0; i--) args.Add($"{held[i]}:0");
        _runner.Run("ydotool", args, 5000);
    }

    public void Dispose() => ReleaseAll();
}

/// <summary>dotool keyboard: commands on stdin; "type" handles text in the configured keyboard layout.</summary>
internal sealed class DotoolKeyboardBackend : IKeyboardBackend
{
    private readonly ICommandRunner _runner;
    private readonly object _gate = new();
    private readonly List<string> _held = new();

    public DotoolKeyboardBackend(ICommandRunner runner) => _runner = runner;

    public string Name => InputRoutes.Dotool;

    internal static void Send(ICommandRunner runner, string commands)
    {
        var text = commands.EndsWith('\n') ? commands : commands + "\n";
        InputRoutes.Check(runner.Run("dotool", Array.Empty<string>(), 10_000 + text.Length * 20, Encoding.UTF8.GetBytes(text)), "dotool", "send input");
    }

    /// <summary>dotool key names: k:CODE for evdev codes, joined with '+'.</summary>
    internal static string KeySpec(IReadOnlyList<string> modifiers, string? key)
    {
        var parts = modifiers.Select(m => "k:" + XkbKeys.Modifier(m).EvdevCode.ToString(CultureInfo.InvariantCulture)).ToList();
        if (key != null)
        {
            var k = XkbKeys.Resolve(key);
            parts.Add(k.EvdevCode != 0 ? "k:" + k.EvdevCode.ToString(CultureInfo.InvariantCulture) : "x:" + k.KeysymName);
        }
        return string.Join("+", parts);
    }

    public void Type(string text, int delayMsPerChar)
    {
        var sb = new StringBuilder();
        if (delayMsPerChar > 0) sb.Append("typedelay ").Append(delayMsPerChar.ToString(CultureInfo.InvariantCulture)).Append('\n');
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Length > 0) sb.Append("type ").Append(lines[i]).Append('\n');
            if (i < lines.Length - 1) sb.Append("key enter\n");
        }
        Send(_runner, sb.ToString());
    }

    public void Combo(IReadOnlyList<string> modifiers, string? key) => Send(_runner, "key " + KeySpec(modifiers, key));

    public void KeyDown(string normalizedKey)
    {
        var spec = KeySpec(Array.Empty<string>(), normalizedKey);
        Send(_runner, "keydown " + spec);
        lock (_gate) if (!_held.Contains(spec)) _held.Add(spec);
    }

    public void KeyUp(string normalizedKey)
    {
        var spec = KeySpec(Array.Empty<string>(), normalizedKey);
        Send(_runner, "keyup " + spec);
        lock (_gate) _held.Remove(spec);
    }

    public void ReleaseAll()
    {
        List<string> held;
        lock (_gate)
        {
            held = _held.ToList();
            _held.Clear();
        }
        if (held.Count == 0) return;
        try { Send(_runner, string.Join("\n", held.AsEnumerable().Reverse().Select(h => "keyup " + h))); }
        catch (InvalidOperationException) { }
    }

    public void Dispose() => ReleaseAll();
}

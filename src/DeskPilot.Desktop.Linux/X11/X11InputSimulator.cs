using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.X11.Interop;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>
/// Mouse and keyboard through the XTEST extension, in root-window pixels. Characters that are not on the
/// keyboard layout are typed by temporarily binding a spare keycode to the character's keysym
/// (XChangeKeyboardMapping), the technique xdotool uses, and restoring the mapping afterwards.
/// Keeps track of every key and button it pressed so <see cref="ReleaseAll"/> can lift anything left down.
/// </summary>
public sealed unsafe class X11InputSimulator : IInputSimulator, IDisposable
{
    internal const int ClickHoldMs = 30;
    internal const int ClickGapMs = 60;
    internal const int KeyHoldMs = 30;
    internal const int ModifierSettleMs = 10;
    internal const int TypeKeyHoldMs = 3;
    internal const int TypeGapMs = 4;
    internal const int ScrollGapMs = 10;
    internal const int SmoothStepMs = 16;
    internal const int MoveTolerancePx = 1;
    /// <summary>Lets clients see the MappingNotify for a remapped key before its key press arrives.</summary>
    internal const int RemapSettleMs = 30;
    /// <summary>
    /// Toolkits look a keycode up when they process the event, which can be after the event was sent; restoring
    /// the mapping too early makes them see an empty key. This is the wait before a remapped key is restored.
    /// </summary>
    internal const int RemapRestoreDelayMs = 150;
    internal const int MaxSpareKeycodes = 10;

    // X button numbers.
    internal const uint ButtonLeft = 1, ButtonMiddle = 2, ButtonRight = 3, WheelUp = 4, WheelDown = 5, WheelLeft = 6, WheelRight = 7;

    private readonly XConnection _conn = new();
    // Insertion order matters: keys are released in reverse order of pressing.
    private readonly List<int> _pressedKeys = new();
    private readonly HashSet<uint> _pressedButtons = new();
    // Spare keycodes bound by KeyDown for keys not on the layout, with their original keysyms, until KeyUp.
    private readonly Dictionary<int, uint[]> _heldRemaps = new();
    private int _xtestGeneration = -1;

    internal IReadOnlyCollection<int> PressedKeys { get { using var l = _conn.Acquire(); return _pressedKeys.ToList(); } }
    internal IReadOnlyCollection<uint> PressedButtons { get { using var l = _conn.Acquire(); return _pressedButtons.ToList(); } }

    // ---------------------------------------------------------------- mouse

    public void MoveMouse(int x, int y)
    {
        using var lease = AcquireForInput();
        MoveCore(lease, x, y);
        ThrowOnError(lease, "move the mouse");
    }

    public void MoveMouseSmooth(int x, int y, int durationMs)
    {
        using var lease = AcquireForInput();
        if (durationMs > 0)
        {
            var from = QueryPointer(lease);
            int steps = Math.Clamp(durationMs / SmoothStepMs, 2, 150);
            int pause = Math.Max(1, durationMs / steps);
            var path = SmoothPath(from, new ScreenPoint(x, y), steps);
            for (int i = 0; i < path.Count - 1; i++)
            {
                Xtst.XTestFakeMotionEvent(lease.Display, lease.Screen, path[i].X, path[i].Y, 0);
                Xlib.XFlush(lease.Display);
                Thread.Sleep(pause);
            }
        }
        MoveCore(lease, x, y);
        ThrowOnError(lease, "move the mouse");
    }

    private static void MoveCore(in XConnection.Lease lease, int x, int y)
    {
        Xtst.XTestFakeMotionEvent(lease.Display, lease.Screen, x, y, 0);
        Xlib.XSync(lease.Display, X.False);
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var p = QueryPointer(lease);
            if (Math.Abs(p.X - x) <= MoveTolerancePx && Math.Abs(p.Y - y) <= MoveTolerancePx) return;
            Thread.Sleep(5);
        }
        // Some servers clamp or delay XTest motion; warping is the fallback (it is not seen as device motion).
        Xlib.XWarpPointer(lease.Display, X.None, lease.Root, 0, 0, 0, 0, x, y);
        Xlib.XSync(lease.Display, X.False);
    }

    public void MouseDown(MouseButton button)
    {
        using var lease = AcquireForInput();
        ButtonCore(lease, ToX(button), down: true);
        ThrowOnError(lease, "press the mouse button");
    }

    public void MouseUp(MouseButton button)
    {
        using var lease = AcquireForInput();
        ButtonCore(lease, ToX(button), down: false);
        ThrowOnError(lease, "release the mouse button");
    }

    public void Click(MouseButton button, int clicks)
    {
        clicks = Math.Clamp(clicks, 1, 3);
        using var lease = AcquireForInput();
        uint b = ToX(button);
        for (int i = 0; i < clicks; i++)
        {
            ButtonCore(lease, b, down: true);
            Thread.Sleep(ClickHoldMs);
            ButtonCore(lease, b, down: false);
            if (i < clicks - 1) Thread.Sleep(ClickGapMs);
        }
        ThrowOnError(lease, "click");
    }

    public void Scroll(int dx, int dy)
    {
        using var lease = AcquireForInput();
        // One button press per notch; positive dy = down, positive dx = right.
        for (int i = 0; i < Math.Abs(dy); i++)
        {
            uint b = dy > 0 ? WheelDown : WheelUp;
            ButtonCore(lease, b, down: true);
            ButtonCore(lease, b, down: false);
            Thread.Sleep(ScrollGapMs);
        }
        for (int i = 0; i < Math.Abs(dx); i++)
        {
            uint b = dx > 0 ? WheelRight : WheelLeft;
            ButtonCore(lease, b, down: true);
            ButtonCore(lease, b, down: false);
            Thread.Sleep(ScrollGapMs);
        }
        ThrowOnError(lease, "scroll");
    }

    internal static uint ToX(MouseButton button) => button switch
    {
        MouseButton.Left => ButtonLeft,
        MouseButton.Middle => ButtonMiddle,
        MouseButton.Right => ButtonRight,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    /// <summary>
    /// The physical button that produces a logical one under the pointer mapping (map[i] = logical button of
    /// physical button i+1; a left-handed xmodmap swaps 1 and 3). XTEST sends physical buttons.
    /// </summary>
    internal static uint PhysicalButton(ReadOnlySpan<byte> pointerMapping, uint logical)
    {
        if (pointerMapping.Length >= logical && pointerMapping[(int)logical - 1] == logical) return logical;
        for (int i = 0; i < pointerMapping.Length; i++)
            if (pointerMapping[i] == logical) return (uint)(i + 1);
        return logical;
    }

    private void ButtonCore(in XConnection.Lease lease, uint logical, bool down)
    {
        uint physical = logical;
        byte* map = stackalloc byte[256];
        int n = Xlib.XGetPointerMapping(lease.Display, map, 256);
        if (n > 0) physical = PhysicalButton(new ReadOnlySpan<byte>(map, Math.Min(n, 256)), logical);
        Xtst.XTestFakeButtonEvent(lease.Display, physical, down ? X.True : X.False, 0);
        Xlib.XFlush(lease.Display);
        if (down) _pressedButtons.Add(physical);
        else _pressedButtons.Remove(physical);
    }

    public ScreenPoint GetCursorPosition()
    {
        using var lease = _conn.Acquire();
        return QueryPointer(lease);
    }

    private static ScreenPoint QueryPointer(in XConnection.Lease lease)
    {
        nuint root, child;
        int rx, ry, wx, wy;
        uint mask;
        if (Xlib.XQueryPointer(lease.Display, lease.Root, &root, &child, &rx, &ry, &wx, &wy, &mask) == 0)
            throw new InvalidOperationException("The pointer is on another X screen.");
        return new ScreenPoint(rx, ry);
    }

    /// <summary>Test hook: the modifier and button mask the server reports, and how many keys it sees held down.</summary>
    internal (uint Mask, int KeysDown) QueryInputState()
    {
        using var lease = _conn.Acquire();
        nuint root, child;
        int rx, ry, wx, wy;
        uint mask;
        Xlib.XQueryPointer(lease.Display, lease.Root, &root, &child, &rx, &ry, &wx, &wy, &mask);
        byte* keys = stackalloc byte[32];
        Xlib.XQueryKeymap(lease.Display, keys);
        int down = 0;
        for (int i = 0; i < 32; i++) down += System.Numerics.BitOperations.PopCount(keys[i]);
        return (mask, down);
    }

    /// <summary>Test hook: the current keyboard mapping.</summary>
    internal X11Keymap KeymapForTests()
    {
        using var lease = _conn.Acquire();
        return LoadKeymap(lease.Display);
    }

    /// <summary>Eased (cubic in/out) path; the last point is exactly the target.</summary>
    internal static IReadOnlyList<ScreenPoint> SmoothPath(ScreenPoint from, ScreenPoint to, int steps)
    {
        steps = Math.Max(1, steps);
        var points = new List<ScreenPoint>(steps);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            points.Add(new ScreenPoint((int)Math.Round(from.X + (to.X - from.X) * e), (int)Math.Round(from.Y + (to.Y - from.Y) * e)));
        }
        points[^1] = to;
        return points;
    }

    // ---------------------------------------------------------------- keyboard

    /// <summary>One character of typed text: an existing key (with Shift when needed) or a keysym to bind to a spare key.</summary>
    internal readonly record struct TextKey(KeyChoice? Key, uint RemapKeysym);

    /// <summary>
    /// The keys for a text: '\n' Return, '\t' Tab, '\r' and other control characters nothing, surrogate pairs one
    /// character; characters not on the active layout get their keysym for a temporary remap.
    /// </summary>
    internal static List<TextKey> PlanText(string text, X11Keymap keymap)
    {
        var list = new List<TextKey>(text.Length);
        foreach (var rune in text.EnumerateRunes())
        {
            int cp = rune.Value;
            if (cp == '\n' || cp == '\t')
            {
                uint ks = cp == '\n' ? X11Keysyms.Return : X11Keysyms.Tab;
                var named = keymap.FindKeysym(ks);
                list.Add(named != null ? new TextKey(named, 0) : new TextKey(null, ks));
                continue;
            }
            if (cp < 0x20 || cp == 0x7f) continue;
            var choice = keymap.FindCodePoint(cp);
            list.Add(choice != null ? new TextKey(choice, 0) : new TextKey(null, X11Keysyms.ForCodePoint(cp)));
        }
        return list;
    }

    /// <summary>
    /// Splits planned keys into runs whose distinct remapped keysyms fit in the spare keycodes, so each run binds its
    /// spares once, types, waits and restores.
    /// </summary>
    internal static List<(int Start, int End, List<uint> Remaps)> ChunkByRemaps(IReadOnlyList<TextKey> keys, int spareCount)
    {
        var chunks = new List<(int, int, List<uint>)>();
        int i = 0;
        spareCount = Math.Max(1, spareCount);
        while (i < keys.Count)
        {
            var remaps = new List<uint>();
            int j = i;
            for (; j < keys.Count; j++)
            {
                uint ks = keys[j].RemapKeysym;
                if (ks == 0 || remaps.Contains(ks)) continue;
                if (remaps.Count == spareCount) break;
                remaps.Add(ks);
            }
            chunks.Add((i, j, remaps));
            i = j;
        }
        return chunks;
    }

    public void TypeText(string text, int delayMsPerChar)
    {
        if (string.IsNullOrEmpty(text)) return;
        using var lease = AcquireForInput();
        var keymap = LoadKeymap(lease.Display);
        var keys = PlanText(text, keymap);
        if (keys.Count == 0) return;
        var shift = ModifierKeycode(keymap, "shift");
        var spares = SparesFor(keymap);

        foreach (var (start, end, remaps) in ChunkByRemaps(keys, spares.Count))
        {
            var bound = new Dictionary<uint, int>();
            var originals = new List<(int Keycode, uint[] Syms)>();
            try
            {
                for (int r = 0; r < remaps.Count; r++)
                {
                    int kc = spares[r];
                    originals.Add((kc, keymap.GetAll(kc)));
                    BindKeysym(lease.Display, kc, remaps[r]);
                    bound[remaps[r]] = kc;
                }
                if (remaps.Count > 0)
                {
                    Xlib.XSync(lease.Display, X.False);
                    Thread.Sleep(RemapSettleMs);
                }

                for (int i = start; i < end; i++)
                {
                    var k = keys[i];
                    var choice = k.Key ?? new KeyChoice(bound[k.RemapKeysym], false);
                    bool withShift = choice.Shift && !_pressedKeys.Contains(shift);
                    if (withShift) KeyEvent(lease.Display, shift, down: true);
                    KeyEvent(lease.Display, choice.Keycode, down: true);
                    Thread.Sleep(TypeKeyHoldMs);
                    KeyEvent(lease.Display, choice.Keycode, down: false);
                    if (withShift) KeyEvent(lease.Display, shift, down: false);
                    Thread.Sleep(delayMsPerChar > 0 ? delayMsPerChar : TypeGapMs);
                }
            }
            finally
            {
                if (originals.Count > 0)
                {
                    Xlib.XSync(lease.Display, X.False);
                    Thread.Sleep(RemapRestoreDelayMs);
                    foreach (var (kc, syms) in originals) RestoreKeysyms(lease.Display, kc, syms);
                    Xlib.XSync(lease.Display, X.False);
                }
            }
        }
        ThrowOnError(lease, "type");
    }

    public void PressCombo(KeyCombo combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        using var lease = AcquireForInput();
        var keymap = LoadKeymap(lease.Display);
        KeyChoice? key = null;
        (int Keycode, uint[] Syms)? remap = null;
        if (combo.Key != null) key = ResolveKey(lease.Display, keymap, combo.Key, out remap);

        var names = new List<string>(combo.Modifiers);
        if (key is { Shift: true } && !names.Contains("shift")) names.Add("shift");
        var modifiers = names.Select(n => ModifierKeycode(keymap, n)).ToList();

        var pressedHere = new List<int>();
        try
        {
            if (remap != null)
            {
                Xlib.XSync(lease.Display, X.False);
                Thread.Sleep(RemapSettleMs);
            }
            foreach (var m in modifiers)
            {
                // A modifier already held through KeyDown stays held; the combo must not release it.
                if (_pressedKeys.Contains(m) || pressedHere.Contains(m)) continue;
                KeyEvent(lease.Display, m, down: true);
                pressedHere.Add(m);
            }
            if (modifiers.Count > 0) Thread.Sleep(ModifierSettleMs);
            if (key is { } k)
            {
                KeyEvent(lease.Display, k.Keycode, down: true);
                pressedHere.Add(k.Keycode);
                Thread.Sleep(KeyHoldMs);
                KeyEvent(lease.Display, k.Keycode, down: false);
                pressedHere.Remove(k.Keycode);
            }
            else
            {
                Thread.Sleep(KeyHoldMs);
            }
            if (modifiers.Count > 0) Thread.Sleep(ModifierSettleMs);
        }
        finally
        {
            for (int i = pressedHere.Count - 1; i >= 0; i--) KeyEvent(lease.Display, pressedHere[i], down: false);
            Xlib.XSync(lease.Display, X.False);
            if (remap is { } r)
            {
                Thread.Sleep(RemapRestoreDelayMs);
                RestoreKeysyms(lease.Display, r.Keycode, r.Syms);
                Xlib.XSync(lease.Display, X.False);
            }
        }
        ThrowOnError(lease, "press " + combo);
    }

    public void KeyDown(string key)
    {
        using var lease = AcquireForInput();
        var keymap = LoadKeymap(lease.Display);
        var choice = ResolveKey(lease.Display, keymap, key, out var remap);
        if (remap is { } r)
        {
            _heldRemaps[r.Keycode] = r.Syms;
            Xlib.XSync(lease.Display, X.False);
            Thread.Sleep(RemapSettleMs);
        }
        if (choice.Shift)
        {
            int shift = ModifierKeycode(keymap, "shift");
            if (!_pressedKeys.Contains(shift)) KeyEvent(lease.Display, shift, down: true);
        }
        KeyEvent(lease.Display, choice.Keycode, down: true);
        ThrowOnError(lease, "press " + key);
    }

    public void KeyUp(string key)
    {
        using var lease = AcquireForInput();
        var keymap = LoadKeymap(lease.Display);
        // A key bound to a spare keycode by KeyDown is found again through that binding.
        if (!X11Keysyms.TryGetKeysym(key, out var keysym, out var error)) throw new ArgumentException(error, nameof(key));
        int? held = _heldRemaps.Keys.Cast<int?>().FirstOrDefault(kc => keymap.Get(kc!.Value, 0) == keysym);
        if (held is int heldKc)
        {
            KeyEvent(lease.Display, heldKc, down: false);
            Xlib.XSync(lease.Display, X.False);
            Thread.Sleep(RemapRestoreDelayMs);
            RestoreKeysyms(lease.Display, heldKc, _heldRemaps[heldKc]);
            _heldRemaps.Remove(heldKc);
            Xlib.XSync(lease.Display, X.False);
        }
        else
        {
            var choice = Find(keymap, key, keysym) ?? throw new ArgumentException($"The key '{key}' is not on the keyboard.", nameof(key));
            KeyEvent(lease.Display, choice.Keycode, down: false);
            if (choice.Shift)
            {
                int shift = ModifierKeycode(keymap, "shift");
                if (_pressedKeys.Contains(shift) && !KeyCombo.ModifierNames.Contains(KeyCombo.NormalizeName(key)))
                    KeyEvent(lease.Display, shift, down: false);
            }
        }
        ThrowOnError(lease, "release " + key);
    }

    public void ReleaseAll()
    {
        // Nothing pressed: do not open a display (or require XTEST) just to release nothing.
        if (_pressedKeys.Count == 0 && _pressedButtons.Count == 0 && _heldRemaps.Count == 0) return;
        XConnection.Lease lease;
        try { lease = AcquireForInput(); }
        catch (Exception)
        {
            // No display: nothing can be held down on it.
            _pressedKeys.Clear();
            _pressedButtons.Clear();
            _heldRemaps.Clear();
            return;
        }
        using (lease)
        {
            try
            {
                for (int i = _pressedKeys.Count - 1; i >= 0; i--)
                    Xtst.XTestFakeKeyEvent(lease.Display, (uint)_pressedKeys[i], X.False, 0);
                foreach (var b in _pressedButtons) Xtst.XTestFakeButtonEvent(lease.Display, b, X.False, 0);
                Xlib.XSync(lease.Display, X.False);
                if (_heldRemaps.Count > 0)
                {
                    Thread.Sleep(RemapRestoreDelayMs);
                    foreach (var (kc, syms) in _heldRemaps) RestoreKeysyms(lease.Display, kc, syms);
                    Xlib.XSync(lease.Display, X.False);
                }
            }
            catch (Exception) { /* best effort */ }
            finally
            {
                _pressedKeys.Clear();
                _pressedButtons.Clear();
                _heldRemaps.Clear();
                lease.ResetErrors();
            }
        }
    }

    private void KeyEvent(nint display, int keycode, bool down)
    {
        Xtst.XTestFakeKeyEvent(display, (uint)keycode, down ? X.True : X.False, 0);
        Xlib.XFlush(display);
        if (down) { if (!_pressedKeys.Contains(keycode)) _pressedKeys.Add(keycode); }
        else _pressedKeys.Remove(keycode);
    }

    private static KeyChoice? Find(X11Keymap keymap, string name, uint keysym)
    {
        var choice = keymap.FindKeysym(keysym);
        if (choice != null) return choice;
        // Single characters may be listed under another keysym of the same character (EuroSign...).
        var normalized = KeyCombo.NormalizeName(name);
        var runes = normalized.EnumerateRunes().ToList();
        return runes.Count == 1 ? keymap.FindCodePoint(runes[0].Value) : null;
    }

    /// <summary>The key for a name; keys not on the layout are bound to a spare keycode (returned for restoring).</summary>
    private KeyChoice ResolveKey(nint display, X11Keymap keymap, string name, out (int Keycode, uint[] Syms)? remap)
    {
        remap = null;
        if (!X11Keysyms.TryGetKeysym(name, out var keysym, out var error)) throw new ArgumentException(error, nameof(name));
        if (X11Keysyms.IsModifier(KeyCombo.NormalizeName(name))) return new KeyChoice(ModifierKeycode(keymap, KeyCombo.NormalizeName(name)), false);
        var choice = Find(keymap, name, keysym);
        if (choice != null) return choice.Value;

        var spares = SparesFor(keymap);
        int kc = spares[0];
        remap = (kc, keymap.GetAll(kc));
        BindKeysym(display, kc, keysym);
        return new KeyChoice(kc, false);
    }

    /// <summary>The keycode of Control_L / Shift_L / Alt_L / Super_L (or their right-hand or Meta equivalents).</summary>
    internal static int ModifierKeycode(X11Keymap keymap, string name)
    {
        uint[] candidates = name switch
        {
            "ctrl" => new[] { X11Keysyms.ControlL, X11Keysyms.ControlR },
            "shift" => new[] { X11Keysyms.ShiftL, X11Keysyms.ShiftR },
            "alt" => new[] { X11Keysyms.AltL, X11Keysyms.AltR, X11Keysyms.MetaL },
            "win" => new[] { X11Keysyms.SuperL, X11Keysyms.SuperR },
            _ => throw new ArgumentException($"'{name}' is not a modifier (use ctrl, alt, shift or win)."),
        };
        foreach (var ks in candidates)
            if (keymap.FindKeysym(ks) is { } c) return c.Keycode;
        throw new InvalidOperationException($"The keyboard map has no key for '{name}', so it cannot be pressed.");
    }

    /// <summary>Spare keycodes for remaps: empty ones, else the highest keycode (its keysyms are saved and restored).</summary>
    private List<int> SparesFor(X11Keymap keymap)
    {
        var spares = keymap.SpareKeycodes(MaxSpareKeycodes);
        if (spares.Count == 0) spares.Add(keymap.MaxKeycode);
        return spares;
    }

    private static void BindKeysym(nint display, int keycode, uint keysym)
    {
        // The same keysym on both levels, so Shift and Caps Lock do not change what the key types.
        nuint* syms = stackalloc nuint[2];
        syms[0] = keysym;
        syms[1] = keysym;
        Xlib.XChangeKeyboardMapping(display, keycode, 2, syms, 1);
    }

    private static void RestoreKeysyms(nint display, int keycode, uint[] original)
    {
        int n = Math.Max(1, original.Length);
        nuint* syms = stackalloc nuint[n];
        for (int i = 0; i < n; i++) syms[i] = i < original.Length ? original[i] : 0;
        Xlib.XChangeKeyboardMapping(display, keycode, n, syms, 1);
    }

    internal static X11Keymap LoadKeymap(nint display)
    {
        int min, max, per;
        Xlib.XDisplayKeycodes(display, &min, &max);
        int count = max - min + 1;
        var syms = Xlib.XGetKeyboardMapping(display, (uint)min, count, &per);
        if (syms == null || per <= 0) throw new InvalidOperationException("Could not read the X keyboard mapping.");
        uint[] copy;
        try
        {
            copy = new uint[count * per];
            for (int i = 0; i < copy.Length; i++) copy[i] = (uint)syms[i];
        }
        finally
        {
            Xlib.XFree(syms);
        }

        int group = 0;
        bool caps = false;
        XkbStateRec state;
        if (Xlib.XkbGetState(display, X.XkbUseCoreKbd, &state) == X.Success)
        {
            group = state.group;
            caps = (state.locked_mods & X.LockMask) != 0;
        }
        return new X11Keymap(min, per, copy, group, caps);
    }

    // ---------------------------------------------------------------- plumbing

    private XConnection.Lease AcquireForInput()
    {
        X11Native.Require(X11Library.Xtst);
        var lease = _conn.Acquire();
        try
        {
            if (_xtestGeneration != lease.Generation)
            {
                int eventBase, errorBase, major, minor;
                if (Xtst.XTestQueryExtension(lease.Display, &eventBase, &errorBase, &major, &minor) == 0)
                    throw new InvalidOperationException("The X server has no XTEST extension, so DeskPilot cannot send mouse and keyboard input.");
                _xtestGeneration = lease.Generation;
            }
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static void ThrowOnError(in XConnection.Lease lease, string what)
    {
        if (lease.Sync() is { } error)
            throw new InvalidOperationException($"The X server rejected the input while trying to {what}: {error}.");
    }

    public void Dispose()
    {
        ReleaseAll();
        _conn.Dispose();
    }
}

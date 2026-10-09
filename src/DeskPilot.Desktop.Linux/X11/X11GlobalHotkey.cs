using System.Globalization;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.X11.Interop;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>A global hotkey on X11 (XGrabKey on its own display connection and thread). Not available on Wayland.</summary>
public sealed unsafe class X11GlobalHotkey : IDisposable
{
    internal const int PollIntervalMs = 25;
    internal const int RepeatIgnoreMs = 300;

    private readonly object _gate = new();
    private nint _display;
    private nuint _root;
    private int _keycode;
    private uint _modifiers;
    private uint[] _lockVariants = Array.Empty<uint>();
    private Action _pressed = () => { };
    private Thread? _thread;
    private volatile bool _stop;

    private X11GlobalHotkey() { }

    /// <summary>Human-readable form, e.g. "Ctrl+Alt+X".</summary>
    public string Display { get; private set; } = "";

    /// <summary>Registers the combo; pressed is raised on a background thread. Returns null with an error message when it cannot.</summary>
    public static X11GlobalHotkey? TryRegister(KeyCombo combo, Action pressed, out string error)
    {
        ArgumentNullException.ThrowIfNull(combo);
        ArgumentNullException.ThrowIfNull(pressed);
        var name = DisplayName(combo);
        if (combo.Key == null)
        {
            error = $"'{name}' has no main key; add one, e.g. Ctrl+Alt+X";
            return null;
        }
        if (!X11Keysyms.TryGetKeysym(combo.Key, out var keysym, out _))
        {
            error = $"unknown key '{combo.Key}' in '{name}'";
            return null;
        }

        nint display = 0;
        try
        {
            X11Native.EnsureInitialized();
            display = Xlib.XOpenDisplay(null);
            if (display == 0)
            {
                error = "Cannot open the X display, so the global hotkey is not available.";
                return null;
            }
            XErrorTrap.Register(display);
            var root = Xlib.XRootWindow(display, Xlib.XDefaultScreen(display));

            var keymap = X11InputSimulator.LoadKeymap(display);
            var choice = keymap.FindKeysym(keysym);
            if (choice == null)
            {
                error = $"{name}: the key is not on the current keyboard layout";
                Close(display);
                return null;
            }

            if (combo.Has("ctrl") && combo.Has("alt") && ServerAction(keymap.GetAll(choice.Value.Keycode)) is { } action)
            {
                error = $"{name} {action} on X11, so DeskPilot would never see it; choose another combination such as Ctrl+Alt+X";
                Close(display);
                return null;
            }

            var masks = ReadModifierMasks(display);
            uint modifiers = 0;
            if (combo.Has("ctrl")) modifiers |= X.ControlMask;
            if (combo.Has("shift") || choice.Value.Shift) modifiers |= X.ShiftMask;
            if (combo.Has("alt")) modifiers |= masks.Alt;
            if (combo.Has("win")) modifiers |= masks.Super;
            var variants = LockVariants(modifiers, X.LockMask, masks.NumLock, masks.ScrollLock);

            XErrorTrap.Install();
            XErrorTrap.Reset(display);
            foreach (var v in variants)
                Xlib.XGrabKey(display, choice.Value.Keycode, modifiers | v, root, X.False, X.GrabModeAsync, X.GrabModeAsync);
            var grabError = XErrorTrap.Sync(display);
            if (grabError != null)
            {
                foreach (var v in variants) Xlib.XUngrabKey(display, choice.Value.Keycode, modifiers | v, root);
                XErrorTrap.Sync(display);
                error = grabError.Value.IsBadAccess
                    ? $"{name} is already used by another program"
                    : $"The X server refused {name} ({grabError.Value})";
                Close(display);
                return null;
            }

            var hotkey = new X11GlobalHotkey
            {
                _display = display,
                _root = root,
                _keycode = choice.Value.Keycode,
                _modifiers = modifiers,
                _lockVariants = variants,
                _pressed = pressed,
                Display = name,
            };
            hotkey._thread = new Thread(hotkey.Run) { IsBackground = true, Name = "DeskPilot X11 hotkey" };
            hotkey._thread.Start();
            error = "";
            return hotkey;
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            if (display != 0) Close(display);
            error = ex.Message;
            return null;
        }
    }

    private void Run()
    {
        long lastPress = long.MinValue / 2;
        var ev = default(XEvent);
        while (!_stop)
        {
            bool fire = false;
            lock (_gate)
            {
                if (_display == 0) return;
                while (!_stop && Xlib.XPending(_display) > 0)
                {
                    Xlib.XNextEvent(_display, &ev);
                    if (ev.Type != X.KeyPress) continue;
                    var key = (XKeyEvent*)&ev;
                    if (key->keycode != (uint)_keycode) continue;
                    long now = Environment.TickCount64;
                    // Auto-repeat while the keys are held sends more presses: only the first one counts.
                    if (now - lastPress >= RepeatIgnoreMs) fire = true;
                    lastPress = now;
                }
            }
            if (fire)
            {
                try { _pressed(); }
                catch (Exception) { /* the handler's problem; the hotkey keeps working */ }
            }
            Thread.Sleep(PollIntervalMs);
        }
    }

    public void Dispose()
    {
        _stop = true;
        var thread = _thread;
        if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
        lock (_gate)
        {
            if (_display == 0) return;
            foreach (var v in _lockVariants) Xlib.XUngrabKey(_display, _keycode, _modifiers | v, _root);
            XErrorTrap.Sync(_display);
            Close(_display);
            _display = 0;
        }
    }

    private static void Close(nint display)
    {
        try { Xlib.XCloseDisplay(display); }
        catch (Exception) { }
        XErrorTrap.Unregister(display);
    }

    /// <summary>
    /// XKB binds Ctrl+Alt on some keys to X server actions (F1-F12 switch virtual terminals, Backspace may stop the
    /// server). The server consumes those presses before any grab sees them, so such a hotkey could never fire.
    /// Returns what the key does, or null. <paramref name="keysyms"/> is the key's whole core-mapping row.
    /// </summary>
    internal static string? ServerAction(IEnumerable<uint> keysyms)
    {
        foreach (var ks in keysyms)
        {
            if (ks is >= 0x1008FE01 and <= 0x1008FE0C) return $"switches to virtual terminal {ks - 0x1008FE00}";
            if (ks == 0xfed5) return "can stop the X server";
            if (ks is >= 0x1008FE0D and <= 0x1008FEFF) return "is an X server action";
        }
        return null;
    }

    internal readonly record struct ModifierMasks(uint Alt, uint Super, uint NumLock, uint ScrollLock);

    private static ModifierMasks ReadModifierMasks(nint display)
    {
        var map = Xlib.XGetModifierMapping(display);
        if (map == null) return new ModifierMasks(X.Mod1Mask, X.Mod4Mask, X.Mod2Mask, 0);
        try
        {
            int per = map->max_keypermod;
            var codes = new ReadOnlySpan<byte>(map->modifiermap, 8 * per).ToArray();
            int Kc(uint ks) => Xlib.XKeysymToKeycode(display, ks);
            uint alt = MaskFor(codes, per, Kc(X11Keysyms.AltL), Kc(X11Keysyms.MetaL));
            uint super = MaskFor(codes, per, Kc(X11Keysyms.SuperL), Kc(X11Keysyms.SuperR));
            uint num = MaskFor(codes, per, Kc(X11Keysyms.NumLock));
            uint scroll = MaskFor(codes, per, Kc(X11Keysyms.ScrollLock));
            return new ModifierMasks(alt != 0 ? alt : X.Mod1Mask, super != 0 ? super : X.Mod4Mask, num != 0 ? num : X.Mod2Mask, scroll);
        }
        finally
        {
            Xlib.XFreeModifiermap(map);
        }
    }

    /// <summary>
    /// The modifier bit (Mod1..Mod5) holding any of the keycodes, from the 8 x keysPerModifier modifier map
    /// (rows: Shift, Lock, Control, Mod1..Mod5). 0 when none does; only Mod1-Mod5 are considered.
    /// </summary>
    internal static uint MaskFor(ReadOnlySpan<byte> modifierMap, int keysPerModifier, params int[] keycodes)
    {
        for (int mod = 3; mod < 8; mod++)
        {
            for (int k = 0; k < keysPerModifier; k++)
            {
                int kc = modifierMap[mod * keysPerModifier + k];
                if (kc != 0 && Array.IndexOf(keycodes, kc) >= 0) return 1u << mod;
            }
        }
        return 0;
    }

    /// <summary>
    /// Every combination of the lock modifiers (Caps Lock, Num Lock, Scroll Lock) that are not part of the hotkey
    /// itself, so the hotkey works whatever lock is on. Always includes 0.
    /// </summary>
    internal static uint[] LockVariants(uint hotkeyModifiers, params uint[] lockMasks)
    {
        var bits = lockMasks.Where(m => m != 0 && (m & hotkeyModifiers) == 0).Distinct().ToList();
        var result = new List<uint>();
        for (int subset = 0; subset < 1 << bits.Count; subset++)
        {
            uint v = 0;
            for (int i = 0; i < bits.Count; i++)
                if ((subset & (1 << i)) != 0) v |= bits[i];
            if (!result.Contains(v)) result.Add(v);
        }
        return result.ToArray();
    }

    /// <summary>"Ctrl+Alt+X" style text for messages, matching the Windows wording.</summary>
    internal static string DisplayName(KeyCombo combo)
    {
        var parts = new List<string>();
        if (combo.Has("ctrl")) parts.Add("Ctrl");
        if (combo.Has("alt")) parts.Add("Alt");
        if (combo.Has("shift")) parts.Add("Shift");
        if (combo.Has("win")) parts.Add("Super");
        if (combo.Key is { } key) parts.Add(KeyDisplay(key));
        return string.Join("+", parts);
    }

    private static string KeyDisplay(string key)
    {
        if (key.Length == 1) return key.ToUpperInvariant();
        if (key.Length is 2 or 3 && key[0] == 'f' && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f))
            return "F" + f.ToString(CultureInfo.InvariantCulture);
        return key switch
        {
            "escape" => "Esc",
            "pageup" => "PageUp",
            "pagedown" => "PageDown",
            "scrolllock" => "ScrollLock",
            "printscreen" => "PrintScreen",
            "capslock" => "CapsLock",
            "numlock" => "NumLock",
            _ => char.ToUpperInvariant(key[0]) + key[1..],
        };
    }
}

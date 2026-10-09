using DeskPilot.Core.Abstractions;
using static DeskPilot.Desktop.Windows.NativeMethods;

namespace DeskPilot.Desktop.Windows;

/// <summary>
/// Pure construction of SendInput records. No Win32 calls happen here, so every flag, sign and
/// ordering rule can be unit-tested.
/// </summary>
internal static class InputBuilder
{
    /// <summary>Tag put in dwExtraInfo so DeskPilot's own injected input can be told apart from the user's.</summary>
    public static readonly nint InjectedMarker = 0x44504C54; // "DPLT"

    public static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) => new()
    {
        type = INPUT_MOUSE,
        U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = InjectedMarker } },
    };

    public static INPUT Key(ushort vk, ushort scan, bool keyUp, bool extended)
    {
        uint flags = 0;
        if (keyUp) flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDEDKEY;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = InjectedMarker } },
        };
    }

    public static INPUT Unicode(char unit, bool keyUp) => new()
    {
        type = INPUT_KEYBOARD,
        U = new InputUnion
        {
            ki = new KEYBDINPUT { wVk = 0, wScan = unit, dwFlags = KEYEVENTF_UNICODE | (keyUp ? KEYEVENTF_KEYUP : 0), dwExtraInfo = InjectedMarker },
        },
    };

    /// <summary>
    /// Maps a physical pixel to SendInput's 0..65535 absolute range over the virtual desktop. Aims at the
    /// pixel centre so both conventions Windows uses to map back (x*w/65536 and x*w/65535) land on it.
    /// </summary>
    public static (int Dx, int Dy) Normalize(int x, int y, ScreenRect virtualScreen)
    {
        static int Axis(int p, int origin, int size)
        {
            if (size <= 1) return 0;
            double v = (p - origin + 0.5) * 65536.0 / size;
            return (int)Math.Clamp(Math.Round(v), 0, 65535);
        }
        return (Axis(x, virtualScreen.X, virtualScreen.Width), Axis(y, virtualScreen.Y, virtualScreen.Height));
    }

    public static INPUT MoveAbsolute(int x, int y, ScreenRect virtualScreen)
    {
        var (dx, dy) = Normalize(x, y, virtualScreen);
        return Mouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, dx, dy);
    }

    public static uint ButtonFlag(MouseButton button, bool up) => button switch
    {
        MouseButton.Left => up ? MOUSEEVENTF_LEFTUP : MOUSEEVENTF_LEFTDOWN,
        MouseButton.Right => up ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_RIGHTDOWN,
        MouseButton.Middle => up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN,
        _ => throw new ArgumentOutOfRangeException(nameof(button), button, "Unknown mouse button."),
    };

    /// <summary>SendInput button flags address physical buttons, so a swapped (left-handed) mouse needs the logical button flipped.</summary>
    public static MouseButton ToPhysical(MouseButton logical, bool buttonsSwapped) =>
        !buttonsSwapped ? logical : logical switch
        {
            MouseButton.Left => MouseButton.Right,
            MouseButton.Right => MouseButton.Left,
            _ => logical,
        };

    public static INPUT Button(MouseButton physicalButton, bool up) => Mouse(ButtonFlag(physicalButton, up));

    /// <summary>One wheel notch. Positive notches = down (negative wheel delta), as the interface defines.</summary>
    public static INPUT VerticalWheel(int notchesDown) => Mouse(MOUSEEVENTF_WHEEL, data: -notchesDown * WHEEL_DELTA);

    /// <summary>One horizontal wheel notch. Positive notches = right (positive delta).</summary>
    public static INPUT HorizontalWheel(int notchesRight) => Mouse(MOUSEEVENTF_HWHEEL, data: notchesRight * WHEEL_DELTA);

    /// <summary>
    /// Key events for one character of typed text: '\n' is Enter, '\t' is Tab, '\r' and other control
    /// characters produce nothing, everything else is sent as KEYEVENTF_UNICODE per UTF-16 unit.
    /// </summary>
    public static IEnumerable<INPUT> TextUnit(char c, Func<ushort, ushort> scanOf)
    {
        switch (c)
        {
            case '\n':
                yield return Key(KeyMap.VK_RETURN, scanOf(KeyMap.VK_RETURN), false, false);
                yield return Key(KeyMap.VK_RETURN, scanOf(KeyMap.VK_RETURN), true, false);
                yield break;
            case '\t':
                yield return Key(KeyMap.VK_TAB, scanOf(KeyMap.VK_TAB), false, false);
                yield return Key(KeyMap.VK_TAB, scanOf(KeyMap.VK_TAB), true, false);
                yield break;
        }
        if (c < 0x20 || c == 0x7F) yield break;
        yield return Unicode(c, false);
        yield return Unicode(c, true);
    }

    /// <summary>
    /// Splits text into "characters" (a surrogate pair stays together) with the inputs for each.
    /// A surrogate pair is sent as both key-downs then both key-ups, in one SendInput call.
    /// </summary>
    public static List<INPUT[]> TextChunks(string text, Func<ushort, ushort> scanOf)
    {
        var chunks = new List<INPUT[]>();
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                char low = text[i + 1];
                chunks.Add(new[] { Unicode(c, false), Unicode(low, false), Unicode(c, true), Unicode(low, true) });
                i++;
                continue;
            }
            var inputs = TextUnit(c, scanOf).ToArray();
            if (inputs.Length > 0) chunks.Add(inputs);
        }
        return chunks;
    }

    /// <summary>Modifiers down in order, the key down and up, modifiers up in reverse order.</summary>
    public static List<INPUT> Combo(IReadOnlyList<KeyStroke> modifiers, KeyStroke? key, Func<ushort, ushort> scanOf)
    {
        var list = new List<INPUT>();
        foreach (var m in modifiers) list.Add(Key(m.Vk, scanOf(m.Vk), false, m.Extended));
        if (key is { } k)
        {
            list.Add(Key(k.Vk, scanOf(k.Vk), false, k.Extended));
            list.Add(Key(k.Vk, scanOf(k.Vk), true, k.Extended));
        }
        for (int i = modifiers.Count - 1; i >= 0; i--) list.Add(Key(modifiers[i].Vk, scanOf(modifiers[i].Vk), true, modifiers[i].Extended));
        return list;
    }

    /// <summary>Eased (smoothstep-like, cubic in/out) path from one point to another; the last point is exactly the target.</summary>
    public static IReadOnlyList<ScreenPoint> SmoothPath(ScreenPoint from, ScreenPoint to, int steps)
    {
        steps = Math.Max(1, steps);
        var points = new List<ScreenPoint>(steps);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / steps;
            double e = t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            int x = (int)Math.Round(from.X + (to.X - from.X) * e);
            int y = (int)Math.Round(from.Y + (to.Y - from.Y) * e);
            points.Add(new ScreenPoint(x, y));
        }
        points[^1] = to;
        return points;
    }
}

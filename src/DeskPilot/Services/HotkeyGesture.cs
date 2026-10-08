using System.Globalization;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Services;

/// <summary>A global hotkey parsed from text such as "Ctrl+Alt+X", "Ctrl+Shift+F12" or "Pause".</summary>
public sealed record HotkeyGesture(uint Modifiers, uint VirtualKey, string Display)
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    private static readonly Dictionary<string, (uint Vk, string Display)> Named = new(StringComparer.Ordinal)
    {
        ["pause"] = (0x13, "Pause"), ["break"] = (0x13, "Pause"),
        ["scrolllock"] = (0x91, "ScrollLock"),
        ["printscreen"] = (0x2C, "PrintScreen"),
        ["insert"] = (0x2D, "Insert"), ["delete"] = (0x2E, "Delete"),
        ["home"] = (0x24, "Home"), ["end"] = (0x23, "End"),
        ["pageup"] = (0x21, "PageUp"), ["pagedown"] = (0x22, "PageDown"),
        ["up"] = (0x26, "Up"), ["down"] = (0x28, "Down"), ["left"] = (0x25, "Left"), ["right"] = (0x27, "Right"),
        ["space"] = (0x20, "Space"), ["enter"] = (0x0D, "Enter"), ["tab"] = (0x09, "Tab"),
        ["escape"] = (0x1B, "Esc"), ["backspace"] = (0x08, "Backspace"),
        ["numlock"] = (0x90, "NumLock"), ["capslock"] = (0x14, "CapsLock"), ["contextmenu"] = (0x5D, "Menu"),
        ["multiply"] = (0x6A, "Num*"), ["add"] = (0x6B, "Num+"), ["subtract"] = (0x6D, "Num-"), ["decimal"] = (0x6E, "Num."), ["divide"] = (0x6F, "Num/"),
        [","] = (0xBC, ","), ["."] = (0xBE, "."), ["-"] = (0xBD, "-"), ["+"] = (0xBB, "="), ["="] = (0xBB, "="),
        [";"] = (0xBA, ";"), ["/"] = (0xBF, "/"), ["`"] = (0xC0, "`"), ["["] = (0xDB, "["), ["\\"] = (0xDC, "\\"),
        ["]"] = (0xDD, "]"), ["'"] = (0xDE, "'"),
    };

    /// <summary>Keys that are fine as a hotkey without any modifier (they do nothing while typing).</summary>
    private static readonly HashSet<string> StandaloneKeys = new(StringComparer.Ordinal) { "pause", "break", "scrolllock" };

    public override string ToString() => Display;

    public static bool TryParse(string? text, out HotkeyGesture? gesture, out string error)
    {
        gesture = null;
        if (!KeyCombo.TryParse(text, out var combo, out error)) return false;
        if (combo.Key == null)
        {
            error = $"'{text}' has no main key; add one, e.g. Ctrl+Alt+X";
            return false;
        }

        uint mods = 0;
        var display = new List<string>();
        if (combo.Has("ctrl")) { mods |= ModControl; display.Add("Ctrl"); }
        if (combo.Has("alt")) { mods |= ModAlt; display.Add("Alt"); }
        if (combo.Has("shift")) { mods |= ModShift; display.Add("Shift"); }
        if (combo.Has("win")) { mods |= ModWin; display.Add("Win"); }

        var key = combo.Key;
        if (!TryMapKey(key, out var vk, out var keyDisplay, out var isFunctionKey))
        {
            error = $"unknown key '{key}' in '{text}'";
            return false;
        }

        // A global hotkey swallows its keys system-wide: refuse ones that would break normal typing.
        bool typingModifiersOnly = (mods & ~ModShift) == 0;
        if (typingModifiersOnly && !isFunctionKey && !StandaloneKeys.Contains(key))
        {
            error = $"'{text}' would block normal typing; use a combination with Ctrl, Alt or Win, a function key, or Pause";
            return false;
        }

        display.Add(keyDisplay);
        gesture = new HotkeyGesture(mods, vk, string.Join("+", display));
        error = "";
        return true;
    }

    private static bool TryMapKey(string key, out uint vk, out string display, out bool isFunctionKey)
    {
        isFunctionKey = false;
        vk = 0;
        display = "";
        if (key.Length == 1 && key[0] is >= 'a' and <= 'z')
        {
            vk = (uint)char.ToUpperInvariant(key[0]);
            display = ((char)vk).ToString();
            return true;
        }
        if (key.Length == 1 && key[0] is >= '0' and <= '9')
        {
            vk = key[0];
            display = key;
            return true;
        }
        if (key.Length is >= 2 and <= 3 && key[0] == 'f' &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + f - 1);
            display = "F" + f.ToString(CultureInfo.InvariantCulture);
            isFunctionKey = true;
            return true;
        }
        if (key.StartsWith("numpad", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9')
        {
            vk = (uint)(0x60 + key[6] - '0');
            display = "Num" + key[6];
            return true;
        }
        if (Named.TryGetValue(key, out var named))
        {
            vk = named.Vk;
            display = named.Display;
            return true;
        }
        return false;
    }
}

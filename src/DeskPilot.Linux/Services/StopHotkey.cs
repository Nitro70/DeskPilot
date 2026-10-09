using System.Globalization;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux;

namespace DeskPilot.Linux.Services;

/// <summary>
/// The global stop hotkey as text ("Ctrl+Alt+X"): validation and the display form. Mapping the key to an
/// X11 keysym is the hotkey implementation's job; this only refuses combinations that would break typing.
/// </summary>
public static class StopHotkey
{
    private static readonly Dictionary<string, string> NamedKeys = new(StringComparer.Ordinal)
    {
        ["pause"] = "Pause", ["break"] = "Pause", ["scrolllock"] = "ScrollLock", ["printscreen"] = "PrintScreen",
        ["insert"] = "Insert", ["delete"] = "Delete", ["home"] = "Home", ["end"] = "End",
        ["pageup"] = "PageUp", ["pagedown"] = "PageDown",
        ["up"] = "Up", ["down"] = "Down", ["left"] = "Left", ["right"] = "Right",
        ["space"] = "Space", ["enter"] = "Enter", ["tab"] = "Tab", ["escape"] = "Esc", ["backspace"] = "Backspace",
        ["numlock"] = "NumLock", ["capslock"] = "CapsLock", ["contextmenu"] = "Menu",
        [","] = ",", ["."] = ".", ["-"] = "-", ["+"] = "=", ["="] = "=", [";"] = ";", ["/"] = "/", ["`"] = "`",
        ["["] = "[", ["\\"] = "\\", ["]"] = "]", ["'"] = "'",
    };

    /// <summary>Keys that are fine as a hotkey without any modifier (they do nothing while typing).</summary>
    private static readonly HashSet<string> StandaloneKeys = new(StringComparer.Ordinal) { "pause", "break", "scrolllock" };

    /// <summary>Parses and validates a hotkey. On success <paramref name="display"/> is e.g. "Ctrl+Alt+X".</summary>
    public static bool TryParse(string? text, out KeyCombo combo, out string display, out string error)
    {
        display = "";
        if (!KeyCombo.TryParse(text, out combo, out error)) return false;
        if (combo.Key == null)
        {
            error = $"'{text}' has no main key; add one, e.g. Ctrl+Alt+X";
            return false;
        }

        var parts = new List<string>();
        if (combo.Has("ctrl")) parts.Add("Ctrl");
        if (combo.Has("alt")) parts.Add("Alt");
        if (combo.Has("shift")) parts.Add("Shift");
        if (combo.Has("win")) parts.Add("Super");

        var key = combo.Key;
        if (!TryDisplayKey(key, out var keyDisplay, out var isFunctionKey))
        {
            error = $"unknown key '{key}' in '{text}'";
            return false;
        }

        // A global hotkey swallows its keys everywhere: refuse ones that would break normal typing.
        bool onlyShift = !combo.Has("ctrl") && !combo.Has("alt") && !combo.Has("win");
        if (onlyShift && !isFunctionKey && !StandaloneKeys.Contains(key))
        {
            error = $"'{text}' would block normal typing; use a combination with Ctrl, Alt or Super, a function key, or Pause";
            return false;
        }

        parts.Add(keyDisplay);
        display = string.Join("+", parts);
        error = "";
        return true;
    }

    /// <summary>The display form, or the raw text when it does not parse.</summary>
    public static string Display(string? text) =>
        TryParse(text, out _, out var display, out _) ? display : (text ?? "").Trim();

    private static bool TryDisplayKey(string key, out string display, out bool isFunctionKey)
    {
        isFunctionKey = false;
        display = "";
        if (key.Length == 1 && key[0] is >= 'a' and <= 'z')
        {
            display = char.ToUpperInvariant(key[0]).ToString();
            return true;
        }
        if (key.Length == 1 && key[0] is >= '0' and <= '9')
        {
            display = key;
            return true;
        }
        if (key.Length is >= 2 and <= 3 && key[0] == 'f' &&
            int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var f) && f is >= 1 and <= 24)
        {
            display = "F" + f.ToString(CultureInfo.InvariantCulture);
            isFunctionKey = true;
            return true;
        }
        if (key.StartsWith("numpad", StringComparison.Ordinal) && key.Length == 7 && key[6] is >= '0' and <= '9')
        {
            display = "Num" + key[6];
            return true;
        }
        if (NamedKeys.TryGetValue(key, out var named))
        {
            display = named;
            return true;
        }
        return false;
    }
}

/// <summary>How the user can stop the agent in this session, as shown in the status bar and the overlay.</summary>
public static class StopHint
{
    public const string WaylandStatus = "Stop: overlay button, tray or top-left corner";
    public const string WaylandStatusNoCorner = "Stop: overlay button or tray";
    public const string WaylandTooltip =
        "Wayland does not let apps register global hotkeys. Stop DeskPilot with the Stop button on the overlay, " +
        "the tray icon, or by moving the mouse into the top-left corner of the screen (when that failsafe is on).";

    /// <summary>True when a global stop hotkey can exist in this session (X11 only).</summary>
    public static bool HotkeySupported(LinuxSessionKind kind) => kind != LinuxSessionKind.Wayland;

    /// <summary>Status bar text: the hotkey under X11, the other stop controls under Wayland.</summary>
    public static string StatusText(LinuxSessionKind kind, string? hotkeyText, bool failsafeCorner = true)
    {
        if (!HotkeySupported(kind)) return failsafeCorner ? WaylandStatus : WaylandStatusNoCorner;
        var display = StopHotkey.Display(hotkeyText);
        return display.Length == 0 ? "No stop hotkey" : $"Stop: {display}";
    }

    /// <summary>The short hint in the overlay's footer ("Ctrl+Alt+X to stop"); empty when there is nothing to add.</summary>
    public static string OverlayText(LinuxSessionKind kind, string? hotkeyText, bool hotkeyActive, bool failsafeCorner)
    {
        if (HotkeySupported(kind) && hotkeyActive)
        {
            var display = StopHotkey.Display(hotkeyText);
            if (display.Length > 0) return $"{display} to stop";
        }
        return failsafeCorner ? "Top-left corner stops" : "";
    }
}

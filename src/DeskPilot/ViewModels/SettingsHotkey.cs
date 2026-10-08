using System.Windows.Input;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.ViewModels;

/// <summary>
/// Turns a captured key press into the text stored in <c>UiSettings.StopHotkey</c>, e.g. "Ctrl+Alt+X",
/// and checks that a stored hotkey is usable. The text is readable by both KeyCombo and WPF's
/// KeyGestureConverter.
/// </summary>
public static class SettingsHotkey
{
    public const string DefaultHotkey = "Ctrl+Alt+X";

    private static readonly HashSet<Key> ModifierKeysOnly = new()
    {
        Key.LeftCtrl, Key.RightCtrl, Key.LeftAlt, Key.RightAlt, Key.LeftShift, Key.RightShift,
        Key.LWin, Key.RWin, Key.System, Key.None, Key.ImeProcessed, Key.DeadCharProcessed,
    };

    // Keys that make sense as a global hotkey without Ctrl/Alt/Win because nobody types them.
    private static readonly HashSet<string> StandaloneKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "pause", "scrolllock", "printscreen", "insert",
    };

    public static bool IsModifierOnly(Key key) => ModifierKeysOnly.Contains(key);

    /// <summary>Display text for a combination, or null while only modifiers are held.</summary>
    public static string? Format(ModifierKeys modifiers, Key key)
    {
        if (IsModifierOnly(key)) return null;
        var name = KeyName(key);
        if (name == null) return null;
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(name);
        return string.Join("+", parts);
    }

    /// <summary>"Ctrl+Alt+" style prefix shown while the user is still holding modifiers.</summary>
    public static string FormatModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "" : string.Join("+", parts) + "+";
    }

    public static string? KeyName(Key key)
    {
        if (key >= Key.A && key <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
        if (key >= Key.F1 && key <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Tab => "Tab",
            Key.Escape => "Esc",
            Key.Delete => "Delete",
            Key.Insert => "Insert",
            Key.Home => "Home",
            Key.End => "End",
            Key.PageUp => "PageUp",
            Key.PageDown => "PageDown",
            Key.Up => "Up",
            Key.Down => "Down",
            Key.Left => "Left",
            Key.Right => "Right",
            Key.Pause => "Pause",
            Key.Scroll => "ScrollLock",
            Key.PrintScreen => "PrintScreen",
            Key.CapsLock => "CapsLock",
            Key.NumLock => "NumLock",
            Key.Multiply => "Multiply",
            Key.Add => "Add",
            Key.Subtract => "Subtract",
            Key.Divide => "Divide",
            Key.Decimal => "Decimal",
            Key.Apps => "Apps",
            _ when key.ToString().StartsWith("Oem", StringComparison.Ordinal) => key.ToString(),
            _ => null,
        };
    }

    /// <summary>A stop hotkey must have one main key and Ctrl, Alt or Win (except keys nobody types, like Pause).</summary>
    public static bool TryValidate(string? text, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Click the box and press a key combination, for example Ctrl+Alt+X.";
            return false;
        }
        if (!KeyCombo.TryParse(text, out var combo, out var parseError))
        {
            error = "Not a key combination: " + parseError;
            return false;
        }
        if (combo.Key == null)
        {
            error = "Add a main key to the modifiers, for example Ctrl+Alt+X.";
            return false;
        }
        var hasStrongModifier = combo.Has("ctrl") || combo.Has("alt") || combo.Has("win");
        var isFunctionKey = combo.Key.Length >= 2 && combo.Key[0] == 'f' && combo.Key[1..].All(char.IsDigit);
        if (!hasStrongModifier && !isFunctionKey && !StandaloneKeys.Contains(combo.Key))
        {
            error = "Include Ctrl, Alt or Win so normal typing cannot trigger it.";
            return false;
        }
        return true;
    }
}

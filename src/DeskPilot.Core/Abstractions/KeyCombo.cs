namespace DeskPilot.Core.Abstractions;

/// <summary>
/// A parsed key combination such as "ctrl+shift+t". Modifiers are normalized to
/// ctrl / shift / alt / win; the remaining key name is normalized (lower case, aliases folded).
/// This type only normalizes names; mapping names to virtual-key codes is the input simulator's job.
/// </summary>
public sealed class KeyCombo
{
    public static readonly IReadOnlySet<string> ModifierNames = new HashSet<string> { "ctrl", "shift", "alt", "win" };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["control"] = "ctrl", ["ctl"] = "ctrl", ["lctrl"] = "ctrl", ["rctrl"] = "ctrl",
        ["lshift"] = "shift", ["rshift"] = "shift",
        ["menu"] = "alt", ["option"] = "alt", ["lalt"] = "alt", ["ralt"] = "alt", ["altgr"] = "alt",
        ["windows"] = "win", ["super"] = "win", ["meta"] = "win", ["cmd"] = "win", ["command"] = "win", ["lwin"] = "win", ["rwin"] = "win", ["start"] = "win",
        ["return"] = "enter", ["ret"] = "enter",
        ["esc"] = "escape",
        ["del"] = "delete",
        ["ins"] = "insert",
        ["bksp"] = "backspace", ["back"] = "backspace", ["bs"] = "backspace",
        ["pgup"] = "pageup", ["page_up"] = "pageup", ["prior"] = "pageup",
        ["pgdn"] = "pagedown", ["pgdown"] = "pagedown", ["page_down"] = "pagedown", ["next"] = "pagedown",
        ["arrowup"] = "up", ["arrow_up"] = "up", ["uparrow"] = "up",
        ["arrowdown"] = "down", ["arrow_down"] = "down", ["downarrow"] = "down",
        ["arrowleft"] = "left", ["arrow_left"] = "left", ["leftarrow"] = "left",
        ["arrowright"] = "right", ["arrow_right"] = "right", ["rightarrow"] = "right",
        ["spacebar"] = "space", [" "] = "space",
        ["prtsc"] = "printscreen", ["prtscr"] = "printscreen", ["print"] = "printscreen", ["print_screen"] = "printscreen", ["snapshot"] = "printscreen",
        ["apps"] = "contextmenu", ["context_menu"] = "contextmenu",
        ["caps"] = "capslock", ["caps_lock"] = "capslock",
        ["num_lock"] = "numlock", ["scroll_lock"] = "scrolllock",
        ["plus"] = "+", ["minus"] = "-", ["comma"] = ",", ["period"] = ".", ["dot"] = ".",
    };

    public IReadOnlyList<string> Modifiers { get; }
    /// <summary>The main key, or null for a modifier-only combo (e.g. "win").</summary>
    public string? Key { get; }

    private KeyCombo(IReadOnlyList<string> modifiers, string? key)
    {
        Modifiers = modifiers;
        Key = key;
    }

    public bool Has(string modifier) => Modifiers.Contains(modifier);

    /// <summary>Normalized text form, e.g. "ctrl+shift+t" (modifiers in fixed order).</summary>
    public override string ToString()
    {
        var parts = new List<string>(Modifiers);
        if (Key != null) parts.Add(Key);
        return string.Join("+", parts);
    }

    public static string NormalizeName(string raw)
    {
        var t = raw.Trim();
        if (t.Length == 0) return t;
        if (Aliases.TryGetValue(t, out var a)) return a;
        return t.Length == 1 ? t.ToLowerInvariant() : t.ToLowerInvariant().Replace(" ", "");
    }

    /// <summary>
    /// Parses "ctrl+c", "Ctrl + Shift + Esc", "win", "alt+f4", "ctrl+plus", "ctrl++".
    /// Separators: '+' or '-' between names ("ctrl-c" works too) when unambiguous.
    /// </summary>
    public static bool TryParse(string? text, out KeyCombo combo, out string error)
    {
        combo = null!;
        error = "";
        if (string.IsNullOrWhiteSpace(text)) { error = "empty key combination"; return false; }

        var s = text.Trim();
        var tokens = new List<string>();
        // Handle a trailing literal '+' ("ctrl++") and a literal '-' ("ctrl+-").
        if (s.Length > 1 && s.EndsWith("++")) { tokens.AddRange(SplitTokens(s[..^2])); tokens.Add("+"); }
        else if (s == "+") tokens.Add("+");
        else tokens.AddRange(SplitTokens(s));

        var mods = new List<string>();
        string? key = null;
        foreach (var raw in tokens)
        {
            var n = NormalizeName(raw);
            if (n.Length == 0) continue;
            if (ModifierNames.Contains(n))
            {
                if (!mods.Contains(n)) mods.Add(n);
                continue;
            }
            if (key != null) { error = $"more than one non-modifier key in '{text}' ('{key}' and '{n}'); send them as separate presses"; return false; }
            key = n;
        }
        if (mods.Count == 0 && key == null) { error = $"no keys in '{text}'"; return false; }

        // Fixed modifier order for stable output.
        var ordered = new[] { "ctrl", "alt", "shift", "win" }.Where(mods.Contains).ToList();
        combo = new KeyCombo(ordered, key);
        return true;
    }

    public static KeyCombo Parse(string text) =>
        TryParse(text, out var c, out var e) ? c : throw new FormatException(e);

    private static IEnumerable<string> SplitTokens(string s)
    {
        if (s.Contains('+')) return s.Split('+');
        // "ctrl-c" style, but keep a lone "-" and names like "f-keys" sane: only split when every part looks like a key name.
        if (s.Length > 1 && s.Contains('-') && !s.StartsWith('-') && !s.EndsWith('-'))
        {
            var parts = s.Split('-');
            if (parts.Take(parts.Length - 1).All(p => ModifierNames.Contains(NormalizeName(p)))) return parts;
        }
        return new[] { s };
    }
}

using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Windows;

/// <summary>A virtual key plus the modifiers the keyboard layout needs to produce it.</summary>
internal readonly record struct KeyStroke(ushort Vk, bool Extended, bool NeedsShift = false, bool NeedsCtrl = false, bool NeedsAlt = false);

/// <summary>Maps normalized key names (see <see cref="KeyCombo"/>) to virtual-key codes.</summary>
internal static class KeyMap
{
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_PAUSE = 0x13, VK_CAPITAL = 0x14, VK_ESCAPE = 0x1B, VK_SPACE = 0x20;
    public const ushort VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4, VK_LWIN = 0x5B;

    public const string Examples =
        "a-z, 0-9, f1-f24, enter, escape, tab, space, backspace, delete, insert, home, end, pageup, pagedown, " +
        "up, down, left, right, printscreen, contextmenu, capslock, numlock, scrolllock, pause, volumeup, volumedown, " +
        "volumemute, medianext, mediaprev, mediaplaypause, mediastop, browserback, browserforward, browserrefresh, " +
        "browserhome, numpad0-numpad9, multiply, add, subtract, decimal, divide, ctrl, alt, shift, win, " +
        "or a single character such as + - = , . / ; [ ]";

    private static readonly Dictionary<string, (ushort Vk, bool Ext)> Named = BuildNamed();

    private static readonly Dictionary<string, KeyStroke> Modifiers = new(StringComparer.Ordinal)
    {
        ["ctrl"] = new(VK_LCONTROL, false),
        ["shift"] = new(VK_LSHIFT, false),
        ["alt"] = new(VK_LMENU, false),
        ["win"] = new(VK_LWIN, true),
    };

    private static readonly Dictionary<string, char> CharAliases = new(StringComparer.Ordinal)
    {
        ["equals"] = '=', ["equal"] = '=', ["slash"] = '/', ["forwardslash"] = '/', ["backslash"] = '\\',
        ["semicolon"] = ';', ["colon"] = ':', ["quote"] = '\'', ["apostrophe"] = '\'', ["singlequote"] = '\'',
        ["doublequote"] = '"', ["backtick"] = '`', ["backquote"] = '`', ["grave"] = '`', ["tilde"] = '~',
        ["lbracket"] = '[', ["leftbracket"] = '[', ["openbracket"] = '[', ["rbracket"] = ']', ["rightbracket"] = ']',
        ["closebracket"] = ']', ["hyphen"] = '-', ["dash"] = '-', ["underscore"] = '_', ["asterisk"] = '*',
        ["star"] = '*', ["hash"] = '#', ["pound"] = '#', ["at"] = '@', ["exclamation"] = '!', ["question"] = '?',
        ["lessthan"] = '<', ["greaterthan"] = '>', ["pipe"] = '|', ["ampersand"] = '&', ["percent"] = '%',
        ["caret"] = '^', ["dollar"] = '$', ["lparen"] = '(', ["rparen"] = ')',
    };

    private static Dictionary<string, (ushort, bool)> BuildNamed()
    {
        var d = new Dictionary<string, (ushort, bool)>(StringComparer.Ordinal)
        {
            ["enter"] = (VK_RETURN, false), ["escape"] = (VK_ESCAPE, false), ["tab"] = (VK_TAB, false),
            ["space"] = (VK_SPACE, false), ["backspace"] = (VK_BACK, false),
            ["delete"] = (0x2E, true), ["insert"] = (0x2D, true), ["home"] = (0x24, true), ["end"] = (0x23, true),
            ["pageup"] = (0x21, true), ["pagedown"] = (0x22, true),
            ["left"] = (0x25, true), ["up"] = (0x26, true), ["right"] = (0x27, true), ["down"] = (0x28, true),
            ["printscreen"] = (0x2C, true), ["contextmenu"] = (0x5D, true),
            ["capslock"] = (VK_CAPITAL, false), ["numlock"] = (0x90, true), ["scrolllock"] = (0x91, false),
            ["pause"] = (VK_PAUSE, false), ["break"] = (0x03, true), ["clear"] = (0x0C, false), ["help"] = (0x2F, false),
            ["sleep"] = (0x5F, false),
            ["volumemute"] = (0xAD, true), ["volumedown"] = (0xAE, true), ["volumeup"] = (0xAF, true),
            ["mute"] = (0xAD, true),
            ["medianext"] = (0xB0, true), ["mediaprev"] = (0xB1, true), ["mediaprevious"] = (0xB1, true),
            ["mediastop"] = (0xB2, true), ["mediaplaypause"] = (0xB3, true), ["playpause"] = (0xB3, true),
            ["browserback"] = (0xA6, true), ["browserforward"] = (0xA7, true), ["browserrefresh"] = (0xA8, true),
            ["browserstop"] = (0xA9, true), ["browsersearch"] = (0xAA, true), ["browserfavorites"] = (0xAB, true),
            ["browserhome"] = (0xAC, true), ["launchmail"] = (0xB4, true), ["launchapp1"] = (0xB6, true), ["launchapp2"] = (0xB7, true),
            ["multiply"] = (0x6A, false), ["add"] = (0x6B, false), ["separator"] = (0x6C, false), ["subtract"] = (0x6D, false),
            ["decimal"] = (0x6E, false), ["divide"] = (0x6F, true),
            ["numpadmultiply"] = (0x6A, false), ["numpadadd"] = (0x6B, false), ["numpadplus"] = (0x6B, false),
            ["numpadsubtract"] = (0x6D, false), ["numpadminus"] = (0x6D, false), ["numpaddecimal"] = (0x6E, false),
            ["numpaddivide"] = (0x6F, true), ["numpadenter"] = (VK_RETURN, true),
        };
        for (int i = 1; i <= 24; i++) d["f" + i] = ((ushort)(0x70 + i - 1), false);
        for (int i = 0; i <= 9; i++)
        {
            d["numpad" + i] = ((ushort)(0x60 + i), false);
            d["num" + i] = ((ushort)(0x60 + i), false);
        }
        return d;
    }

    public static bool IsModifier(string normalizedName) => Modifiers.ContainsKey(normalizedName);

    public static KeyStroke Modifier(string normalizedName) =>
        Modifiers.TryGetValue(normalizedName, out var k) ? k : throw new ArgumentException($"'{normalizedName}' is not a modifier (use ctrl, alt, shift or win).");

    /// <summary>
    /// Resolves a key name. <paramref name="vkKeyScan"/> is VkKeyScanEx on the target keyboard layout
    /// (low byte = VK, high byte = shift state, -1 = not on this layout); injected so tests need no layout.
    /// </summary>
    public static bool TryResolve(string name, Func<char, short> vkKeyScan, out KeyStroke stroke, out string error)
    {
        stroke = default;
        error = "";
        var n = KeyCombo.NormalizeName(name ?? "");
        if (n.Length == 0) { error = $"Empty key name. Use names like {Examples}."; return false; }

        if (Modifiers.TryGetValue(n, out var mod)) { stroke = mod; return true; }
        if (Named.TryGetValue(n, out var named)) { stroke = new KeyStroke(named.Vk, named.Ext); return true; }

        if (n.Length == 1)
        {
            char c = n[0];
            if (c is >= 'a' and <= 'z') { stroke = new KeyStroke((ushort)char.ToUpperInvariant(c), false); return true; }
            if (c is >= '0' and <= '9') { stroke = new KeyStroke(c, false); return true; }
            return TryFromLayout(c, vkKeyScan, out stroke, out error);
        }

        if (CharAliases.TryGetValue(n, out var aliased)) return TryFromLayout(aliased, vkKeyScan, out stroke, out error);

        error = $"Unknown key '{name}'. Use names like {Examples}.";
        return false;
    }

    public static KeyStroke Resolve(string name, Func<char, short> vkKeyScan) =>
        TryResolve(name, vkKeyScan, out var s, out var e) ? s : throw new ArgumentException(e, nameof(name));

    private static bool TryFromLayout(char c, Func<char, short> vkKeyScan, out KeyStroke stroke, out string error)
    {
        stroke = default;
        error = "";
        short r = vkKeyScan(c);
        if (r == -1 || (r & 0xFF) == 0xFF)
        {
            error = $"The character '{c}' is not on the current keyboard layout. Use type_text to enter it, or one of: {Examples}.";
            return false;
        }
        int state = (r >> 8) & 0xFF;
        stroke = new KeyStroke((ushort)(r & 0xFF), false, (state & 1) != 0, (state & 2) != 0, (state & 4) != 0);
        return true;
    }

    /// <summary>
    /// The modifier strokes for a combo: the combo's own modifiers in order, plus any the layout needs for
    /// the main key (e.g. shift for '+' on a US layout, ctrl+alt for AltGr characters), without duplicates.
    /// </summary>
    public static List<KeyStroke> ModifiersFor(IEnumerable<string> comboModifiers, KeyStroke? key)
    {
        var names = new List<string>(comboModifiers);
        if (key is { } k)
        {
            if (k.NeedsCtrl && !names.Contains("ctrl")) names.Add("ctrl");
            if (k.NeedsAlt && !names.Contains("alt")) names.Add("alt");
            if (k.NeedsShift && !names.Contains("shift")) names.Add("shift");
        }
        return names.Select(Modifier).ToList();
    }
}

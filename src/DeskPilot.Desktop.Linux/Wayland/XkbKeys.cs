using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>A key as the Wayland input routes need it: the xkb keysym (name for wtype, value for the portal) and the evdev code (ydotool, dotool).</summary>
internal readonly record struct XkbKey(string KeysymName, int Keysym, int EvdevCode, bool IsModifier = false);

/// <summary>Maps DeskPilot's normalized key names (see <see cref="KeyCombo"/>) to xkb keysyms and Linux evdev key codes.</summary>
internal static class XkbKeys
{
    public const string Examples =
        "a-z, 0-9, f1-f24, enter, escape, tab, space, backspace, delete, insert, home, end, pageup, pagedown, " +
        "up, down, left, right, printscreen, contextmenu, capslock, numlock, scrolllock, pause, volumeup, volumedown, " +
        "volumemute, medianext, mediaprev, mediaplaypause, mediastop, browserback, browserforward, browserrefresh, " +
        "browserhome, numpad0-numpad9, multiply, add, subtract, decimal, divide, ctrl, alt, shift, win, " +
        "or a single character such as + - = , . / ; [ ]";

    private static readonly Dictionary<string, XkbKey> Named = BuildNamed();

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

    // US-layout evdev codes for the printable ASCII characters; shifted ones also need Shift on ydotool.
    private static readonly Dictionary<char, (int Code, bool Shift)> AsciiEvdev = BuildAscii();

    private static Dictionary<string, XkbKey> BuildNamed()
    {
        var d = new Dictionary<string, XkbKey>(StringComparer.Ordinal)
        {
            ["ctrl"] = new("Control_L", 0xffe3, 29, true),
            ["shift"] = new("Shift_L", 0xffe1, 42, true),
            ["alt"] = new("Alt_L", 0xffe9, 56, true),
            ["win"] = new("Super_L", 0xffeb, 125, true),
            ["enter"] = new("Return", 0xff0d, 28),
            ["escape"] = new("Escape", 0xff1b, 1),
            ["tab"] = new("Tab", 0xff09, 15),
            ["space"] = new("space", 0x20, 57),
            ["backspace"] = new("BackSpace", 0xff08, 14),
            ["delete"] = new("Delete", 0xffff, 111),
            ["insert"] = new("Insert", 0xff63, 110),
            ["home"] = new("Home", 0xff50, 102),
            ["end"] = new("End", 0xff57, 107),
            ["pageup"] = new("Prior", 0xff55, 104),
            ["pagedown"] = new("Next", 0xff56, 109),
            ["left"] = new("Left", 0xff51, 105),
            ["up"] = new("Up", 0xff52, 103),
            ["right"] = new("Right", 0xff53, 106),
            ["down"] = new("Down", 0xff54, 108),
            ["printscreen"] = new("Print", 0xff61, 99),
            ["contextmenu"] = new("Menu", 0xff67, 127),
            ["capslock"] = new("Caps_Lock", 0xffe5, 58),
            ["numlock"] = new("Num_Lock", 0xff7f, 69),
            ["scrolllock"] = new("Scroll_Lock", 0xff14, 70),
            ["pause"] = new("Pause", 0xff13, 119),
            ["break"] = new("Break", 0xff6b, 119),
            ["clear"] = new("Clear", 0xff0b, 0),
            ["help"] = new("Help", 0xff6a, 138),
            ["sleep"] = new("XF86Sleep", 0x1008ff2f, 142),
            ["volumemute"] = new("XF86AudioMute", 0x1008ff12, 113),
            ["mute"] = new("XF86AudioMute", 0x1008ff12, 113),
            ["volumedown"] = new("XF86AudioLowerVolume", 0x1008ff11, 114),
            ["volumeup"] = new("XF86AudioRaiseVolume", 0x1008ff13, 115),
            ["medianext"] = new("XF86AudioNext", 0x1008ff17, 163),
            ["mediaprev"] = new("XF86AudioPrev", 0x1008ff16, 165),
            ["mediaprevious"] = new("XF86AudioPrev", 0x1008ff16, 165),
            ["mediastop"] = new("XF86AudioStop", 0x1008ff15, 166),
            ["mediaplaypause"] = new("XF86AudioPlay", 0x1008ff14, 164),
            ["playpause"] = new("XF86AudioPlay", 0x1008ff14, 164),
            ["browserback"] = new("XF86Back", 0x1008ff26, 158),
            ["browserforward"] = new("XF86Forward", 0x1008ff27, 159),
            ["browserrefresh"] = new("XF86Refresh", 0x1008ff29, 173),
            ["browserstop"] = new("XF86Stop", 0x1008ff28, 128),
            ["browsersearch"] = new("XF86Search", 0x1008ff1b, 217),
            ["browserfavorites"] = new("XF86Favorites", 0x1008ff30, 156),
            ["browserhome"] = new("XF86HomePage", 0x1008ff18, 172),
            ["launchmail"] = new("XF86Mail", 0x1008ff19, 155),
            ["launchapp1"] = new("XF86MyComputer", 0x1008ff33, 157),
            ["launchapp2"] = new("XF86Calculator", 0x1008ff1d, 140),
            ["multiply"] = new("KP_Multiply", 0xffaa, 55),
            ["add"] = new("KP_Add", 0xffab, 78),
            ["separator"] = new("KP_Separator", 0xffac, 121),
            ["subtract"] = new("KP_Subtract", 0xffad, 74),
            ["decimal"] = new("KP_Decimal", 0xffae, 83),
            ["divide"] = new("KP_Divide", 0xffaf, 98),
            ["numpadmultiply"] = new("KP_Multiply", 0xffaa, 55),
            ["numpadadd"] = new("KP_Add", 0xffab, 78),
            ["numpadplus"] = new("KP_Add", 0xffab, 78),
            ["numpadsubtract"] = new("KP_Subtract", 0xffad, 74),
            ["numpadminus"] = new("KP_Subtract", 0xffad, 74),
            ["numpaddecimal"] = new("KP_Decimal", 0xffae, 83),
            ["numpaddivide"] = new("KP_Divide", 0xffaf, 98),
            ["numpadenter"] = new("KP_Enter", 0xff8d, 96),
        };
        int[] fCodes = { 59, 60, 61, 62, 63, 64, 65, 66, 67, 68, 87, 88, 183, 184, 185, 186, 187, 188, 189, 190, 191, 192, 193, 194 };
        for (int i = 1; i <= 24; i++) d["f" + i] = new XkbKey("F" + i, 0xffbe + i - 1, fCodes[i - 1]);
        int[] kpCodes = { 82, 79, 80, 81, 75, 76, 77, 71, 72, 73 };
        for (int i = 0; i <= 9; i++)
        {
            var k = new XkbKey("KP_" + i, 0xffb0 + i, kpCodes[i]);
            d["numpad" + i] = k;
            d["num" + i] = k;
        }
        return d;
    }

    private static Dictionary<char, (int, bool)> BuildAscii()
    {
        var d = new Dictionary<char, (int, bool)>();
        const string row1 = "qwertyuiop", row2 = "asdfghjkl", row3 = "zxcvbnm";
        for (int i = 0; i < row1.Length; i++) { d[row1[i]] = (16 + i, false); d[char.ToUpperInvariant(row1[i])] = (16 + i, true); }
        for (int i = 0; i < row2.Length; i++) { d[row2[i]] = (30 + i, false); d[char.ToUpperInvariant(row2[i])] = (30 + i, true); }
        for (int i = 0; i < row3.Length; i++) { d[row3[i]] = (44 + i, false); d[char.ToUpperInvariant(row3[i])] = (44 + i, true); }
        const string digits = "1234567890", shiftedDigits = "!@#$%^&*()";
        for (int i = 0; i < 10; i++) { d[digits[i]] = (2 + i, false); d[shiftedDigits[i]] = (2 + i, true); }
        void Add(char plain, char shifted, int code) { d[plain] = (code, false); d[shifted] = (code, true); }
        Add('-', '_', 12); Add('=', '+', 13); Add('[', '{', 26); Add(']', '}', 27); Add(';', ':', 39);
        Add('\'', '"', 40); Add('`', '~', 41); Add('\\', '|', 43); Add(',', '<', 51); Add('.', '>', 52); Add('/', '?', 53);
        d[' '] = (57, false);
        d['\n'] = (28, false);
        d['\t'] = (15, false);
        return d;
    }

    /// <summary>The keysym of a character: Latin-1 maps directly, everything else to 0x01000000 + code point (xkb rule).</summary>
    public static int KeysymForCodePoint(int cp) => cp switch
    {
        '\n' or '\r' => 0xff0d,
        '\t' => 0xff09,
        '\b' => 0xff08,
        0x1b => 0xff1b,
        >= 0x20 and <= 0x7e => cp,
        >= 0xa0 and <= 0xff => cp,
        _ => 0x01000000 | cp,
    };

    /// <summary>A keysym name xkbcommon accepts for any character ("a", or "U20AC" for the euro sign).</summary>
    public static string KeysymNameForCodePoint(int cp) =>
        cp is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' ? ((char)cp).ToString() : $"U{cp:X4}";

    public static bool TryResolve(string name, out XkbKey key, out string error)
    {
        key = default;
        error = "";
        var n = KeyCombo.NormalizeName(name ?? "");
        if (n.Length == 0) { error = $"Empty key name. Use names like {Examples}."; return false; }
        if (Named.TryGetValue(n, out key)) return true;

        string? text = n.Length == 1 || (n.Length == 2 && char.IsSurrogatePair(n, 0)) ? n : null;
        if (text == null && CharAliases.TryGetValue(n, out var aliased)) text = aliased.ToString();
        if (text != null)
        {
            int cp = char.ConvertToUtf32(text, 0);
            int evdev = AsciiEvdev.TryGetValue(char.ToLowerInvariant(text[0]), out var e) && text.Length == 1 ? e.Code : 0;
            key = new XkbKey(KeysymNameForCodePoint(cp), KeysymForCodePoint(cp), evdev);
            return true;
        }

        error = $"Unknown key '{name}'. Use names like {Examples}.";
        return false;
    }

    public static XkbKey Resolve(string name) =>
        TryResolve(name, out var k, out var e) ? k : throw new ArgumentException(e, nameof(name));

    public static XkbKey Modifier(string normalized) =>
        Named.TryGetValue(normalized, out var k) && k.IsModifier ? k : throw new ArgumentException($"'{normalized}' is not a modifier (use ctrl, alt, shift or win).");

    /// <summary>wtype's -M/-m modifier name for a DeskPilot modifier.</summary>
    public static string WtypeModifier(string normalized) => normalized switch
    {
        "ctrl" => "ctrl",
        "shift" => "shift",
        "alt" => "alt",
        "win" => "logo",
        _ => throw new ArgumentException($"'{normalized}' is not a modifier"),
    };

    /// <summary>US-layout evdev code (and whether Shift is needed) for an ASCII character, for ydotool.</summary>
    public static bool TryAsciiEvdev(char c, out int code, out bool shift)
    {
        if (AsciiEvdev.TryGetValue(c, out var e)) { code = e.Code; shift = e.Shift; return true; }
        code = 0;
        shift = false;
        return false;
    }

    /// <summary>Text split into Unicode code points, with CR LF and lone CR folded into LF.</summary>
    public static IEnumerable<int> CodePoints(string text)
    {
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        for (int i = 0; i < t.Length; i++)
        {
            if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1]))
            {
                yield return char.ConvertToUtf32(t[i], t[i + 1]);
                i++;
            }
            else if (!char.IsSurrogate(t[i]))
            {
                yield return t[i];
            }
        }
    }
}

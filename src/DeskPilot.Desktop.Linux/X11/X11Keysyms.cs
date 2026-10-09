using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.X11;

/// <summary>
/// Key names (as normalized by <see cref="KeyCombo"/>) and characters to X keysyms. Pure tables, no X calls,
/// so they are tested on any platform. Values from X11/keysymdef.h and X11/XF86keysym.h.
/// </summary>
internal static class X11Keysyms
{
    public const uint NoSymbol = 0;

    public const uint BackSpace = 0xff08, Tab = 0xff09, Clear = 0xff0b, Return = 0xff0d, Pause = 0xff13, ScrollLock = 0xff14,
        Escape = 0xff1b, Delete = 0xffff;
    public const uint Home = 0xff50, Left = 0xff51, Up = 0xff52, Right = 0xff53, Down = 0xff54, Prior = 0xff55, Next = 0xff56, End = 0xff57;
    public const uint Print = 0xff61, Insert = 0xff63, Menu = 0xff67, Help = 0xff6a, Break = 0xff6b, NumLock = 0xff7f;
    public const uint KP_Enter = 0xff8d, KP_Multiply = 0xffaa, KP_Add = 0xffab, KP_Separator = 0xffac, KP_Subtract = 0xffad,
        KP_Decimal = 0xffae, KP_Divide = 0xffaf, KP_0 = 0xffb0;
    public const uint F1 = 0xffbe;
    public const uint ShiftL = 0xffe1, ShiftR = 0xffe2, ControlL = 0xffe3, ControlR = 0xffe4, CapsLock = 0xffe5,
        MetaL = 0xffe7, MetaR = 0xffe8, AltL = 0xffe9, AltR = 0xffea, SuperL = 0xffeb, SuperR = 0xffec, ModeSwitch = 0xff7e,
        IsoLevel3Shift = 0xfe03;
    public const uint EuroSign = 0x20ac;

    public const uint XF86AudioLowerVolume = 0x1008FF11, XF86AudioMute = 0x1008FF12, XF86AudioRaiseVolume = 0x1008FF13,
        XF86AudioPlay = 0x1008FF14, XF86AudioStop = 0x1008FF15, XF86AudioPrev = 0x1008FF16, XF86AudioNext = 0x1008FF17,
        XF86HomePage = 0x1008FF18, XF86Mail = 0x1008FF19, XF86Search = 0x1008FF1B, XF86Calculator = 0x1008FF1D,
        XF86Back = 0x1008FF26, XF86Forward = 0x1008FF27, XF86Stop = 0x1008FF28, XF86Sleep = 0x1008FF2F,
        XF86Favorites = 0x1008FF30, XF86MyComputer = 0x1008FF33, XF86Reload = 0x1008FF73;

    public const string Examples =
        "a-z, 0-9, f1-f24, enter, escape, tab, space, backspace, delete, insert, home, end, pageup, pagedown, " +
        "up, down, left, right, printscreen, contextmenu, capslock, numlock, scrolllock, pause, volumeup, volumedown, " +
        "volumemute, medianext, mediaprev, mediaplaypause, mediastop, browserback, browserforward, browserrefresh, " +
        "browserhome, numpad0-numpad9, multiply, add, subtract, decimal, divide, ctrl, alt, shift, win, " +
        "or a single character such as + - = , . / ; [ ]";

    private static readonly Dictionary<string, uint> Modifiers = new(StringComparer.Ordinal)
    {
        ["ctrl"] = ControlL, ["shift"] = ShiftL, ["alt"] = AltL, ["win"] = SuperL,
    };

    private static readonly Dictionary<string, uint> Named = BuildNamed();

    // Same spelled-out punctuation names the Windows key map accepts.
    private static readonly Dictionary<string, char> CharAliases = new(StringComparer.Ordinal)
    {
        ["equals"] = '=', ["equal"] = '=', ["slash"] = '/', ["forwardslash"] = '/', ["backslash"] = '\\',
        ["semicolon"] = ';', ["colon"] = ':', ["quote"] = '\'', ["apostrophe"] = '\'', ["singlequote"] = '\'',
        ["doublequote"] = '"', ["backtick"] = '`', ["backquote"] = '`', ["grave"] = '`', ["tilde"] = '~',
        ["lbracket"] = '[', ["leftbracket"] = '[', ["openbracket"] = '[', ["bracketleft"] = '[',
        ["rbracket"] = ']', ["rightbracket"] = ']', ["closebracket"] = ']', ["bracketright"] = ']',
        ["hyphen"] = '-', ["dash"] = '-', ["underscore"] = '_', ["asterisk"] = '*',
        ["star"] = '*', ["hash"] = '#', ["pound"] = '#', ["at"] = '@', ["exclamation"] = '!', ["question"] = '?',
        ["lessthan"] = '<', ["greaterthan"] = '>', ["pipe"] = '|', ["ampersand"] = '&', ["percent"] = '%',
        ["caret"] = '^', ["dollar"] = '$', ["lparen"] = '(', ["rparen"] = ')',
    };

    private static Dictionary<string, uint> BuildNamed()
    {
        var d = new Dictionary<string, uint>(StringComparer.Ordinal)
        {
            ["enter"] = Return, ["escape"] = Escape, ["tab"] = Tab, ["space"] = ' ', ["backspace"] = BackSpace,
            ["delete"] = Delete, ["insert"] = Insert, ["home"] = Home, ["end"] = End,
            ["pageup"] = Prior, ["pagedown"] = Next,
            ["left"] = Left, ["up"] = Up, ["right"] = Right, ["down"] = Down,
            ["printscreen"] = Print, ["contextmenu"] = Menu,
            ["capslock"] = CapsLock, ["numlock"] = NumLock, ["scrolllock"] = ScrollLock,
            ["pause"] = Pause, ["break"] = Break, ["clear"] = Clear, ["help"] = Help, ["sleep"] = XF86Sleep,
            ["volumemute"] = XF86AudioMute, ["mute"] = XF86AudioMute,
            ["volumedown"] = XF86AudioLowerVolume, ["volumeup"] = XF86AudioRaiseVolume,
            ["medianext"] = XF86AudioNext, ["mediaprev"] = XF86AudioPrev, ["mediaprevious"] = XF86AudioPrev,
            ["mediastop"] = XF86AudioStop, ["mediaplaypause"] = XF86AudioPlay, ["playpause"] = XF86AudioPlay,
            ["browserback"] = XF86Back, ["browserforward"] = XF86Forward, ["browserrefresh"] = XF86Reload,
            ["browserstop"] = XF86Stop, ["browsersearch"] = XF86Search, ["browserfavorites"] = XF86Favorites,
            ["browserhome"] = XF86HomePage, ["launchmail"] = XF86Mail, ["launchapp1"] = XF86MyComputer,
            ["launchapp2"] = XF86Calculator,
            ["multiply"] = KP_Multiply, ["add"] = KP_Add, ["separator"] = KP_Separator, ["subtract"] = KP_Subtract,
            ["decimal"] = KP_Decimal, ["divide"] = KP_Divide,
            ["numpadmultiply"] = KP_Multiply, ["numpadadd"] = KP_Add, ["numpadplus"] = KP_Add,
            ["numpadsubtract"] = KP_Subtract, ["numpadminus"] = KP_Subtract, ["numpaddecimal"] = KP_Decimal,
            ["numpaddivide"] = KP_Divide, ["numpadenter"] = KP_Enter,
        };
        for (uint i = 1; i <= 24; i++) d["f" + i] = F1 + i - 1;
        for (uint i = 0; i <= 9; i++)
        {
            d["numpad" + i] = KP_0 + i;
            d["num" + i] = KP_0 + i;
        }
        return d;
    }

    /// <summary>Every key name this table resolves besides single characters (for tests and help).</summary>
    internal static IEnumerable<string> AllNames => Modifiers.Keys.Concat(Named.Keys).Concat(CharAliases.Keys);

    public static bool IsModifier(string normalizedName) => Modifiers.ContainsKey(normalizedName);

    /// <summary>Resolves a key name or a single character to a keysym.</summary>
    public static bool TryGetKeysym(string? name, out uint keysym, out string error)
    {
        keysym = NoSymbol;
        error = "";
        var n = KeyCombo.NormalizeName(name ?? "");
        if (n.Length == 0) { error = $"Empty key name. Use names like {Examples}."; return false; }
        if (Modifiers.TryGetValue(n, out keysym)) return true;
        if (Named.TryGetValue(n, out keysym)) return true;
        if (CharAliases.TryGetValue(n, out var c)) { keysym = ForCodePoint(c); return true; }

        // One character, possibly a surrogate pair.
        var runes = n.EnumerateRunes().ToList();
        if (runes.Count == 1 && runes[0].Value >= 0x20 && runes[0].Value != 0x7f)
        {
            keysym = ForCodePoint(runes[0].Value);
            return true;
        }
        error = $"Unknown key '{name}'. Use names like {Examples}.";
        return false;
    }

    /// <summary>
    /// The keysym that types a Unicode code point: '\n' Return, '\t' Tab, Latin-1 characters their own code,
    /// everything else 0x01000000 | code point (the Unicode keysym range every toolkit understands).
    /// </summary>
    public static uint ForCodePoint(int codePoint) => codePoint switch
    {
        '\n' => Return,
        '\t' => Tab,
        '\b' => BackSpace,
        0x1b => Escape,
        >= 0x20 and <= 0x7e => (uint)codePoint,
        >= 0xa0 and <= 0xff => (uint)codePoint,
        _ => 0x01000000u | (uint)codePoint,
    };

    /// <summary>The character a keysym types, or null for keysyms that are not characters (function keys, keypad...).</summary>
    public static int? ToCodePoint(uint keysym)
    {
        if (keysym is >= 0x20 and <= 0x7e or >= 0xa0 and <= 0xff) return (int)keysym;
        if ((keysym & 0xff000000) == 0x01000000)
        {
            int cp = (int)(keysym & 0x00ffffff);
            return cp is >= 0x100 and <= 0x10ffff ? cp : null;
        }
        if (keysym == EuroSign) return 0x20ac;
        return null;
    }
}

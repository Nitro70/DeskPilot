namespace DeskPilot.Desktop.Linux.X11;

/// <summary>A keycode to press, with Shift when the keysym sits on the shifted level of that key.</summary>
internal readonly record struct KeyChoice(int Keycode, bool Shift);

/// <summary>
/// The core keyboard mapping (XGetKeyboardMapping) as plain data, plus the XKB group in use. Pure logic:
/// finding the key and level for a keysym, and picking spare keycodes for temporary remaps.
/// Core mapping columns per keycode: group 1 level 1, group 1 level 2, group 2 level 1, group 2 level 2, then
/// level 3 and up (AltGr). Only levels 1 and 2 are used for typing; anything else goes through a remapped spare key.
/// </summary>
internal sealed class X11Keymap
{
    private readonly uint[] _syms;

    public int MinKeycode { get; }
    public int MaxKeycode { get; }
    public int KeysymsPerKeycode { get; }
    /// <summary>Active XKB group (0-based).</summary>
    public int Group { get; }
    /// <summary>Caps Lock is locked on.</summary>
    public bool CapsLock { get; }

    public X11Keymap(int minKeycode, int keysymsPerKeycode, uint[] syms, int group = 0, bool capsLock = false)
    {
        if (keysymsPerKeycode <= 0) throw new ArgumentOutOfRangeException(nameof(keysymsPerKeycode));
        MinKeycode = minKeycode;
        KeysymsPerKeycode = keysymsPerKeycode;
        _syms = syms;
        MaxKeycode = minKeycode + syms.Length / keysymsPerKeycode - 1;
        Group = group;
        CapsLock = capsLock;
    }

    public uint Get(int keycode, int column)
    {
        if (keycode < MinKeycode || keycode > MaxKeycode || column < 0 || column >= KeysymsPerKeycode) return X11Keysyms.NoSymbol;
        return _syms[(keycode - MinKeycode) * KeysymsPerKeycode + column];
    }

    /// <summary>All keysyms of one keycode (to restore it after a temporary remap).</summary>
    public uint[] GetAll(int keycode)
    {
        var result = new uint[KeysymsPerKeycode];
        for (int c = 0; c < KeysymsPerKeycode; c++) result[c] = Get(keycode, c);
        return result;
    }

    /// <summary>First core-mapping column of a group; groups 3 and 4 are not addressed (their characters get remapped).</summary>
    private int? GroupBase(int group) => group is 0 or 1 && 2 * group < KeysymsPerKeycode ? 2 * group : null;

    /// <summary>
    /// The key and level that produce a keysym in a group: level 1 on any key wins over level 2 (then Shift).
    /// A key with only a level-1 entry (NoSymbol on level 2) behaves the same on both levels.
    /// </summary>
    private KeyChoice? FindInGroup(uint keysym, int group)
    {
        if (keysym == X11Keysyms.NoSymbol || GroupBase(group) is not int b) return null;
        for (int kc = MinKeycode; kc <= MaxKeycode; kc++)
            if (Get(kc, b) == keysym) return new KeyChoice(kc, false);
        for (int kc = MinKeycode; kc <= MaxKeycode; kc++)
            if (Get(kc, b + 1) == keysym) return new KeyChoice(kc, true);
        return null;
    }

    /// <summary>
    /// A key for a named keysym (Return, F5, Control_L, a letter in a shortcut...): the active group first, then
    /// group 1, like shortcuts on a real keyboard (ctrl+c is the C key whatever layout is active).
    /// </summary>
    public KeyChoice? FindKeysym(uint keysym)
    {
        var inGroup = FindInGroup(keysym, Group);
        if (inGroup != null || Group == 0) return inGroup;
        return FindInGroup(keysym, 0);
    }

    /// <summary>
    /// A key that types the character in the active group, with Shift when it is on level 2, corrected for
    /// Caps Lock on letters. Null when the character is not on the keyboard (it is typed through a remap then).
    /// </summary>
    public KeyChoice? FindCodePoint(int codePoint)
    {
        if (GroupBase(Group) is not int b) return null;
        KeyChoice? choice = null;
        // Look for the keysym itself and any keysym that maps to the same character (EuroSign for U+20AC...).
        for (int level = 0; level < 2 && choice == null; level++)
        {
            for (int kc = MinKeycode; kc <= MaxKeycode; kc++)
            {
                var ks = Get(kc, b + level);
                if (ks != X11Keysyms.NoSymbol && X11Keysyms.ToCodePoint(ks) == codePoint)
                {
                    choice = new KeyChoice(kc, level == 1);
                    break;
                }
            }
        }

        // An upper-case letter on a key that only lists the lower case (level 2 empty) is that key with Shift.
        if (choice == null && codePoint <= 0xffff && char.IsUpper((char)codePoint))
        {
            int lower = char.ToLowerInvariant((char)codePoint);
            for (int kc = MinKeycode; kc <= MaxKeycode; kc++)
            {
                if (X11Keysyms.ToCodePoint(Get(kc, b)) == lower && Get(kc, b + 1) == X11Keysyms.NoSymbol)
                {
                    choice = new KeyChoice(kc, true);
                    break;
                }
            }
        }

        if (choice is { } c && CapsLock && IsCasedLetter(codePoint)) return c with { Shift = !c.Shift };
        return choice;
    }

    private static bool IsCasedLetter(int codePoint)
    {
        if (codePoint > 0xffff) return false;
        char ch = (char)codePoint;
        return char.ToUpperInvariant(ch) != char.ToLowerInvariant(ch);
    }

    /// <summary>Keycodes with no keysyms at all, highest first: safe to remap temporarily.</summary>
    public List<int> SpareKeycodes(int max)
    {
        var list = new List<int>();
        for (int kc = MaxKeycode; kc >= MinKeycode && list.Count < max; kc--)
        {
            bool empty = true;
            for (int c = 0; c < KeysymsPerKeycode && empty; c++)
                if (Get(kc, c) != X11Keysyms.NoSymbol) empty = false;
            if (empty) list.Add(kc);
        }
        return list;
    }
}

using System.Diagnostics;
using System.Reflection;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux.X11;
using DeskPilot.Desktop.Linux.X11.Interop;
using SkiaSharp;

namespace DeskPilot.Linux.Tests;

// ------------------------------------------------------------------------------------------------ pure logic

public class X11KeysymTests
{
    // Every key name the Windows key map resolves, so the same press_keys calls work on X11.
    private static readonly string[] WindowsKeyNames =
    {
        "enter", "escape", "tab", "space", "backspace", "delete", "insert", "home", "end", "pageup", "pagedown",
        "left", "up", "right", "down", "printscreen", "contextmenu", "capslock", "numlock", "scrolllock", "pause",
        "break", "clear", "help", "sleep", "volumemute", "volumedown", "volumeup", "mute", "medianext", "mediaprev",
        "mediaprevious", "mediastop", "mediaplaypause", "playpause", "browserback", "browserforward", "browserrefresh",
        "browserstop", "browsersearch", "browserfavorites", "browserhome", "launchmail", "launchapp1", "launchapp2",
        "multiply", "add", "separator", "subtract", "decimal", "divide", "numpadmultiply", "numpadadd", "numpadplus",
        "numpadsubtract", "numpadminus", "numpaddecimal", "numpaddivide", "numpadenter", "ctrl", "shift", "alt", "win",
        "equals", "equal", "slash", "forwardslash", "backslash", "semicolon", "colon", "quote", "apostrophe",
        "singlequote", "doublequote", "backtick", "backquote", "grave", "tilde", "lbracket", "leftbracket",
        "openbracket", "rbracket", "rightbracket", "closebracket", "hyphen", "dash", "underscore", "asterisk", "star",
        "hash", "pound", "at", "exclamation", "question", "lessthan", "greaterthan", "pipe", "ampersand", "percent",
        "caret", "dollar", "lparen", "rparen",
    };

    private static IEnumerable<string> KeyComboAliasTargets()
    {
        var field = typeof(KeyCombo).GetField("Aliases", BindingFlags.NonPublic | BindingFlags.Static)!;
        var aliases = (Dictionary<string, string>)field.GetValue(null)!;
        // " " is listed as an alias but NormalizeName trims it away before the lookup, so it is never a key name.
        return aliases.Keys.Concat(aliases.Values).Where(n => !string.IsNullOrWhiteSpace(n));
    }

    [Fact]
    public void Keysym_table_covers_every_key_name()
    {
        var names = new List<string>(WindowsKeyNames);
        names.AddRange(KeyComboAliasTargets());
        names.AddRange(KeyCombo.ModifierNames);
        names.AddRange(X11Keysyms.AllNames);
        for (int i = 1; i <= 24; i++) names.Add("f" + i);
        for (int i = 0; i <= 9; i++) { names.Add("numpad" + i); names.Add("num" + i); names.Add(i.ToString()); }
        for (char c = 'a'; c <= 'z'; c++) names.Add(c.ToString());
        names.AddRange("+-=,./;'[]\\`~!@#$%^&*()_{}|:\"<>?".Select(c => c.ToString()));

        var missing = names.Where(n => !X11Keysyms.TryGetKeysym(n, out var ks, out _) || ks == 0).Distinct().ToList();
        Assert.True(missing.Count == 0, "No keysym for: " + string.Join(", ", missing));
    }

    [Theory]
    [InlineData("ctrl", 0xffe3u)]
    [InlineData("alt", 0xffe9u)]
    [InlineData("shift", 0xffe1u)]
    [InlineData("win", 0xffebu)]
    [InlineData("enter", 0xff0du)]
    [InlineData("escape", 0xff1bu)]
    [InlineData("backspace", 0xff08u)]
    [InlineData("pageup", 0xff55u)]
    [InlineData("pagedown", 0xff56u)]
    [InlineData("printscreen", 0xff61u)]
    [InlineData("contextmenu", 0xff67u)]
    [InlineData("capslock", 0xffe5u)]
    [InlineData("scrolllock", 0xff14u)]
    [InlineData("f1", 0xffbeu)]
    [InlineData("f12", 0xffc9u)]
    [InlineData("f24", 0xffd5u)]
    [InlineData("numpad5", 0xffb5u)]
    [InlineData("multiply", 0xffaau)]
    [InlineData("volumeup", 0x1008FF13u)]
    [InlineData("volumedown", 0x1008FF11u)]
    [InlineData("volumemute", 0x1008FF12u)]
    [InlineData("medianext", 0x1008FF17u)]
    [InlineData("mediaprev", 0x1008FF16u)]
    [InlineData("mediaplaypause", 0x1008FF14u)]
    [InlineData("mediastop", 0x1008FF15u)]
    [InlineData("browserback", 0x1008FF26u)]
    [InlineData("browserforward", 0x1008FF27u)]
    [InlineData("browserrefresh", 0x1008FF73u)]
    [InlineData("browserhome", 0x1008FF18u)]
    [InlineData("-", 0x2du)]
    [InlineData("+", 0x2bu)]
    [InlineData("=", 0x3du)]
    [InlineData(",", 0x2cu)]
    [InlineData(".", 0x2eu)]
    [InlineData("/", 0x2fu)]
    [InlineData(";", 0x3bu)]
    [InlineData("'", 0x27u)]
    [InlineData("[", 0x5bu)]
    [InlineData("]", 0x5du)]
    [InlineData("\\", 0x5cu)]
    [InlineData("`", 0x60u)]
    [InlineData("A", 0x61u)] // key names are case-insensitive, like on Windows
    [InlineData("Return", 0xff0du)]
    [InlineData("Esc", 0xff1bu)]
    public void Key_names_map_to_keysyms(string name, uint expected)
    {
        Assert.True(X11Keysyms.TryGetKeysym(name, out var ks, out var error), error);
        Assert.Equal(expected, ks);
    }

    [Fact]
    public void Unknown_names_have_a_helpful_error()
    {
        Assert.False(X11Keysyms.TryGetKeysym("notakey", out _, out var error));
        Assert.Contains("Unknown key", error);
        Assert.Contains("f1-f24", error);
    }

    [Theory]
    [InlineData('a', 0x61u)]
    [InlineData('Z', 0x5au)]
    [InlineData(' ', 0x20u)]
    [InlineData('~', 0x7eu)]
    [InlineData('é', 0xe9u)]   // e acute, Latin-1 keysym = code point
    [InlineData('ö', 0xf6u)]   // o umlaut
    [InlineData('€', 0x10020acu)] // euro sign, Unicode keysym
    [InlineData('✓', 0x1002713u)] // check mark
    [InlineData('ф', 0x1000444u)] // Cyrillic ef
    [InlineData('\n', 0xff0du)]
    [InlineData('\t', 0xff09u)]
    public void Unicode_keysym_for_characters(char c, uint expected) =>
        Assert.Equal(expected, X11Keysyms.ForCodePoint(c));

    [Fact]
    public void Unicode_keysym_for_astral_characters_and_back()
    {
        Assert.Equal(0x101F600u, X11Keysyms.ForCodePoint(0x1F600));
        Assert.Equal(0x1F600, X11Keysyms.ToCodePoint(0x101F600));
        Assert.Equal(0x2713, X11Keysyms.ToCodePoint(0x1002713));
        Assert.Equal(0xf6, X11Keysyms.ToCodePoint(0xf6));
        Assert.Equal(0x20ac, X11Keysyms.ToCodePoint(X11Keysyms.EuroSign));
        Assert.Null(X11Keysyms.ToCodePoint(X11Keysyms.Return));
        Assert.Null(X11Keysyms.ToCodePoint(X11Keysyms.KP_0));
    }

    [Fact]
    public void Missing_library_messages_name_the_packages()
    {
        Assert.Equal("libXtst is missing: install libxtst6 (Debian/Ubuntu) or libXtst (Fedora)", X11Native.MissingMessage(X11Library.Xtst));
        Assert.Contains("libx11-6", X11Native.MissingMessage(X11Library.X11));
        Assert.Contains("libxrandr2", X11Native.MissingMessage(X11Library.Xrandr));
        Assert.Contains("libxfixes3", X11Native.MissingMessage(X11Library.Xfixes));
    }
}

public class X11KeymapTests
{
    private const uint NoSym = 0;

    /// <summary>A small US-like core mapping, 4 columns per keycode (group 1 level 1/2, group 2 level 1/2).</summary>
    private static X11Keymap UsKeymap(int group = 0, bool caps = false, Action<Dictionary<int, uint[]>>? edit = null)
    {
        var keys = new Dictionary<int, uint[]>
        {
            [10] = new uint[] { '1', '!', '1', '!' },
            [21] = new uint[] { '=', '+', '=', '+' },
            [36] = new uint[] { X11Keysyms.Return, NoSym, X11Keysyms.Return, NoSym },
            [38] = new uint[] { 'a', 'A', 0x1000444, 0x1000424 },
            [39] = new uint[] { 's', NoSym, NoSym, NoSym },
            [50] = new uint[] { X11Keysyms.ShiftL, NoSym, X11Keysyms.ShiftL, NoSym },
            [59] = new uint[] { ',', '<', ',', '<' },
            [65] = new uint[] { ' ', NoSym, ' ', NoSym },
            [94] = new uint[] { '<', '>', '<', '>' },
        };
        edit?.Invoke(keys);
        const int min = 8, max = 255, per = 4;
        var syms = new uint[(max - min + 1) * per];
        foreach (var (kc, list) in keys)
            for (int c = 0; c < per; c++) syms[(kc - min) * per + c] = list[c];
        return new X11Keymap(min, per, syms, group, caps);
    }

    [Fact]
    public void Shifted_level_needs_shift()
    {
        var km = UsKeymap();
        Assert.Equal(new KeyChoice(38, false), km.FindCodePoint('a'));
        Assert.Equal(new KeyChoice(38, true), km.FindCodePoint('A'));
        Assert.Equal(new KeyChoice(10, true), km.FindCodePoint('!'));
        Assert.Equal(new KeyChoice(21, true), km.FindCodePoint('+'));
        Assert.Equal(new KeyChoice(21, false), km.FindCodePoint('='));
        Assert.Equal(new KeyChoice(65, false), km.FindCodePoint(' '));
    }

    [Fact]
    public void Unshifted_level_on_any_key_wins()
    {
        // '<' is level 2 of the comma key but level 1 of the extra 102nd key: no Shift needed.
        Assert.Equal(new KeyChoice(94, false), UsKeymap().FindCodePoint('<'));
    }

    [Fact]
    public void Upper_case_of_a_one_level_letter_key_uses_shift()
    {
        Assert.Equal(new KeyChoice(39, true), UsKeymap().FindCodePoint('S'));
    }

    [Fact]
    public void Characters_not_on_the_layout_are_not_found()
    {
        var km = UsKeymap();
        Assert.Null(km.FindCodePoint('ö'));
        Assert.Null(km.FindCodePoint('✓'));
    }

    [Fact]
    public void Caps_lock_flips_shift_for_letters_only()
    {
        var km = UsKeymap(caps: true);
        Assert.Equal(new KeyChoice(38, true), km.FindCodePoint('a'));
        Assert.Equal(new KeyChoice(38, false), km.FindCodePoint('A'));
        Assert.Equal(new KeyChoice(10, true), km.FindCodePoint('!'));
        Assert.Equal(new KeyChoice(10, false), km.FindCodePoint('1'));
    }

    [Fact]
    public void Second_layout_group_types_its_own_characters_and_shortcuts_fall_back_to_the_first()
    {
        var km = UsKeymap(group: 1);
        Assert.Equal(new KeyChoice(38, false), km.FindCodePoint('ф'));
        Assert.Equal(new KeyChoice(38, true), km.FindCodePoint('Ф'));
        Assert.Null(km.FindCodePoint('a'));
        Assert.Equal(new KeyChoice(38, false), km.FindKeysym('a'));
        Assert.Equal(new KeyChoice(36, false), km.FindKeysym(X11Keysyms.Return));
    }

    [Fact]
    public void Spare_keycodes_are_empty_ones_from_the_top()
    {
        var km = UsKeymap(edit: k =>
        {
            for (int kc = 8; kc <= 255; kc++)
                if (!k.ContainsKey(kc) && kc is not (9 or 200 or 255)) k[kc] = new uint[] { 0x1000000u + (uint)kc, NoSym, NoSym, NoSym };
        });
        Assert.Equal(new[] { 255, 200, 9 }, km.SpareKeycodes(5));
        Assert.Equal(new[] { 255, 200 }, km.SpareKeycodes(2));
        Assert.Equal(new uint[] { 'a', 'A', 0x1000444, 0x1000424 }, km.GetAll(38));
    }

    [Fact]
    public void Text_plan_uses_existing_keys_and_remaps_the_rest()
    {
        var plan = X11InputSimulator.PlanText("hi A✓\r\n\t", UsKeymap(edit: k =>
        {
            k[43] = new uint[] { 'h', 'H', 'h', 'H' };
            k[31] = new uint[] { 'i', 'I', 'i', 'I' };
        }));
        Assert.Equal(7, plan.Count); // \r is dropped
        Assert.Equal(new KeyChoice(43, false), plan[0].Key);
        Assert.Equal(new KeyChoice(65, false), plan[2].Key);
        Assert.Equal(new KeyChoice(38, true), plan[3].Key);
        Assert.Null(plan[4].Key);
        Assert.Equal(0x1002713u, plan[4].RemapKeysym);
        Assert.Equal(new KeyChoice(36, false), plan[5].Key);
        // Tab is not on this small keymap, so it is typed through a remap like any other missing key.
        Assert.Equal(X11Keysyms.Tab, plan[6].RemapKeysym);
    }

    [Fact]
    public void Remap_chunks_fit_the_spare_keycodes()
    {
        var keys = new List<X11InputSimulator.TextKey>
        {
            new(null, 0x1002713), new(new KeyChoice(38, false), 0), new(null, 0x1002717), new(null, 0x1002713),
        };
        var one = X11InputSimulator.ChunkByRemaps(keys, 1);
        Assert.Equal(new[] { (0, 2), (2, 3), (3, 4) }, one.Select(c => (c.Start, c.End)));
        Assert.Equal(new[] { 0x1002713u }, one[0].Remaps);
        var two = X11InputSimulator.ChunkByRemaps(keys, 2);
        Assert.Single(two);
        Assert.Equal(new[] { 0x1002713u, 0x1002717u }, two[0].Remaps);
    }

    [Fact]
    public void Modifier_keycodes_come_from_the_keymap()
    {
        var km = UsKeymap();
        Assert.Equal(50, X11InputSimulator.ModifierKeycode(km, "shift"));
        Assert.Throws<InvalidOperationException>(() => X11InputSimulator.ModifierKeycode(km, "ctrl"));
    }

    [Fact]
    public void Pointer_mapping_inverse_finds_the_physical_button()
    {
        Assert.Equal(1u, X11InputSimulator.PhysicalButton(new byte[] { 1, 2, 3, 4, 5 }, 1));
        Assert.Equal(3u, X11InputSimulator.PhysicalButton(new byte[] { 3, 2, 1, 4, 5 }, 1));
        Assert.Equal(1u, X11InputSimulator.PhysicalButton(new byte[] { 3, 2, 1, 4, 5 }, 3));
        Assert.Equal(7u, X11InputSimulator.PhysicalButton(new byte[] { 1, 2, 3 }, 7));
    }

    [Fact]
    public void Smooth_path_ends_on_the_target()
    {
        var path = X11InputSimulator.SmoothPath(new ScreenPoint(0, 0), new ScreenPoint(100, 50), 10);
        Assert.Equal(10, path.Count);
        Assert.Equal(new ScreenPoint(100, 50), path[^1]);
        Assert.True(path[0].X < 10);
    }
}

public class X11PixelAndParsingTests
{
    [Fact]
    public void Converts_32_bit_rows_with_padding()
    {
        // 2x2, 12 bytes per line (4 bytes padding), B G R X order.
        var src = new byte[]
        {
            0x10, 0x20, 0x30, 0x00, 0x40, 0x50, 0x60, 0x00, 0xAA, 0xAA, 0xAA, 0xAA,
            0x01, 0x02, 0x03, 0x00, 0xff, 0xff, 0xff, 0x00, 0xAA, 0xAA, 0xAA, 0xAA,
        };
        var dst = new uint[4];
        X11Pixels.ToBgra(src, 2, 2, 12, 32, false, 0xff0000, 0xff00, 0xff, dst, 2);
        Assert.Equal(new uint[] { 0xff302010, 0xff605040, 0xff030201, 0xffffffff }, dst);
    }

    [Fact]
    public void Converts_24_bit_16_bit_and_msb_first()
    {
        var dst = new uint[1];
        X11Pixels.ToBgra(new byte[] { 0x10, 0x20, 0x30, 0 }, 1, 1, 4, 24, false, 0xff0000, 0xff00, 0xff, dst, 1);
        Assert.Equal(0xff302010u, dst[0]);

        // RGB565 pure red, little endian: 0xF800.
        X11Pixels.ToBgra(new byte[] { 0x00, 0xF8 }, 1, 1, 2, 16, false, 0xF800, 0x07E0, 0x001F, dst, 1);
        Assert.Equal(0xffff0000u, dst[0]);

        // 32-bit big-endian XRGB.
        X11Pixels.ToBgra(new byte[] { 0x00, 0x30, 0x20, 0x10 }, 1, 1, 4, 32, true, 0xff0000, 0xff00, 0xff, dst, 1);
        Assert.Equal(0xff302010u, dst[0]);

        // Depth 30 (10 bits per channel): full white.
        X11Pixels.ToBgra(BitConverter.GetBytes(0x3fffffffu), 1, 1, 4, 32, false, 0x3ff00000, 0xffc00, 0x3ff, dst, 1);
        Assert.Equal(0xffffffffu, dst[0]);
    }

    [Fact]
    public void Cursor_pixels_keep_alpha()
    {
        var dst = new uint[2];
        X11Pixels.CursorToBgra(new nuint[] { 0x80402010, 0 }, dst);
        Assert.Equal(new uint[] { 0x80402010, 0 }, dst);
    }

    [Fact]
    public void Parses_xft_dpi()
    {
        Assert.Equal(144.0, X11ScreenCapture.ParseXftDpi("Xft.antialias:\t1\nXft.dpi:\t144\nXft.hinting:\t1\n"));
        Assert.Equal(96.0, X11ScreenCapture.ParseXftDpi("Xft.dpi: 96"));
        Assert.Null(X11ScreenCapture.ParseXftDpi("Xft.dpiX: 3\n*.dpi: 120"));
        Assert.Null(X11ScreenCapture.ParseXftDpi(null));
    }

    [Fact]
    public void Work_area_is_clipped_to_the_monitor()
    {
        var mon = new ScreenRect(1920, 0, 1920, 1080);
        Assert.Equal(new ScreenRect(1920, 0, 1920, 1040), X11ScreenCapture.WorkAreaFor(mon, new ScreenRect(0, 0, 3840, 1040)));
        Assert.Equal(mon, X11ScreenCapture.WorkAreaFor(mon, null));
        Assert.Equal(mon, X11ScreenCapture.WorkAreaFor(mon, new ScreenRect(0, 0, 100, 100)));
    }

    [Fact]
    public void Frame_bounds_add_the_frame_and_drop_client_side_shadows()
    {
        var client = new ScreenRect(100, 100, 400, 300);
        Assert.Equal(new ScreenRect(99, 80, 402, 321), X11WindowManager.FrameBounds(client, new nuint[] { 1, 1, 20, 1 }, null));
        Assert.Equal(new ScreenRect(110, 110, 380, 280), X11WindowManager.FrameBounds(client, null, new nuint[] { 10, 10, 10, 10 }));
        Assert.Equal(client, X11WindowManager.FrameBounds(client, null, null));
    }

    [Fact]
    public void Parses_wm_class()
    {
        Assert.Equal(("xterm", "XTerm"), X11WindowManager.ParseWmClass(Encoding.Latin1.GetBytes("xterm\0XTerm\0")));
        Assert.Equal(((string?)null, (string?)null), X11WindowManager.ParseWmClass(Array.Empty<byte>()));
    }

    [Fact]
    public void Window_list_filter()
    {
        static X11WindowManager.WindowFacts F(string title, bool viewable = true, bool minimized = false, nuint? desktop = null, string? type = null) =>
            new(new WindowInfo(1, title, "C", "p", 1, new ScreenRect(0, 0, 10, 10), true, minimized, false, false, false), viewable, desktop, type);

        Assert.True(X11WindowManager.ShouldList(F("Editor"), 0));
        Assert.False(X11WindowManager.ShouldList(F(""), 0));
        Assert.False(X11WindowManager.ShouldList(F("Panel", type: "_NET_WM_WINDOW_TYPE_DOCK"), 0));
        Assert.False(X11WindowManager.ShouldList(F("Desktop", type: "_NET_WM_WINDOW_TYPE_DESKTOP"), 0));
        Assert.True(X11WindowManager.ShouldList(F("Minimized", viewable: false, minimized: true), 0));
        Assert.False(X11WindowManager.ShouldList(F("Withdrawn", viewable: false), 0));
        Assert.False(X11WindowManager.ShouldList(F("Elsewhere", desktop: 2), 0));
        Assert.True(X11WindowManager.ShouldList(F("Sticky", desktop: 0xFFFFFFFF), 0));
        Assert.True(X11WindowManager.ShouldList(F("No desktops", desktop: 3), null));
    }

    [Fact]
    public void Hotkey_names_and_lock_variants()
    {
        Assert.Equal("Ctrl+Alt+X", X11GlobalHotkey.DisplayName(KeyCombo.Parse("ctrl+alt+x")));
        Assert.Equal("Ctrl+Shift+F12", X11GlobalHotkey.DisplayName(KeyCombo.Parse("ctrl+shift+f12")));
        Assert.Equal("Super+Esc", X11GlobalHotkey.DisplayName(KeyCombo.Parse("win+esc")));
        Assert.Equal("Pause", X11GlobalHotkey.DisplayName(KeyCombo.Parse("pause")));

        var v = X11GlobalHotkey.LockVariants(X.ControlMask | X.Mod1Mask, X.LockMask, X.Mod2Mask, 0);
        Assert.Equal(new uint[] { 0, X.LockMask, X.Mod2Mask, X.LockMask | X.Mod2Mask }, v);
        var withScroll = X11GlobalHotkey.LockVariants(X.ControlMask, X.LockMask, X.Mod2Mask, X.Mod5Mask);
        Assert.Equal(8, withScroll.Length);
        // A lock bit that is part of the hotkey itself is not varied.
        Assert.Equal(new uint[] { 0, X.LockMask }, X11GlobalHotkey.LockVariants(X.Mod2Mask, X.LockMask, X.Mod2Mask));
    }

    [Fact]
    public void Server_action_keys_are_recognized()
    {
        // The F12 row of the default XKB keymap as read in CI.
        Assert.Equal("switches to virtual terminal 12",
            X11GlobalHotkey.ServerAction(new uint[] { 0xffc9, 0xffc9, 0xffc9, 0xffc9, 0xffc9, 0xffc9, 0x1008fe0c }));
        Assert.Equal("can stop the X server", X11GlobalHotkey.ServerAction(new uint[] { 0xff08, 0xff08, 0xff08, 0xff08, 0xfed5 }));
        Assert.Null(X11GlobalHotkey.ServerAction(new uint[] { 'x', 'X', 'x', 'X' }));
    }

    [Fact]
    public void Modifier_map_lookup()
    {
        // 2 keys per modifier; rows Shift, Lock, Control, Mod1..Mod5.
        var map = new byte[] { 50, 62, 66, 0, 37, 105, 64, 108, 77, 0, 0, 0, 133, 134, 92, 0 };
        Assert.Equal(X.Mod1Mask, X11GlobalHotkey.MaskFor(map, 2, 64));
        Assert.Equal(X.Mod2Mask, X11GlobalHotkey.MaskFor(map, 2, 77));
        Assert.Equal(X.Mod4Mask, X11GlobalHotkey.MaskFor(map, 2, 133));
        Assert.Equal(0u, X11GlobalHotkey.MaskFor(map, 2, 50)); // Shift row is not a ModN
        Assert.Equal(0u, X11GlobalHotkey.MaskFor(map, 2, 200));
    }
}

// ------------------------------------------------------------------------------------------------ real X11 session

/// <summary>The desktop tests move the pointer, type and change focus: they run alone, after the parallel tests.</summary>
[CollectionDefinition("X11 desktop session", DisableParallelization = true)]
public sealed class X11DesktopCollection { }

[Collection("X11 desktop session")]
public sealed class X11DesktopTests : IDisposable
{
    private readonly ITestOutputHelper _out;
    private readonly X11ScreenCapture _capture = new();
    private readonly X11InputSimulator _input = new();
    private readonly X11WindowManager _windows = new();
    private readonly List<Process> _started = new();

    public X11DesktopTests(ITestOutputHelper output) => _out = output;

    public void Dispose()
    {
        foreach (var p in _started)
        {
            try
            {
                if (!p.HasExited) p.Kill(true);
                p.WaitForExit(3000);
            }
            catch (Exception) { }
            p.Dispose();
        }
        try { _input.ReleaseAll(); } catch (Exception) { }
        _input.Dispose();
        _capture.Dispose();
        _windows.Dispose();
    }

    private Process Start(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // GTK 4 (zenity 4) has no GL under Xvfb; the cairo renderer avoids a slow or failing GL probe.
        psi.Environment["GSK_RENDERER"] = "cairo";
        psi.Environment["GDK_BACKEND"] = "x11";
        psi.Environment["NO_AT_BRIDGE"] = "1";
        var p = Process.Start(psi)!;
        _started.Add(p);
        return p;
    }

    private static string UniqueTitle() => "DeskPilotTest-" + Guid.NewGuid().ToString("N")[..12];

    private WindowInfo WaitForWindow(string title, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var w = _windows.ListWindows().FirstOrDefault(w => w.Title.Contains(title, StringComparison.Ordinal));
            if (w != null && !w.Bounds.IsEmpty) return w;
            Thread.Sleep(100);
        }
        var all = string.Join("; ", _windows.ListWindows().Select(w => $"'{w.Title}' {w.ProcessName} {w.Bounds}"));
        throw new TimeoutException($"No window titled '{title}' after {timeoutMs} ms. Windows: {all}");
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (condition()) return true;
            Thread.Sleep(50);
        }
        return condition();
    }

    private static string ReadOutput(Process p, int timeoutMs = 15000)
    {
        var stdout = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(true); } catch (Exception) { }
            throw new TimeoutException("The dialog did not close; stderr: " + p.StandardError.ReadToEnd());
        }
        return stdout.Result.TrimEnd('\n');
    }

    [X11Fact]
    public void Monitors_and_virtual_screen_are_1920x1080()
    {
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), _capture.GetVirtualScreen());
        var monitors = _capture.GetMonitors();
        foreach (var m in monitors) _out.WriteLine($"monitor {m.Index} {m.DeviceName} {m.Bounds} work {m.WorkArea} primary={m.IsPrimary} scale={m.Scale}");
        Assert.NotEmpty(monitors);
        Assert.Single(monitors, m => m.IsPrimary);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), monitors[0].Bounds);
        Assert.Equal(1.0, monitors[0].Scale);
        Assert.False(monitors[0].WorkArea.IsEmpty);
    }

    [X11Fact]
    public void Xft_dpi_sets_the_monitor_scale()
    {
        var (_, original) = CiEnvironmentTests.Run("xrdb", "-query");
        var saved = Path.Combine(Path.GetTempPath(), "deskpilot-xrdb-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(saved, original);
        try
        {
            var (exit, output) = CiEnvironmentTests.Run("sh", "-c", "echo 'Xft.dpi: 144' | xrdb -nocpp -merge");
            Assert.True(exit == 0, output);
            Assert.All(_capture.GetMonitors(), m => Assert.Equal(1.5, m.Scale));
        }
        finally
        {
            if (string.IsNullOrWhiteSpace(original)) CiEnvironmentTests.Run("xrdb", "-remove");
            else CiEnvironmentTests.Run("xrdb", "-nocpp", "-load", saved);
            File.Delete(saved);
        }
        if (string.IsNullOrWhiteSpace(original)) Assert.All(_capture.GetMonitors(), m => Assert.Equal(1.0, m.Scale));
    }

    [X11Fact]
    public void Randr_monitors_are_listed()
    {
        try
        {
            var (e1, o1) = CiEnvironmentTests.Run("xrandr", "--setmonitor", "DeskPilotLeft", "960/254x1080/286+0+0", "none");
            Assert.True(e1 == 0, o1);
            var (e2, o2) = CiEnvironmentTests.Run("xrandr", "--setmonitor", "DeskPilotRight", "960/254x1080/286+960+0", "none");
            Assert.True(e2 == 0, o2);

            var monitors = _capture.GetMonitors();
            foreach (var m in monitors) _out.WriteLine($"monitor {m.Index} {m.DeviceName} {m.Bounds} primary={m.IsPrimary}");
            Assert.Contains(monitors, m => m.DeviceName == "DeskPilotLeft" && m.Bounds == new ScreenRect(0, 0, 960, 1080));
            Assert.Contains(monitors, m => m.DeviceName == "DeskPilotRight" && m.Bounds == new ScreenRect(960, 0, 960, 1080));
            Assert.Single(monitors, m => m.IsPrimary);
            Assert.Equal(Enumerable.Range(0, monitors.Count), monitors.Select(m => m.Index));
            Assert.Equal(new ScreenRect(0, 0, 1920, 1080), _capture.GetVirtualScreen());
        }
        finally
        {
            CiEnvironmentTests.Run("xrandr", "--delmonitor", "DeskPilotLeft");
            CiEnvironmentTests.Run("xrandr", "--delmonitor", "DeskPilotRight");
        }
    }

    [X11Fact]
    public void A_polkit_agent_window_counts_as_a_privilege_prompt()
    {
        var title = UniqueTitle();
        // The instance name (WM_CLASS) is what identifies the agent here; the process itself is an ordinary xterm.
        Start("xterm", "-name", "polkit-gnome-authentication-agent-1", "-T", title, "-geometry", "40x5+600+100", "-e", "sleep", "60");
        var w = WaitForWindow(title);
        Assert.True(w.IsUacPrompt);
        Assert.True(_windows.IsUacPromptActive());
    }

    [X11Fact]
    public void A_window_of_a_root_process_is_elevated()
    {
        var (canSudo, _) = CiEnvironmentTests.Run("sudo", "-n", "true");
        if (canSudo != 0) Assert.Skip("needs passwordless sudo (CI runners have it)");
        var title = UniqueTitle();
        try
        {
            Start("sudo", "-n", "--preserve-env=DISPLAY,XAUTHORITY", "timeout", "40", "xterm", "-T", title, "-geometry", "40x5+600+300", "-e", "sleep", "35");
            var w = WaitForWindow(title);
            _out.WriteLine($"{w.Title} pid={w.ProcessId} name={w.ProcessName} elevated={w.IsElevated}");
            Assert.True(w.IsElevated);
            Assert.False(_windows.IsCurrentProcessElevated);
        }
        finally
        {
            CiEnvironmentTests.Run("sudo", "-n", "pkill", "-f", title);
        }
    }

    [X11Fact]
    public void Capture_of_the_root_background_has_its_color()
    {
        var (exit, output) = CiEnvironmentTests.Run("xsetroot", "-solid", "#3366cc");
        Assert.True(exit == 0, output);
        var region = new ScreenRect(1800, 960, 100, 100);
        var frame = _capture.Capture(new CaptureRequest(region, 100, 100, ImageFormatKind.Png, 90, false, 0));
        Assert.Equal("image/png", frame.MediaType);
        Assert.Equal(region, frame.Source);
        using var bmp = SKBitmap.Decode(frame.Data);
        var c = bmp.GetPixel(50, 50);
        Assert.Equal((0x33, 0x66, 0xcc), (c.Red, c.Green, c.Blue));
        Assert.Equal(new SKColor(0x33, 0x66, 0xcc), bmp.GetPixel(2, 97));
    }

    [X11Fact]
    public void Capture_sizes_formats_and_timing()
    {
        var full = new ScreenRect(0, 0, 1920, 1080);
        _capture.Capture(new CaptureRequest(full, 1280, 720, ImageFormatKind.Jpeg, 80, true, 0)); // warm up

        var times = new List<long>();
        CapturedFrame? jpeg = null;
        for (int i = 0; i < 5; i++)
        {
            var sw = Stopwatch.StartNew();
            jpeg = _capture.Capture(new CaptureRequest(full, 1280, 720, ImageFormatKind.Jpeg, 80, true, 0));
            times.Add(sw.ElapsedMilliseconds);
        }
        _out.WriteLine("1920x1080 -> 1280x720 JPEG capture ms: " + string.Join(", ", times));
        Assert.Equal("image/jpeg", jpeg!.MediaType);
        using (var decoded = SKBitmap.Decode(jpeg.Data))
            Assert.Equal((1280, 720), (decoded.Width, decoded.Height));
        Assert.True(times.Min() < 400, "capture took " + string.Join(", ", times) + " ms");

        var png = _capture.Capture(new CaptureRequest(full, 640, 360, ImageFormatKind.Png, 80, false, 100));
        using (var decoded = SKBitmap.Decode(png.Data))
            Assert.Equal((640, 360), (decoded.Width, decoded.Height));

        // Full size when no target size is given, and a source hanging off the screen is padded, not an error.
        var native = _capture.Capture(new CaptureRequest(new ScreenRect(10, 20, 300, 200), 0, 0, ImageFormatKind.Png, 80, false, 0));
        Assert.Equal((300, 200), (native.Width, native.Height));
        var offscreen = _capture.Capture(new CaptureRequest(new ScreenRect(1900, 1060, 100, 100), 100, 100, ImageFormatKind.Png, 80, false, 0));
        using (var decoded = SKBitmap.Decode(offscreen.Data))
            Assert.Equal(SKColors.Black, decoded.GetPixel(90, 90));
    }

    [X11Fact]
    public void Cursor_overlay_can_be_read()
    {
        _input.MoveMouse(700, 500);
        var cursor = _capture.GetCursorForTests();
        Assert.NotNull(cursor);
        _out.WriteLine($"cursor at {cursor!.X},{cursor.Y} image {cursor.Image?.Width}x{cursor.Image?.Height} hotspot {cursor.HotspotX},{cursor.HotspotY}");
        Assert.Equal((700, 500), (cursor.X, cursor.Y));
        cursor.Image?.Dispose();
        var frame = _capture.Capture(new CaptureRequest(new ScreenRect(600, 400, 200, 200), 200, 200, ImageFormatKind.Png, 80, true, 0));
        Assert.NotEmpty(frame.Data);
    }

    [X11Fact]
    public void Lists_an_xterm_with_its_process_and_bounds()
    {
        var title = UniqueTitle();
        var p = Start("xterm", "-T", title, "-geometry", "60x15+120+140", "-e", "sleep", "60");
        var w = WaitForWindow(title);
        _out.WriteLine($"{w.Title} pid={w.ProcessId} name={w.ProcessName} class={w.ClassName} bounds={w.Bounds}");
        Assert.Equal(p.Id, w.ProcessId);
        Assert.Equal("xterm", w.ProcessName);
        Assert.Equal("XTerm", w.ClassName);
        Assert.False(w.Bounds.IsEmpty);
        Assert.True(w.IsVisible);
        Assert.False(w.IsMinimized);
        Assert.False(w.IsElevated);
        Assert.False(w.IsUacPrompt);
        Assert.False(_windows.IsUacPromptActive());
        Assert.False(_windows.IsCurrentProcessElevated);
        Assert.InRange(w.Bounds.X, 100, 140);
        Assert.InRange(w.Bounds.Y, 100, 160);
    }

    [X11Fact]
    public void Focus_and_hit_testing()
    {
        var t1 = UniqueTitle();
        var t2 = UniqueTitle();
        Start("xterm", "-T", t1, "-geometry", "50x12+100+300", "-e", "sleep", "60");
        Start("xterm", "-T", t2, "-geometry", "50x12+900+300", "-e", "sleep", "60");
        var a = WaitForWindow(t1);
        var b = WaitForWindow(t2);

        Assert.True(_windows.FocusWindow(a.Handle));
        Assert.Equal(a.Handle, _windows.GetForegroundWindow()?.Handle);
        Assert.True(_windows.ListWindows().Single(w => w.Handle == a.Handle).IsForeground);
        Assert.True(_windows.FocusWindow(b.Handle));
        Assert.Equal(b.Handle, _windows.GetForegroundWindow()?.Handle);

        // The most recently focused window is first in the front-to-back list.
        var order = _windows.ListWindows().Select(w => w.Handle).ToList();
        Assert.True(order.IndexOf(b.Handle) < order.IndexOf(a.Handle));

        Assert.Equal(a.Handle, _windows.GetWindowAt(a.Bounds.Center.X, a.Bounds.Center.Y)?.Handle);
        Assert.Equal(b.Handle, _windows.GetWindowAt(b.Bounds.Center.X, b.Bounds.Center.Y)?.Handle);
        Assert.Null(_windows.GetWindowAt(1910, 1070));
        Assert.False(_windows.FocusWindow(0));
    }

    [X11Fact]
    public void Clicking_an_unfocused_window_focuses_it()
    {
        var t1 = UniqueTitle();
        var t2 = UniqueTitle();
        Start("xterm", "-T", t1, "-geometry", "50x12+100+600", "-e", "sleep", "60");
        Start("xterm", "-T", t2, "-geometry", "50x12+900+600", "-e", "sleep", "60");
        var a = WaitForWindow(t1);
        var b = WaitForWindow(t2);
        Assert.True(_windows.FocusWindow(a.Handle));

        var target = new ScreenPoint(b.Bounds.X + b.Bounds.Width / 2, b.Bounds.Y + b.Bounds.Height * 2 / 3);
        _input.MoveMouse(target.X, target.Y);
        Assert.Equal(target, _input.GetCursorPosition());
        _input.Click(MouseButton.Left, 1);
        Assert.True(WaitUntil(() => _windows.GetForegroundWindow()?.Handle == b.Handle, 3000),
            "foreground is " + _windows.GetForegroundWindow()?.Title);
    }

    [X11Fact]
    public void Types_unicode_text_through_the_keysym_remap()
    {
        var title = UniqueTitle();
        var p = Start("zenity", "--entry", "--title=" + title, "--text=Type here");
        var w = WaitForWindow(title);
        Assert.True(_windows.FocusWindow(w.Handle));
        Thread.Sleep(500);

        const string text = "hello wörld 123 ✓";
        _input.TypeText(text, 0);
        _input.PressCombo(KeyCombo.Parse("enter"));
        Assert.Equal(text, ReadOutput(p));
    }

    [X11Fact]
    public void Key_combos_and_shifted_characters()
    {
        var title = UniqueTitle();
        var p = Start("zenity", "--entry", "--title=" + title, "--text=Type here");
        var w = WaitForWindow(title);
        Assert.True(_windows.FocusWindow(w.Handle));
        Thread.Sleep(500);

        _input.TypeText("this gets replaced", 0);
        _input.PressCombo(KeyCombo.Parse("ctrl+a"));
        const string text = "Shifted: A+B=C! (x_y) <ok>?";
        _input.TypeText(text, 0);
        _input.PressCombo(KeyCombo.Parse("end"));
        _input.PressCombo(KeyCombo.Parse("backspace"));
        _input.PressCombo(KeyCombo.Parse("shift+1"));
        // A key that is not on the layout, as a combo and held down (both bind a spare keycode for the press).
        _input.PressCombo(KeyCombo.Parse("€"));
        _input.KeyDown("ö");
        _input.KeyUp("ö");
        // Shift held through KeyDown applies to the next key.
        _input.KeyDown("shift");
        _input.PressCombo(KeyCombo.Parse("a"));
        _input.KeyUp("shift");
        _input.PressCombo(KeyCombo.Parse("enter"));
        Assert.Equal(text[..^1] + "!€öA", ReadOutput(p));
        Assert.Empty(_input.PressedKeys);
    }

    [X11Fact]
    public void Minimized_windows_are_reported_and_focus_restores_them()
    {
        var title = UniqueTitle();
        Start("xterm", "-T", title, "-geometry", "50x10+400+400", "-e", "sleep", "60");
        var w = WaitForWindow(title);
        var (exit, output) = CiEnvironmentTests.Run("xdotool", "windowminimize", ((ulong)w.Handle).ToString());
        Assert.True(exit == 0, output);
        Assert.True(WaitUntil(() => _windows.ListWindows().Any(x => x.Handle == w.Handle && x.IsMinimized), 3000), "not reported as minimized");
        Assert.NotEqual(w.Handle, _windows.GetWindowAt(w.Bounds.Center.X, w.Bounds.Center.Y)?.Handle);

        Assert.True(_windows.FocusWindow(w.Handle));
        var restored = _windows.ListWindows().Single(x => x.Handle == w.Handle);
        Assert.False(restored.IsMinimized);
        Assert.True(restored.IsForeground);
    }

    [X11Fact]
    public void Dragging_a_title_bar_moves_the_window()
    {
        var title = UniqueTitle();
        Start("xterm", "-T", title, "-geometry", "50x10+300+300", "-e", "sleep", "60");
        var w = WaitForWindow(title);
        var client = _windows.GetClientRect(w.Handle)!.Value;
        Assert.True(client.Y > w.Bounds.Y, $"no title bar above the client area: frame {w.Bounds}, client {client}");

        var from = new ScreenPoint(client.Center.X, (w.Bounds.Y + client.Y) / 2);
        _input.MoveMouse(from.X, from.Y);
        _input.MouseDown(MouseButton.Left);
        _input.MoveMouseSmooth(from.X + 200, from.Y + 150, 300);
        _input.MouseUp(MouseButton.Left);

        Assert.True(WaitUntil(() =>
        {
            var now = _windows.ListWindows().Single(x => x.Handle == w.Handle).Bounds;
            return Math.Abs(now.X - (w.Bounds.X + 200)) <= 3 && Math.Abs(now.Y - (w.Bounds.Y + 150)) <= 3;
        }, 3000), "window is at " + _windows.ListWindows().Single(x => x.Handle == w.Handle).Bounds + ", was " + w.Bounds);
    }

    [X11Fact]
    public void Double_and_triple_clicks_select_a_word_and_a_line()
    {
        var title = UniqueTitle();
        Start("xterm", "-T", title, "-geometry", "50x6+300+700", "-e", "sh", "-c", "echo DeskPilotWord second; sleep 60");
        var w = WaitForWindow(title);
        var client = _windows.GetClientRect(w.Handle)!.Value;
        Thread.Sleep(500);

        // First text row of xterm: a few pixels in from the client area's top-left corner.
        _input.MoveMouse(client.X + 20, client.Y + 8);
        _input.Click(MouseButton.Left, 2);
        Assert.True(WaitUntil(() => PrimarySelection() == "DeskPilotWord", 3000), "selection: " + PrimarySelection());
        _input.Click(MouseButton.Left, 3);
        Assert.True(WaitUntil(() => PrimarySelection() == "DeskPilotWord second", 3000), "selection: " + PrimarySelection());
    }

    private static string PrimarySelection()
    {
        var (_, output) = CiEnvironmentTests.Run("xclip", "-o", "-selection", "primary");
        return output.Trim();
    }

    [X11Fact]
    public void Wheel_scrolls_an_xterm_back_and_forth()
    {
        var title = UniqueTitle();
        Start("xterm", "-T", title, "-sl", "500", "-geometry", "50x10+1100+200", "-e", "sh", "-c", "seq 1 300; sleep 60");
        var w = WaitForWindow(title);
        var client = _windows.GetClientRect(w.Handle)!.Value;
        Thread.Sleep(500);
        _input.MoveMouse(client.Center.X, client.Center.Y);
        Thread.Sleep(200);

        byte[] Shot() => _capture.Capture(new CaptureRequest(client, client.Width, client.Height, ImageFormatKind.Png, 90, false, 0)).Data;
        var before = Shot();
        _input.Scroll(0, -3);
        Thread.Sleep(400);
        var up = Shot();
        _input.Scroll(0, 3);
        Thread.Sleep(400);
        var back = Shot();

        Assert.True(DifferentPixels(before, up) > 100, "scrolling up changed nothing");
        Assert.True(DifferentPixels(before, back) < 50, "scrolling down did not return to the end");
    }

    private static int DifferentPixels(byte[] a, byte[] b)
    {
        using var x = SKBitmap.Decode(a);
        using var y = SKBitmap.Decode(b);
        int n = 0;
        for (int i = 0; i < x.Width; i++)
            for (int j = 0; j < x.Height; j++)
                if (x.GetPixel(i, j) != y.GetPixel(i, j)) n++;
        return n;
    }

    [X11Fact]
    public void Moves_the_mouse_exactly()
    {
        _input.MoveMouse(123, 456);
        Assert.Equal(new ScreenPoint(123, 456), _input.GetCursorPosition());
        _input.MoveMouseSmooth(800, 600, 200);
        Assert.Equal(new ScreenPoint(800, 600), _input.GetCursorPosition());
        _input.MoveMouse(0, 0);
        Assert.Equal(new ScreenPoint(0, 0), _input.GetCursorPosition());
        _input.MoveMouse(1919, 1079);
        Assert.Equal(new ScreenPoint(1919, 1079), _input.GetCursorPosition());
    }

    [X11Fact]
    public void Global_hotkey_fires_and_reports_conflicts() => HotkeyRoundTrip("ctrl+alt+x", "Ctrl+Alt+X");

    [X11Fact]
    public void Global_hotkey_with_shift_and_a_function_key() => HotkeyRoundTrip("ctrl+shift+f12", "Ctrl+Shift+F12");

    [X11Fact]
    public void Global_hotkey_refuses_ctrl_alt_function_keys_that_switch_terminals()
    {
        // XKB binds Ctrl+Alt+F1..F12 to a VT switch; the server consumes the press before any grab (verified in CI:
        // a grab of Ctrl+Alt+F12 never fired), so registering it must fail with a reason instead of never firing.
        var keymap = _input.KeymapForTests();
        X11Keysyms.TryGetKeysym("f12", out var f12, out _);
        var key = keymap.FindKeysym(f12);
        _out.WriteLine($"F12 keycode {key?.Keycode}: " + string.Join(" ", keymap.GetAll(key?.Keycode ?? 0).Select(s => "0x" + s.ToString("x"))));

        var hotkey = X11GlobalHotkey.TryRegister(KeyCombo.Parse("ctrl+alt+f12"), () => { }, out var error);
        Assert.Null(hotkey);
        Assert.Equal("Ctrl+Alt+F12 switches to virtual terminal 12 on X11, so DeskPilot would never see it; choose another combination such as Ctrl+Alt+X", error);
    }

    private void HotkeyRoundTrip(string text, string display)
    {
        using var pressed = new ManualResetEventSlim();
        var combo = KeyCombo.Parse(text);
        using (var hotkey = X11GlobalHotkey.TryRegister(combo, () => pressed.Set(), out var error))
        {
            Assert.True(hotkey != null, error);
            Assert.Equal(display, hotkey!.Display);

            _input.PressCombo(combo);
            Assert.True(pressed.Wait(3000), $"the hotkey {display} did not fire");

            var second = X11GlobalHotkey.TryRegister(combo, () => { }, out var conflict);
            Assert.Null(second);
            Assert.Equal($"{display} is already used by another program", conflict);
        }

        // Released on dispose: it can be registered again.
        using var again = X11GlobalHotkey.TryRegister(combo, () => { }, out var againError);
        Assert.True(again != null, againError);
    }

    [X11Fact]
    public void Release_all_leaves_nothing_pressed()
    {
        _input.MoveMouse(1910, 1070);
        _input.KeyDown("shift");
        _input.KeyDown("ctrl");
        _input.MouseDown(MouseButton.Left);
        var (heldMask, heldKeys) = _input.QueryInputState();
        Assert.NotEqual(0u, heldMask & X.ShiftMask);
        Assert.NotEqual(0u, heldMask & X.ControlMask);
        Assert.NotEqual(0u, heldMask & X.Button1Mask);
        Assert.Equal(2, heldKeys);

        _input.ReleaseAll();
        var (mask, keys) = _input.QueryInputState();
        Assert.Equal(0u, mask & (X.ShiftMask | X.ControlMask | X.Mod1Mask | X.Mod4Mask | X.Button1Mask | X.Button2Mask | X.Button3Mask));
        Assert.Equal(0, keys);
        Assert.Empty(_input.PressedKeys);
        Assert.Empty(_input.PressedButtons);

        _input.Scroll(0, 2);
        _input.Scroll(-1, -1);
        _input.Click(MouseButton.Left, 2);
        _input.PressCombo(KeyCombo.Parse("escape"));
        Assert.Equal(0u, _input.QueryInputState().Mask & 0x1fffu);
    }
}

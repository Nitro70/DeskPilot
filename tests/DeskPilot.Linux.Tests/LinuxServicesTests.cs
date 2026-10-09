using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.Services;

namespace DeskPilot.Linux.Tests;

// ------------------------------------------------------------------ launcher: pure logic

public class LinuxLauncherClassificationTests
{
    private const string Home = "/home/tester";

    private static LinuxLaunchTarget Classify(string target, string[]? files = null, string[]? dirs = null, Dictionary<string, string>? commands = null, Dictionary<string, string>? env = null)
    {
        var f = new HashSet<string>(files ?? Array.Empty<string>());
        var d = new HashSet<string>(dirs ?? Array.Empty<string>());
        return LinuxAppLauncher.Classify(target, f.Contains, d.Contains,
            n => commands != null && commands.TryGetValue(n, out var p) ? p : null,
            n => env != null && env.TryGetValue(n, out var v) ? v : null,
            Home);
    }

    [Theory]
    [InlineData("https://example.com/a?b=c")]
    [InlineData("http://localhost:8080/")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("file:///tmp/report.pdf")]
    [InlineData("steam://rungameid/570")]
    [InlineData("vscode://file/tmp/x")]
    public void Urls_and_schemes_are_uris(string target)
    {
        var t = Classify(target);
        Assert.Equal(LinuxLaunchKind.Uri, t.Kind);
        Assert.Equal(target, t.Value);
    }

    [Fact]
    public void Www_and_bare_host_paths_become_https()
    {
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Uri, "https://www.example.com"), Classify("www.example.com"));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Uri, "https://example.com/page"), Classify("example.com/page"));
    }

    [Fact]
    public void Home_and_variables_expand_to_existing_paths()
    {
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/home/tester/Documents"), Classify("~/Documents", dirs: new[] { "/home/tester/Documents" }));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/home/tester"), Classify("~", dirs: new[] { "/home/tester" }));
        var env = new Dictionary<string, string> { ["MYDIR"] = "/data/x" };
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/data/x/notes.txt"), Classify("$MYDIR/notes.txt", files: new[] { "/data/x/notes.txt" }, env: env));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/data/x/notes.txt"), Classify("${MYDIR}/notes.txt", files: new[] { "/data/x/notes.txt" }, env: env));
        // Relative paths are taken from the home folder; quotes around the target are dropped.
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/home/tester/Documents/a b.pdf"), Classify("\"Documents/a b.pdf\"", files: new[] { "/home/tester/Documents/a b.pdf" }));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Path, "/opt/app/run"), Classify("/opt/app/../app/./run", files: new[] { "/opt/app/run" }));
    }

    [Fact]
    public void Missing_paths_are_reported()
    {
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.MissingPath, "/nope/missing.txt"), Classify("/nope/missing.txt"));
        Assert.Equal(LinuxLaunchKind.MissingPath, Classify("Documents/missing.pdf").Kind);
        Assert.Equal(LinuxLaunchKind.MissingPath, Classify("$UNSET_VAR/x").Kind);
    }

    [Fact]
    public void Commands_on_path_and_app_names()
    {
        var commands = new Dictionary<string, string> { ["firefox"] = "/usr/bin/firefox" };
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.Command, "/usr/bin/firefox"), Classify("firefox", commands: commands));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.AppName, "Text Editor"), Classify("Text Editor", commands: commands));
        Assert.Equal(new LinuxLaunchTarget(LinuxLaunchKind.AppName, "gnome-calculator"), Classify("gnome-calculator"));
        Assert.Throws<ArgumentException>(() => Classify("   "));
    }

    [Fact]
    public void Path_helpers()
    {
        Assert.Equal("/a/c/d", LinuxAppLauncher.NormalizePath("/a/./b/../c//d"));
        Assert.Equal("/", LinuxAppLauncher.NormalizePath("/.."));
        Assert.Equal("/home/u/x", LinuxAppLauncher.ExpandPath("~/x", _ => null, "/home/u/"));
        Assert.Equal("$NOPE/x", LinuxAppLauncher.ExpandPath("$NOPE/x", _ => null, "/home/u"));
        Assert.True(LinuxAppLauncher.LooksLikePath("./run.sh"));
        Assert.False(LinuxAppLauncher.LooksLikePath("gedit"));
    }

    [Fact]
    public void Arguments_split_like_a_shell()
    {
        Assert.Equal(new[] { "a", "b c", "d \"e\"", "f g", "", "h$i" }, LinuxTools.SplitArguments("a 'b c' \"d \\\"e\\\"\" f\\ g '' \"h\\$i\""));
        Assert.Empty(LinuxTools.SplitArguments("   "));
        Assert.Equal("'it'\\''s'", LinuxTools.ShellQuote("it's"));
        Assert.Equal("/usr/bin/x", LinuxTools.ShellQuote("/usr/bin/x"));
        Assert.Equal("'a b'", LinuxTools.ShellQuote("a b"));
        Assert.Equal("''", LinuxTools.ShellQuote(""));
    }

    [Fact]
    public void Xdg_open_errors_are_readable()
    {
        Assert.Contains("no app is set up", LinuxAppLauncher.XdgOpenError(3, "x.weird"));
        Assert.Contains("does not exist", LinuxAppLauncher.XdgOpenError(2, "x"));
    }
}

public class DesktopEntryTests
{
    private static readonly string[] German = { "de_DE", "de" };

    private const string Sample = """
        # comment
        [Desktop Entry]
        Type=Application
        Name=Text Editor
        Name[de]=Texteditor
        Name[fr]=Éditeur de texte
        GenericName=Plain Text Editor
        GenericName[de]=Einfacher Texteditor
        Comment=Edit text files
        Keywords=Text;Plaintext;Write\;Notes;
        Keywords[de]=Schreiben;
        Exec=env GTK_DEBUG=0 gnome-text-editor --new-window %U
        TryExec=gnome-text-editor
        Icon=org.gnome.TextEditor
        Terminal=false
        OnlyShowIn=GNOME;Unity;
        X-Ignored=yes

        [Desktop Action new-window]
        Name=New Window
        Exec=should-not-win
        """;

    [Fact]
    public void Parses_the_desktop_entry_group_only()
    {
        var e = DesktopEntries.Parse(Sample, "org.gnome.TextEditor.desktop", "/x/org.gnome.TextEditor.desktop", 0, true, German)!;
        Assert.Equal("Application", e.Type);
        Assert.Equal("Text Editor", e.Name);
        Assert.Equal("Texteditor", e.LocalizedName);
        Assert.Equal("Texteditor", e.DisplayName);
        Assert.Contains("Éditeur de texte", e.AllLocalizedNames);
        Assert.Equal("Plain Text Editor", e.GenericName);
        Assert.Equal("Einfacher Texteditor", e.LocalizedGenericName);
        Assert.Equal(new[] { "Text", "Plaintext", "Write;Notes", "Schreiben" }, e.Keywords);
        Assert.Equal("env GTK_DEBUG=0 gnome-text-editor --new-window %U", e.Exec);
        Assert.Equal("gnome-text-editor", e.ExecProgram);
        Assert.Equal("gnome-text-editor", e.TryExec);
        Assert.Equal("org.gnome.TextEditor", e.IdStem);
        Assert.Equal(new[] { "GNOME", "Unity" }, e.OnlyShowIn);
        Assert.False(e.Terminal);
        Assert.False(e.NoDisplay);
        Assert.Null(DesktopEntries.Parse("[Other]\nName=x", "x.desktop", "/x", 0, true, German));
    }

    [Fact]
    public void Unescapes_values_and_lists()
    {
        Assert.Equal("a b\nc\\d\\x", DesktopEntries.Unescape("a\\sb\\nc\\\\d\\x"));
        Assert.Equal(new[] { "a;b", "c" }, DesktopEntries.SplitList("a\\;b;;c;"));
    }

    [Fact]
    public void Locale_variants_follow_the_spec_order()
    {
        Assert.Equal(new[] { "de_DE@euro", "de_DE", "de@euro", "de" }, DesktopEntries.LocaleVariants("de_DE.UTF-8@euro").ToArray());
        Assert.Equal(new[] { "pt_BR", "pt" }, DesktopEntries.LocaleVariants("pt_BR.UTF-8").ToArray());
        Assert.Empty(DesktopEntries.LocaleVariants("C.UTF-8"));
        var env = new Dictionary<string, string> { ["LANGUAGE"] = "fr:de", ["LANG"] = "en_US.UTF-8" };
        Assert.Equal(new[] { "fr", "de", "en_US", "en" }, DesktopEntries.CurrentLocales(n => env.GetValueOrDefault(n)));
    }

    [Fact]
    public void Application_directories_follow_xdg_then_flatpak_and_snap()
    {
        var env = new Dictionary<string, string> { ["XDG_DATA_DIRS"] = "/usr/share/:/opt/share:relative/ignored:/usr/share" };
        var (dirs, xdgCount) = DesktopEntries.ApplicationDirectories(n => env.GetValueOrDefault(n), "/home/u");
        Assert.Equal(new[]
        {
            "/home/u/.local/share/applications",
            "/usr/share/applications",
            "/opt/share/applications",
            "/var/lib/flatpak/exports/share/applications",
            "/home/u/.local/share/flatpak/exports/share/applications",
            "/var/lib/snapd/desktop/applications",
        }, dirs);
        Assert.Equal(3, xdgCount);

        var (defaults, _) = DesktopEntries.ApplicationDirectories(n => n == "XDG_DATA_HOME" ? "/data/home" : null, "/home/u");
        Assert.Equal("/data/home/applications", defaults[0]);
        Assert.Equal("/usr/local/share/applications", defaults[1]);
        Assert.Equal("/usr/share/applications", defaults[2]);
    }

    private static DesktopEntry Entry(string id, string name, string? generic = null, string? exec = null, string[]? keywords = null,
        bool noDisplay = false, string[]? onlyShowIn = null, int dir = 0)
    {
        var e = new DesktopEntry { Id = id, FilePath = "/apps/" + id, DirectoryIndex = dir, InXdgDirectory = true };
        e.Type = "Application";
        e.Name = name;
        e.GenericName = generic;
        e.Exec = exec ?? id.Replace(".desktop", "");
        e.NoDisplay = noDisplay;
        if (keywords != null) e.Keywords.AddRange(keywords);
        if (onlyShowIn != null) e.OnlyShowIn.AddRange(onlyShowIn);
        return e;
    }

    private static readonly List<DesktopEntry> Apps = new()
    {
        Entry("org.gnome.gedit.desktop", "gedit", "Text Editor", "gedit %U"),
        Entry("org.gnome.TextEditor.desktop", "Text Editor", exec: "gnome-text-editor %U", keywords: new[] { "Notepad" }),
        Entry("firefox.desktop", "Firefox Web Browser", "Web Browser", "firefox %u", new[] { "Internet", "WWW" }),
        Entry("org.kde.kate.desktop", "Kate", "Advanced Text Editor", "kate -b %U", onlyShowIn: new[] { "KDE" }),
        Entry("text-helper.desktop", "Text Editor Helper", noDisplay: true),
        Entry("org.gnome.Calculator.desktop", "Calculator", "Calculator", "gnome-calculator", new[] { "calculation", "arithmetic" }),
    };

    private static readonly string[] Gnome = { "GNOME" };

    [Theory]
    [InlineData("Text Editor", "org.gnome.TextEditor.desktop")]
    [InlineData("text editor", "org.gnome.TextEditor.desktop")]
    [InlineData("gedit", "org.gnome.gedit.desktop")]
    [InlineData("firefox", "firefox.desktop")]
    [InlineData("Firefox", "firefox.desktop")]
    [InlineData("web browser", "firefox.desktop")]
    [InlineData("internet", "firefox.desktop")]
    [InlineData("calc", "org.gnome.Calculator.desktop")]
    [InlineData("gnome-calculator", "org.gnome.Calculator.desktop")]
    [InlineData("TextEditor", "org.gnome.TextEditor.desktop")]
    [InlineData("org.gnome.TextEditor.desktop", "org.gnome.TextEditor.desktop")]
    [InlineData("notepad", "org.gnome.TextEditor.desktop")]
    [InlineData("kate", "org.kde.kate.desktop")]
    [InlineData("Browser Web", "firefox.desktop")]
    public void Finds_the_best_entry(string query, string expectedId)
    {
        var best = DesktopEntries.FindBest(Apps, query, Gnome);
        Assert.NotNull(best);
        Assert.Equal(expectedId, best!.Entry.Id);
    }

    [Fact]
    public void Ranks_exact_over_prefix_over_contains_over_all_words()
    {
        Assert.Equal(DesktopEntries.MatchTier.Exact, DesktopEntries.Match("Text Editor", "text  editor"));
        Assert.Equal(DesktopEntries.MatchTier.StartsWith, DesktopEntries.Match("Text Editor", "text"));
        Assert.Equal(DesktopEntries.MatchTier.Contains, DesktopEntries.Match("Text Editor", "editor"));
        Assert.Equal(DesktopEntries.MatchTier.AllWords, DesktopEntries.Match("Text Editor", "editor text"));
        Assert.Equal(DesktopEntries.MatchTier.None, DesktopEntries.Match("Text Editor", "spreadsheet"));

        var ranked = DesktopEntries.RankAll(Apps, "text editor", Gnome);
        Assert.Equal("org.gnome.TextEditor.desktop", ranked[0].Entry.Id);
        // gedit's GenericName is an exact match too, but a Name match ranks first.
        Assert.Equal("org.gnome.gedit.desktop", ranked[1].Entry.Id);
        // The NoDisplay helper matches by prefix only and after the shown entries of its tier.
        Assert.Contains(ranked, r => r.Entry.Id == "text-helper.desktop" && r.Penalized);
        Assert.Null(DesktopEntries.FindBest(Apps, "helper", Gnome));
        Assert.Null(DesktopEntries.FindBest(Apps, "nothing like this", Gnome));
    }

    [Fact]
    public void Only_show_in_penalizes_other_desktops()
    {
        var kate = Apps.Single(a => a.Id == "org.kde.kate.desktop");
        Assert.False(DesktopEntries.ShownIn(kate, Gnome));
        Assert.True(DesktopEntries.ShownIn(kate, new[] { "KDE" }));
        Assert.False(DesktopEntries.ShownIn(kate, Array.Empty<string>()));
        // "advanced text editor" is a GenericName exact match, still found though penalized.
        Assert.Equal("org.kde.kate.desktop", DesktopEntries.FindBest(Apps, "advanced text editor", Gnome)!.Entry.Id);
    }

    [Fact]
    public void Exec_field_codes_are_removed_or_filled()
    {
        Assert.Equal("gedit", DesktopEntries.BuildExecCommand("gedit %U", Array.Empty<string>()));
        Assert.Equal("gedit '/tmp/a b.txt' /tmp/c", DesktopEntries.BuildExecCommand("gedit %U", new[] { "/tmp/a b.txt", "/tmp/c" }));
        Assert.Equal("viewer /tmp/a", DesktopEntries.BuildExecCommand("viewer %f", new[] { "/tmp/a", "/tmp/b" }));
        Assert.Equal("app --x    100%", DesktopEntries.BuildExecCommand("app --x %i %c %k 100%%", Array.Empty<string>()));
        Assert.Equal("app --flag https://x.org", DesktopEntries.BuildExecCommand("app --flag", new[] { "https://x.org" }));
        Assert.Equal("sh -c \"echo  two  spaces\"", DesktopEntries.BuildExecCommand("sh -c \"echo  two  spaces\" %F", Array.Empty<string>()));
        Assert.Equal("app %z", DesktopEntries.BuildExecCommand("app %z", Array.Empty<string>()));
    }

    [Fact]
    public void Load_honors_precedence_subfolders_and_hidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "dp-apps-" + Guid.NewGuid().ToString("N"));
        var a = Path.Combine(root, "a");
        var b = Path.Combine(root, "b");
        Directory.CreateDirectory(Path.Combine(a, "kde4"));
        Directory.CreateDirectory(b);
        try
        {
            File.WriteAllText(Path.Combine(a, "dup.desktop"), "[Desktop Entry]\nType=Application\nName=From A\nExec=a\n");
            File.WriteAllText(Path.Combine(b, "dup.desktop"), "[Desktop Entry]\nType=Application\nName=From B\nExec=b\n");
            File.WriteAllText(Path.Combine(a, "kde4", "tool.desktop"), "[Desktop Entry]\nType=Application\nName=Tool\nExec=tool\n");
            File.WriteAllText(Path.Combine(a, "gone.desktop"), "[Desktop Entry]\nType=Application\nName=Gone\nHidden=true\n");
            File.WriteAllText(Path.Combine(b, "gone.desktop"), "[Desktop Entry]\nType=Application\nName=Gone B\nExec=gone\n");
            File.WriteAllText(Path.Combine(b, "link.desktop"), "[Desktop Entry]\nType=Link\nName=A link\nURL=https://x\n");

            var entries = DesktopEntries.Load(new[] { a, b }, 1, Array.Empty<string>());
            Assert.Equal("From A", entries.Single(e => e.Id == "dup.desktop").Name);
            var tool = entries.Single(e => e.Id == "kde4-tool.desktop");
            Assert.True(tool.InXdgDirectory);
            Assert.DoesNotContain(entries, e => e.Id == "gone.desktop");
            Assert.DoesNotContain(entries, e => e.Id == "link.desktop");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void Elevation_is_detected_in_desktop_entries()
    {
        var targets = new DeskPilot.Core.Settings.SafetySettings().ElevatedLaunchTargets;
        DesktopEntry Make(string exec, bool substitute = false, string id = "x.desktop")
        {
            var e = Entry(id, "X", exec: exec);
            e.SubstituteUid = substitute;
            return e;
        }

        Assert.Contains("pkexec", LinuxAppLauncher.ElevationReason(Make("pkexec /usr/bin/thing"), targets));
        Assert.Contains("sudo", LinuxAppLauncher.ElevationReason(Make("/usr/bin/sudo -E bar %f"), targets));
        Assert.Contains("su", LinuxAppLauncher.ElevationReason(Make("xterm -e su -"), targets));
        Assert.Contains("kdesu", LinuxAppLauncher.ElevationReason(Make("sh -c 'kdesu dolphin'"), targets));
        Assert.Contains("X-KDE-SubstituteUID", LinuxAppLauncher.ElevationReason(Make("kate", substitute: true), targets));
        Assert.Contains("gparted", LinuxAppLauncher.ElevationReason(Make("env FOO=1 /usr/sbin/gparted %f"), targets));
        Assert.Contains("synaptic", LinuxAppLauncher.ElevationReason(Make("synaptic-wrapper", id: "synaptic.desktop"), targets));
        Assert.Null(LinuxAppLauncher.ElevationReason(Make("sudoku %f"), targets));
        Assert.Null(LinuxAppLauncher.ElevationReason(Make("pseudo-app --issue"), targets));
        Assert.Null(LinuxAppLauncher.ElevationReason(Make("gedit %U"), targets));

        Assert.NotNull(LinuxAppLauncher.ProgramElevationReason("/usr/bin/pkexec", targets));
        Assert.NotNull(LinuxAppLauncher.ProgramElevationReason("/usr/bin/doas", targets));
        Assert.NotNull(LinuxAppLauncher.ProgramElevationReason("/usr/sbin/gparted", targets));
        Assert.Null(LinuxAppLauncher.ProgramElevationReason("/usr/bin/gedit", targets));
        Assert.NotNull(LinuxAppLauncher.ProgramElevationReason("/usr/bin/gedit", new[] { "gedit" }));
    }
}

// ------------------------------------------------------------------ clipboard and shell: pure logic

public class LinuxClipboardPlanTests
{
    private static LinuxSessionInfo Session(LinuxSessionKind kind, string? display = ":0") =>
        new(kind, display, kind == LinuxSessionKind.Wayland ? "wayland-0" : null, null, null);

    private static Func<string, string?> Tools(params string[] present) => n => present.Contains(n) ? "/usr/bin/" + n : null;

    [Fact]
    public void Wayland_uses_wl_clipboard()
    {
        var read = LinuxClipboard.Plan(Session(LinuxSessionKind.Wayland), Tools("wl-paste", "wl-copy", "xclip"), LinuxClipboard.ClipboardOp.Read);
        Assert.Equal("/usr/bin/wl-paste", read.FileName);
        Assert.Equal(new[] { "--no-newline", "--type", "text" }, read.Arguments);
        var write = LinuxClipboard.Plan(Session(LinuxSessionKind.Wayland), Tools("wl-paste", "wl-copy"), LinuxClipboard.ClipboardOp.Write);
        Assert.Equal("wl-copy", write.Tool);
        var clear = LinuxClipboard.Plan(Session(LinuxSessionKind.Wayland), Tools("wl-copy"), LinuxClipboard.ClipboardOp.Clear);
        Assert.Equal(new[] { "--clear" }, clear.Arguments);
    }

    [Fact]
    public void Wayland_falls_back_to_x11_tools_through_xwayland()
    {
        var read = LinuxClipboard.Plan(Session(LinuxSessionKind.Wayland), Tools("xclip"), LinuxClipboard.ClipboardOp.Read);
        Assert.Equal("xclip", read.Tool);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            LinuxClipboard.Plan(Session(LinuxSessionKind.Wayland, display: null), Tools("xclip"), LinuxClipboard.ClipboardOp.Read));
        Assert.Contains("wl-clipboard", ex.Message);
    }

    [Fact]
    public void X11_uses_xclip_then_xsel()
    {
        var read = LinuxClipboard.Plan(Session(LinuxSessionKind.X11), Tools("xclip", "xsel"), LinuxClipboard.ClipboardOp.Read);
        Assert.Equal(new[] { "-selection", "clipboard", "-o" }, read.Arguments);
        var write = LinuxClipboard.Plan(Session(LinuxSessionKind.X11), Tools("xsel"), LinuxClipboard.ClipboardOp.Write);
        Assert.Equal(new[] { "--clipboard", "--input" }, write.Arguments);
        var clear = LinuxClipboard.Plan(Session(LinuxSessionKind.X11), Tools("xsel"), LinuxClipboard.ClipboardOp.Clear);
        Assert.Equal(new[] { "--clipboard", "--clear" }, clear.Arguments);
        var ex = Assert.Throws<InvalidOperationException>(() => LinuxClipboard.Plan(Session(LinuxSessionKind.X11), Tools(), LinuxClipboard.ClipboardOp.Write));
        Assert.Contains("xclip", ex.Message);
        Assert.Throws<InvalidOperationException>(() => LinuxClipboard.Plan(Session(LinuxSessionKind.None, null), Tools("xclip"), LinuxClipboard.ClipboardOp.Read));
    }

    [Fact]
    public void Empty_clipboard_messages_are_not_errors()
    {
        Assert.True(LinuxClipboard.IsNoTextMessage("Nothing is copied"));
        Assert.True(LinuxClipboard.IsNoTextMessage("No suitable type of content copied"));
        Assert.True(LinuxClipboard.IsNoTextMessage("Error: target UTF8_STRING not available"));
        Assert.False(LinuxClipboard.IsNoTextMessage("Error: Can't open display: :9"));
        Assert.False(LinuxClipboard.IsNoTextMessage(""));
    }
}

public class LinuxShellStartInfoTests
{
    [Fact]
    public void Bash_runs_without_profile_and_sh_is_the_fallback()
    {
        string? Find(string n) => n == "bash" ? "/usr/bin/bash" : null;
        var (psi, note, ownSession) = LinuxShellRunner.BuildStartInfo("echo hi", "bash", "/tmp", Find, _ => "en_US.UTF-8");
        Assert.EndsWith("bash", psi.FileName);
        Assert.Equal(new[] { "--noprofile", "--norc", "-c", "echo hi" }, psi.ArgumentList);
        Assert.Null(note);
        Assert.False(ownSession);
        Assert.True(psi.RedirectStandardInput);
        Assert.False(psi.Environment.ContainsKey("SUDO_ASKPASS"));

        var (sh, _, _) = LinuxShellRunner.BuildStartInfo("echo hi", "sh", "/tmp", _ => null, _ => null);
        Assert.Equal(new[] { "-c", "echo hi" }, sh.ArgumentList);
        // No locale at all: the child gets a UTF-8 one so its output decodes.
        Assert.Equal("C.UTF-8", sh.Environment["LANG"]);

        var (withSetsid, _, own) = LinuxShellRunner.BuildStartInfo("true", "sh", "/tmp", n => n == "setsid" ? "/usr/bin/setsid" : null, _ => "C.UTF-8");
        Assert.Equal("/usr/bin/setsid", withSetsid.FileName);
        Assert.EndsWith("sh", withSetsid.ArgumentList[0]);
        Assert.True(own);

        Assert.Throws<ArgumentException>(() => LinuxShellRunner.BuildStartInfo("x", "powershell", "/tmp", Find, _ => null));
    }

    [Fact]
    public void Missing_bash_runs_sh_with_a_note()
    {
        if (File.Exists("/bin/bash") || File.Exists("/usr/bin/bash")) return; // only meaningful where bash is absent
        var (psi, note, _) = LinuxShellRunner.BuildStartInfo("echo hi", "bash", "/tmp", _ => null, _ => null);
        Assert.Equal(new[] { "-c", "echo hi" }, psi.ArgumentList);
        Assert.Contains("bash was not found", note);
    }

    [Fact]
    public void Capped_text_notes_truncation()
    {
        var t = new CappedText(5);
        t.Append("hello world");
        Assert.Equal("hello\n[output truncated: 6 more characters not shown]", t.ToString());
    }
}

// ------------------------------------------------------------------ AT-SPI: pure logic

public class AtSpiLogicTests
{
    private const ulong Showing = 1UL << AtSpiInspector.State.Showing;
    private const ulong Active = 1UL << AtSpiInspector.State.Active;
    private const ulong Focusable = 1UL << AtSpiInspector.State.Focusable;

    [Fact]
    public void Roles_map_to_ui_automation_types()
    {
        Assert.Equal("Button", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.PushButton, 0));
        Assert.Equal("Button", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.ToggleButton, 0));
        Assert.Equal("Edit", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Text, 0));
        Assert.Equal("Edit", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Entry, 0));
        Assert.Equal("Edit", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.PasswordText, 0));
        Assert.Equal("MenuItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.MenuItem, 0));
        Assert.Equal("CheckBox", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.CheckBox, 0));
        Assert.Equal("RadioButton", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.RadioButton, 0));
        Assert.Equal("ComboBox", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.ComboBox, 0));
        Assert.Equal("ListItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.ListItem, 0));
        Assert.Equal("TabItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.PageTab, 0));
        Assert.Equal("Hyperlink", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Link, 0));
        Assert.Equal("TreeItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.TreeItem, 0));
        Assert.Equal("DataItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.TableCell, 0));
        Assert.Equal("DataItem", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.TableRow, 0));
        Assert.Equal("Document", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.DocumentWeb, 0));
        Assert.Equal("Slider", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Slider, 0));
        Assert.Null(AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Label, 0));
        Assert.Null(AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Panel, Focusable));
        Assert.Null(AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Image, 0));
        Assert.Equal("Image", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Image, Focusable));
        Assert.Equal("Pane", AtSpiInspector.ControlTypeFor(AtSpiInspector.Role.Section, Focusable));
        Assert.Equal("Window", AtSpiInspector.GenericControlType(AtSpiInspector.Role.Frame));
        Assert.Equal("Text", AtSpiInspector.GenericControlType(AtSpiInspector.Role.Label));
        Assert.Equal("Button", AtSpiInspector.GenericControlType(AtSpiInspector.Role.PushButton));
    }

    [Fact]
    public void Password_values_are_never_read()
    {
        var pw = new AtSpiInspector.Node(default, AtSpiInspector.Role.PasswordText, Showing, "Secret");
        var entry = new AtSpiInspector.Node(default, AtSpiInspector.Role.Entry, Showing, "User name");
        var named = new AtSpiInspector.Node(default, AtSpiInspector.Role.Entry, Showing, "Enter Password");
        var button = new AtSpiInspector.Node(default, AtSpiInspector.Role.PushButton, Showing, "OK");
        Assert.False(AtSpiInspector.MayReadValue(pw, insidePassword: false));
        Assert.True(AtSpiInspector.MayReadValue(entry, insidePassword: false));
        Assert.False(AtSpiInspector.MayReadValue(entry, insidePassword: true));
        Assert.False(AtSpiInspector.MayReadValue(named, insidePassword: false));
        Assert.False(AtSpiInspector.MayReadValue(button, insidePassword: false));
        Assert.True(AtSpiInspector.IsMasked("•••"));
        Assert.False(AtSpiInspector.IsMasked("abc"));
    }

    [Fact]
    public void Text_is_collapsed_and_capped()
    {
        Assert.Equal("a b c", AtSpiInspector.Trim("  a \n b\t c "));
        Assert.Equal("ab", AtSpiInspector.Trim("a￼b"));
        Assert.Equal(AtSpiInspector.MaxTextLength, AtSpiInspector.Trim(new string('x', 500)).Length);
    }

    [Fact]
    public void Offset_applies_only_to_window_relative_toolkits()
    {
        var bounds = new ScreenRect(300, 200, 400, 300);
        var frame = new ScreenRect(0, 0, 400, 300);
        // GTK 4: every screen position is (0, 0) and only window coordinates are real.
        var gtk4Probe = (new ScreenRect(0, 0, 200, 30), new ScreenRect(10, 40, 200, 30));
        Assert.Equal(new AtSpiInspector.Offset(300, 200, true), AtSpiInspector.ComputeOffset(frame, frame, bounds, gtk4Probe));
        // GTK 3 / Qt under Wayland: screen and window coordinates are the same window-relative ones.
        var samePlace = (new ScreenRect(10, 40, 200, 30), new ScreenRect(10, 40, 200, 30));
        Assert.Equal(new AtSpiInspector.Offset(300, 200, true), AtSpiInspector.ComputeOffset(frame, frame, bounds, samePlace));
        Assert.Equal(new AtSpiInspector.Offset(300, 200, true), AtSpiInspector.ComputeOffset(frame, default, bounds, null));
        // Server-side decorations in the bounds: 2 px borders, 24 px title bar.
        var framed = new ScreenRect(300, 200, 404, 326);
        Assert.Equal(new AtSpiInspector.Offset(302, 224, true), AtSpiInspector.ComputeOffset(frame, frame, framed, gtk4Probe));
        // Client-side shadows: the frame is 20 px larger on every side than the visible window.
        var shadowed = new ScreenRect(0, 0, 440, 340);
        Assert.Equal(new AtSpiInspector.Offset(280, 180, true), AtSpiInspector.ComputeOffset(shadowed, shadowed, bounds, gtk4Probe));
        // Real screen coordinates (GTK 3 / Qt on X11): nothing to do, whether or not the window position is known.
        Assert.Equal(new AtSpiInspector.Offset(0, 0, false), AtSpiInspector.ComputeOffset(new ScreenRect(300, 200, 400, 300), frame, bounds, null));
        Assert.Equal(new AtSpiInspector.Offset(0, 0, false), AtSpiInspector.ComputeOffset(new ScreenRect(300, 200, 400, 300), frame, default, null));
        // A window really in the top-left corner: a child's screen and window positions differ, so they are real.
        var corner = (new ScreenRect(2, 24, 400, 300), new ScreenRect(0, 0, 400, 300));
        Assert.Equal(new AtSpiInspector.Offset(0, 0, false),
            AtSpiInspector.ComputeOffset(new ScreenRect(0, 0, 404, 326), new ScreenRect(0, 0, 404, 326), new ScreenRect(2, 24, 400, 300), corner));
        // Window-relative positions and an unknown window position: nothing can be placed on the screen.
        Assert.Equal(new AtSpiInspector.Offset(0, 0, true, false), AtSpiInspector.ComputeOffset(frame, frame, default, gtk4Probe));
        // A window-relative toolkit with its window at the origin: nothing to add.
        Assert.Equal(new AtSpiInspector.Offset(0, 0, true), AtSpiInspector.ComputeOffset(frame, frame, new ScreenRect(0, 0, 400, 300), gtk4Probe));
        Assert.Equal(1u, new AtSpiInspector.Offset(1, 1, true).CoordType);
        Assert.Equal(0u, new AtSpiInspector.Offset(0, 0, false).CoordType);
    }

    [Fact]
    public void Elements_are_cut_to_their_frame()
    {
        var frame = new ScreenRect(100, 100, 300, 200);
        var placed = new AtSpiInspector.Offset(0, 0, false);
        static UiElementInfo E(ScreenRect r) => new("x", "Button", r, null, true, true, null);
        Assert.Equal(new ScreenRect(350, 250, 50, 50), AtSpiInspector.ClipToFrame(E(new ScreenRect(350, 250, 80, 80)), placed, frame)!.Bounds);
        Assert.Null(AtSpiInspector.ClipToFrame(E(new ScreenRect(500, 500, 10, 10)), placed, frame));
        Assert.Null(AtSpiInspector.ClipToFrame(E(default), placed, frame));
        var inside = E(new ScreenRect(110, 110, 10, 10));
        Assert.Same(inside, AtSpiInspector.ClipToFrame(inside, placed, frame));
        // Window position unknown: kept, with empty bounds, so the name still shows.
        var unplaced = E(default);
        Assert.Same(unplaced, AtSpiInspector.ClipToFrame(unplaced, new AtSpiInspector.Offset(0, 0, true, false), frame));
    }

    [Fact]
    public void Frames_are_chosen_by_title_then_state()
    {
        AtSpiInspector.Toplevel T(string name, ulong states, string path) => new(new AtspiRef(":1.5", path), AtSpiInspector.Role.Frame, states, name, 42);
        var main = T("Document 1 - Writer", Showing, "/a");
        var prefs = T("Preferences", Showing | Active, "/b");
        var hidden = T("Hidden", 0, "/c");
        var list = new[] { main, prefs, hidden };

        Assert.Same(main, AtSpiInspector.ChooseFrame(list, "Document 1 - Writer", pidMatched: true));
        Assert.Same(prefs, AtSpiInspector.ChooseFrame(list, "  preferences ", pidMatched: true));
        Assert.Same(main, AtSpiInspector.ChooseFrame(list, "Document 1 - Writer (modified)", pidMatched: true));
        // Another process's frame must carry the title itself.
        Assert.Null(AtSpiInspector.ChooseFrame(list, "Document 1 - Writer (modified)", pidMatched: false));
        Assert.Same(main, AtSpiInspector.ChooseFrame(list, "document 1 - writer", pidMatched: false));
        // No title match within the window's own process: the active frame.
        Assert.Same(prefs, AtSpiInspector.ChooseFrame(list, "Something else", pidMatched: true));
        Assert.Null(AtSpiInspector.ChooseFrame(list, "Something else", pidMatched: false));
        Assert.Same(main, AtSpiInspector.ChooseFrame(new[] { main, hidden }, null, pidMatched: true));
        Assert.Null(AtSpiInspector.ChooseFrame(Array.Empty<AtSpiInspector.Toplevel>(), "x", pidMatched: true));
    }

    [Fact]
    public async Task Timeouts_return_partial_results_and_cancellation_cancels()
    {
        var partial = await AtSpiInspector.RunWithTimeout<int>(async t => { await Task.Delay(10_000, t); return 1; }, () => 7, () => { },
            TimeSpan.FromMilliseconds(100), CancellationToken.None);
        Assert.Equal(7, partial);

        var done = await AtSpiInspector.RunWithTimeout<int>(t => Task.FromResult(3), () => 7, () => { }, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(3, done);

        using var cts = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtSpiInspector.RunWithTimeout<int>(async t => { await Task.Delay(10_000, t); return 1; }, () => 7, () => { }, TimeSpan.FromSeconds(5), cts.Token));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AtSpiInspector.RunWithTimeout<int>(_ => throw new InvalidOperationException("no bus"), () => 7, () => { }, TimeSpan.FromSeconds(5), CancellationToken.None));
    }

    [Fact]
    public async Task Unknown_window_handle_gives_no_elements()
    {
        var inspector = new AtSpiInspector(new FakeWindows());
        var elements = await inspector.GetElementsAsync(12345, 10, TestContext.Current.CancellationToken);
        Assert.Empty(elements);
    }
}

/// <summary>A window manager that knows exactly one window (the test app), so the AT-SPI tests do not depend on the X11 module.</summary>
internal sealed class FakeWindows : IWindowManager
{
    public WindowInfo? Window { get; set; }
    public IReadOnlyList<WindowInfo> ListWindows() => Window == null ? Array.Empty<WindowInfo>() : new[] { Window };
    public WindowInfo? GetForegroundWindow() => Window;
    public WindowInfo? GetWindowAt(int x, int y) => Window != null && Window.Bounds.Contains(x, y) ? Window : null;
    public bool FocusWindow(nint handle) => false;
    public bool IsCurrentProcessElevated => false;
    public bool IsUacPromptActive() => false;
}

// ------------------------------------------------------------------ real session tests

public class LinuxShellRunnerTests
{
    private static readonly LinuxShellRunner Runner = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [LinuxFact]
    public async Task Echo_exit_codes_and_stderr()
    {
        var r = await Runner.RunAsync("echo hello; echo oops >&2; exit 3", "bash", "/tmp", 10_000, Ct);
        Assert.Equal(3, r.ExitCode);
        Assert.Equal("hello\n", r.StdOut);
        Assert.Contains("oops", r.StdErr);
        Assert.False(r.TimedOut);

        var sh = await Runner.RunAsync("printf '%s' \"$0\"; exit 0", "sh", "/tmp", 10_000, Ct);
        Assert.Equal(0, sh.ExitCode);
        Assert.Contains("sh", sh.StdOut);

        var ok = await Runner.RunAsync("true", "bash", "/tmp", 10_000, Ct);
        Assert.Equal(0, ok.ExitCode);
        var fail = await Runner.RunAsync("false", "", "/tmp", 10_000, Ct);
        Assert.Equal(1, fail.ExitCode);
    }

    [LinuxFact]
    public async Task Unicode_round_trips_and_stdin_is_closed()
    {
        const string text = "héllo 世界 ✓ \U0001F600";
        var r = await Runner.RunAsync($"printf '%s' '{text}'; cat; echo done", "bash", "/tmp", 10_000, Ct);
        Assert.Equal(text + "done\n", r.StdOut);
    }

    [LinuxFact]
    public async Task Working_directory_falls_back_to_home()
    {
        var tmp = await Runner.RunAsync("pwd", "bash", "/tmp", 10_000, Ct);
        Assert.Equal("/tmp", tmp.StdOut.Trim());
        var home = await Runner.RunAsync("pwd", "bash", "/definitely/not/here", 10_000, Ct);
        Assert.Equal(Environment.GetEnvironmentVariable("HOME")?.TrimEnd('/'), home.StdOut.Trim().TrimEnd('/'));
    }

    [LinuxFact]
    public async Task Has_no_terminal_and_its_own_session()
    {
        var r = await Runner.RunAsync("tty; ps -o sid= -p $$; echo $$", "bash", "/tmp", 10_000, Ct);
        var lines = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("not a tty", lines[0]);
        if (LinuxTools.FindTool("setsid") != null) Assert.Equal(lines[2], lines[1]);
    }

    [LinuxFact]
    public async Task Output_is_capped_at_64_kb()
    {
        var r = await Runner.RunAsync("head -c 100000 /dev/zero | tr '\\0' a", "bash", "/tmp", 20_000, Ct);
        Assert.StartsWith(new string('a', 1000), r.StdOut);
        Assert.Contains("[output truncated: 34464 more characters not shown]", r.StdOut);
    }

    [LinuxFact]
    public async Task Timeout_kills_the_whole_tree()
    {
        var sw = Stopwatch.StartNew();
        var r = await Runner.RunAsync("sleep 30 & echo $!; sleep 30", "bash", "/tmp", 1500, Ct);
        sw.Stop();
        Assert.True(r.TimedOut);
        Assert.Equal(-1, r.ExitCode);
        Assert.Contains("timed out", r.StdErr);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        var childPid = int.Parse(r.StdOut.Trim().Split('\n')[0]);
        Assert.True(await GoneAsync(childPid), $"background sleep {childPid} survived the timeout");
    }

    [LinuxFact]
    public async Task A_background_program_does_not_hold_the_command()
    {
        var sw = Stopwatch.StartNew();
        var r = await Runner.RunAsync("(sleep 4; echo late) & echo $!; echo started", "bash", "/tmp", 20_000, Ct);
        sw.Stop();
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("started", r.StdOut);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3.8), $"took {sw.Elapsed}");
        // Not killed: it was started on purpose.
        var pid = int.Parse(r.StdOut.Split('\n')[0].Trim());
        Assert.True(Directory.Exists($"/proc/{pid}"));
        try { Process.GetProcessById(pid).Kill(true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
    }

    [LinuxFact]
    public async Task Cancellation_stops_the_command()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cts.CancelAfter(500);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Runner.RunAsync("sleep 20", "bash", "/tmp", 30_000, cts.Token));
    }

    internal static async Task<bool> GoneAsync(int pid)
    {
        for (int i = 0; i < 40; i++)
        {
            if (!Directory.Exists($"/proc/{pid}")) return true;
            try
            {
                // A zombie waiting for its (new) parent to reap it is gone as far as we are concerned.
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                if (stat[(stat.LastIndexOf(')') + 2)..].StartsWith('Z')) return true;
            }
            catch (IOException) { return true; }
            await Task.Delay(100);
        }
        return false;
    }
}

public class LinuxClipboardTests
{
    private static async Task RoundTripAsync()
    {
        var clipboard = new LinuxClipboard(LinuxSession.Detect());
        var text = "DeskPilot clip ✓ é " + Guid.NewGuid().ToString("N") + "\nsecond line";
        clipboard.SetText(text);
        // The tool hands the selection to a forked server; give it a moment to own it.
        string? got = null;
        for (int i = 0; i < 30 && got != text; i++)
        {
            got = clipboard.GetText();
            if (got != text) await Task.Delay(100);
        }
        Assert.Equal(text, got);

        clipboard.SetText("");
        string? cleared = "x";
        for (int i = 0; i < 30 && !string.IsNullOrEmpty(cleared); i++)
        {
            cleared = clipboard.GetText();
            if (!string.IsNullOrEmpty(cleared)) await Task.Delay(100);
        }
        Assert.True(string.IsNullOrEmpty(cleared), $"clipboard still holds '{cleared}'");

        clipboard.SetText("plain");
        for (int i = 0; i < 30 && got != "plain"; i++)
        {
            got = clipboard.GetText();
            if (got != "plain") await Task.Delay(100);
        }
        Assert.Equal("plain", got);
    }

    [X11Fact]
    public Task Round_trip_on_x11() => RoundTripAsync();

    [WaylandFact]
    public Task Round_trip_on_wayland() => RoundTripAsync();
}

public class LinuxLauncherTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dp-launch-" + Guid.NewGuid().ToString("N"));
    private readonly string _dataHome;
    private readonly string _dataDirs;
    private readonly List<int> _started = new();

    public LinuxLauncherTests()
    {
        _dataHome = Path.Combine(_root, "home-share");
        _dataDirs = Path.Combine(_root, "system-share");
        Directory.CreateDirectory(Path.Combine(_dataHome, "applications"));
        Directory.CreateDirectory(Path.Combine(_dataDirs, "applications"));
    }

    public void Dispose()
    {
        foreach (var pid in _started)
        {
            try { Process.GetProcessById(pid).Kill(true); } catch (ArgumentException) { } catch (InvalidOperationException) { }
        }
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private LinuxAppLauncher Launcher() => new(null, n => n switch
    {
        "XDG_DATA_HOME" => _dataHome,
        // The system folders stay listed: GLib tools find their GSettings schemas there.
        "XDG_DATA_DIRS" => _dataDirs + ":/usr/local/share:/usr/share",
        _ => Environment.GetEnvironmentVariable(n),
    }, LinuxTools.FindTool, null);

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0) return true;
            await Task.Delay(100);
        }
        return false;
    }

    [LinuxFact]
    public async Task Launches_an_app_by_name_from_a_desktop_file()
    {
        var id = "deskpilot-marker-" + Guid.NewGuid().ToString("N")[..8];
        var marker = Path.Combine(_root, "marker.txt");
        File.WriteAllText(Path.Combine(_dataHome, "applications", id + ".desktop"),
            "[Desktop Entry]\nType=Application\n" +
            $"Name=DeskPilot Marker {id}\n" +
            "GenericName=Marker writer\n" +
            $"Exec=sh -c \"echo launched > '{marker}'\"\n" +
            "Terminal=false\n");

        var result = Launcher().Launch($"DeskPilot Marker {id}", null, allowElevation: false);
        Assert.True(result.Success, result.Message);
        Assert.Equal($"Opened DeskPilot Marker {id} ({id}.desktop)", result.Message);
        Assert.True(await WaitForFileAsync(marker, TimeSpan.FromSeconds(15)), "the desktop entry's command did not run");
        Assert.Equal("launched", File.ReadAllText(marker).Trim());
    }

    [LinuxFact]
    public void Refuses_entries_that_ask_for_root()
    {
        var marker = Path.Combine(_root, "should-not-exist.txt");
        File.WriteAllText(Path.Combine(_dataHome, "applications", "rootish.desktop"),
            $"[Desktop Entry]\nType=Application\nName=Rootish Tool\nExec=pkexec sh -c \"touch '{marker}'\"\n");
        var result = Launcher().Launch("Rootish Tool", null, allowElevation: false);
        Assert.False(result.Success);
        Assert.Contains("Administrator mode", result.Message);
        Assert.Contains("pkexec", result.Message);
        Assert.False(File.Exists(marker));

        var direct = Launcher().Launch("pkexec", "true", allowElevation: false);
        Assert.False(direct.Success);
        Assert.Contains("Administrator mode", direct.Message);
    }

    [LinuxFact]
    public async Task Starts_commands_detached_in_their_own_session()
    {
        var result = Launcher().Launch("sleep", "20", allowElevation: false);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(result.ProcessId);
        var pid = result.ProcessId!.Value;
        _started.Add(pid);
        Assert.Contains("sleep", result.Message);

        var stat = await File.ReadAllTextAsync($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
        // Fields after the name: state, ppid, pgrp, session.
        if (LinuxTools.FindTool("setsid") != null) Assert.Equal(pid.ToString(), fields[3]);
        Assert.Equal("/dev/null", new FileInfo($"/proc/{pid}/fd/0").LinkTarget);
        Assert.Equal("/dev/null", new FileInfo($"/proc/{pid}/fd/1").LinkTarget);
        Assert.Equal("/dev/null", new FileInfo($"/proc/{pid}/fd/2").LinkTarget);
    }

    [LinuxFact]
    public async Task Runs_a_script_with_arguments_and_reports_missing_paths()
    {
        var script = Path.Combine(_root, "write.sh");
        var output = Path.Combine(_root, "script-out.txt");
        File.WriteAllText(script, "#!/bin/sh\necho \"$1\" > \"$2\"\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var result = Launcher().Launch(script, $"'hello there' '{output}'", allowElevation: false);
        Assert.True(result.Success, result.Message);
        Assert.True(await WaitForFileAsync(output, TimeSpan.FromSeconds(10)));
        Assert.Equal("hello there", File.ReadAllText(output).Trim());

        // A command with its arguments written into the target itself.
        var output2 = Path.Combine(_root, "script-out-2.txt");
        var inline = Launcher().Launch($"sh '{script}' 'from target' '{output2}'", null, allowElevation: false);
        Assert.True(inline.Success, inline.Message);
        Assert.True(await WaitForFileAsync(output2, TimeSpan.FromSeconds(10)));
        Assert.Equal("from target", File.ReadAllText(output2).Trim());

        var missing = Launcher().Launch(Path.Combine(_root, "nope", "x.txt"), null, allowElevation: false);
        Assert.False(missing.Success);
        Assert.Contains("Nothing exists", missing.Message);

        var unknown = Launcher().Launch("No Such Application " + Guid.NewGuid().ToString("N")[..6], null, allowElevation: false);
        Assert.False(unknown.Success);
        Assert.Contains("Could not find", unknown.Message);
    }
}

// ------------------------------------------------------------------ AT-SPI against real apps (X11 job)

public class AtSpiInspectorTests
{
    private readonly ITestOutputHelper _output;

    public AtSpiInspectorTests(ITestOutputHelper output) => _output = output;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The client-area geometry of a window, from xwininfo, polled until the window is mapped.</summary>
    private static async Task<ScreenRect?> WindowGeometryAsync(string title, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var psi = new ProcessStartInfo("xwininfo") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-name");
            psi.ArgumentList.Add(title);
            using (var p = Process.Start(psi)!)
            {
                var text = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                int? Get(string key)
                {
                    var line = text.Split('\n').FirstOrDefault(l => l.Trim().StartsWith(key, StringComparison.Ordinal));
                    return line != null && int.TryParse(line[(line.IndexOf(':') + 1)..].Trim(), out var v) ? v : null;
                }
                if (p.ExitCode == 0 && Get("Absolute upper-left X") is { } x && Get("Absolute upper-left Y") is { } y && Get("Width") is { } w && Get("Height") is { } h
                    && text.Contains("IsViewable", StringComparison.Ordinal))
                    return new ScreenRect(x, y, w, h);
            }
            await Task.Delay(200);
        }
        return null;
    }

    private static Process StartApp(string file, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment.Remove("NO_AT_BRIDGE");
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    private static void Kill(Process p)
    {
        try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        try { p.WaitForExit(3000); } catch (InvalidOperationException) { }
        p.Dispose();
    }

    private async Task<IReadOnlyList<UiElementInfo>> WaitForElementsAsync(AtSpiInspector inspector, nint handle, Func<IReadOnlyList<UiElementInfo>, bool> ready, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        IReadOnlyList<UiElementInfo> last = Array.Empty<UiElementInfo>();
        Exception? lastError = null;
        while (sw.Elapsed < timeout)
        {
            try
            {
                last = await inspector.GetElementsAsync(handle, 200, Ct);
                lastError = null;
                if (ready(last)) return last;
            }
            catch (InvalidOperationException ex)
            {
                lastError = ex;
            }
            await Task.Delay(300, Ct);
        }
        if (lastError != null) _output.WriteLine("last error: " + lastError.Message);
        return last;
    }

    private async Task DumpAsync(AtSpiInspector inspector, int pid)
    {
        try
        {
            foreach (var line in await inspector.DumpAsync(pid, 400, Ct)) _output.WriteLine(line);
        }
        catch (Exception ex)
        {
            _output.WriteLine("dump failed: " + ex);
        }
    }

    private void Log(IEnumerable<UiElementInfo> elements)
    {
        foreach (var e in elements) _output.WriteLine($"{e.ControlType} '{e.Name}' {e.Bounds} enabled={e.IsEnabled} focusable={e.IsFocusable} id={e.AutomationId} value={e.Value}");
    }

    private static void AssertInside(ScreenRect window, UiElementInfo e)
    {
        Assert.False(e.Bounds.IsEmpty, $"{e.ControlType} '{e.Name}' has no bounds");
        Assert.True(e.Bounds.X >= window.X - 4 && e.Bounds.Y >= window.Y - 4 && e.Bounds.Right <= window.Right + 4 && e.Bounds.Bottom <= window.Bottom + 4,
            $"{e.ControlType} '{e.Name}' at {e.Bounds} is outside the window {window}");
    }

    /// <summary>Role numbers are checked against the role names the toolkit reports, so the role table cannot drift.</summary>
    private static void AssertRoleNamesAgree(IEnumerable<string> dump)
    {
        // ATK-style names (GTK 3, Qt) and GTK 4's own names for the same role numbers.
        var expected = new Dictionary<string, string[]>
        {
            ["Button"] = new[] { "push button", "toggle button", "push button menu", "button", "toggle" },
            ["Edit"] = new[] { "text", "entry", "password text", "editbar", "text box", "search box" },
            ["CheckBox"] = new[] { "check box", "checkbox", "switch" },
        };
        foreach (var line in dump)
        {
            var typeAt = line.LastIndexOf(" type=", StringComparison.Ordinal);
            if (typeAt < 0) continue;
            var type = line[(typeAt + 6)..].Trim();
            if (!expected.TryGetValue(type, out var names)) continue;
            var start = line.IndexOf('\'') + 1;
            var roleName = line[start..line.IndexOf('\'', start)];
            Assert.True(names.Contains(roleName), $"role table mismatch: {line}");
        }
    }

    [X11Fact]
    public Task Finds_entry_and_buttons_in_a_gtk4_dialog() => Gtk4DialogAsync(t => WindowGeometryAsync(t, TimeSpan.FromSeconds(20)));

    /// <summary>The same dialog as a native Wayland client of sway; its position comes from sway's tree.</summary>
    [WaylandFact]
    public Task Finds_entry_and_buttons_in_a_gtk4_dialog_on_wayland() => Gtk4DialogAsync(t => SwayGeometryAsync(t, TimeSpan.FromSeconds(20)));

    /// <summary>Content rectangle of a sway window (rect plus window_rect), polled until it is mapped.</summary>
    private static async Task<ScreenRect?> SwayGeometryAsync(string title, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var psi = new ProcessStartInfo("swaymsg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add("get_tree");
            using (var p = Process.Start(psi)!)
            {
                var json = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                if (p.ExitCode == 0 && FindSwayNode(System.Text.Json.JsonDocument.Parse(json).RootElement, title) is { } node)
                {
                    var rect = node.GetProperty("rect");
                    var inner = node.GetProperty("window_rect");
                    var r = new ScreenRect(
                        rect.GetProperty("x").GetInt32() + inner.GetProperty("x").GetInt32(),
                        rect.GetProperty("y").GetInt32() + inner.GetProperty("y").GetInt32(),
                        inner.GetProperty("width").GetInt32(),
                        inner.GetProperty("height").GetInt32());
                    if (!r.IsEmpty) return r;
                }
            }
            await Task.Delay(200);
        }
        return null;
    }

    private static System.Text.Json.JsonElement? FindSwayNode(System.Text.Json.JsonElement node, string title)
    {
        if (node.TryGetProperty("name", out var name) && name.ValueKind == System.Text.Json.JsonValueKind.String && name.GetString() == title
            && node.TryGetProperty("window_rect", out _))
            return node;
        foreach (var key in new[] { "nodes", "floating_nodes" })
        {
            if (!node.TryGetProperty(key, out var children) || children.ValueKind != System.Text.Json.JsonValueKind.Array) continue;
            foreach (var child in children.EnumerateArray())
                if (FindSwayNode(child, title) is { } hit) return hit;
        }
        return null;
    }

    private async Task Gtk4DialogAsync(Func<string, Task<ScreenRect?>> geometryOf)
    {
        var title = "DeskPilot zenity " + Guid.NewGuid().ToString("N")[..8];
        var app = StartApp("zenity", "--entry", "--title", title, "--text", "DeskPilot test prompt", "--entry-text", "hello dp");
        try
        {
            var geometry = await geometryOf(title);
            Assert.True(geometry != null, "zenity did not show its window");
            _output.WriteLine($"window geometry {geometry}");
            var window = new WindowInfo(77, title, "zenity", "zenity", app.Id, geometry!.Value, true, false, true, false, false);
            var inspector = new AtSpiInspector(new FakeWindows { Window = window });

            var elements = await WaitForElementsAsync(inspector, 77,
                l => l.Any(e => e.ControlType == "Button" && e.Name == "OK") && l.Any(e => e.ControlType == "Edit"), TimeSpan.FromSeconds(25));
            Log(elements);
            var dump = await inspector.DumpAsync(app.Id, 400, Ct);
            foreach (var line in dump) _output.WriteLine(line);

            var ok = elements.FirstOrDefault(e => e.ControlType == "Button" && e.Name == "OK");
            Assert.NotNull(ok);
            var cancel = elements.FirstOrDefault(e => e.ControlType == "Button" && e.Name == "Cancel");
            Assert.NotNull(cancel);
            var edit = elements.First(e => e.ControlType == "Edit");
            Assert.Equal("hello dp", edit.Value);
            foreach (var e in elements) AssertInside(geometry.Value, e);
            AssertRoleNamesAgree(dump);
            // Real positions, not every control piled up at the window's corner: the buttons sit side by side below the entry.
            Assert.NotEqual(ok!.Bounds.X, cancel!.Bounds.X);
            Assert.True(edit.Bounds.Bottom <= ok.Bounds.Y + 2, $"entry {edit.Bounds} is not above the buttons {ok.Bounds}");

            // Handle 0 means the foreground window.
            var foreground = await inspector.GetElementsAsync(0, 200, Ct);
            Assert.Contains(foreground, e => e.ControlType == "Button" && e.Name == "OK");

            var at = await inspector.GetElementAtAsync(ok!.Bounds.Center.X, ok.Bounds.Center.Y, Ct);
            Assert.NotNull(at);
            Assert.Equal("OK", at!.Name);
            Assert.Equal("Button", at.ControlType);

            var atEdit = await inspector.GetElementAtAsync(edit.Bounds.Center.X, edit.Bounds.Center.Y, Ct);
            Assert.NotNull(atEdit);
            Assert.Equal("Edit", atEdit!.ControlType);
        }
        catch
        {
            await DumpAsync(new AtSpiInspector(new FakeWindows()), 0);
            throw;
        }
        finally
        {
            Kill(app);
        }
    }

    private const string Gtk3Script = """
        import sys, gi
        gi.require_version("Gtk", "3.0")
        from gi.repository import Gtk
        w = Gtk.Window(title=sys.argv[1])
        w.set_default_size(460, 260)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        e = Gtk.Entry(); e.set_text("visible text")
        p = Gtk.Entry(); p.set_visibility(False); p.set_text("hunter2secret")
        b = Gtk.Button(label="Press Me")
        c = Gtk.CheckButton(label="Check Me")
        for x in (e, p, b, c): box.pack_start(x, False, False, 0)
        w.add(box)
        w.connect("destroy", Gtk.main_quit)
        w.show_all()
        Gtk.main()
        """;

    [X11Fact]
    public Task Reads_a_gtk3_window_without_reading_the_password() => Gtk3WindowAsync(t => WindowGeometryAsync(t, TimeSpan.FromSeconds(20)));

    /// <summary>GTK 3 as a native Wayland client: it reports window-relative "screen" coordinates.</summary>
    [WaylandFact]
    public Task Reads_a_gtk3_window_on_wayland() => Gtk3WindowAsync(t => SwayGeometryAsync(t, TimeSpan.FromSeconds(20)));

    private async Task Gtk3WindowAsync(Func<string, Task<ScreenRect?>> geometryOf)
    {
        var script = Path.Combine(Path.GetTempPath(), "dp-gtk3-" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(script, Gtk3Script);
        var title = "DeskPilot GTK3 " + Guid.NewGuid().ToString("N")[..8];
        var app = StartApp("python3", script, title);
        try
        {
            var geometry = await geometryOf(title);
            Assert.True(geometry != null, "the GTK 3 test window did not appear");
            _output.WriteLine($"window geometry {geometry}");
            var window = new WindowInfo(78, title, "python3", "python3", app.Id, geometry!.Value, true, false, true, false, false);
            var inspector = new AtSpiInspector(new FakeWindows { Window = window });

            var elements = await WaitForElementsAsync(inspector, 78,
                l => l.Any(e => e.Name == "Press Me") && l.Count(e => e.ControlType == "Edit") >= 2, TimeSpan.FromSeconds(25));
            Log(elements);
            var dump = await inspector.DumpAsync(app.Id, 400, Ct);
            foreach (var line in dump) _output.WriteLine(line);

            var button = elements.FirstOrDefault(e => e.ControlType == "Button" && e.Name == "Press Me");
            Assert.NotNull(button);
            Assert.Contains(elements, e => e.ControlType == "CheckBox" && e.Name == "Check Me");
            var edits = elements.Where(e => e.ControlType == "Edit").ToList();
            Assert.Equal(2, edits.Count);
            Assert.Contains(edits, e => e.Value == "visible text");
            Assert.Contains(edits, e => e.Value == null);
            Assert.DoesNotContain(elements, e => (e.Value ?? "").Contains("hunter2", StringComparison.Ordinal) || e.Name.Contains("hunter2", StringComparison.Ordinal));
            foreach (var e in elements) AssertInside(geometry.Value, e);
            AssertRoleNamesAgree(dump);

            var at = await inspector.GetElementAtAsync(button!.Bounds.Center.X, button.Bounds.Center.Y, Ct);
            Assert.NotNull(at);
            Assert.Equal("Press Me", at!.Name);

            var password = edits.First(e => e.Value == null);
            var atPassword = await inspector.GetElementAtAsync(password.Bounds.Center.X, password.Bounds.Center.Y, Ct);
            Assert.NotNull(atPassword);
            Assert.Null(atPassword!.Value);
        }
        catch
        {
            await DumpAsync(new AtSpiInspector(new FakeWindows()), 0);
            throw;
        }
        finally
        {
            Kill(app);
            try { File.Delete(script); } catch (IOException) { }
        }
    }

    [X11Fact]
    public async Task Element_lookups_stay_within_their_timeouts()
    {
        var inspector = new AtSpiInspector(new FakeWindows());
        var sw = Stopwatch.StartNew();
        // Nothing is under this point (no window known): null, quickly.
        var at = await inspector.GetElementAtAsync(5, 5, Ct);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
        _output.WriteLine($"element at (5,5): {at?.ControlType} '{at?.Name}'");
    }
}

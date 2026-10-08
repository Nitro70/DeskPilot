using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;

namespace DeskPilot.Tests;

public class SafetyTests
{
    private static readonly WindowInfo Notepad = ToolsTestFakes.Notepad;
    private static readonly ScreenPoint Point = new(100, 100);

    private sealed class Setup
    {
        public AppSettings Settings { get; } = new();
        public ToolsTestFakes.Windows Windows { get; } = new();
        public ToolsTestFakes.Ui Ui { get; } = new();
        public List<string> Warnings { get; } = new();
        public SafetyGuard Guard { get; }

        public Setup(Action<Setup>? configure = null, bool withUi = true)
        {
            Windows.Foreground = Notepad;
            Windows.At = (_, _) => Notepad;
            Windows.List.Add(Notepad);
            configure?.Invoke(this);
            Guard = new SafetyGuard(() => Settings, Windows, withUi ? Ui : null)
            {
                Warn = Warnings.Add,
                ElementLookupTimeout = TimeSpan.FromMilliseconds(100),
            };
        }

        public Task<SafetyVerdict> Check(ProposedAction action) => Guard.CheckAsync(action, CancellationToken.None);
    }

    private static ProposedAction Click(ScreenPoint? at = null, ActionRisk risk = ActionRisk.Medium) => new("click", "Click", risk, Target: at ?? Point);
    private static ProposedAction Move(ScreenPoint? at = null) => new("move_mouse", "Move", ActionRisk.Low, Target: at ?? Point);
    private static ProposedAction Type(string text, ActionRisk risk = ActionRisk.Medium) => new("type_text", "Type", risk, Text: text);
    private static ProposedAction Keys(string keys) => new("press_keys", "Keys", ActionRisk.Medium, Keys: KeyCombo.Parse(keys));
    private static ProposedAction Launch(string target, string? args = null) => new("launch", "Open", ActionRisk.High, LaunchTarget: target, Command: args);
    private static ProposedAction Command(string command) => new("run_command", "Run", ActionRisk.High, Command: command);
    private static ProposedAction Focus(WindowInfo w, bool attach = true)
    {
        var action = new ProposedAction("focus_window", "Focus", ActionRisk.Medium, Text: w.Title);
        return attach ? action.WithWindow(w) : action;
    }

    // ------------------------------------------------------------ UAC prompt

    [Fact]
    public async Task Uac_prompt_denies_everything_except_observation()
    {
        var s = new Setup(x => x.Windows.UacActive = true);
        foreach (var action in new[] { Click(), Move(), Type("hi"), Keys("enter"), Launch("notepad"), Command("dir"),
                     new ProposedAction("set_clipboard", "Set", ActionRisk.Medium, Text: "x"), Focus(Notepad) })
        {
            var verdict = await s.Check(action);
            Assert.Equal(SafetyDecision.Deny, verdict.Decision);
            Assert.Contains("UAC", verdict.Reason);
            Assert.Contains("Only the user can answer it", verdict.Reason);
        }

        foreach (var tool in new[] { "screenshot", "zoom", "wait", "list_windows", "ui_elements" })
            Assert.Equal(SafetyDecision.Allow, (await s.Check(new ProposedAction(tool, tool, ActionRisk.None))).Decision);
    }

    [Fact]
    public async Task Uac_prompt_denies_even_in_admin_mode()
    {
        var s = new Setup(x =>
        {
            x.Windows.UacActive = true;
            x.Settings.Safety.AllowAdmin = true;
        });
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Click())).Decision);
    }

    // ------------------------------------------------------------ DeskPilot's own windows

    [Fact]
    public async Task Own_windows_are_never_a_target()
    {
        var own = ToolsTestFakes.Window("DeskPilot", "DeskPilot", pid: Environment.ProcessId);
        var s = new Setup(x =>
        {
            x.Windows.At = (_, _) => own;
            x.Settings.Safety.AllowAdmin = true;
        });

        var click = await s.Check(Click());
        Assert.Equal(SafetyDecision.Deny, click.Decision);
        Assert.Contains("DeskPilot's own window", click.Reason);
        Assert.Contains("focus_window", click.Reason);

        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(own))).Decision);

        s.Windows.Foreground = own;
        var typed = await s.Check(Type("hello"));
        Assert.Equal(SafetyDecision.Deny, typed.Decision);
        Assert.Contains("keyboard focus", typed.Reason);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Keys("ctrl+v"))).Decision);

        // Non-window actions are unaffected.
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Launch("notepad"))).Decision);
    }

    // ------------------------------------------------------------ blocked processes

    [Theory]
    [InlineData("keepass")]
    [InlineData("KeePass.exe")]
    [InlineData(" KEEPASS ")]
    public async Task Blocked_process_cannot_be_clicked(string entry)
    {
        var vault = ToolsTestFakes.Window("Passwords - KeePass", "KeePass");
        var s = new Setup(x =>
        {
            x.Windows.At = (_, _) => vault;
            x.Settings.Safety.BlockedProcesses.Add(entry);
        });
        var verdict = await s.Check(Click());
        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Contains("blocked in DeskPilot settings", verdict.Reason);
        Assert.Contains("ask the user", verdict.Reason);
    }

    [Fact]
    public async Task Blocked_process_applies_to_focus_keyboard_and_moves_even_in_admin_mode()
    {
        var vault = ToolsTestFakes.Window("Passwords", "keepass.exe");
        var s = new Setup(x =>
        {
            x.Settings.Safety.AllowAdmin = true;
            x.Settings.Safety.BlockedProcesses.Add("keepass");
            x.Windows.List.Add(vault);
        });

        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(vault))).Decision);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(vault, attach: false))).Decision); // by exact title

        s.Windows.At = (_, _) => vault;
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Move())).Decision);

        s.Windows.At = (_, _) => Notepad;
        s.Windows.Foreground = vault;
        var typed = await s.Check(Type("secret"));
        Assert.Equal(SafetyDecision.Deny, typed.Decision);
        Assert.Contains("will not type into it", typed.Reason);

        s.Windows.Foreground = Notepad;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type("fine"))).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Focus(Notepad))).Decision);
    }

    [Fact]
    public async Task Focus_by_title_fallback_checks_every_window_with_that_title()
    {
        var safe = ToolsTestFakes.Window("Login", "browser", pid: 1);
        var blocked = ToolsTestFakes.Window("Login", "keepass", pid: 2);
        var s = new Setup(x =>
        {
            x.Windows.List.AddRange(new[] { safe, blocked });
            x.Settings.Safety.BlockedProcesses.Add("keepass");
        });
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(safe, attach: false))).Decision);
        // With the exact window attached only that window counts.
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Focus(safe))).Decision);
    }

    // ------------------------------------------------------------ administrator rules

    [Fact]
    public async Task Elevated_windows_are_off_limits_unless_admin_mode()
    {
        var admin = ToolsTestFakes.Window("Administrator: Windows PowerShell", "powershell", elevated: true);
        var s = new Setup(x => x.Windows.At = (_, _) => admin);

        var click = await s.Check(Click());
        Assert.Equal(SafetyDecision.Deny, click.Decision);
        Assert.Contains("runs as administrator", click.Reason);
        Assert.Contains("enable Administrator mode in DeskPilot settings", click.Reason);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Move())).Decision);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(admin))).Decision);

        s.Windows.At = (_, _) => Notepad;
        s.Windows.Foreground = admin;
        var typed = await s.Check(Type("Get-Process"));
        Assert.Equal(SafetyDecision.Deny, typed.Decision);
        Assert.Contains("will not type into it", typed.Reason);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Keys("ctrl+c"))).Decision);
        // A pointer action on a normal window is fine even when an elevated window has focus.
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);

        s.Settings.Safety.AllowAdmin = true;
        s.Windows.At = (_, _) => admin;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type("Get-Process"))).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Focus(admin))).Decision);
    }

    [Fact]
    public async Task Uac_and_credential_windows_are_denied()
    {
        var consent = ToolsTestFakes.Window("User Account Control", "consent", uac: true);
        var s = new Setup(x => x.Windows.At = (_, _) => consent);
        var verdict = await s.Check(Click());
        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Contains("security prompt", verdict.Reason);

        s.Windows.At = (_, _) => Notepad;
        s.Windows.Foreground = consent;
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Type("x"))).Decision);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Focus(consent))).Decision);
    }

    [Theory]
    [InlineData("ctrl+shift+enter", SafetyDecision.Deny)]
    [InlineData("Ctrl + Shift + Return", SafetyDecision.Deny)]
    [InlineData("ctrl+alt+shift+enter", SafetyDecision.Deny)]
    [InlineData("ctrl+enter", SafetyDecision.Allow)]
    [InlineData("shift+enter", SafetyDecision.Allow)]
    [InlineData("enter", SafetyDecision.Allow)]
    public async Task Ctrl_shift_enter_is_denied_without_admin_mode(string keys, SafetyDecision expected)
    {
        var s = new Setup();
        var verdict = await s.Check(Keys(keys));
        Assert.Equal(expected, verdict.Decision);
        if (expected == SafetyDecision.Deny) Assert.Contains("ctrl+shift+enter", verdict.Reason);

        s.Settings.Safety.AllowAdmin = true;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Keys(keys))).Decision);
    }

    [Theory]
    [InlineData("Run as administrator")]
    [InlineData("RUN AS ADMIN")]
    [InlineData("Troubleshoot compatibility | Run as administrator")]
    public async Task Clicking_run_as_administrator_is_denied(string name)
    {
        var s = new Setup(x => x.Ui.At = (_, _, _) =>
            Task.FromResult<UiElementInfo?>(new UiElementInfo(name, "MenuItem", new ScreenRect(90, 90, 100, 20), null, true, true, null)));
        var verdict = await s.Check(Click());
        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Contains("starts a program as administrator", verdict.Reason);

        // Only clicks trigger the lookup.
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Move())).Decision);

        s.Settings.Safety.AllowAdmin = true;
        int calls = s.Ui.ElementAtCalls;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);
        Assert.Equal(calls, s.Ui.ElementAtCalls);
    }

    [Fact]
    public async Task Run_as_admin_lookup_is_best_effort()
    {
        var s = new Setup(x => x.Ui.At = (_, _, _) =>
            Task.FromResult<UiElementInfo?>(new UiElementInfo("Open", "MenuItem", new ScreenRect(90, 90, 100, 20), null, true, true, null)));
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);

        s.Ui.At = (_, _, _) => throw new InvalidOperationException("UIA broke");
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);

        s.Ui.At = (_, _, _) => Task.FromException<UiElementInfo?>(new TimeoutException());
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);

        // An inspector that ignores cancellation is cut off by the timeout (100 ms in this setup).
        s.Ui.At = async (_, _, _) =>
        {
            await Task.Delay(5000);
            return new UiElementInfo("Run as administrator", "MenuItem", default, null, true, true, null);
        };
        var started = DateTime.UtcNow;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));

        var noUi = new Setup(withUi: false);
        Assert.Equal(SafetyDecision.Allow, (await noUi.Check(Click())).Decision);
    }

    [Theory]
    [InlineData("runas /user:Administrator cmd")]
    [InlineData("Start-Process powershell -Verb RunAs")]
    [InlineData("start-process pwsh -verb 'runas'")]
    [InlineData("sudo apt update")]
    [InlineData("gsudo notepad")]
    [InlineData("psexec -s cmd")]
    [InlineData("nircmd elevate cmd")]
    public async Task Elevation_patterns_block_typed_text_and_commands(string text)
    {
        var s = new Setup();
        var typed = await s.Check(Type(text));
        Assert.Equal(SafetyDecision.Deny, typed.Decision);
        Assert.Contains("elevation pattern", typed.Reason);
        Assert.Contains("The text matches", typed.Reason);

        var command = await s.Check(Command(text));
        Assert.Equal(SafetyDecision.Deny, command.Decision);
        Assert.Contains("The command matches", command.Reason);

        Assert.Equal(SafetyDecision.Deny, (await s.Check(new ProposedAction("set_clipboard", "Set", ActionRisk.Medium, Text: text))).Decision);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Launch("powershell", text))).Decision);

        s.Settings.Safety.AllowAdmin = true;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type(text))).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Command(text))).Decision);
    }

    [Theory]
    [InlineData("Hello, this is a normal sentence.")]
    [InlineData("pseudo code and runaway trains")]
    [InlineData("Get-ChildItem -Recurse")]
    public async Task Ordinary_text_passes_the_patterns(string text)
    {
        var s = new Setup();
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type(text))).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Command(text))).Decision);
    }

    [Fact]
    public async Task Focus_window_title_is_not_treated_as_typed_text()
    {
        var w = ToolsTestFakes.Window("sudo - manual page", "browser");
        var s = new Setup(x => x.Windows.List.Add(w));
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Focus(w))).Decision);
    }

    [Fact]
    public async Task Invalid_patterns_are_ignored_and_logged_once()
    {
        var s = new Setup(x =>
        {
            x.Settings.Safety.ElevationTextPatterns = new List<string> { "([unclosed", "", @"\bdanger\b" };
        });
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type("hello"))).Decision);
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Type("hello again"))).Decision);
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Type("DANGER zone"))).Decision);
        var warning = Assert.Single(s.Warnings);
        Assert.Contains("([unclosed", warning);
    }

    [Theory]
    [InlineData("regedit")]
    [InlineData("REGEDIT.EXE")]
    [InlineData(@"C:\Windows\regedit.exe")]
    [InlineData("\"C:\\Windows\\regedit.exe\"")]
    [InlineData("gpedit.msc")]
    [InlineData(@"C:\Windows\System32\gpedit.msc")]
    [InlineData("gpedit")]
    [InlineData("UserAccountControlSettings")]
    [InlineData("regedit /s settings.reg")]
    public async Task Elevated_launch_targets_are_denied(string target)
    {
        var s = new Setup();
        var verdict = await s.Check(Launch(target));
        Assert.Equal(SafetyDecision.Deny, verdict.Decision);
        Assert.Contains("always asks for administrator rights", verdict.Reason);

        s.Settings.Safety.AllowAdmin = true;
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Launch(target))).Decision);
    }

    [Theory]
    [InlineData("mmc", "gpedit.msc", SafetyDecision.Deny)]
    [InlineData("cmd", "/c start regedit", SafetyDecision.Deny)]
    [InlineData("notepad", "regedit.txt", SafetyDecision.Allow)]
    [InlineData("notepad", null, SafetyDecision.Allow)]
    [InlineData("https://example.com/regedit-guide", null, SafetyDecision.Allow)]
    [InlineData("ms-settings:display", null, SafetyDecision.Allow)]
    public async Task Elevated_launch_targets_check_arguments_and_file_names(string target, string? args, SafetyDecision expected)
    {
        var s = new Setup();
        Assert.Equal(expected, (await s.Check(Launch(target, args))).Decision);
    }

    // ------------------------------------------------------------ confirmation modes

    [Theory]
    [InlineData(ConfirmMode.Never, ActionRisk.High, SafetyDecision.Allow)]
    [InlineData(ConfirmMode.Never, ActionRisk.Medium, SafetyDecision.Allow)]
    [InlineData(ConfirmMode.RiskyOnly, ActionRisk.High, SafetyDecision.NeedsConfirmation)]
    [InlineData(ConfirmMode.RiskyOnly, ActionRisk.Medium, SafetyDecision.Allow)]
    [InlineData(ConfirmMode.RiskyOnly, ActionRisk.Low, SafetyDecision.Allow)]
    [InlineData(ConfirmMode.Always, ActionRisk.High, SafetyDecision.NeedsConfirmation)]
    [InlineData(ConfirmMode.Always, ActionRisk.Medium, SafetyDecision.NeedsConfirmation)]
    [InlineData(ConfirmMode.Always, ActionRisk.Low, SafetyDecision.Allow)]
    [InlineData(ConfirmMode.Always, ActionRisk.None, SafetyDecision.Allow)]
    public async Task Confirm_mode_by_risk(ConfirmMode mode, ActionRisk risk, SafetyDecision expected)
    {
        var s = new Setup(x => x.Settings.Safety.Confirm = mode);
        var verdict = await s.Check(Click(risk: risk));
        Assert.Equal(expected, verdict.Decision);
        if (expected == SafetyDecision.NeedsConfirmation) Assert.Contains("confirmation", verdict.Reason);
    }

    [Fact]
    public async Task Deny_rules_win_over_confirmation()
    {
        var admin = ToolsTestFakes.Window("Admin", "cmd", elevated: true);
        var s = new Setup(x =>
        {
            x.Settings.Safety.Confirm = ConfirmMode.Always;
            x.Windows.At = (_, _) => admin;
        });
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Click())).Decision);
    }

    [Fact]
    public async Task Settings_are_read_on_every_check()
    {
        var s = new Setup();
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Launch("notepad"))).Decision);
        s.Settings.Safety.Confirm = ConfirmMode.RiskyOnly;
        Assert.Equal(SafetyDecision.NeedsConfirmation, (await s.Check(Launch("notepad"))).Decision);
        s.Settings.Safety.ElevatedLaunchTargets.Add("notepad");
        Assert.Equal(SafetyDecision.Deny, (await s.Check(Launch("notepad"))).Decision);
    }

    [Fact]
    public async Task Desktop_failures_do_not_crash_the_guard()
    {
        var s = new Setup(x =>
        {
            x.Windows.At = (_, _) => throw new InvalidOperationException("no window");
        });
        Assert.Equal(SafetyDecision.Allow, (await s.Check(Click())).Decision);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var s = new Setup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Guard.CheckAsync(Click(), cts.Token));
    }

    [Fact]
    public void Helpers_match_names()
    {
        Assert.True(SafetyGuard.IsRunAsAdminName("Run as Administrator"));
        Assert.False(SafetyGuard.IsRunAsAdminName("Run"));
        Assert.False(SafetyGuard.IsRunAsAdminName(null));
        Assert.True(SafetyGuard.IsBlocked(ToolsTestFakes.Window("x", "Bitwarden"), new[] { "bitwarden.EXE" }));
        Assert.False(SafetyGuard.IsBlocked(ToolsTestFakes.Window("x", "Bitwarden"), new[] { "bit" }));
        Assert.Equal("regedit", SafetyGuard.MatchElevatedLaunchTarget(@"C:\Windows\REGEDIT.lnk", null, new[] { "regedit" }));
        Assert.Null(SafetyGuard.MatchElevatedLaunchTarget("regedit2", null, new[] { "regedit" }));
    }
}

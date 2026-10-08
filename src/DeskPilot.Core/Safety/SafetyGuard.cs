using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Safety;

/// <summary>
/// Decides whether a proposed action may run, following the "Safety rules" in docs/DESIGN.md.
/// Rules run in a fixed order: UAC prompt, DeskPilot's own windows, blocked processes, the
/// administrator rules (only when Administrator mode is off) and finally the confirmation mode.
/// </summary>
public sealed class SafetyGuard : ISafetyGuard
{
    /// <summary>Tools that only observe the screen; they stay allowed while a UAC prompt is open.</summary>
    internal static readonly IReadOnlySet<string> ObserveOnlyTools =
        new HashSet<string>(StringComparer.Ordinal) { "screenshot", "zoom", "wait", "list_windows", "ui_elements" };

    /// <summary>Tools whose input goes to the foreground window.</summary>
    internal static readonly IReadOnlySet<string> KeyboardTools =
        new HashSet<string>(StringComparer.Ordinal) { "type_text", "press_keys" };

    private const string AskUserOrAdminMode =
        "Ask the user to do this step themselves, or to enable Administrator mode in DeskPilot settings.";

    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".lnk", ".msc", ".cpl", ".scr", ".pif", ".ps1", ".vbs",
    };

    private readonly Func<AppSettings> _settings;
    private readonly IWindowManager _windows;
    private readonly IUiInspector? _ui;
    private readonly ConcurrentDictionary<string, Regex?> _patterns = new(StringComparer.Ordinal);

    public SafetyGuard(Func<AppSettings> settings, IWindowManager windows, IUiInspector? ui = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _ui = ui;
    }

    /// <summary>Process id treated as "DeskPilot itself". Tests may override it.</summary>
    internal int OwnProcessId { get; set; } = Environment.ProcessId;

    /// <summary>Budget for the best-effort "Run as administrator" element lookup at a click point.</summary>
    internal TimeSpan ElementLookupTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Where warnings (invalid patterns) go. Tests may override it.</summary>
    internal Action<string> Warn { get; set; } = Log.Warn;

    public async Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(action);
        ct.ThrowIfCancellationRequested();

        var settings = _settings();
        var safety = settings.Safety;
        var tool = action.Tool ?? "";
        bool observeOnly = ObserveOnlyTools.Contains(tool);
        bool keyboard = KeyboardTools.Contains(tool);

        // 1. A UAC prompt sits on the secure desktop: nothing DeskPilot sends can answer it, and input
        //    aimed at the normal desktop would land somewhere unexpected once it closes.
        if (!observeOnly && IsUacPromptActive())
            return SafetyVerdict.Denied(
                "A Windows UAC (administrator permission) prompt is open. Only the user can answer it: DeskPilot cannot see or click it. " +
                "Tell the user to approve or cancel it, then use the wait tool or end your turn.");

        WindowInfo? pointerWindow = action.Target is { } point ? WindowAt(point) : null;
        IReadOnlyList<WindowInfo> focusWindows = tool == "focus_window" ? FocusTargets(action) : Array.Empty<WindowInfo>();
        WindowInfo? foreground = keyboard ? Foreground() : null;

        // 2. DeskPilot's own windows are never a target (they are hidden from screenshots, and typing into
        //    the chat box would let the model talk to itself).
        if (pointerWindow != null && pointerWindow.ProcessId == OwnProcessId)
            return SafetyVerdict.Denied(
                "That point is on DeskPilot's own window, which is hidden from screenshots. DeskPilot never operates its own windows. " +
                "Bring the app you want to the front first (focus_window), or ask the user to move DeskPilot's window out of the way.");
        if (focusWindows.Any(w => w.ProcessId == OwnProcessId))
            return SafetyVerdict.Denied("That is DeskPilot's own window; DeskPilot never operates itself. Pick another window.");
        if (foreground != null && foreground.ProcessId == OwnProcessId)
            return SafetyVerdict.Denied(
                "DeskPilot's own window has the keyboard focus, so the input would go into DeskPilot itself. " +
                "Bring the target app to the front first (focus_window, or click into it), then try again.");

        // 3. Programs the user blocked.
        var blocked = safety.BlockedProcesses ?? new List<string>();
        if (pointerWindow != null && IsBlocked(pointerWindow, blocked))
            return BlockedVerdict(pointerWindow);
        foreach (var w in focusWindows)
            if (IsBlocked(w, blocked)) return BlockedVerdict(w);
        if (foreground != null && IsBlocked(foreground, blocked))
            return SafetyVerdict.Denied(
                $"The active window {Describe(foreground)} belongs to a program the user blocked in DeskPilot settings, so DeskPilot will not type into it. " +
                "Do not interact with it; switch to another window or ask the user to do this step themselves.");

        // 4. Administrator rules.
        if (!safety.AllowAdmin)
        {
            var adminVerdict = await CheckAdminRulesAsync(action, safety, pointerWindow, focusWindows, foreground, ct).ConfigureAwait(false);
            if (adminVerdict != null) return adminVerdict;
        }

        // 5. Confirmation mode.
        bool needsConfirmation = safety.Confirm switch
        {
            ConfirmMode.RiskyOnly => action.Risk >= ActionRisk.High,
            ConfirmMode.Always => action.Risk >= ActionRisk.Medium,
            _ => false,
        };
        if (needsConfirmation)
            return SafetyVerdict.Confirm($"This {action.Risk.ToString().ToLowerInvariant()}-risk action needs the user's confirmation (confirm mode: {safety.Confirm}).");

        return SafetyVerdict.Allowed;
    }

    private async Task<SafetyVerdict?> CheckAdminRulesAsync(ProposedAction action, SafetySettings safety, WindowInfo? pointerWindow,
        IReadOnlyList<WindowInfo> focusWindows, WindowInfo? foreground, CancellationToken ct)
    {
        var tool = action.Tool ?? "";

        if (pointerWindow != null && pointerWindow.IsUacPrompt)
            return UacWindowVerdict(pointerWindow);
        if (pointerWindow != null && pointerWindow.IsElevated)
            return SafetyVerdict.Denied(
                $"The window at that point, {Describe(pointerWindow)}, runs as administrator and Administrator mode is off, so DeskPilot will not interact with it. " +
                AskUserOrAdminMode);

        foreach (var w in focusWindows)
        {
            if (w.IsUacPrompt) return UacWindowVerdict(w);
            if (w.IsElevated)
                return SafetyVerdict.Denied(
                    $"{Describe(w)} runs as administrator and Administrator mode is off, so DeskPilot will not work with it. " + AskUserOrAdminMode);
        }

        if (foreground != null && foreground.IsUacPrompt)
            return UacWindowVerdict(foreground);
        if (foreground != null && foreground.IsElevated)
            return SafetyVerdict.Denied(
                $"The active window {Describe(foreground)} runs as administrator and Administrator mode is off, so DeskPilot will not type into it. " +
                AskUserOrAdminMode);

        if (action.Keys is { } keys && keys.Has("ctrl") && keys.Has("shift") && keys.Key == "enter")
            return SafetyVerdict.Denied(
                "ctrl+shift+enter starts programs as administrator, and Administrator mode is off. Press enter alone to start it normally. " +
                AskUserOrAdminMode);

        if (tool == "click" && action.Target is { } clickPoint)
        {
            var elementName = await RunAsAdminElementAtAsync(clickPoint, ct).ConfigureAwait(false);
            if (elementName != null)
                return SafetyVerdict.Denied(
                    $"The element at that point ('{Clip(elementName, 60)}') starts a program as administrator, and Administrator mode is off. " +
                    "Use the normal Open command instead. " + AskUserOrAdminMode);
        }

        foreach (var (kind, text) in TextsToScan(action))
        {
            var pattern = MatchElevationPattern(text, safety.ElevationTextPatterns);
            if (pattern != null)
                return SafetyVerdict.Denied(
                    $"The {kind} matches the elevation pattern '{pattern}' " +
                    "(it would run something as administrator), and Administrator mode is off. Do it without elevation, or " +
                    "ask the user to do this step themselves or to enable Administrator mode in DeskPilot settings.");
        }

        if (tool == "launch" || action.LaunchTarget != null)
        {
            var hit = MatchElevatedLaunchTarget(action.LaunchTarget, action.Command, safety.ElevatedLaunchTargets ?? new List<string>());
            if (hit != null)
                return SafetyVerdict.Denied(
                    $"'{hit}' always asks for administrator rights, and Administrator mode is off. " +
                    "Ask the user to open it themselves, or to enable Administrator mode in DeskPilot settings.");
        }

        return null;
    }

    private static SafetyVerdict UacWindowVerdict(WindowInfo w) => SafetyVerdict.Denied(
        $"{Describe(w)} is a Windows security prompt (UAC or credentials). Only the user can answer it. " +
        "Tell the user to answer it, then use the wait tool or end your turn.");

    private static SafetyVerdict BlockedVerdict(WindowInfo w) => SafetyVerdict.Denied(
        $"{Describe(w)} belongs to a program the user blocked in DeskPilot settings ({w.ProcessName}). " +
        "Do not interact with it; ask the user to do this step themselves.");

    private static IEnumerable<(string Kind, string Value)> TextsToScan(ProposedAction action)
    {
        // focus_window carries the window title in Text; a title is not typed input.
        if (!string.IsNullOrEmpty(action.Text) && action.Tool != "focus_window") yield return ("text", action.Text);
        if (!string.IsNullOrEmpty(action.Command)) yield return (action.Tool == "launch" ? "launch arguments" : "command", action.Command);
        if (!string.IsNullOrEmpty(action.LaunchTarget)) yield return ("launch target", action.LaunchTarget);
    }

    /// <summary>Returns the first configured pattern that matches the text, or null.</summary>
    internal string? MatchElevationPattern(string text, IEnumerable<string>? patterns)
    {
        if (patterns == null) return null;
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            var regex = _patterns.GetOrAdd(pattern, CompilePattern);
            if (regex == null) continue;
            try
            {
                if (regex.IsMatch(text)) return pattern;
            }
            catch (RegexMatchTimeoutException)
            {
                Warn($"Safety: elevation pattern '{pattern}' timed out and was skipped for one check.");
            }
        }
        return null;
    }

    private Regex? CompilePattern(string pattern)
    {
        try
        {
            return new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException ex)
        {
            Warn($"Safety: ignoring invalid elevation pattern '{pattern}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Returns the configured entry that the launch target or one of its arguments names, or null.
    /// Matches the file name with or without its extension, also inside full paths.
    /// </summary>
    internal static string? MatchElevatedLaunchTarget(string? target, string? arguments, IEnumerable<string> entries)
    {
        var tokens = new List<string>();
        if (!string.IsNullOrWhiteSpace(target))
        {
            tokens.Add(target.Trim());
            tokens.AddRange(CommandLine.Split(target));
        }
        if (!string.IsNullOrWhiteSpace(arguments)) tokens.AddRange(CommandLine.Split(arguments));

        var list = entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).ToList();
        foreach (var token in tokens)
        {
            var name = FileNameOf(token);
            if (name.Length == 0) continue;
            var nameExt = Path.GetExtension(name);
            var nameStem = Path.GetFileNameWithoutExtension(name);
            foreach (var entry in list)
            {
                if (name.Equals(entry, StringComparison.OrdinalIgnoreCase)) return entry;
                var entryExt = Path.GetExtension(entry);
                var entryStem = Path.GetFileNameWithoutExtension(entry);
                if (entryExt.Length == 0)
                {
                    // "regedit" matches regedit.exe / regedit.lnk, but not a document such as regedit.txt.
                    if (nameStem.Equals(entry, StringComparison.OrdinalIgnoreCase) && (nameExt.Length == 0 || ExecutableExtensions.Contains(nameExt)))
                        return entry;
                }
                else if (nameExt.Length == 0 && name.Equals(entryStem, StringComparison.OrdinalIgnoreCase))
                {
                    // "gpedit.msc" also matches a bare "gpedit".
                    return entry;
                }
            }
        }
        return null;
    }

    private static string FileNameOf(string token)
    {
        var t = token.Trim().Trim('"', '\'').TrimEnd(',', ';');
        int slash = t.LastIndexOfAny(new[] { '\\', '/' });
        return slash >= 0 ? t[(slash + 1)..] : t;
    }

    internal static bool IsBlocked(WindowInfo window, IEnumerable<string> blocked)
    {
        var process = StripExe(window.ProcessName);
        if (process.Length == 0) return false;
        return blocked.Any(b => !string.IsNullOrWhiteSpace(b) && StripExe(b).Equals(process, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripExe(string? name)
    {
        var n = (name ?? "").Trim();
        return n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;
    }

    private async Task<string?> RunAsAdminElementAtAsync(ScreenPoint p, CancellationToken ct)
    {
        if (_ui == null) return null;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(ElementLookupTimeout);
        try
        {
            // WaitAsync bounds the wait even if the inspector ignores its token.
            var element = await _ui.GetElementAtAsync(p.X, p.Y, cts.Token).WaitAsync(ElementLookupTimeout, ct).ConfigureAwait(false);
            if (element != null && IsRunAsAdminName(element.Name)) return element.Name;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // Best effort: UI Automation can be slow or fail for some windows.
        }
        return null;
    }

    internal static bool IsRunAsAdminName(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.Contains("run as administrator", StringComparison.OrdinalIgnoreCase) ||
         name.Contains("run as admin", StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<WindowInfo> FocusTargets(ProposedAction action)
    {
        var attached = ActionTargets.GetWindow(action);
        if (attached != null) return new[] { attached };
        if (string.IsNullOrEmpty(action.Text)) return Array.Empty<WindowInfo>();
        try
        {
            // Without the attached window, judge every window with that exact title (conservative).
            return _windows.ListWindows().Where(w => string.Equals(w.Title, action.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<WindowInfo>();
        }
    }

    private bool IsUacPromptActive()
    {
        try { return _windows.IsUacPromptActive(); }
        catch (Exception) { return false; }
    }

    private WindowInfo? WindowAt(ScreenPoint p)
    {
        try { return _windows.GetWindowAt(p.X, p.Y); }
        catch (Exception) { return null; }
    }

    private WindowInfo? Foreground()
    {
        try { return _windows.GetForegroundWindow(); }
        catch (Exception) { return null; }
    }

    private static string Describe(WindowInfo w) =>
        $"'{Clip(string.IsNullOrWhiteSpace(w.Title) ? "(untitled)" : w.Title, 60)}' ({w.ProcessName})";

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";
}

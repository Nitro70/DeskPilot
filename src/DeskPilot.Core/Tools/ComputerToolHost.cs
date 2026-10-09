using System.Globalization;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Tools;

/// <summary>
/// The computer tools the model sees (screenshot, click, type...). Every input action goes through the same
/// pipeline: validate, map coordinates, failsafe checks, safety guard (+ confirmation), observer, perform
/// (or describe in dry-run mode), then an optional fresh screenshot. Settings are read live on every call.
/// </summary>
public sealed partial class ComputerToolHost : IToolHost
{
    internal const int MaxTypeTextLength = 5000;
    internal const int OutputStreamLimit = 8000;
    internal const int ClickPauseMs = 30;
    internal const int DragDurationMs = 300;
    internal const int LaunchMinSettleMs = 1500;
    private const int FailsafeRadius = 3;
    private const double UserMoveThreshold = 12;

    private static readonly IReadOnlySet<string> AllToolNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "screenshot", "zoom", "click", "move_mouse", "drag", "scroll", "type_text", "press_keys", "wait",
        "list_windows", "focus_window", "launch", "ui_elements", "get_clipboard", "set_clipboard", "run_command",
    };

    private readonly DesktopServices _desktop;
    private readonly ISafetyGuard _safety;
    private readonly Func<AppSettings> _settings;
    private readonly AgentRunControl _control;
    private readonly IUserConfirmation? _confirmation;
    private readonly IInputActionObserver? _observer;

    private readonly object _cursorGate = new();
    private ScreenPoint? _lastSetCursor;
    private int _lastSeenStep;

    public ComputerToolHost(DesktopServices desktop, ISafetyGuard safety, Func<AppSettings> settings, AgentRunControl control,
        IUserConfirmation? confirmation = null, IInputActionObserver? observer = null)
    {
        _desktop = desktop ?? throw new ArgumentNullException(nameof(desktop));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _control = control ?? throw new ArgumentNullException(nameof(control));
        _confirmation = confirmation;
        _observer = observer;
    }

    /// <summary>All waits go through this so tests run instantly.</summary>
    internal Func<int, CancellationToken, Task> Delay { get; set; } = DefaultDelay;

    /// <summary>Working directory for run_command.</summary>
    internal Func<string> UserProfileDirectory { get; set; } = DefaultUserProfile;

    /// <summary>Process id treated as "DeskPilot itself". Tests may override it.</summary>
    internal int OwnProcessId { get; set; } = Environment.ProcessId;

    /// <summary>Upper bound for one UI Automation element query.</summary>
    internal TimeSpan UiTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>The coordinate mapping screenshots currently use (depends on monitor selection and size limits).</summary>
    public CoordinateMapper CurrentMapper() => CoordinateMapper.ForSettings(_desktop.Screen, _settings().Screen);

    /// <summary>One line for the system prompt, e.g. "screenshots are 1280x720 and show the primary monitor (2560x1440 physical pixels)".</summary>
    public string DescribeScreen()
    {
        var (mapper, description) = BuildMapping(_desktop.Screen, _settings().Screen);
        var source = mapper.Source;
        return $"screenshots are {mapper.ImageWidth}x{mapper.ImageHeight} and show {description} ({source.Width}x{source.Height} physical pixels)";
    }

    public async Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var tool = (name ?? "").Trim();
        var args = arguments.ValueKind == JsonValueKind.Object ? arguments : JsonArgs.EmptyObject();
        try
        {
            TrackTurn();
            var settings = _settings();
            if (!AllToolNames.Contains(tool))
                return ToolResult.Error($"Unknown tool '{name}'. Available tools: {string.Join(", ", GetTools().Select(t => t.Name))}.");
            var disabled = DisabledReason(tool, settings.Safety);
            if (disabled != null) return ToolResult.Error(disabled);

            var call = new Call(settings, _desktop.Screen);
            return tool switch
            {
                "screenshot" => await ScreenshotAsync(call, ct).ConfigureAwait(false),
                "zoom" => await ZoomAsync(call, args, ct).ConfigureAwait(false),
                "click" => await ClickAsync(call, args, ct).ConfigureAwait(false),
                "move_mouse" => await MoveMouseAsync(call, args, ct).ConfigureAwait(false),
                "drag" => await DragAsync(call, args, ct).ConfigureAwait(false),
                "scroll" => await ScrollAsync(call, args, ct).ConfigureAwait(false),
                "type_text" => await TypeTextAsync(call, args, ct).ConfigureAwait(false),
                "press_keys" => await PressKeysAsync(call, args, ct).ConfigureAwait(false),
                "wait" => await WaitAsync(call, args, ct).ConfigureAwait(false),
                "list_windows" => ListWindows(call),
                "focus_window" => await FocusWindowAsync(call, args, ct).ConfigureAwait(false),
                "launch" => await LaunchAsync(call, args, ct).ConfigureAwait(false),
                "ui_elements" => await UiElementsAsync(call, args, ct).ConfigureAwait(false),
                "get_clipboard" => GetClipboard(),
                "set_clipboard" => await SetClipboardAsync(call, args, ct).ConfigureAwait(false),
                "run_command" => await RunCommandAsync(call, args, ct).ConfigureAwait(false),
                _ => ToolResult.Error($"Unknown tool '{name}'."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{tool} failed: {ex.Message}");
        }
    }

    private static string? DisabledReason(string tool, SafetySettings safety) => tool switch
    {
        "launch" when !safety.AllowAppLaunch =>
            "The launch tool is turned off in DeskPilot settings (Safety: allow launching apps). Open apps another way, " +
            "for example press_keys \"win\", type_text the app name, then press_keys \"enter\".",
        "get_clipboard" or "set_clipboard" when !safety.AllowClipboard =>
            "Clipboard access is turned off in DeskPilot settings (Safety: allow clipboard).",
        "run_command" when !safety.AllowShellCommands =>
            "Running shell commands is turned off in DeskPilot settings (Safety: allow shell commands). Use the desktop instead, or ask the user to enable it.",
        _ => null,
    };

    // ---------------------------------------------------------------- input action pipeline

    private sealed record ActionOutcome(bool Success, string Text);

    private sealed class ActionPlan
    {
        public required ProposedAction Action { get; init; }
        /// <summary>Physical point the input goes to (null for keyboard and non-pointer actions).</summary>
        public ScreenPoint? ObserverTarget { get; init; }
        /// <summary>The observer was already told about this input (see ResolvePointerAsync).</summary>
        public bool ObserverEngaged { get; init; }
        public required Func<CancellationToken, Task<ActionOutcome>> Perform { get; init; }
        public required string DryRunText { get; init; }
        public bool ScreenshotAfter { get; init; } = true;
        public int MinSettleMs { get; init; }
        /// <summary>An extra deny-only check, e.g. the drop point of a drag.</summary>
        public ProposedAction? PreCheck { get; init; }
    }

    private async Task<ToolResult> RunActionAsync(Call call, ActionPlan plan, CancellationToken ct)
    {
        bool observing = plan.ObserverEngaged;
        try
        {
            var stop = CheckUserOverride(call);
            if (stop != null)
            {
                _control.RequestStop(stop);
                return ToolResult.Error($"STOPPED: {stop}. Do not call any more tools; end your turn now with a one-line summary of where you got to.");
            }

            if (plan.PreCheck != null)
            {
                var pre = await _safety.CheckAsync(plan.PreCheck, ct).ConfigureAwait(false);
                if (pre.Decision == SafetyDecision.Deny) return Blocked(pre.Reason);
            }

            var refusal = await ApproveAsync(plan.Action, ct).ConfigureAwait(false);
            if (refusal != null) return refusal;

            ActionOutcome outcome;
            if (call.Safety.DryRun)
            {
                outcome = new ActionOutcome(true, plan.DryRunText);
            }
            else
            {
                try
                {
                    if (!observing && _observer != null)
                    {
                        observing = true;
                        await _observer.BeforeInputAsync(plan.ObserverTarget, ct).ConfigureAwait(false);
                    }
                    ct.ThrowIfCancellationRequested();
                    outcome = await plan.Perform(ct).ConfigureAwait(false);
                }
                catch
                {
                    ReleaseAllQuietly();
                    throw;
                }
                finally
                {
                    if (observing)
                    {
                        observing = false;
                        AfterInputQuietly();
                    }
                }
            }

            if (!outcome.Success) return ToolResult.Error(outcome.Text);
            if (!plan.ScreenshotAfter || !call.Screen.ScreenshotAfterAction) return ToolResult.Ok(outcome.Text);

            if (!call.Safety.DryRun)
                await Delay(Math.Max(Math.Max(0, call.Screen.ActionSettleDelayMs), plan.MinSettleMs), ct).ConfigureAwait(false);
            return await WithScreenshotAsync(call, outcome.Text, ct).ConfigureAwait(false);
        }
        finally
        {
            if (observing) AfterInputQuietly();
        }
    }

    private static ToolResult Blocked(string reason) => ToolResult.Error($"Blocked by DeskPilot safety: {reason}");

    /// <summary>Runs the safety guard and, when needed, the user confirmation. Returns null when the action may run.</summary>
    private async Task<ToolResult?> ApproveAsync(ProposedAction action, CancellationToken ct)
    {
        var verdict = await _safety.CheckAsync(action, ct).ConfigureAwait(false);
        switch (verdict.Decision)
        {
            case SafetyDecision.Allow:
                return null;
            case SafetyDecision.Deny:
                return Blocked(verdict.Reason);
        }

        if (_control.AllowAllThisTurn) return null;
        if (_confirmation == null)
            return Blocked(
                $"this action needs the user's confirmation ({verdict.Reason.TrimEnd('.')}), but no confirmation prompt is available. " +
                "Ask the user to do this step themselves or to change the confirmation setting.");

        var choice = await _confirmation.ConfirmAsync(action, ct).ConfigureAwait(false);
        switch (choice)
        {
            case ConfirmationChoice.Deny:
                return ToolResult.Error(
                    $"The user declined this action ({action.Summary}). Do not retry it or work around it; continue another way or end your turn and ask the user.");
            case ConfirmationChoice.AllowAllThisTurn:
                _control.AllowAllThisTurn = true;
                return null;
            default:
                return null;
        }
    }

    /// <summary>Failsafe corner and "stop on user mouse move". Returns the stop reason, or null to continue.</summary>
    private string? CheckUserOverride(Call call)
    {
        var safety = call.Safety;
        if (!safety.FailsafeCorner && !safety.StopOnUserMouseMove) return null;

        var cursor = SafeCursor();
        if (cursor is not { } c) return null;
        ScreenPoint? last;
        lock (_cursorGate) last = _lastSetCursor;

        if (safety.FailsafeCorner)
        {
            var corner = PrimaryTopLeft();
            bool userThere = Near(c, corner, FailsafeRadius);
            bool weLeftItThere = last is { } l && Near(l, corner, FailsafeRadius);
            if (userThere && !weLeftItThere) return "Failsafe: mouse moved to the top-left corner";
        }

        if (safety.StopOnUserMouseMove && last is { } expected)
        {
            double dx = c.X - expected.X, dy = c.Y - expected.Y;
            if (Math.Sqrt(dx * dx + dy * dy) > UserMoveThreshold)
                return "the user moved the mouse (Stop on mouse move is on)";
        }
        return null;
    }

    private static bool Near(ScreenPoint a, ScreenPoint b, int radius) => Math.Abs(a.X - b.X) <= radius && Math.Abs(a.Y - b.Y) <= radius;

    private ScreenPoint PrimaryTopLeft()
    {
        try
        {
            var monitors = _desktop.Screen.GetMonitors();
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
            if (primary != null) return new ScreenPoint(primary.Bounds.X, primary.Bounds.Y);
        }
        catch (Exception)
        {
            // Fall back to the usual primary origin.
        }
        return new ScreenPoint(0, 0);
    }

    /// <summary>
    /// Forgets the last cursor position at the start of a new turn: between turns the user is free to use
    /// the mouse. ObservedToolHost counts every call, so a step number that did not grow means a new turn.
    /// Without that wrapper the count stays 0 and the position is kept.
    /// </summary>
    private void TrackTurn()
    {
        lock (_cursorGate)
        {
            int step = _control.Steps;
            if (step != 0 && step <= _lastSeenStep) _lastSetCursor = null;
            _lastSeenStep = step;
        }
    }

    private void SetCursor(ScreenPoint p)
    {
        lock (_cursorGate) _lastSetCursor = p;
    }

    /// <summary>
    /// Finds the window under a pointer target. DeskPilot's floating overlay is invisible in screenshots and
    /// steps aside when the observer says input is coming to its area, so when the overlay is what sits at the
    /// point, tell the observer first; the summary and the safety guard then see the real window underneath.
    /// </summary>
    private async Task<(WindowInfo? Window, bool Engaged)> ResolvePointerAsync(ScreenPoint p, CancellationToken ct)
    {
        var window = SafeWindowAt(p);
        if (window == null || window.ProcessId != OwnProcessId || _observer == null) return (window, false);
        try
        {
            await _observer.BeforeInputAsync(p, ct).ConfigureAwait(false);
        }
        catch
        {
            AfterInputQuietly();
            throw;
        }
        return (SafeWindowAt(p), true);
    }

    private void ReleaseAllQuietly()
    {
        try { _desktop.Input.ReleaseAll(); }
        catch (Exception) { /* nothing more we can do */ }
    }

    private void AfterInputQuietly()
    {
        try { _observer?.AfterInput(); }
        catch (Exception) { /* the observer is cosmetic */ }
    }

    // ---------------------------------------------------------------- screenshots

    private async Task<ToolResult> WithScreenshotAsync(Call call, string text, CancellationToken ct)
    {
        try
        {
            var (shotText, image) = await CaptureAsync(call, ct).ConfigureAwait(false);
            return image == null ? ToolResult.Ok(text + "\n" + shotText) : ToolResult.Ok(text + "\n" + shotText, image);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Ok($"{text}\n(The screenshot after the action failed: {ex.Message}. Call screenshot to see the screen.)");
        }
    }

    /// <summary>A screenshot (image + text line), or a text description for models without vision.</summary>
    private async Task<(string Text, ToolImage? Image)> CaptureAsync(Call call, CancellationToken ct)
    {
        if (!call.Vision) return (await DescribeScreenAsTextAsync(call, ct).ConfigureAwait(false), null);

        var screen = call.Screen;
        var mapper = call.Mapper;
        // A grid labelled in image pixels would contradict 0-1000 coordinates, so it is only drawn in pixel mode.
        int grid = mapper.Mode == CoordinateMode.ScreenshotPixels ? Math.Max(0, screen.GridSpacing) : 0;
        var request = new CaptureRequest(mapper.Source, mapper.ImageWidth, mapper.ImageHeight, ParseFormat(screen.Format),
            Math.Clamp(screen.JpegQuality, 1, 100), screen.DrawCursor, grid);
        var frame = await CaptureFrameAsync(request, ct).ConfigureAwait(false);
        var image = ToolImage.FromBytes(frame.Data, frame.MediaType, frame.Width, frame.Height);
        return (StateLine(call, "Screenshot"), image);
    }

    /// <summary>Captures with DeskPilot's own UI hidden where the platform cannot exclude it from capture.</summary>
    private async Task<CapturedFrame> CaptureFrameAsync(CaptureRequest request, CancellationToken ct)
    {
        if (_observer is not ICaptureObserver co) return _desktop.Screen.Capture(request);
        await co.BeforeCaptureAsync(ct).ConfigureAwait(false);
        try { return _desktop.Screen.Capture(request); }
        finally { co.AfterCapture(); }
    }

    /// <summary>"Screenshot 1280x720 of the primary monitor (2560x1440 px). Active window: 'Title' (process). Mouse at (x, y)."</summary>
    private string StateLine(Call call, string what)
    {
        var mapper = call.Mapper;
        var src = mapper.Source;
        var line = $"{what} {mapper.ImageWidth}x{mapper.ImageHeight} of {call.SourceDescription} ({src.Width}x{src.Height} px)";
        if (mapper.Mode == CoordinateMode.Normalized1000) line += ", coordinates 0-1000 on both axes";
        line += ". ";

        var fg = SafeForeground();
        line += fg != null ? $"Active window: {Describe(fg)}. " : "Active window: none. ";

        var cursor = SafeCursor();
        if (cursor is { } c)
        {
            if (src.Contains(c))
            {
                var (mx, my) = mapper.FromScreen(c);
                line += $"Mouse at ({N(mx)}, {N(my)}).";
            }
            else
            {
                line += "Mouse is outside the captured area.";
            }
        }
        return line.TrimEnd();
    }

    private async Task<string> DescribeScreenAsTextAsync(Call call, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("Text description of the screen (this model cannot see images). ");
        sb.Append(StateLine(call, "Coordinate space")).Append('\n');

        var windows = SafeListWindows().Where(w => w.ProcessId != OwnProcessId).ToList();
        sb.Append("Open windows, front to back:").Append('\n');
        if (windows.Count == 0) sb.Append("(none)").Append('\n');
        foreach (var w in windows.Take(15)) sb.Append(WindowLine(call, w)).Append('\n');
        if (windows.Count > 15) sb.Append($"(and {windows.Count - 15} more; use list_windows)").Append('\n');

        var fg = SafeForeground();
        if (fg == null)
        {
            sb.Append("No active window.");
        }
        else if (fg.ProcessId == OwnProcessId)
        {
            sb.Append("DeskPilot's own window is in front; use focus_window to bring the app you need forward.");
        }
        else
        {
            sb.Append($"UI elements of the active window {Describe(fg)}, centers in {SpaceName(call)}:").Append('\n');
            try
            {
                var elements = await GetElementsAsync(fg.Handle, 80, ct).ConfigureAwait(false);
                var lines = ElementLines(call, elements, null, 60, out _);
                if (lines.Count == 0) sb.Append("(no interactive UI elements found; the app may not expose UI Automation)");
                else sb.Append(string.Join("\n", lines));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                sb.Append($"(UI elements unavailable: {ex.Message})");
            }
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int max, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(UiTimeout);
        try
        {
            return await _desktop.Ui.GetElementsAsync(window, max, cts.Token).WaitAsync(UiTimeout, ct).ConfigureAwait(false)
                   ?? Array.Empty<UiElementInfo>();
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"UI Automation did not answer within {UiTimeout.TotalSeconds:0.#} s.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"UI Automation did not answer within {UiTimeout.TotalSeconds:0.#} s.");
        }
    }

    // ---------------------------------------------------------------- formatting helpers

    private string WindowLine(Call call, WindowInfo w)
    {
        var flags = new List<string>();
        if (w.IsForeground) flags.Add("active");
        if (w.IsMinimized) flags.Add("minimized");
        if (w.IsUacPrompt) flags.Add("UAC prompt");
        if (w.IsElevated) flags.Add(call.Safety.AllowAdmin ? "elevated" : "elevated, off limits while Administrator mode is off");
        if (SafetyGuard.IsBlocked(w, call.Safety.BlockedProcesses ?? new List<string>())) flags.Add("blocked by the user");
        if (!w.IsMinimized && w.Bounds.Intersect(call.Mapper.Source).IsEmpty) flags.Add("on another monitor, not in the screenshot");

        var line = $"- '{Clip(TitleOf(w), 80)}' [{w.ProcessName}]";
        if (!w.IsMinimized)
        {
            var (x, y, width, height) = call.Mapper.FromScreen(w.Bounds);
            line += $" at ({N(x)}, {N(y)}) {N(width)}x{N(height)}";
        }
        if (flags.Count > 0) line += $" ({string.Join(", ", flags)})";
        return line;
    }

    /// <summary>Formats elements whose center lies in the captured area, numbered from 1.</summary>
    private static List<string> ElementLines(Call call, IReadOnlyList<UiElementInfo> elements, string? filter, int max, out int matched)
    {
        var lines = new List<string>();
        matched = 0;
        var mapper = call.Mapper;
        foreach (var e in elements)
        {
            if (e == null || e.Bounds.IsEmpty) continue;
            var center = e.Bounds.Center;
            if (!mapper.Source.Contains(center)) continue;
            if (!string.IsNullOrEmpty(filter) &&
                !(e.Name ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                !(e.ControlType ?? "").Contains(filter, StringComparison.OrdinalIgnoreCase))
                continue;

            matched++;
            if (lines.Count >= max) continue;
            var (cx, cy) = mapper.FromScreen(center);
            var (_, _, w, h) = mapper.FromScreen(e.Bounds);
            var name = string.IsNullOrEmpty(e.Name) && !string.IsNullOrEmpty(e.AutomationId) ? $"'' (id {e.AutomationId})" : $"'{Clip(OneLine(e.Name ?? ""), 80)}'";
            var line = $"[{lines.Count + 1}] {e.ControlType} {name} at ({N(cx)}, {N(cy)}) size {N(Math.Max(1, w))}x{N(Math.Max(1, h))}";
            if (!e.IsEnabled) line += " (disabled)";
            if (!string.IsNullOrEmpty(e.Value)) line += $" value \"{Clip(OneLine(e.Value), 60)}\"";
            lines.Add(line);
        }
        return lines;
    }

    private static string SpaceName(Call call) =>
        call.Mapper.Mode == CoordinateMode.Normalized1000 ? "0-1000 coordinates" : "screenshot pixels";

    /// <summary>"screenshot pixels (x 0-1279, y 0-719)" or "0-1000 coordinates (x and y 0-1000)".</summary>
    private static string SpaceRange(Call call)
    {
        var m = call.Mapper;
        return m.Mode == CoordinateMode.Normalized1000
            ? "0-1000 coordinates (x and y from 0 to 1000)"
            : $"screenshot pixels (x 0-{m.ImageWidth - 1}, y 0-{m.ImageHeight - 1})";
    }

    internal static string Describe(WindowInfo w) => $"'{Clip(TitleOf(w), 60)}' ({w.ProcessName})";

    private static string TitleOf(WindowInfo w) => string.IsNullOrWhiteSpace(w.Title) ? "(untitled)" : w.Title;

    private static string On(WindowInfo? w) => w == null ? "" : $" on {Describe(w)}";

    /// <summary>Rounds for display, avoiding "-0".</summary>
    internal static string N(double v) =>
        ((long)Math.Round(double.IsFinite(v) ? v : 0, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture);

    private static string F(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    internal static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    /// <summary>Single-line form for summaries: line breaks shown as \n.</summary>
    private static string OneLine(string s) => s.Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n").Replace('\t', ' ');

    private static string LowerFirst(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static ImageFormatKind ParseFormat(string? format) =>
        (format ?? "").Trim().ToLowerInvariant() switch
        {
            "png" => ImageFormatKind.Png,
            _ => ImageFormatKind.Jpeg,
        };

    // ---------------------------------------------------------------- safe desktop reads

    private ScreenPoint? SafeCursor()
    {
        try { return _desktop.Input.GetCursorPosition(); }
        catch (Exception) { return null; }
    }

    private WindowInfo? SafeForeground()
    {
        try { return _desktop.Windows.GetForegroundWindow(); }
        catch (Exception) { return null; }
    }

    private WindowInfo? SafeWindowAt(ScreenPoint p)
    {
        try { return _desktop.Windows.GetWindowAt(p.X, p.Y); }
        catch (Exception) { return null; }
    }

    private IReadOnlyList<WindowInfo> SafeListWindows()
    {
        try { return _desktop.Windows.ListWindows() ?? Array.Empty<WindowInfo>(); }
        catch (Exception) { return Array.Empty<WindowInfo>(); }
    }

    /// <summary>The mapper and a phrase for what it shows, from one monitor query so both agree.</summary>
    private static (CoordinateMapper Mapper, string Description) BuildMapping(IScreenCapture screen, ScreenSettings settings)
    {
        var (source, description) = CoordinateMapper.ResolveSourceDescribed(screen, settings);
        var (w, h) = CoordinateMapper.FitWithin(source.Width, source.Height, settings.MaxImageWidth, settings.MaxImageHeight);
        return (new CoordinateMapper(source, w, h, settings.Coordinates), description);
    }

    private static Task DefaultDelay(int ms, CancellationToken ct) => ms <= 0 ? Task.CompletedTask : Task.Delay(ms, ct);

    private static string DefaultUserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Path.GetTempPath() : home;
    }

    /// <summary>Per-call snapshot of settings plus the coordinate mapping (computed on first use).</summary>
    private sealed class Call
    {
        private readonly Lazy<(CoordinateMapper Mapper, string Description)> _mapping;

        public Call(AppSettings settings, IScreenCapture screen)
        {
            Settings = settings;
            _mapping = new Lazy<(CoordinateMapper, string)>(() => BuildMapping(screen, settings.Screen));
        }

        public AppSettings Settings { get; }
        public SafetySettings Safety => Settings.Safety;
        public ScreenSettings Screen => Settings.Screen;
        public CoordinateMapper Mapper => _mapping.Value.Mapper;
        public string SourceDescription => _mapping.Value.Description;
        public bool Vision => Settings.ActiveProfile?.SupportsVision ?? true;
    }
}

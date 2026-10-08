using System.Globalization;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Safety;

namespace DeskPilot.Core.Tools;

public sealed partial class ComputerToolHost
{
    private const int KeyRepeatPauseMs = 40;
    private const int TypeChunkLength = 200;
    private const int ClipboardReadLimit = 20000;
    private const int ClipboardWriteLimit = 100_000;
    private const int MaxElements = 200;
    private const int DefaultElements = 80;

    // ---------------------------------------------------------------- observation tools

    private async Task<ToolResult> ScreenshotAsync(Call call, CancellationToken ct)
    {
        var (text, image) = await CaptureAsync(call, ct).ConfigureAwait(false);
        return image == null ? ToolResult.Ok(text) : ToolResult.Ok(text, image);
    }

    private Task<ToolResult> ZoomAsync(Call call, JsonElement args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string usage = $"zoom takes x, y, width and height (numbers): the region's top-left corner and size in {SpaceRange(call)}.";
        if (!call.Vision)
            return Task.FromResult(ToolResult.Error("zoom returns an image, and this model cannot see images. Use ui_elements to read names and positions instead."));

        var x = JsonArgs.GetDouble(args, "x");
        var y = JsonArgs.GetDouble(args, "y");
        var w = JsonArgs.GetDouble(args, "width");
        var h = JsonArgs.GetDouble(args, "height");
        if (x is not { } rx || y is not { } ry || w is not { } rw || h is not { } rh ||
            !double.IsFinite(rx) || !double.IsFinite(ry) || !double.IsFinite(rw) || !double.IsFinite(rh))
            return Task.FromResult(Usage("Missing or invalid region.", usage));
        if (rw <= 0 || rh <= 0)
            return Task.FromResult(Usage("'width' and 'height' must be greater than 0.", usage));

        var mapper = call.Mapper;
        if (rx < 0 || ry < 0 || rx >= mapper.ModelWidth || ry >= mapper.ModelHeight)
            return Task.FromResult(Usage($"The region's top-left corner ({N(rx)}, {N(ry)}) is outside the screen.", usage));

        bool clipped = rx + rw > mapper.ModelWidth + 0.5 || ry + rh > mapper.ModelHeight + 0.5;
        var physical = mapper.ToScreenRect(rx, ry, rw, rh);
        if (physical.IsEmpty || physical.Width < 2 || physical.Height < 2)
            return Task.FromResult(Usage("The region is too small to zoom into; make it a few pixels wide and high at least.", usage));

        var screen = call.Screen;
        var (tw, th) = CoordinateMapper.FitWithin(physical.Width, physical.Height, screen.MaxImageWidth, screen.MaxImageHeight);
        // No grid: its labels would be zoom-image pixels, which are not valid click coordinates.
        var frame = _desktop.Screen.Capture(new CaptureRequest(physical, tw, th, ParseFormat(screen.Format),
            Math.Clamp(screen.JpegQuality, 1, 100), screen.DrawCursor, 0));
        var image = ToolImage.FromBytes(frame.Data, frame.MediaType, frame.Width, frame.Height);

        var (mx, my, mw, mh) = mapper.FromScreen(physical);
        double shotPixelsWide = physical.Width * (double)mapper.ImageWidth / Math.Max(1, mapper.Source.Width);
        double factor = frame.Width / Math.Max(1e-9, shotPixelsWide);
        double perU = mw / Math.Max(1, frame.Width), perV = mh / Math.Max(1, frame.Height);
        var text =
            $"Zoom of the region ({N(mx)}, {N(my)}) {N(mw)}x{N(mh)} in {SpaceName(call)}{(clipped ? " (clipped to the screen)" : "")}, " +
            $"shown as a {frame.Width}x{frame.Height} image ({F(factor)}x the detail of the screenshot). " +
            $"Clicks must still use full-screenshot coordinates, not zoom-image pixels: a point (u, v) in this image is at " +
            $"({N(mx)} + u * {P(perU)}, {N(my)} + v * {P(perV)}) in {SpaceName(call)}.";
        return Task.FromResult(ToolResult.Ok(text, image));
    }

    private async Task<ToolResult> WaitAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "wait takes seconds (a number from 0.1 to 30).";
        var seconds = JsonArgs.GetDouble(args, "seconds");
        if (seconds is not { } s || !double.IsFinite(s)) return Usage("'seconds' is missing.", usage);
        if (s < 0.1 || s > 30) return Usage($"'seconds' must be between 0.1 and 30, not {F(s)}.", usage);

        await Delay((int)Math.Round(s * 1000, MidpointRounding.AwayFromZero), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var (text, image) = await CaptureAsync(call, ct).ConfigureAwait(false);
        var result = $"Waited {F(s)} s.\n{text}";
        return image == null ? ToolResult.Ok(result) : ToolResult.Ok(result, image);
    }

    private ToolResult ListWindows(Call call)
    {
        var windows = (_desktop.Windows.ListWindows() ?? Array.Empty<WindowInfo>())
            .Where(w => w != null && w.ProcessId != OwnProcessId)
            .ToList();
        if (windows.Count == 0) return ToolResult.Ok("No open windows with a title were found.");

        var sb = new StringBuilder();
        sb.Append($"{windows.Count} open windows, front to back (position = top-left corner; position and size in {SpaceName(call)}):").Append('\n');
        foreach (var w in windows) sb.Append(WindowLine(call, w)).Append('\n');
        return ToolResult.Ok(sb.ToString().TrimEnd());
    }

    private async Task<ToolResult> UiElementsAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "ui_elements takes optional window_title (part of a title, or a process name), filter (text) and max (1-200).";
        int max = DefaultElements;
        if (JsonArgs.Has(args, "max"))
        {
            var m = JsonArgs.GetDouble(args, "max");
            if (m is not { } md || !double.IsFinite(md)) return Usage("'max' must be a number from 1 to 200.", usage);
            max = (int)Math.Clamp(Math.Round(md), 1, MaxElements);
        }
        var filter = JsonArgs.GetString(args, "filter")?.Trim();
        if (string.IsNullOrEmpty(filter)) filter = null;
        var title = JsonArgs.GetString(args, "window_title")?.Trim();

        WindowInfo? window;
        if (!string.IsNullOrEmpty(title))
        {
            window = FindWindow(_desktop.Windows.ListWindows() ?? Array.Empty<WindowInfo>(), title, OwnProcessId);
            if (window == null) return Usage($"No open window matches '{title}'. Use list_windows to see the open windows.", usage);
        }
        else
        {
            window = _desktop.Windows.GetForegroundWindow();
            if (window == null) return Usage("There is no active window; pass window_title.", usage);
        }

        if (window.ProcessId == OwnProcessId)
            return ToolResult.Error("That is DeskPilot's own window, which DeskPilot does not inspect. Bring the app you need to the front (focus_window) or pass window_title.");
        if (window.IsMinimized)
            return ToolResult.Error($"{Describe(window)} is minimized; call focus_window first.");

        var elements = await GetElementsAsync(window.Handle, filter == null ? max : MaxElements, ct).ConfigureAwait(false);
        var lines = ElementLines(call, elements, filter, max, out int matched);

        var sb = new StringBuilder();
        sb.Append($"UI elements of {Describe(window)}{(filter != null ? $" matching '{filter}'" : "")}, centers and sizes in {SpaceName(call)}:").Append('\n');
        if (lines.Count == 0)
        {
            sb.Append(elements.Count == 0
                ? "(no interactive UI elements found; the app may not expose UI Automation, so use the screenshot and zoom instead)"
                : filter != null
                    ? "(no element matches the filter)"
                    : "(none of its elements are inside the captured screen area)");
        }
        else
        {
            sb.Append(string.Join("\n", lines));
            if (matched > lines.Count) sb.Append($"\n({matched - lines.Count} more not shown; raise max or use filter.)");
        }
        return ToolResult.Ok(sb.ToString());
    }

    private ToolResult GetClipboard()
    {
        var text = _desktop.Clipboard.GetText();
        if (string.IsNullOrEmpty(text)) return ToolResult.Ok("The clipboard is empty or does not hold text.");
        return ToolResult.Ok($"Clipboard text ({text.Length} characters):\n{TruncateMiddle(text, ClipboardReadLimit)}");
    }

    // ---------------------------------------------------------------- pointer actions

    private async Task<ToolResult> ClickAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "click takes x and y (numbers), and optionally button (left, right or middle), clicks (1-3) and modifiers (e.g. [\"ctrl\"]).";
        var problem = ReadPoint(call, args, "x", "y", out var x, out var y);
        if (problem != null) return Usage(problem, usage);
        if (!TryReadButton(args, out var button, out problem)) return Usage(problem!, usage);
        int clicks = 1;
        if (JsonArgs.Has(args, "clicks"))
        {
            var c = JsonArgs.GetDouble(args, "clicks");
            if (c is not { } cd || cd != Math.Floor(cd) || cd < 1 || cd > 3)
                return Usage("'clicks' must be 1, 2 (double-click) or 3 (triple-click).", usage);
            clicks = (int)cd;
        }
        if (!TryReadModifiers(args, out var modifiers, out problem)) return Usage(problem!, usage);

        var p = call.Mapper.ToScreen(x, y);
        var (window, engaged) = await ResolvePointerAsync(p, ct).ConfigureAwait(false);
        var with = modifiers.Count > 0 ? $" with {string.Join("+", modifiers)}" : "";
        var what = $"{ButtonName(button)}{with} at ({N(x)}, {N(y)}){On(window)}";
        var summary = $"{ClickVerb(clicks)} {what}";

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("click", summary, ActionRisk.Medium, Target: p),
            ObserverTarget = p,
            ObserverEngaged = engaged,
            DryRunText = DryRunText(summary),
            Perform = async c =>
            {
                _desktop.Input.MoveMouse(p.X, p.Y);
                SetCursor(p);
                await Delay(ClickPauseMs, c).ConfigureAwait(false);
                WithModifiers(modifiers, () => _desktop.Input.Click(button, clicks));
                return new ActionOutcome(true, $"{ClickVerbPast(clicks)} {what}.");
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> MoveMouseAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "move_mouse takes x and y (numbers).";
        var problem = ReadPoint(call, args, "x", "y", out var x, out var y);
        if (problem != null) return Usage(problem, usage);

        var p = call.Mapper.ToScreen(x, y);
        var (window, engaged) = await ResolvePointerAsync(p, ct).ConfigureAwait(false);
        var where = $"({N(x)}, {N(y)}){On(window)}";
        var summary = $"Move the mouse to {where}";

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("move_mouse", summary, ActionRisk.Low, Target: p),
            ObserverTarget = p,
            ObserverEngaged = engaged,
            DryRunText = DryRunText(summary),
            Perform = c =>
            {
                _desktop.Input.MoveMouse(p.X, p.Y);
                SetCursor(p);
                return Task.FromResult(new ActionOutcome(true, $"Moved the mouse to {where}."));
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> DragAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "drag takes from_x, from_y, to_x and to_y (numbers), and optionally button (left, right or middle).";
        var problem = ReadPoint(call, args, "from_x", "from_y", out var fx, out var fy);
        if (problem != null) return Usage(problem, usage);
        problem = ReadPoint(call, args, "to_x", "to_y", out var tx, out var ty);
        if (problem != null) return Usage(problem, usage);
        if (!TryReadButton(args, out var button, out problem)) return Usage(problem!, usage);

        var from = call.Mapper.ToScreen(fx, fy);
        var to = call.Mapper.ToScreen(tx, ty);
        var (fromWindow, engaged) = await ResolvePointerAsync(from, ct).ConfigureAwait(false);
        var observerTarget = from;
        WindowInfo? toWindow;
        if (engaged)
        {
            toWindow = SafeWindowAt(to);
        }
        else
        {
            (toWindow, engaged) = await ResolvePointerAsync(to, ct).ConfigureAwait(false);
            if (engaged) observerTarget = to;
        }

        var what = $"{ButtonName(button)} from ({N(fx)}, {N(fy)}){On(fromWindow)} to ({N(tx)}, {N(ty)}){On(toWindow)}";
        var summary = $"Drag {what}";

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("drag", summary, ActionRisk.Medium, Target: from),
            // The drop point must pass the same window rules; risk None so it never asks twice.
            PreCheck = new ProposedAction("drag", summary, ActionRisk.None, Target: to),
            ObserverTarget = observerTarget,
            ObserverEngaged = engaged,
            DryRunText = DryRunText(summary),
            Perform = async c =>
            {
                _desktop.Input.MoveMouse(from.X, from.Y);
                SetCursor(from);
                await Delay(ClickPauseMs, c).ConfigureAwait(false);
                _desktop.Input.MouseDown(button);
                try
                {
                    await Delay(ClickPauseMs, c).ConfigureAwait(false);
                    await Task.Run(() => _desktop.Input.MoveMouseSmooth(to.X, to.Y, DragDurationMs), c).ConfigureAwait(false);
                    SetCursor(to);
                }
                finally
                {
                    _desktop.Input.MouseUp(button);
                }
                return new ActionOutcome(true, $"Dragged {what}.");
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> ScrollAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "scroll takes direction (up, down, left or right), and optionally amount (1-30 notches, default 3) and x and y (numbers, together) to scroll at a point.";
        var rawDirection = JsonArgs.GetString(args, "direction")?.Trim();
        var direction = rawDirection?.ToLowerInvariant();
        if (direction is not ("up" or "down" or "left" or "right"))
            return Usage(string.IsNullOrEmpty(rawDirection) ? "'direction' is missing." : $"'direction' must be up, down, left or right, not '{rawDirection}'.", usage);

        int amount = 3;
        if (JsonArgs.Has(args, "amount"))
        {
            var a = JsonArgs.GetDouble(args, "amount");
            if (a is not { } ad || ad != Math.Floor(ad) || ad < 1 || ad > 30)
                return Usage("'amount' must be a whole number from 1 to 30.", usage);
            amount = (int)ad;
        }

        bool hasX = JsonArgs.Has(args, "x"), hasY = JsonArgs.Has(args, "y");
        if (hasX != hasY) return Usage("Give both x and y, or neither.", usage);
        ScreenPoint? at = null;
        double x = 0, y = 0;
        if (hasX)
        {
            var problem = ReadPoint(call, args, "x", "y", out x, out y);
            if (problem != null) return Usage(problem, usage);
            at = call.Mapper.ToScreen(x, y);
        }

        WindowInfo? window = null;
        bool engaged = false;
        ScreenPoint? target = at;
        if (at is { } point)
        {
            (window, engaged) = await ResolvePointerAsync(point, ct).ConfigureAwait(false);
        }
        else
        {
            // Without a point the wheel goes to whatever is under the cursor, so that is what the guard judges.
            target = SafeCursor();
            if (target is { } cursor) window = SafeWindowAt(cursor);
        }

        var what = $"{direction} {amount} {(amount == 1 ? "notch" : "notches")} " +
                   (at != null ? $"at ({N(x)}, {N(y)})" : "at the mouse position") + On(window);
        var summary = $"Scroll {what}";
        int dx = direction switch { "left" => -amount, "right" => amount, _ => 0 };
        int dy = direction switch { "up" => -amount, "down" => amount, _ => 0 };

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("scroll", summary, ActionRisk.Low, Target: target),
            ObserverTarget = target,
            ObserverEngaged = engaged,
            DryRunText = DryRunText(summary),
            Perform = async c =>
            {
                if (at is { } p)
                {
                    _desktop.Input.MoveMouse(p.X, p.Y);
                    SetCursor(p);
                    await Delay(ClickPauseMs, c).ConfigureAwait(false);
                }
                _desktop.Input.Scroll(dx, dy);
                return new ActionOutcome(true, $"Scrolled {what}.");
            },
        }, ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- keyboard actions

    private async Task<ToolResult> TypeTextAsync(Call call, JsonElement args, CancellationToken ct)
    {
        string usage = $"type_text takes text (a string of at most {MaxTypeTextLength} characters; \\n presses Enter) and optionally press_enter (true or false).";
        if (!JsonArgs.Has(args, "text")) return Usage("'text' is missing.", usage);
        var text = JsonArgs.GetString(args, "text") ?? "";
        bool pressEnter = JsonArgs.GetBool(args, "press_enter") ?? false;
        if (text.Length > MaxTypeTextLength)
            return Usage($"'text' has {text.Length} characters, more than the {MaxTypeTextLength} allowed per call. Split it into several type_text calls.", usage);
        if (text.Length == 0 && !pressEnter) return Usage("'text' is empty.", usage);

        var foreground = SafeForeground();
        var into = foreground != null ? Describe(foreground) : "the active window";
        // A typed newline is an Enter press, which DESIGN rates High (it submits forms and sends messages).
        var risk = pressEnter || text.IndexOfAny(new[] { '\n', '\r' }) >= 0 ? ActionRisk.High : ActionRisk.Medium;
        var summary = text.Length > 0
            ? $"Type \"{Clip(OneLine(text), 80)}\" ({text.Length} characters) into {into}{(pressEnter ? ", then press Enter" : "")}"
            : $"Press Enter in {into}";
        int typingDelay = Math.Max(0, call.Screen.TypingDelayMs);

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("type_text", summary, risk, Text: text),
            DryRunText = DryRunText(summary),
            Perform = async c =>
            {
                if (text.Length > 0) await TypeChunkedAsync(text, typingDelay, c).ConfigureAwait(false);
                if (pressEnter)
                {
                    if (text.Length > 0) await Delay(ClickPauseMs, c).ConfigureAwait(false);
                    _desktop.Input.PressCombo(KeyCombo.Parse("enter"));
                }
                return new ActionOutcome(true, text.Length > 0
                    ? $"Typed {text.Length} characters into {into}{(pressEnter ? " and pressed Enter" : "")}."
                    : $"Pressed Enter in {into}.");
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Types in chunks so a stop request interrupts long text, without splitting surrogate pairs or CR LF.</summary>
    private async Task TypeChunkedAsync(string text, int delayMsPerChar, CancellationToken ct)
    {
        int i = 0;
        while (i < text.Length)
        {
            ct.ThrowIfCancellationRequested();
            int len = Math.Min(TypeChunkLength, text.Length - i);
            if (i + len < text.Length)
            {
                if (char.IsHighSurrogate(text[i + len - 1])) len--;
                else if (text[i + len - 1] == '\r' && text[i + len] == '\n') len--;
            }
            var part = text.Substring(i, len);
            await Task.Run(() => _desktop.Input.TypeText(part, delayMsPerChar), ct).ConfigureAwait(false);
            i += len;
        }
    }

    private async Task<ToolResult> PressKeysAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "press_keys takes keys (one combination such as \"ctrl+s\", \"enter\" or \"alt+tab\") and optionally repeat (1-50).";
        string? keys = args.TryGetProperty("keys", out var k) && k.ValueKind == JsonValueKind.Array
            ? string.Join("+", JsonArgs.GetStringList(args, "keys"))
            : JsonArgs.GetString(args, "keys");
        if (string.IsNullOrWhiteSpace(keys)) return Usage("'keys' is missing.", usage);
        if (!KeyCombo.TryParse(keys, out var combo, out var error))
            return Usage($"Invalid keys '{keys}': {error}.", usage);

        int repeat = 1;
        if (JsonArgs.Has(args, "repeat"))
        {
            var r = JsonArgs.GetDouble(args, "repeat");
            if (r is not { } rd || rd != Math.Floor(rd) || rd < 1 || rd > 50)
                return Usage("'repeat' must be a whole number from 1 to 50.", usage);
            repeat = (int)rd;
        }

        var foreground = SafeForeground();
        var into = foreground != null ? Describe(foreground) : "the active window";
        var times = repeat > 1 ? $" x{repeat}" : "";
        var summary = $"Press {combo}{times} in {into}";

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("press_keys", summary, IsHighRiskCombo(combo) ? ActionRisk.High : ActionRisk.Medium, Keys: combo),
            DryRunText = DryRunText(summary),
            Perform = async c =>
            {
                for (int i = 0; i < repeat; i++)
                {
                    c.ThrowIfCancellationRequested();
                    _desktop.Input.PressCombo(combo);
                    if (i < repeat - 1) await Delay(KeyRepeatPauseMs, c).ConfigureAwait(false);
                }
                return new ActionOutcome(true, $"Pressed {combo}{times} in {into}.");
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>Enter, Delete, Alt+F4, Ctrl+W, Ctrl+Q, Win+L and Ctrl+Alt+Delete are High risk (docs/DESIGN.md).</summary>
    internal static bool IsHighRiskCombo(KeyCombo combo) => combo.Key switch
    {
        "enter" or "delete" => true,
        "f4" => combo.Has("alt"),
        "w" or "q" => combo.Has("ctrl"),
        "l" => combo.Has("win"),
        _ => false,
    };

    // ---------------------------------------------------------------- windows, apps, clipboard, shell

    private async Task<ToolResult> FocusWindowAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "focus_window takes title (part of a window title, case-insensitive, or a process name).";
        var title = JsonArgs.GetString(args, "title")?.Trim();
        if (string.IsNullOrEmpty(title)) return Usage("'title' is missing.", usage);

        var windows = _desktop.Windows.ListWindows() ?? Array.Empty<WindowInfo>();
        var window = FindWindow(windows, title, OwnProcessId);
        if (window == null)
        {
            var open = windows.Where(w => w != null && w.ProcessId != OwnProcessId).Take(12).Select(Describe).ToList();
            return ToolResult.Error($"No open window matches '{title}'. " +
                                    (open.Count > 0 ? $"Open windows: {string.Join(", ", open)}. " : "") +
                                    "Use list_windows for details.");
        }

        var summary = $"Focus window {Describe(window)}";
        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("focus_window", summary, ActionRisk.Medium, Text: window.Title).WithWindow(window),
            DryRunText = DryRunText(summary),
            Perform = c =>
            {
                bool ok = _desktop.Windows.FocusWindow(window.Handle);
                return Task.FromResult(ok
                    ? new ActionOutcome(true, $"Focused {Describe(window)}.")
                    : new ActionOutcome(false, $"Windows did not bring {Describe(window)} to the front. Try clicking it on the taskbar, or alt+tab."));
            },
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Title match, front-most first: exact, then starts-with, then contains (all case-insensitive), then the
    /// process name. DeskPilot's own windows sort last so they only match when nothing else does.
    /// </summary>
    internal static WindowInfo? FindWindow(IEnumerable<WindowInfo> windows, string query, int ownProcessId)
    {
        var q = query.Trim();
        if (q.Length == 0) return null;
        var list = windows.Where(w => w != null).OrderBy(w => w.ProcessId == ownProcessId ? 1 : 0).ToList();
        var process = q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? q[..^4] : q;
        return list.FirstOrDefault(w => string.Equals((w.Title ?? "").Trim(), q, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(w => (w.Title ?? "").TrimStart().StartsWith(q, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(w => (w.Title ?? "").Contains(q, StringComparison.OrdinalIgnoreCase))
               ?? list.FirstOrDefault(w => string.Equals(w.ProcessName, process, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<ToolResult> LaunchAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "launch takes target (an app name, program, full path, folder or URL) and optionally arguments.";
        var target = JsonArgs.GetString(args, "target")?.Trim();
        if (string.IsNullOrEmpty(target)) return Usage("'target' is missing.", usage);
        if (target.Length > 2048) return Usage("'target' is too long.", usage);
        var arguments = JsonArgs.GetString(args, "arguments")?.Trim();
        if (string.IsNullOrEmpty(arguments)) arguments = null;

        var summary = $"Open '{Clip(target, 120)}'{(arguments != null ? $" with arguments '{Clip(arguments, 120)}'" : "")}";
        bool allowElevation = call.Safety.AllowAdmin;

        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("launch", summary, ActionRisk.High, LaunchTarget: target, Command: arguments),
            DryRunText = DryRunText(summary),
            MinSettleMs = LaunchMinSettleMs,
            Perform = async c =>
            {
                var result = await Task.Run(() => _desktop.Launcher.Launch(target, arguments, allowElevation), c).ConfigureAwait(false);
                if (result == null || !result.Success)
                    return new ActionOutcome(false, $"Could not open '{target}': {result?.Message ?? "no result"}");
                var pid = result.ProcessId is { } id ? $" (process id {id})" : "";
                var message = string.IsNullOrWhiteSpace(result.Message) ? "" : " " + result.Message.Trim();
                return new ActionOutcome(true, $"Opened '{target}'{pid}.{message}");
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> SetClipboardAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "set_clipboard takes text (a string).";
        if (!JsonArgs.Has(args, "text")) return Usage("'text' is missing.", usage);
        var text = JsonArgs.GetString(args, "text") ?? "";
        if (text.Length > ClipboardWriteLimit) return Usage($"'text' is too long ({text.Length} characters; at most {ClipboardWriteLimit}).", usage);

        var summary = $"Set the clipboard to \"{Clip(OneLine(text), 80)}\" ({text.Length} characters)";
        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("set_clipboard", summary, ActionRisk.Medium, Text: text),
            DryRunText = DryRunText(summary),
            // DESIGN lists the tools that return a screenshot; the clipboard does not change the screen.
            ScreenshotAfter = false,
            Perform = c =>
            {
                _desktop.Clipboard.SetText(text);
                return Task.FromResult(new ActionOutcome(true, $"The clipboard now holds {text.Length} characters of text."));
            },
        }, ct).ConfigureAwait(false);
    }

    private async Task<ToolResult> RunCommandAsync(Call call, JsonElement args, CancellationToken ct)
    {
        const string usage = "run_command takes command (a string), and optionally shell (powershell or cmd) and timeout_seconds (1-600, default 60).";
        var command = JsonArgs.GetString(args, "command");
        if (string.IsNullOrWhiteSpace(command)) return Usage("'command' is missing.", usage);

        var rawShell = JsonArgs.GetString(args, "shell")?.Trim();
        string? shell = (rawShell ?? "").ToLowerInvariant() switch
        {
            "" or "powershell" or "powershell.exe" or "pwsh" or "ps" => "powershell",
            "cmd" or "cmd.exe" => "cmd",
            _ => null,
        };
        if (shell == null) return Usage($"'shell' must be powershell or cmd, not '{rawShell}'.", usage);

        int timeoutSeconds = 60;
        if (JsonArgs.Has(args, "timeout_seconds"))
        {
            var t = JsonArgs.GetDouble(args, "timeout_seconds");
            if (t is not { } td || !double.IsFinite(td) || td < 1 || td > 600)
                return Usage("'timeout_seconds' must be between 1 and 600.", usage);
            timeoutSeconds = (int)Math.Round(td, MidpointRounding.AwayFromZero);
        }

        var summary = $"Run {shell} command: {Clip(OneLine(command), 200)}";
        return await RunActionAsync(call, new ActionPlan
        {
            Action = new ProposedAction("run_command", summary, ActionRisk.High, Command: command),
            DryRunText = $"DRY RUN: would run this {shell} command (not performed): {Clip(command, 500)}",
            ScreenshotAfter = false,
            Perform = async c =>
            {
                var result = await _desktop.Shell.RunAsync(command, shell, UserProfileDirectory(), timeoutSeconds * 1000, c).ConfigureAwait(false);
                return new ActionOutcome(true, FormatShellResult(result, timeoutSeconds));
            },
        }, ct).ConfigureAwait(false);
    }

    internal static string FormatShellResult(ShellResult result, int timeoutSeconds)
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"Exit code: {result.ExitCode}");
        if (result.TimedOut) sb.Append(CultureInfo.InvariantCulture, $" (timed out after {timeoutSeconds} s; the command was stopped)");
        var stdout = (result.StdOut ?? "").TrimEnd();
        var stderr = (result.StdErr ?? "").TrimEnd();
        if (stdout.Length == 0 && stderr.Length == 0)
        {
            sb.Append("\n(no output)");
            return sb.ToString();
        }
        if (stdout.Length > 0) sb.Append("\n--- stdout ---\n").Append(TruncateMiddle(stdout, OutputStreamLimit));
        if (stderr.Length > 0) sb.Append("\n--- stderr ---\n").Append(TruncateMiddle(stderr, OutputStreamLimit));
        return sb.ToString();
    }

    /// <summary>Keeps the start and the end of long text (errors usually sit at the end).</summary>
    internal static string TruncateMiddle(string s, int max)
    {
        if (s.Length <= max) return s;
        int head = max * 3 / 4;
        int tail = max - head;
        if (head > 0 && char.IsHighSurrogate(s[head - 1])) head--;
        if (tail > 0 && char.IsLowSurrogate(s[s.Length - tail])) tail--;
        int omitted = s.Length - head - tail;
        return s[..head] + $"\n[... {omitted} characters omitted ...]\n" + s[^tail..];
    }

    // ---------------------------------------------------------------- argument helpers

    private static ToolResult Usage(string problem, string usage) => ToolResult.Error($"{problem} {usage}");

    private static string DryRunText(string summary) =>
        $"DRY RUN: would {LowerFirst(summary)} (not performed; the screen does not change in dry-run mode).";

    /// <summary>Reads a model-space point. Returns a problem description, or null when valid.</summary>
    private static string? ReadPoint(Call call, JsonElement args, string xName, string yName, out double x, out double y)
    {
        x = y = 0;
        var xv = JsonArgs.GetDouble(args, xName);
        var yv = JsonArgs.GetDouble(args, yName);
        if (xv is not { } xd || yv is not { } yd || !double.IsFinite(xd) || !double.IsFinite(yd))
            return $"'{xName}' and '{yName}' must be numbers in {SpaceRange(call)}.";
        if (!call.Mapper.IsInModelSpace(xd, yd))
            return $"({N(xd)}, {N(yd)}) is outside the screen; '{xName}' and '{yName}' must be in {SpaceRange(call)}.";
        x = xd;
        y = yd;
        return null;
    }

    private static bool TryReadButton(JsonElement args, out MouseButton button, out string? error)
    {
        button = MouseButton.Left;
        error = null;
        var raw = JsonArgs.GetString(args, "button");
        if (string.IsNullOrWhiteSpace(raw)) return true;
        switch (raw.Trim().ToLowerInvariant())
        {
            case "left" or "l" or "primary":
                button = MouseButton.Left;
                return true;
            case "right" or "r" or "secondary":
                button = MouseButton.Right;
                return true;
            case "middle" or "m" or "wheel":
                button = MouseButton.Middle;
                return true;
        }
        error = $"Unknown button '{raw}'. Use left, right or middle.";
        return false;
    }

    private static bool TryReadModifiers(JsonElement args, out List<string> modifiers, out string? error)
    {
        modifiers = new List<string>();
        error = null;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in JsonArgs.GetStringList(args, "modifiers"))
        {
            var name = KeyCombo.NormalizeName(raw);
            if (name.Length == 0) continue;
            if (!KeyCombo.ModifierNames.Contains(name))
            {
                error = $"Unknown modifier '{raw}'. Use ctrl, shift, alt or win.";
                return false;
            }
            seen.Add(name);
        }
        modifiers = new[] { "ctrl", "alt", "shift", "win" }.Where(seen.Contains).ToList();
        return true;
    }

    private void WithModifiers(IReadOnlyList<string> modifiers, Action action)
    {
        var held = new List<string>();
        try
        {
            foreach (var m in modifiers)
            {
                _desktop.Input.KeyDown(m);
                held.Add(m);
            }
            action();
        }
        finally
        {
            bool failed = false;
            for (int i = held.Count - 1; i >= 0; i--)
            {
                try { _desktop.Input.KeyUp(held[i]); }
                catch (Exception) { failed = true; }
            }
            if (failed) ReleaseAllQuietly();
        }
    }

    private static string ButtonName(MouseButton b) => b switch
    {
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        _ => "left",
    };

    private static string ClickVerb(int clicks) => clicks switch { 2 => "Double-click", 3 => "Triple-click", _ => "Click" };

    private static string ClickVerbPast(int clicks) => clicks switch { 2 => "Double-clicked", 3 => "Triple-clicked", _ => "Clicked" };

    private static string P(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

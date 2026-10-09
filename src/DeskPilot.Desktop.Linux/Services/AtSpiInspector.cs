using System.Collections.Concurrent;
using System.Globalization;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>
/// UI elements through AT-SPI2, the Linux accessibility bus (the counterpart of UI Automation on Windows). GTK 3/4,
/// Qt (when accessibility is enabled), Firefox, Chromium/Electron (when enabled), LibreOffice and others publish their
/// widget trees there. The target window (from the window manager) is matched to an AT-SPI application by process id
/// and to one of its frames by title; its descendants are walked with pipelined D-Bus calls.
/// Every query runs on a background task with a timeout and returns what it found so far when the time is up, so a
/// hung app can never block the caller.
/// </summary>
public sealed class AtSpiInspector : IUiInspector
{
    internal static readonly TimeSpan ElementsTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ElementAtTimeout = TimeSpan.FromSeconds(1);
    internal const int MaxTextLength = 80;
    private const int MaxDepth = 60;
    private const int MaxVisitedNodes = 5000;
    private const int MaxChildrenPerNode = 1000;
    private const int MaxManagedChildren = 200;
    private const int MaxValueChars = 512;

    private const uint CoordScreen = 0;
    private const uint CoordWindow = 1;

    private readonly IWindowManager _windows;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, int> _pids = new(StringComparer.Ordinal);
    private AtSpiConnection? _connection;

    /// <param name="windows">Used to map a window handle to its process, and to tell whether screen coordinates are available.</param>
    public AtSpiInspector(IWindowManager windows)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
    }

    public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<UiElementInfo>>(ct);
        maxElements = Math.Max(1, maxElements);
        var collector = new Collector(maxElements);
        return RunWithTimeout<IReadOnlyList<UiElementInfo>>(
            async token =>
            {
                await CollectAsync(window, collector, token).ConfigureAwait(false);
                return collector.Snapshot();
            },
            () => collector.Snapshot(),
            collector.Abandon,
            ElementsTimeout,
            ct);
    }

    public Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<UiElementInfo?>(ct);
        return RunWithTimeout<UiElementInfo?>(
            async token =>
            {
                try
                {
                    return await ElementAtAsync(x, y, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Best effort: no accessibility bus, a vanished element, an app that does not implement Component.
                    return null;
                }
            },
            () => null,
            () => { },
            ElementAtTimeout,
            ct);
    }

    /// <summary>
    /// Runs the work on a background task. Completes with its result, with onTimeout's result after the timeout, or
    /// cancelled when ct fires; the work's own token is cancelled in both cases so it stops sending calls.
    /// </summary>
    internal static Task<T> RunWithTimeout<T>(Func<CancellationToken, Task<T>> work, Func<T> onTimeout, Action abandon, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var workCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try
            {
                tcs.TrySetResult(await work(workCts.Token).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (workCts.IsCancellationRequested)
            {
                // The timer or the caller's token already completed the task.
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        });

        var timer = new Timer(_ =>
        {
            abandon();
            T fallback;
            try { fallback = onTimeout(); }
            catch (Exception) { fallback = default!; }
            tcs.TrySetResult(fallback);
            try { workCts.Cancel(); } catch (ObjectDisposedException) { }
        }, null, timeout, Timeout.InfiniteTimeSpan);
        var reg = ct.CanBeCanceled
            ? ct.Register(() =>
            {
                abandon();
                tcs.TrySetCanceled(ct);
                try { workCts.Cancel(); } catch (ObjectDisposedException) { }
            })
            : default;
        tcs.Task.ContinueWith(_ =>
        {
            timer.Dispose();
            reg.Dispose();
            try { workCts.Cancel(); } catch (ObjectDisposedException) { }
        }, TaskScheduler.Default);
        return tcs.Task;
    }

    // ------------------------------------------------------------------ connection and window matching

    private async Task<AtSpiConnection> GetConnectionAsync(CancellationToken ct)
    {
        var c = _connection;
        if (c is { IsClosed: false }) return c;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            c = _connection;
            if (c is { IsClosed: false }) return c;
            c?.Dispose();
            _connection = null;
            _pids.Clear();
            try
            {
                c = await AtSpiConnection.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Accessibility (AT-SPI) is not available: {ex.Message} The accessibility bus comes with the at-spi2-core package.", ex);
            }
            _connection = c;
            return c;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>The window the caller means: 0 = the foreground window. Null when the window manager does not know it.</summary>
    private WindowInfo? ResolveWindow(nint window, out bool unknownHandle)
    {
        unknownHandle = false;
        try
        {
            if (window == 0) return _windows.GetForegroundWindow();
            var w = _windows.ListWindows()?.FirstOrDefault(x => x.Handle == window);
            if (w != null) return w;
            var fg = _windows.GetForegroundWindow();
            if (fg != null && fg.Handle == window) return fg;
            unknownHandle = true;
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A window manager that cannot list windows (some Wayland compositors): fall back to the active frame.
            return null;
        }
    }

    /// <summary>A toplevel accessible (frame, dialog, window) of an application.</summary>
    internal sealed record Toplevel(AtspiRef Ref, uint Role, ulong States, string Name, int Pid);

    private async Task<List<AtspiRef>> ListApplicationsAsync(AtSpiConnection conn, CancellationToken ct) =>
        await conn.GetChildrenAsync(AtSpiConnection.RegistryRoot, ct).ConfigureAwait(false);

    private async Task<int> PidOfAsync(AtSpiConnection conn, string bus, CancellationToken ct)
    {
        if (_pids.TryGetValue(bus, out var pid)) return pid;
        try
        {
            pid = (int)await conn.GetConnectionPidAsync(bus, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            pid = 0;
        }
        _pids[bus] = pid;
        return pid;
    }

    private static async Task<List<Toplevel>> ToplevelsOfAsync(AtSpiConnection conn, AtspiRef app, int pid, CancellationToken ct)
    {
        List<AtspiRef> children;
        try { children = await conn.GetChildrenAsync(app, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return new List<Toplevel>(); }
        var nodes = await Task.WhenAll(children.Take(64).Select(c => ReadNodeAsync(conn, c, ct))).ConfigureAwait(false);
        return nodes.Where(n => n != null).Select(n => new Toplevel(n!.Ref, n.Role, n.States, n.Name, pid)).ToList();
    }

    /// <summary>
    /// Finds the frame for a window: the applications whose process is the window's, then the frame named like the
    /// window title (else the only, the active or the first showing one). Without a pid match the title alone is
    /// tried across all applications; without any window the active frame is used.
    /// </summary>
    private async Task<(Toplevel Frame, List<Toplevel> Siblings)?> FindFrameAsync(AtSpiConnection conn, WindowInfo? target, CancellationToken ct)
    {
        var apps = await ListApplicationsAsync(conn, ct).ConfigureAwait(false);
        var pids = await Task.WhenAll(apps.Select(a => PidOfAsync(conn, a.Bus, ct))).ConfigureAwait(false);

        if (target != null && target.ProcessId > 0)
        {
            var mine = new List<Toplevel>();
            for (int i = 0; i < apps.Count; i++)
            {
                if (pids[i] == target.ProcessId) mine.AddRange(await ToplevelsOfAsync(conn, apps[i], pids[i], ct).ConfigureAwait(false));
            }
            var chosen = ChooseFrame(mine, target.Title, pidMatched: true);
            if (chosen != null) return (chosen, mine);
        }

        // Flatpak apps reach the bus through a proxy and some toolkits register from a helper process, so the pid can
        // differ: look at every application's frames.
        var all = (await Task.WhenAll(apps.Select((a, i) => ToplevelsOfAsync(conn, a, pids[i], ct))).ConfigureAwait(false))
            .SelectMany(t => t).ToList();
        var byTitle = ChooseFrame(all, target?.Title, pidMatched: false);
        if (byTitle != null) return (byTitle, all.Where(t => t.Ref.Bus == byTitle.Ref.Bus).ToList());
        if (target == null)
        {
            var active = all.FirstOrDefault(t => Has(t.States, State.Active) && IsShown(t.States));
            if (active != null) return (active, all.Where(t => t.Ref.Bus == active.Ref.Bus).ToList());
        }
        return null;
    }

    /// <summary>
    /// Picks the frame for a window title among toplevels. Title: exact, then ignoring case and outer spaces, then one
    /// containing the other. When the toplevels are the window's own process (pidMatched) and no title matches: the
    /// only shown one, else the active one, else the first shown one.
    /// </summary>
    internal static Toplevel? ChooseFrame(IReadOnlyList<Toplevel> toplevels, string? title, bool pidMatched)
    {
        var shown = toplevels.Where(t => IsShown(t.States)).ToList();
        var pool = shown.Count > 0 ? shown : toplevels.ToList();
        var t0 = (title ?? "").Trim();
        if (t0.Length > 0)
        {
            var hit = pool.FirstOrDefault(t => t.Name == title)
                      ?? pool.FirstOrDefault(t => string.Equals(t.Name.Trim(), t0, StringComparison.OrdinalIgnoreCase));
            if (hit == null && t0.Length >= 3)
            {
                hit = pool.FirstOrDefault(t => t.Name.Trim().Length >= 3 &&
                    (t0.Contains(t.Name.Trim(), StringComparison.OrdinalIgnoreCase) || t.Name.Contains(t0, StringComparison.OrdinalIgnoreCase)));
            }
            if (hit != null) return hit;
        }
        if (!pidMatched || pool.Count == 0) return null;
        if (shown.Count == 1) return shown[0];
        return pool.FirstOrDefault(t => Has(t.States, State.Active)) ?? pool[0];
    }

    /// <summary>
    /// How to ask for positions and turn them into screen pixels: the AT-SPI coordinate type to use for extents and
    /// hit tests, and what to add to the results. Placeable is false when positions are window-relative and the window's
    /// own position is unknown, so no screen position can be given.
    /// </summary>
    internal readonly record struct Offset(int Dx, int Dy, bool WindowRelative, bool Placeable = true)
    {
        public uint CoordType => WindowRelative ? CoordWindow : CoordScreen;
    }

    /// <summary>
    /// Toolkits that do not know where their window is report no usable screen coordinates: GTK 4 answers every
    /// "screen" query with (0, 0) and only has window and parent coordinates; GTK 3 and Qt under Wayland report window
    /// coordinates for both. For those the window coordinates are used and shifted by the window's position from the
    /// window manager. A toolkit with real screen coordinates places the frame somewhere other than (0, 0), or, for a
    /// window really in the top-left corner, reports a child at different, non-zero screen and window positions (probe).
    /// The window bounds may include server-side decorations or shadows, so the frame (its size from frameWindow) is
    /// placed inside them: side margins split evenly, the top taking the rest.
    /// </summary>
    internal static Offset ComputeOffset(ScreenRect frameScreen, ScreenRect frameWindow, ScreenRect windowBounds, (ScreenRect Screen, ScreenRect Window)? probe)
    {
        bool realScreen = frameScreen.X != 0 || frameScreen.Y != 0;
        if (!realScreen && probe is { } p && (p.Screen.X != p.Window.X || p.Screen.Y != p.Window.Y) && (p.Screen.X != 0 || p.Screen.Y != 0))
            realScreen = true;
        if (realScreen) return new Offset(0, 0, false);
        if (windowBounds.IsEmpty) return new Offset(0, 0, true, Placeable: false);

        var frame = frameWindow.Width > 0 && frameWindow.Height > 0 ? frameWindow : frameScreen;
        if (frame.Width <= 0 || frame.Height <= 0) return new Offset(windowBounds.X, windowBounds.Y, true);
        int side = Math.Max(0, (windowBounds.Width - frame.Width) / 2);
        int top = Math.Max(0, windowBounds.Height - frame.Height - side);
        return new Offset(windowBounds.X + side, windowBounds.Y + top, true);
    }

    /// <summary>How to turn reported extents into screen pixels for this window, and the frame's screen rectangle.</summary>
    private static async Task<(ScreenRect FrameBounds, Offset Offset)> ResolveOffsetAsync(AtSpiConnection conn, AtspiRef frame, ScreenRect windowBounds, CancellationToken ct)
    {
        var frameScreenTask = ExtentsAsync(conn, frame, CoordScreen, ct);
        var frameWindowTask = ExtentsAsync(conn, frame, CoordWindow, ct);
        var frameScreen = await frameScreenTask.ConfigureAwait(false);
        var frameWindow = await frameWindowTask.ConfigureAwait(false);
        (ScreenRect, ScreenRect)? probe = null;
        if (frameScreen.X == 0 && frameScreen.Y == 0)
        {
            try
            {
                var children = await conn.GetChildrenAsync(frame, ct).ConfigureAwait(false);
                foreach (var child in children.Take(3))
                {
                    var screen = await ExtentsAsync(conn, child, CoordScreen, ct).ConfigureAwait(false);
                    if (screen.IsEmpty) continue;
                    var window = await ExtentsAsync(conn, child, CoordWindow, ct).ConfigureAwait(false);
                    if (window.IsEmpty) continue;
                    probe = (screen, window);
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        var offset = ComputeOffset(frameScreen, frameWindow, windowBounds, probe);
        var frameRect = offset.WindowRelative ? frameWindow : frameScreen;
        var bounds = !offset.Placeable || frameRect.IsEmpty ? default : Shift(frameRect, offset);
        return (bounds, offset);
    }

    private static async Task<ScreenRect> ExtentsAsync(AtSpiConnection conn, AtspiRef node, uint coordType, CancellationToken ct)
    {
        try
        {
            var (x, y, w, h) = await conn.GetExtentsAsync(node, coordType, ct).ConfigureAwait(false);
            return new ScreenRect(x, y, w, h);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return default;
        }
    }

    // ------------------------------------------------------------------ element list

    private async Task CollectAsync(nint window, Collector collector, CancellationToken ct)
    {
        var target = ResolveWindow(window, out bool unknownHandle);
        if (unknownHandle) return;
        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        var found = await FindFrameAsync(conn, target, ct).ConfigureAwait(false);
        if (found == null) return;
        var frame = found.Value.Frame;

        var (clip, offset) = await ResolveOffsetAsync(conn, frame.Ref, target?.Bounds ?? default, ct).ConfigureAwait(false);

        var walker = new Walker(conn, collector, offset, clip, ct);
        var children = await walker.ChildrenOfAsync(frame.Ref, frame.States).ConfigureAwait(false);
        await walker.WalkAsync(children, 1, null, insidePassword: false).ConfigureAwait(false);
    }

    private static ScreenRect Shift(ScreenRect r, Offset o) => new(r.X + o.Dx, r.Y + o.Dy, r.Width, r.Height);

    /// <summary>Role, state and name of one node; null when the node cannot be read (gone, or not accessible).</summary>
    internal sealed record Node(AtspiRef Ref, uint Role, ulong States, string Name);

    private static async Task<Node?> ReadNodeAsync(AtSpiConnection conn, AtspiRef r, CancellationToken ct)
    {
        if (r.IsNull) return null;
        var role = conn.GetRoleAsync(r, ct);
        var state = conn.GetStateAsync(r, ct);
        var name = conn.GetNameAsync(r, ct);
        uint roleValue;
        try { roleValue = await role.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Observe(state);
            Observe(name);
            return null;
        }
        ulong states = 0;
        string nameValue = "";
        try { states = await state.ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        try { nameValue = await name.ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
        return new Node(r, roleValue, states, nameValue ?? "");
    }

    private static void Observe(Task t) => t.ContinueWith(x => _ = x.Exception, TaskContinuationOptions.OnlyOnFaulted);

    /// <summary>Depth-first walk that reads each group of siblings with pipelined calls, so the order matches the tree.</summary>
    private sealed class Walker
    {
        private readonly AtSpiConnection _conn;
        private readonly Collector _collector;
        private readonly Offset _offset;
        private readonly ScreenRect _clip;
        private readonly CancellationToken _ct;
        private int _visited;

        public Walker(AtSpiConnection conn, Collector collector, Offset offset, ScreenRect clip, CancellationToken ct)
        {
            _conn = conn;
            _collector = collector;
            _offset = offset;
            _clip = clip;
            _ct = ct;
        }

        public async Task<List<AtspiRef>> ChildrenOfAsync(AtspiRef node, ulong states)
        {
            try
            {
                if (Has(states, State.ManagesDescendants))
                {
                    // Huge lists and tables create children on demand; only read the first ones by index.
                    int count = await _conn.GetChildCountAsync(node, _ct).ConfigureAwait(false);
                    var refs = await Task.WhenAll(Enumerable.Range(0, Math.Clamp(count, 0, MaxManagedChildren))
                        .Select(i => SafeChildAt(node, i))).ConfigureAwait(false);
                    return refs.Where(r => !r.IsNull).ToList();
                }
                var children = await _conn.GetChildrenAsync(node, _ct).ConfigureAwait(false);
                return children.Count > MaxChildrenPerNode ? children.GetRange(0, MaxChildrenPerNode) : children;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return await ChildrenByIndexAsync(node).ConfigureAwait(false);
            }
        }

        /// <summary>For implementations without GetChildren: ChildCount and GetChildAtIndex.</summary>
        private async Task<List<AtspiRef>> ChildrenByIndexAsync(AtspiRef node)
        {
            try
            {
                int count = await _conn.GetChildCountAsync(node, _ct).ConfigureAwait(false);
                var refs = await Task.WhenAll(Enumerable.Range(0, Math.Clamp(count, 0, MaxManagedChildren)).Select(i => SafeChildAt(node, i))).ConfigureAwait(false);
                return refs.Where(r => !r.IsNull).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new List<AtspiRef>();
            }
        }

        private async Task<AtspiRef> SafeChildAt(AtspiRef node, int index)
        {
            try { return await _conn.GetChildAtIndexAsync(node, index, _ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { return default; }
        }

        public async Task WalkAsync(List<AtspiRef> group, int depth, (int Index, UiElementInfo Info)? parent, bool insidePassword)
        {
            if (depth > MaxDepth || _collector.IsDone || group.Count == 0) return;
            int room = MaxVisitedNodes - _visited;
            if (room <= 0) return;
            if (group.Count > room) group = group.GetRange(0, room);
            _visited += group.Count;

            var nodes = await Task.WhenAll(group.Select(r => ReadNodeAsync(_conn, r, _ct))).ConfigureAwait(false);
            var details = await Task.WhenAll(nodes.Select(n => DetailAsync(n, depth, insidePassword))).ConfigureAwait(false);

            for (int i = 0; i < nodes.Length; i++)
            {
                if (_collector.IsDone) return;
                var node = nodes[i];
                var detail = details[i];
                if (node == null || detail == null) continue;

                (int Index, UiElementInfo Info)? reported = null;
                if (detail.Element != null)
                {
                    var info = detail.Element;
                    if (parent is { } p && p.Info.ControlType == info.ControlType && SameBounds(p.Info.Bounds, info.Bounds))
                    {
                        // A wrapper and its inner widget (GTK 4 entries, combo boxes) are one control: keep the outer one,
                        // with the inner one's value when the outer had none.
                        if (p.Info.Value == null && info.Value != null) _collector.Replace(p.Index, p.Info with { Value = info.Value });
                    }
                    else
                    {
                        int index = _collector.Add(info);
                        if (index >= 0) reported = (index, info);
                    }
                }
                if (detail.Children.Count > 0)
                {
                    bool password = insidePassword || node.Role == Role.PasswordText;
                    await WalkAsync(detail.Children, depth + 1, reported ?? parent, password).ConfigureAwait(false);
                }
            }
        }

        private sealed record Detail(UiElementInfo? Element, List<AtspiRef> Children);

        private async Task<Detail?> DetailAsync(Node? node, int depth, bool insidePassword)
        {
            if (node == null || Has(node.States, State.Defunct)) return null;
            bool showing = Has(node.States, State.Showing);
            var controlType = showing ? ControlTypeFor(node.Role, node.States) : null;
            bool expand = showing && depth < MaxDepth && !LeafRoles.Contains(node.Role);

            var childrenTask = expand ? ChildrenOfAsync(node.Ref, node.States) : Task.FromResult(new List<AtspiRef>());
            var elementTask = controlType != null
                ? DescribeAsync(_conn, node, controlType, _offset, insidePassword, _ct)
                : Task.FromResult<UiElementInfo?>(null);
            var children = await childrenTask.ConfigureAwait(false);
            var element = await elementTask.ConfigureAwait(false);
            // Zero-size or scrolled-out controls are not shown. Without a known window position (Placeable false) every
            // control keeps empty bounds: its name and type are still worth listing.
            if (element != null && _offset.Placeable && (element.Bounds.IsEmpty || (!_clip.IsEmpty && element.Bounds.Intersect(_clip).IsEmpty)))
                element = null;
            return new Detail(element, children);
        }
    }

    private static bool SameBounds(ScreenRect a, ScreenRect b) =>
        Math.Abs(a.X - b.X) <= 2 && Math.Abs(a.Y - b.Y) <= 2 && Math.Abs(a.Width - b.Width) <= 4 && Math.Abs(a.Height - b.Height) <= 4;

    /// <summary>Extents, accessible id and (for edits and ranges that are not passwords) a short value.</summary>
    private static async Task<UiElementInfo?> DescribeAsync(AtSpiConnection conn, Node node, string controlType, Offset offset, bool insidePassword, CancellationToken ct)
    {
        var extents = offset.Placeable ? ExtentsAsync(conn, node.Ref, offset.CoordType, ct) : Task.FromResult(default(ScreenRect));
        var id = SafeStringAsync(conn.GetPropertyAsync(node.Ref, AtSpiConnection.AccessibleInterface, "AccessibleId", ct));
        var value = MayReadValue(node, insidePassword) ? ReadValueAsync(conn, node, ct) : Task.FromResult<string?>(null);
        var bounds = offset.Placeable ? Shift(await extents.ConfigureAwait(false), offset) : default;
        var automationId = await id.ConfigureAwait(false);
        var v = await value.ConfigureAwait(false);
        return new UiElementInfo(
            Trim(node.Name),
            controlType,
            bounds.Width > 0 && bounds.Height > 0 ? bounds : default,
            string.IsNullOrWhiteSpace(automationId) ? null : automationId,
            Has(node.States, State.Enabled) || Has(node.States, State.Sensitive),
            Has(node.States, State.Focusable),
            v);
    }

    private static async Task<string?> SafeStringAsync(Task<Tmds.DBus.Protocol.VariantValue> t)
    {
        try
        {
            var v = await t.ConfigureAwait(false);
            for (int i = 0; i < 3 && v.Type == Tmds.DBus.Protocol.VariantValueType.Variant; i++) v = v.GetVariantValue();
            return v.Type == Tmds.DBus.Protocol.VariantValueType.String ? v.GetString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Never for password fields or anything inside one, nor for fields whose name says password.</summary>
    internal static bool MayReadValue(Node node, bool insidePassword)
    {
        if (insidePassword || node.Role == Role.PasswordText) return false;
        if (LooksLikePassword(node.Name)) return false;
        return TextValueRoles.Contains(node.Role) || NumericValueRoles.Contains(node.Role);
    }

    internal static bool LooksLikePassword(string? name) =>
        !string.IsNullOrEmpty(name) &&
        (name.Contains("password", StringComparison.OrdinalIgnoreCase) || name.Contains("passphrase", StringComparison.OrdinalIgnoreCase)
         || name.Contains("passcode", StringComparison.OrdinalIgnoreCase) || name.Contains("passwort", StringComparison.OrdinalIgnoreCase));

    private static async Task<string?> ReadValueAsync(AtSpiConnection conn, Node node, CancellationToken ct)
    {
        try
        {
            if (TextValueRoles.Contains(node.Role))
            {
                // An explicit end offset: GTK 4 clamps -1 to 0 instead of reading to the end.
                int count = -1;
                try { count = await conn.GetCharacterCountAsync(node.Ref, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
                string text = count switch
                {
                    0 => "",
                    > 0 => await conn.GetTextAsync(node.Ref, 0, Math.Min(count, MaxValueChars), ct).ConfigureAwait(false),
                    _ => await conn.GetTextAsync(node.Ref, 0, -1, ct).ConfigureAwait(false),
                };
                if (!string.IsNullOrEmpty(text) && !IsMasked(text)) return Trim(text.Length > MaxValueChars ? text[..MaxValueChars] : text);
                if (!NumericValueRoles.Contains(node.Role)) return null;
            }
            var number = await conn.GetCurrentValueAsync(node.Ref, ct).ConfigureAwait(false);
            return number?.ToString("0.###", CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Text made only of bullet or asterisk characters is a masked password, not a value.</summary>
    internal static bool IsMasked(string text) => text.Length > 0 && text.All(c => c is '*' or '•' or '●' or '∙' or '·');

    /// <summary>Collapses whitespace and limits text to <see cref="MaxTextLength"/> characters.</summary>
    internal static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        // U+FFFC stands for an embedded object (an image or child widget) in AT-SPI text.
        collapsed = collapsed.Replace("￼", "").Trim();
        return collapsed.Length <= MaxTextLength ? collapsed : collapsed[..(MaxTextLength - 1)] + "…";
    }

    // ------------------------------------------------------------------ element at a point

    private async Task<UiElementInfo?> ElementAtAsync(int x, int y, CancellationToken ct)
    {
        WindowInfo? target = null;
        try { target = _windows.GetWindowAt(x, y); }
        catch (Exception ex) when (ex is not OperationCanceledException) { }

        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        var found = await FindFrameAsync(conn, target, ct).ConfigureAwait(false);
        if (found == null) return null;
        var (frame, siblings) = found.Value;

        var (_, offset) = await ResolveOffsetAsync(conn, frame.Ref, target?.Bounds ?? default, ct).ConfigureAwait(false);
        // Window-relative positions without the window's position: the point cannot be translated.
        if (!offset.Placeable) return null;

        // An open menu or popup is its own toplevel of the same app; with real screen coordinates it can be told apart.
        var root = frame.Ref;
        if (!offset.WindowRelative)
        {
            var popups = siblings.Where(t => t.Ref != frame.Ref && IsShown(t.States) && t.Role is Role.Window or Role.PopupMenu or Role.Menu or Role.ToolTip).ToList();
            var popupExtents = await Task.WhenAll(popups.Select(p => ExtentsAsync(conn, p.Ref, CoordScreen, ct))).ConfigureAwait(false);
            for (int i = 0; i < popups.Count; i++)
            {
                if (!popupExtents[i].IsEmpty && popupExtents[i].Contains(x, y)) { root = popups[i].Ref; break; }
            }
        }

        int qx = x - offset.Dx, qy = y - offset.Dy;
        var current = root;
        for (int i = 0; i < 40; i++)
        {
            AtspiRef child;
            try { child = await conn.GetAccessibleAtPointAsync(current, qx, qy, offset.CoordType, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { break; }
            if (child.IsNull || child == current) break;
            current = child;
        }
        if (current == root) return null;

        // The deepest object is often a label or icon inside a button: report the nearest interactive ancestor, and
        // remember whether any ancestor is a password field.
        var chain = new List<Node>();
        var walk = current;
        for (int i = 0; i < 40 && !walk.IsNull && walk != root; i++)
        {
            var node = await ReadNodeAsync(conn, walk, ct).ConfigureAwait(false);
            if (node == null) break;
            chain.Add(node);
            try { walk = await conn.GetParentAsync(walk, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OperationCanceledException) { break; }
        }
        if (chain.Count == 0) return null;

        int pick = chain.FindIndex(n => ControlTypeFor(n.Role, n.States) != null);
        var chosen = pick >= 0 ? chain[pick] : chain[0];
        bool insidePassword = chain.Skip(Math.Max(0, pick) + 1).Any(n => n.Role == Role.PasswordText);
        var controlType = ControlTypeFor(chosen.Role, chosen.States) ?? GenericControlType(chosen.Role);
        return await DescribeAsync(conn, chosen, controlType, offset, insidePassword, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ diagnostics

    /// <summary>
    /// A readable dump of the accessibility tree of every application whose process id matches (all when pid is 0):
    /// one line per object with depth, role number and role name, states, name and screen extents. For tests and
    /// troubleshooting.
    /// </summary>
    internal async Task<List<string>> DumpAsync(int pid, int maxNodes, CancellationToken ct)
    {
        var lines = new List<string>();
        var conn = await GetConnectionAsync(ct).ConfigureAwait(false);
        var apps = await ListApplicationsAsync(conn, ct).ConfigureAwait(false);
        int count = 0;
        foreach (var app in apps)
        {
            int appPid = await PidOfAsync(conn, app.Bus, ct).ConfigureAwait(false);
            string appName = "";
            try { appName = await conn.GetNameAsync(app, ct).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
            lines.Add($"app {app.Bus} pid={appPid} name='{appName}'");
            if (pid != 0 && appPid != pid) continue;
            var stack = new Stack<(AtspiRef Ref, int Depth)>();
            stack.Push((app, 0));
            while (stack.Count > 0 && count < maxNodes)
            {
                var (r, depth) = stack.Pop();
                count++;
                var node = await ReadNodeAsync(conn, r, ct).ConfigureAwait(false);
                if (node == null) { lines.Add($"{new string(' ', depth * 2)}(unreadable {r})"); continue; }
                string roleName = "";
                try { roleName = await conn.GetRoleNameAsync(r, ct).ConfigureAwait(false); } catch (Exception ex) when (ex is not OperationCanceledException) { }
                var ext = depth == 0 ? default : await ExtentsAsync(conn, r, CoordScreen, ct).ConfigureAwait(false);
                var win = depth == 0 ? default : await ExtentsAsync(conn, r, CoordWindow, ct).ConfigureAwait(false);
                lines.Add($"{new string(' ', depth * 2)}role={node.Role} '{roleName}' states=0x{node.States:x} name='{node.Name}' ext={ext} win={win} type={ControlTypeFor(node.Role, node.States) ?? "-"}");
                List<AtspiRef> children;
                try { children = await conn.GetChildrenAsync(r, ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { continue; }
                for (int i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
            }
        }
        return lines;
    }

    // ------------------------------------------------------------------ roles and states

    /// <summary>AtspiStateType bit numbers.</summary>
    internal static class State
    {
        public const int Active = 1;
        public const int Defunct = 6;
        public const int Editable = 7;
        public const int Enabled = 8;
        public const int Focusable = 11;
        public const int Focused = 12;
        public const int MultiLine = 17;
        public const int Sensitive = 24;
        public const int Showing = 25;
        public const int Visible = 30;
        public const int ManagesDescendants = 31;
    }

    internal static bool Has(ulong states, int bit) => (states & (1UL << bit)) != 0;

    private static bool IsShown(ulong states) => Has(states, State.Showing) || Has(states, State.Visible);

    /// <summary>AtspiRole values used here.</summary>
    internal static class Role
    {
        public const uint Alert = 2;
        public const uint Calendar = 5;
        public const uint Canvas = 6;
        public const uint CheckBox = 7;
        public const uint CheckMenuItem = 8;
        public const uint ColorChooser = 9;
        public const uint ColumnHeader = 10;
        public const uint ComboBox = 11;
        public const uint DateEditor = 12;
        public const uint DesktopIcon = 13;
        public const uint DesktopFrame = 14;
        public const uint Dial = 15;
        public const uint Dialog = 16;
        public const uint DirectoryPane = 17;
        public const uint DrawingArea = 18;
        public const uint FileChooser = 19;
        public const uint Filler = 20;
        public const uint FontChooser = 22;
        public const uint Frame = 23;
        public const uint GlassPane = 24;
        public const uint HtmlContainer = 25;
        public const uint Icon = 26;
        public const uint Image = 27;
        public const uint InternalFrame = 28;
        public const uint Label = 29;
        public const uint LayeredPane = 30;
        public const uint List = 31;
        public const uint ListItem = 32;
        public const uint Menu = 33;
        public const uint MenuBar = 34;
        public const uint MenuItem = 35;
        public const uint OptionPane = 36;
        public const uint PageTab = 37;
        public const uint PageTabList = 38;
        public const uint Panel = 39;
        public const uint PasswordText = 40;
        public const uint PopupMenu = 41;
        public const uint ProgressBar = 42;
        public const uint PushButton = 43;
        public const uint RadioButton = 44;
        public const uint RadioMenuItem = 45;
        public const uint RootPane = 46;
        public const uint RowHeader = 47;
        public const uint ScrollBar = 48;
        public const uint ScrollPane = 49;
        public const uint Separator = 50;
        public const uint Slider = 51;
        public const uint SpinButton = 52;
        public const uint SplitPane = 53;
        public const uint StatusBar = 54;
        public const uint Table = 55;
        public const uint TableCell = 56;
        public const uint TableColumnHeader = 57;
        public const uint TableRowHeader = 58;
        public const uint TearoffMenuItem = 59;
        public const uint Terminal = 60;
        public const uint Text = 61;
        public const uint ToggleButton = 62;
        public const uint ToolBar = 63;
        public const uint ToolTip = 64;
        public const uint Tree = 65;
        public const uint TreeTable = 66;
        public const uint Unknown = 67;
        public const uint Viewport = 68;
        public const uint Window = 69;
        public const uint Header = 71;
        public const uint Footer = 72;
        public const uint Paragraph = 73;
        public const uint Application = 75;
        public const uint Autocomplete = 76;
        public const uint Editbar = 77;
        public const uint Embedded = 78;
        public const uint Entry = 79;
        public const uint Caption = 81;
        public const uint DocumentFrame = 82;
        public const uint Heading = 83;
        public const uint Section = 85;
        public const uint RedundantObject = 86;
        public const uint Form = 87;
        public const uint Link = 88;
        public const uint TableRow = 90;
        public const uint TreeItem = 91;
        public const uint DocumentSpreadsheet = 92;
        public const uint DocumentPresentation = 93;
        public const uint DocumentText = 94;
        public const uint DocumentWeb = 95;
        public const uint DocumentEmail = 96;
        public const uint ListBox = 98;
        public const uint Grouping = 99;
        public const uint Notification = 101;
        public const uint InfoBar = 102;
        public const uint LevelBar = 103;
        public const uint TitleBar = 104;
        public const uint Article = 109;
        public const uint Landmark = 110;
        public const uint Static = 116;
        public const uint PushButtonMenu = 129;
        public const uint Switch = 130;
    }

    /// <summary>Roles whose children are never interesting (and must not be read, for passwords).</summary>
    private static readonly HashSet<uint> LeafRoles = new()
    {
        Role.PushButton, Role.ToggleButton, Role.CheckBox, Role.RadioButton, Role.PasswordText, Role.SpinButton, Role.Slider,
        Role.MenuItem, Role.CheckMenuItem, Role.RadioMenuItem, Role.Link, Role.Terminal, Role.Switch, Role.PushButtonMenu,
        Role.ScrollBar, Role.Separator, Role.ProgressBar, Role.LevelBar,
    };

    private static readonly HashSet<uint> TextValueRoles = new()
    {
        Role.Text, Role.Entry, Role.Editbar, Role.ComboBox, Role.Autocomplete, Role.SpinButton,
    };

    private static readonly HashSet<uint> NumericValueRoles = new() { Role.Slider, Role.Dial, Role.SpinButton };

    /// <summary>Layout and text roles that are not controls even when focusable.</summary>
    private static readonly HashSet<uint> NonInteractiveRoles = new()
    {
        0, Role.Alert, Role.Application, Role.Frame, Role.Dialog, Role.Window, Role.Panel, Role.Filler, Role.ScrollPane, Role.Viewport,
        Role.LayeredPane, Role.RootPane, Role.GlassPane, Role.SplitPane, Role.InternalFrame, Role.OptionPane, Role.MenuBar,
        Role.ToolBar, Role.StatusBar, Role.PageTabList, Role.List, Role.ListBox, Role.Tree, Role.TreeTable, Role.Table,
        Role.HtmlContainer, Role.RedundantObject, Role.Separator, Role.Form, Role.Grouping, Role.Landmark, Role.Heading,
        Role.Paragraph, Role.Static, Role.Label, Role.Caption, Role.ScrollBar, Role.ProgressBar, Role.LevelBar, Role.InfoBar,
        Role.Notification, Role.TitleBar, Role.Header, Role.Footer, Role.Article, Role.Embedded, Role.DirectoryPane,
        Role.FileChooser, Role.ColorChooser, Role.FontChooser, Role.DesktopFrame, Role.PopupMenu, Role.ToolTip,
    };

    /// <summary>
    /// The UI Automation control type name for an interactive role, or null when the object is not a control.
    /// Unlisted roles count as controls only when focusable.
    /// </summary>
    internal static string? ControlTypeFor(uint role, ulong states)
    {
        switch (role)
        {
            case Role.PushButton or Role.ToggleButton or Role.PushButtonMenu: return "Button";
            case Role.Text or Role.Entry or Role.PasswordText or Role.Editbar: return "Edit";
            case Role.SpinButton: return "Spinner";
            case Role.MenuItem or Role.CheckMenuItem or Role.RadioMenuItem or Role.TearoffMenuItem or Role.Menu: return "MenuItem";
            case Role.CheckBox or Role.Switch: return "CheckBox";
            case Role.RadioButton: return "RadioButton";
            case Role.ComboBox or Role.Autocomplete: return "ComboBox";
            case Role.ListItem or Role.DesktopIcon: return "ListItem";
            case Role.PageTab: return "TabItem";
            case Role.Link: return "Hyperlink";
            case Role.TreeItem: return "TreeItem";
            case Role.TableRow or Role.TableCell: return "DataItem";
            case Role.ColumnHeader or Role.RowHeader or Role.TableColumnHeader or Role.TableRowHeader: return "HeaderItem";
            case Role.DocumentFrame or Role.DocumentText or Role.DocumentWeb or Role.DocumentEmail or Role.DocumentSpreadsheet
                or Role.DocumentPresentation or Role.Terminal:
                return "Document";
            case Role.Slider or Role.Dial: return "Slider";
            case Role.DateEditor or Role.Calendar: return "Calendar";
        }
        if (Has(states, State.Focusable) && !NonInteractiveRoles.Contains(role)) return GenericControlType(role);
        return null;
    }

    /// <summary>A UI Automation-style name for any role (used for non-interactive objects at a point).</summary>
    internal static string GenericControlType(uint role) => role switch
    {
        Role.Label or Role.Static or Role.Paragraph or Role.Caption or Role.Heading => "Text",
        Role.Image or Role.Icon => "Image",
        Role.Frame or Role.Dialog or Role.Window or Role.Alert => "Window",
        Role.MenuBar => "MenuBar",
        Role.Menu or Role.PopupMenu => "Menu",
        Role.ToolBar => "ToolBar",
        Role.StatusBar => "StatusBar",
        Role.List or Role.ListBox => "List",
        Role.Tree or Role.TreeTable => "Tree",
        Role.Table => "Table",
        Role.ScrollBar => "ScrollBar",
        Role.ProgressBar or Role.LevelBar => "ProgressBar",
        Role.PageTabList => "Tab",
        Role.Separator => "Separator",
        Role.ToolTip => "ToolTip",
        Role.TitleBar => "TitleBar",
        Role.Panel or Role.Filler or Role.ScrollPane or Role.Viewport or Role.Canvas or Role.DrawingArea or Role.SplitPane
            or Role.Section or Role.Grouping or Role.Form or Role.Landmark => "Pane",
        _ => ControlTypeFor(role, 0) ?? "Custom",
    };

    /// <summary>Thread-safe result list shared between the walker and a caller that may give up early.</summary>
    internal sealed class Collector
    {
        private readonly List<UiElementInfo> _items = new();
        private readonly int _max;
        private volatile bool _abandoned;

        public Collector(int max) => _max = max;

        public bool IsDone
        {
            get
            {
                if (_abandoned) return true;
                lock (_items) return _items.Count >= _max;
            }
        }

        /// <summary>Adds an element; returns its index, or -1 when full or abandoned.</summary>
        public int Add(UiElementInfo item)
        {
            if (_abandoned) return -1;
            lock (_items)
            {
                if (_items.Count >= _max) return -1;
                _items.Add(item);
                return _items.Count - 1;
            }
        }

        public void Replace(int index, UiElementInfo item)
        {
            if (_abandoned) return;
            lock (_items)
            {
                if (index >= 0 && index < _items.Count) _items[index] = item;
            }
        }

        public void Abandon() => _abandoned = true;

        public IReadOnlyList<UiElementInfo> Snapshot()
        {
            lock (_items) return _items.ToList();
        }
    }
}

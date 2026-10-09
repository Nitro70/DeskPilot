using System.Windows.Automation;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Windows;

/// <summary>
/// UI Automation reads. Every query runs on its own background thread with a timeout, because UIA calls
/// into other processes and a hung or busy app (including our own UI thread) can block indefinitely.
/// On timeout GetElementsAsync returns what it found so far; the abandoned thread finishes on its own.
/// </summary>
public sealed class UiAutomationInspector : IUiInspector
{
    internal static readonly TimeSpan ElementsTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan ElementAtTimeout = TimeSpan.FromSeconds(1);
    internal const int MaxTextLength = 80;
    private const int MaxDepth = 60;
    private const int MaxVisitedNodes = 5000;

    private static readonly HashSet<string> InteractiveTypes = new(StringComparer.Ordinal)
    {
        "Button", "Edit", "Hyperlink", "MenuItem", "ListItem", "TabItem", "CheckBox", "RadioButton", "ComboBox",
        "TreeItem", "DataItem", "Document", "Slider", "SplitButton", "Spinner", "HeaderItem",
    };

    public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<IReadOnlyList<UiElementInfo>>(ct);
        maxElements = Math.Max(1, maxElements);
        var collector = new Collector(maxElements);
        return RunWithTimeout(
            () =>
            {
                try { Collect(window, collector); }
                catch (Exception ex) when (IsUiaFailure(ex))
                {
                    // The window vanished or cannot be read; return whatever was found.
                }
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
            () =>
            {
                try
                {
                    var request = BuildCacheRequest();
                    AutomationElement? el;
                    using (request.Activate())
                    {
                        el = AutomationElement.FromPoint(new System.Windows.Point(x, y));
                    }
                    return el == null ? null : Describe(el, requireInteractive: false);
                }
                catch (Exception ex) when (IsUiaFailure(ex))
                {
                    // Nothing readable at that point (a vanished element, a process UIA cannot reach).
                    return null;
                }
            },
            () => null,
            () => { },
            ElementAtTimeout,
            ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> on a fresh background MTA thread (with physical-pixel DPI awareness).
    /// Completes with the work's result, with <paramref name="onTimeout"/>'s result after the timeout,
    /// or cancelled when ct fires. Never blocks the caller.
    /// </summary>
    internal static Task<T> RunWithTimeout<T>(Func<T> work, Func<T> onTimeout, Action abandon, TimeSpan timeout, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var dpi = DpiScope.PerMonitorV2();
                tcs.TrySetResult(work());
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "DeskPilot UIA",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();

        var timer = new Timer(_ =>
        {
            abandon();
            T fallback;
            try { fallback = onTimeout(); }
            catch (Exception) { fallback = default!; }
            tcs.TrySetResult(fallback);
        }, null, timeout, Timeout.InfiniteTimeSpan);
        var reg = ct.CanBeCanceled ? ct.Register(() => { abandon(); tcs.TrySetCanceled(ct); }) : default;
        tcs.Task.ContinueWith(_ => { timer.Dispose(); reg.Dispose(); }, TaskScheduler.Default);
        return tcs.Task;
    }

    private static CacheRequest BuildCacheRequest()
    {
        var request = new CacheRequest
        {
            TreeFilter = Automation.ControlViewCondition,
            // Full mode keeps a live reference so Value can be read on demand (and only for non-password fields).
            AutomationElementMode = AutomationElementMode.Full,
        };
        request.Add(AutomationElement.NameProperty);
        request.Add(AutomationElement.ControlTypeProperty);
        request.Add(AutomationElement.BoundingRectangleProperty);
        request.Add(AutomationElement.AutomationIdProperty);
        request.Add(AutomationElement.IsEnabledProperty);
        request.Add(AutomationElement.IsKeyboardFocusableProperty);
        request.Add(AutomationElement.IsOffscreenProperty);
        request.Add(AutomationElement.IsPasswordProperty);
        request.Add(AutomationElement.IsInvokePatternAvailableProperty);
        request.Add(AutomationElement.IsTogglePatternAvailableProperty);
        request.Add(AutomationElement.IsValuePatternAvailableProperty);
        request.Add(AutomationElement.IsSelectionItemPatternAvailableProperty);
        request.Add(AutomationElement.IsExpandCollapsePatternAvailableProperty);
        return request;
    }

    private static void Collect(nint window, Collector collector)
    {
        if (window == 0) window = NativeMethods.GetForegroundWindow();
        if (window == 0) return;
        var request = BuildCacheRequest();
        AutomationElement root;
        using (request.Activate())
        {
            root = AutomationElement.FromHandle(window);
        }
        var walker = new TreeWalker(Automation.ControlViewCondition);
        int visited = 0;
        Walk(walker, root, request, collector, 0, ref visited);
    }

    private static void Walk(TreeWalker walker, AutomationElement parent, CacheRequest request, Collector collector, int depth, ref int visited)
    {
        if (depth > MaxDepth) return;
        AutomationElement? child;
        try { child = walker.GetFirstChild(parent, request); }
        catch (Exception ex) when (IsUiaFailure(ex)) { return; }

        while (child != null)
        {
            if (collector.IsDone || ++visited > MaxVisitedNodes) return;
            try
            {
                var info = Describe(child, requireInteractive: true);
                if (info != null) collector.Add(info);
            }
            catch (Exception ex) when (IsUiaFailure(ex)) { }

            if (collector.IsDone) return;
            Walk(walker, child, request, collector, depth + 1, ref visited);

            try { child = walker.GetNextSibling(child, request); }
            catch (Exception ex) when (IsUiaFailure(ex)) { return; }
        }
    }

    private static bool IsUiaFailure(Exception ex) =>
        ex is ElementNotAvailableException or System.Runtime.InteropServices.COMException or InvalidOperationException
            or ArgumentException or TimeoutException or UnauthorizedAccessException;

    /// <summary>Builds the element description; null when it should be skipped (off-screen, zero size, not interactive).</summary>
    private static UiElementInfo? Describe(AutomationElement el, bool requireInteractive)
    {
        var rect = (System.Windows.Rect)el.GetCachedPropertyValue(AutomationElement.BoundingRectangleProperty);
        var bounds = ToScreenRect(rect);
        var controlType = ControlTypeName(el.GetCachedPropertyValue(AutomationElement.ControlTypeProperty) as ControlType);

        if (requireInteractive)
        {
            if (bounds.IsEmpty) return null;
            if (CachedBool(el, AutomationElement.IsOffscreenProperty)) return null;
            bool hasPattern =
                CachedBool(el, AutomationElement.IsInvokePatternAvailableProperty) ||
                CachedBool(el, AutomationElement.IsTogglePatternAvailableProperty) ||
                CachedBool(el, AutomationElement.IsValuePatternAvailableProperty) ||
                CachedBool(el, AutomationElement.IsSelectionItemPatternAvailableProperty) ||
                CachedBool(el, AutomationElement.IsExpandCollapsePatternAvailableProperty);
            if (!IsInteractive(controlType, hasPattern)) return null;
        }

        string name = Trim(el.GetCachedPropertyValue(AutomationElement.NameProperty) as string);
        string? automationId = el.GetCachedPropertyValue(AutomationElement.AutomationIdProperty) as string;
        bool isPassword = CachedBool(el, AutomationElement.IsPasswordProperty);
        string? value = null;
        if (!isPassword && CachedBool(el, AutomationElement.IsValuePatternAvailableProperty))
        {
            try
            {
                var v = el.GetCurrentPropertyValue(ValuePattern.ValueProperty, ignoreDefaultValue: true) as string;
                if (!string.IsNullOrEmpty(v)) value = Trim(v);
            }
            catch (Exception ex) when (IsUiaFailure(ex)) { }
        }

        return new UiElementInfo(
            name,
            controlType,
            bounds,
            string.IsNullOrEmpty(automationId) ? null : automationId,
            CachedBool(el, AutomationElement.IsEnabledProperty),
            CachedBool(el, AutomationElement.IsKeyboardFocusableProperty),
            value);
    }

    private static bool CachedBool(AutomationElement el, AutomationProperty property)
    {
        try { return el.GetCachedPropertyValue(property) is true; }
        catch (InvalidOperationException) { return false; }
    }

    internal static bool IsInteractive(string controlType, bool hasInteractivePattern) =>
        hasInteractivePattern || InteractiveTypes.Contains(controlType);

    internal static string ControlTypeName(ControlType? type)
    {
        if (type == null) return "Unknown";
        var name = type.ProgrammaticName ?? "";
        const string Prefix = "ControlType.";
        return name.StartsWith(Prefix, StringComparison.Ordinal) ? name[Prefix.Length..] : name;
    }

    internal static ScreenRect ToScreenRect(System.Windows.Rect r)
    {
        if (r.IsEmpty || double.IsNaN(r.X) || double.IsInfinity(r.Width) || double.IsInfinity(r.Height)) return default;
        int x = (int)Math.Round(r.X), y = (int)Math.Round(r.Y);
        int w = (int)Math.Round(r.Width), h = (int)Math.Round(r.Height);
        return w <= 0 || h <= 0 ? default : new ScreenRect(x, y, w, h);
    }

    /// <summary>Collapses whitespace and limits text to <see cref="MaxTextLength"/> characters.</summary>
    internal static string Trim(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= MaxTextLength ? collapsed : collapsed[..(MaxTextLength - 1)] + "…";
    }

    /// <summary>Thread-safe result list shared between the UIA thread and a caller that may give up early.</summary>
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

        public void Add(UiElementInfo item)
        {
            if (_abandoned) return;
            lock (_items)
            {
                if (_items.Count < _max) _items.Add(item);
            }
        }

        public void Abandon() => _abandoned = true;

        public IReadOnlyList<UiElementInfo> Snapshot()
        {
            lock (_items) return _items.ToList();
        }
    }
}

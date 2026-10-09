using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.ViewModels;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux.Services;

/// <summary>
/// Shows the overlay while a turn runs and keeps it out of the agent's way. Linux has no way to exclude a window
/// from screenshots, so the overlay hides itself for every capture (and waits a frame so the compositor has
/// redrawn what is behind it), and before an input action that targets its area.
/// Session callbacks arrive on any thread; all window work happens on the UI thread.
/// </summary>
public sealed class OverlayController : IInputActionObserver, ICaptureObserver, IDisposable
{
    /// <summary>How long a hide takes to reach the screen before a capture (one or two compositor frames).</summary>
    public const int CaptureHideDelayMs = 50;

    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(2);

    private readonly SettingsStore _store;
    private readonly LinuxSessionKind _sessionKind;
    private OverlayWindow? _window;
    private IAgentSession? _session;
    private bool _turnActive;
    private bool _hiddenForInput;
    private int _captures;
    private bool _hotkeyActive;
    private bool _disposed;

    public OverlayController(SettingsStore store, LinuxSessionKind sessionKind = LinuxSessionKind.X11)
    {
        _store = store;
        _sessionKind = sessionKind;
        ViewModel = new OverlayViewModel(() => StopRequested?.Invoke());
        ApplySettings();
    }

    public OverlayViewModel ViewModel { get; }

    /// <summary>The overlay's Stop button was pressed (UI thread).</summary>
    public event Action? StopRequested;

    public bool IsTurnActive => _turnActive;
    public bool IsHiddenForInput => _hiddenForInput;
    public bool IsHiddenForCapture => _captures > 0;
    public bool IsWindowVisible => _window is { IsVisible: true };
    /// <summary>The overlay window once it was first shown (null before).</summary>
    public OverlayWindow? Window => _window;

    /// <summary>Whether the overlay should be on screen right now.</summary>
    public bool ShouldBeVisible => !_disposed && _turnActive && _store.Current.Ui.ShowOverlay && !_hiddenForInput && _captures == 0;

    public void Attach(IAgentSession session)
    {
        if (_session != null) return;
        _session = session;
        session.StateChanged += OnSessionStateChanged;
        session.EventRaised += OnSessionEvent;
    }

    /// <summary>Tells the overlay whether the global stop hotkey is registered (for its hint). UI thread.</summary>
    public void SetHotkeyActive(bool active)
    {
        _hotkeyActive = active;
        ApplySettings();
    }

    /// <summary>Re-reads overlay settings (position, enabled, stop hint). UI thread.</summary>
    public void ApplySettings()
    {
        var s = _store.Current;
        ViewModel.DryRun = s.Safety.DryRun;
        ViewModel.StopHint = StopHint.OverlayText(_sessionKind, s.Ui.StopHotkey, _hotkeyActive, s.Safety.FailsafeCorner);
        UpdateVisibility();
        if (_window is { IsVisible: true } w) Place(w);
    }

    /// <summary>Applies a session state on the UI thread (also used by tests).</summary>
    public void ApplyState(AgentState state)
    {
        bool active = state is AgentState.Starting or AgentState.Running or AgentState.Stopping;
        if (active && !_turnActive) ViewModel.BeginTurn();
        ViewModel.OnState(state);
        _turnActive = active;
        if (!active) _hiddenForInput = false;
        UpdateVisibility();
    }

    private void OnSessionStateChanged(AgentState state) => Post(() => ApplyState(state));

    private void OnSessionEvent(AgentEvent e) => Post(() => ViewModel.OnEvent(e));

    // ---------------------------------------------------------------- around captures

    public async Task BeforeCaptureAsync(CancellationToken ct)
    {
        if (_disposed) return;
        bool hid = false;
        await OnUiAsync(() =>
        {
            _captures++;
            if (_window is { IsVisible: true } w)
            {
                w.Hide();
                hid = true;
            }
        }, ct).ConfigureAwait(false);
        // The unmap has to reach the screen before the capture reads it.
        if (hid) await Task.Delay(CaptureHideDelayMs, ct).ConfigureAwait(false);
    }

    public void AfterCapture()
    {
        if (_disposed) return;
        Post(() =>
        {
            if (_captures > 0) _captures--;
            UpdateVisibility();
        });
    }

    // ---------------------------------------------------------------- around input

    public async Task BeforeInputAsync(ScreenPoint? target, CancellationToken ct)
    {
        if (target is not { } point || _disposed) return;
        await OnUiAsync(() =>
        {
            if (_window is not { IsVisible: true } w) return;
            if (OverlayPlacement.ShouldHide(w.PhysicalBounds, point))
            {
                _hiddenForInput = true;
                w.Hide();
            }
        }, ct).ConfigureAwait(false);
    }

    public void AfterInput()
    {
        if (_disposed) return;
        Post(() =>
        {
            if (!_hiddenForInput) return;
            _hiddenForInput = false;
            UpdateVisibility();
        });
    }

    // ---------------------------------------------------------------- window

    private void UpdateVisibility()
    {
        if (ShouldBeVisible) ShowOverlay();
        else if (_window is { IsVisible: true } w) w.Hide();
    }

    private void ShowOverlay()
    {
        try
        {
            var w = EnsureWindow();
            if (!w.IsVisible)
            {
                Place(w);
                // Re-applied on every map: the window manager reads the hint when the window is mapped.
                X11WindowHints.TrySetNoInput(w);
                w.Show();
            }
            Place(w);
        }
        catch (Exception ex)
        {
            // The overlay is a convenience; never let it break a run.
            Log.Error("Overlay could not be shown", ex);
        }
    }

    private OverlayWindow EnsureWindow()
    {
        if (_window != null) return _window;
        var w = new OverlayWindow(ViewModel);
        w.Resized += (_, _) => { if (w.IsVisible) Place(w); };
        w.ScalingChanged += (_, _) => { if (w.IsVisible) Place(w); };
        _window = w;
        return w;
    }

    /// <summary>Puts the window in the configured corner of the primary screen's working area.</summary>
    private void Place(OverlayWindow w)
    {
        Avalonia.Platform.Screen? screen = null;
        try { screen = w.Screens.Primary ?? w.Screens.All.FirstOrDefault(); }
        catch (Exception) { }
        if (screen == null) return;

        var wa = screen.WorkingArea;
        double scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var size = w.IsVisible ? w.ClientSize : MeasureSize(w);
        int width = (int)Math.Ceiling(size.Width * scale);
        int height = (int)Math.Ceiling(size.Height * scale);
        var p = OverlayPlacement.Compute(new ScreenRect(wa.X, wa.Y, wa.Width, wa.Height), width, height,
            _store.Current.Ui.OverlayPosition, (int)Math.Round(16 * scale));
        var target = new PixelPoint(p.X, p.Y);
        if (w.Position != target) w.Position = target;
    }

    private static Size MeasureSize(OverlayWindow w)
    {
        double width = double.IsFinite(w.Width) && w.Width > 0 ? w.Width : 400;
        if (w.Content is Control content)
        {
            content.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, content.DesiredSize.Height);
        }
        return new Size(width, 90);
    }

    private static async Task OnUiAsync(Action action, CancellationToken ct)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        var op = Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Send, ct);
        // Never let a stuck UI thread (shutting down) block a tool call.
        var finished = await Task.WhenAny(op.GetTask(), Task.Delay(UiTimeout, ct)).ConfigureAwait(false);
        if (finished != op.GetTask()) Log.Warn("Overlay did not answer in time");
        else await op.GetTask().ConfigureAwait(false);
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) action();
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_session != null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            _session.EventRaised -= OnSessionEvent;
        }
        var w = _window;
        _window = null;
        if (w == null) return;
        if (Dispatcher.UIThread.CheckAccess()) w.Close();
        else Dispatcher.UIThread.Post(w.Close);
    }
}

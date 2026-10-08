using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.ViewModels;
using DeskPilot.Views;

namespace DeskPilot.Services;

/// <summary>
/// Shows the overlay while a turn runs and keeps it out of the agent's way: before an input action
/// that targets the overlay's area it hides the overlay, and shows it again afterwards.
/// Session callbacks arrive on any thread; all window work happens on the UI thread.
/// </summary>
public sealed class OverlayController : IInputActionObserver, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly SettingsStore _store;
    private OverlayWindow? _window;
    private IAgentSession? _session;
    private bool _turnActive;
    private bool _hiddenForInput;
    private bool _disposed;

    public OverlayController(Dispatcher dispatcher, SettingsStore store)
    {
        _dispatcher = dispatcher;
        _store = store;
        ViewModel = new OverlayViewModel(() => StopRequested?.Invoke());
        ApplySettings();
    }

    public OverlayViewModel ViewModel { get; }

    /// <summary>The overlay's Stop button was pressed (UI thread).</summary>
    public event Action? StopRequested;

    public bool IsTurnActive => _turnActive;
    public bool IsHiddenForInput => _hiddenForInput;

    /// <summary>Whether the overlay should be on screen right now.</summary>
    public bool ShouldBeVisible => !_disposed && _turnActive && _store.Current.Ui.ShowOverlay && !_hiddenForInput;

    public void Attach(IAgentSession session)
    {
        if (_session != null) return;
        _session = session;
        session.StateChanged += OnSessionStateChanged;
        session.EventRaised += OnSessionEvent;
    }

    /// <summary>Re-reads overlay settings (position, enabled, hotkey hint). UI thread.</summary>
    public void ApplySettings()
    {
        var s = _store.Current;
        ViewModel.DryRun = s.Safety.DryRun;
        ViewModel.HotkeyHint = HotkeyGesture.TryParse(s.Ui.StopHotkey, out var g, out _) ? g!.Display : "";
        UpdateVisibility();
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

    public async Task BeforeInputAsync(ScreenPoint? target, CancellationToken ct)
    {
        if (target is not { } point || _disposed || _dispatcher.HasShutdownStarted) return;
        await _dispatcher.InvokeAsync(() =>
        {
            if (_window is not { IsVisible: true } w) return;
            var bounds = Win32.GetPhysicalBounds(w.Handle);
            if (bounds is { } b && OverlayPlacement.ShouldHide(b, point))
            {
                _hiddenForInput = true;
                w.Hide();
            }
        }, DispatcherPriority.Send, ct);
    }

    public void AfterInput()
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
        {
            if (!_hiddenForInput) return;
            _hiddenForInput = false;
            UpdateVisibility();
        });
    }

    private void UpdateVisibility()
    {
        if (ShouldBeVisible) ShowOverlay();
        else _window?.Hide();
    }

    private void ShowOverlay()
    {
        try
        {
            var w = EnsureWindow();
            if (!w.IsVisible)
            {
                PlaceBeforeShow(w);
                w.Show();
            }
            Reposition(w);
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
        w.SizeChanged += (_, _) => { if (w.IsVisible) Reposition(w); };
        w.DpiChanged += (_, _) => { if (w.IsVisible) Reposition(w); };
        // Create the HWND now so capture exclusion and no-activate apply before it is ever shown.
        new WindowInteropHelper(w).EnsureHandle();
        _window = w;
        return w;
    }

    private (ScreenRect WorkArea, double Scale) PrimaryWorkArea()
    {
        if (Win32.GetPrimaryWorkArea() is { } wa) return wa;
        var r = SystemParameters.WorkArea;
        return (new ScreenRect((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height), 1.0);
    }

    /// <summary>Estimates the final position from the measured size so the first frame is already in place.</summary>
    private void PlaceBeforeShow(OverlayWindow w)
    {
        var (work, scale) = PrimaryWorkArea();
        if (w.Content is FrameworkElement content)
        {
            content.Measure(new Size(w.Width, double.PositiveInfinity));
            int width = (int)Math.Round(w.Width * scale);
            int height = (int)Math.Round(content.DesiredSize.Height * scale);
            var p = OverlayPlacement.Compute(work, width, height, _store.Current.Ui.OverlayPosition, (int)Math.Round(16 * scale));
            w.Left = p.X / scale;
            w.Top = p.Y / scale;
        }
    }

    private void Reposition(OverlayWindow w)
    {
        var hwnd = w.Handle;
        if (Win32.GetPhysicalBounds(hwnd) is not { } bounds) return;
        var (work, scale) = PrimaryWorkArea();
        var p = OverlayPlacement.Compute(work, bounds.Width, bounds.Height, _store.Current.Ui.OverlayPosition, (int)Math.Round(16 * scale));
        if (p.X == bounds.X && p.Y == bounds.Y) return;
        Win32.SetWindowPos(hwnd, Win32.HWND_TOPMOST, p.X, p.Y, 0, 0, Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
    }

    private void Post(Action action)
    {
        if (_disposed || _dispatcher.HasShutdownStarted) return;
        _dispatcher.BeginInvoke(() =>
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
        if (_window != null && _dispatcher.CheckAccess())
        {
            _window.Close();
            _window = null;
        }
    }
}

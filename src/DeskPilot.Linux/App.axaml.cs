using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Agent;
using DeskPilot.Core.Backends.Http;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.Services;
using DeskPilot.Linux.ViewModels;
using DeskPilot.Linux.Views;

namespace DeskPilot.Linux;

/// <summary>
/// App lifecycle: crash handling, service wiring, tray icon, global stop hotkey, first-run welcome and an orderly
/// shutdown. Program.Main handles --mcp-bridge and the single-instance check before this exists.
/// </summary>
public partial class App : Application
{
    public const string MinimizedSwitch = "--minimized";
    /// <summary>Kept for parity with the Windows app (restart hand-off); a Linux restart needs no special handling.</summary>
    public const string RestartSwitch = "--after-restart";

    private IClassicDesktopStyleApplicationLifetime? _lifetime;
    private LinuxSessionInfo _sessionInfo = new(LinuxSessionKind.None, null, null, null, null);
    private SettingsStore? _store;
    private OverlayController? _overlay;
    private IAgentSession? _session;
    private MainViewModel? _viewModel;
    private MainWindow? _main;
    private StopHotkeyService? _hotkey;
    private TrayIconService? _tray;
    private string? _registeredHotkey;
    private bool _exiting;
    private bool _showingError;
    private int _errorsInWindow;
    private DateTime _errorWindowStart = DateTime.MinValue;

    /// <summary>Set by Program.Main when this process owns the single-instance socket.</summary>
    internal static SingleInstanceGuard? InstanceGuard { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // Headless tests and the designer have no desktop lifetime: only resources and styles load then.
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _lifetime = desktop;
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.ShutdownRequested += OnShutdownRequested;
            InstallExceptionHandlers();
            var args = desktop.Args ?? Array.Empty<string>();
            Dispatcher.UIThread.Post(() => _ = StartAsync(args));
        }
        base.OnFrameworkInitializationCompleted();
    }

    public MainWindow? Main => _main;
    public MainViewModel? ViewModel => _viewModel;

    private async Task StartAsync(string[] args)
    {
        try
        {
            Log.Info($"DeskPilot {typeof(App).Assembly.GetName().Version} starting (Linux)");
            bool startMinimized = args.Any(a => string.Equals(a, MinimizedSwitch, StringComparison.Ordinal));

            _sessionInfo = LinuxSession.Detect();
            Log.Info($"Session: {_sessionInfo.Kind}, desktop {_sessionInfo.Desktop ?? "unknown"}");
            _store = new SettingsStore();

            DesktopServices desktop;
            try
            {
                var store = _store;
                desktop = LinuxDesktopFactory.CreateDefault(() => store.Current);
            }
            catch (Exception ex)
            {
                Log.Error("Desktop services could not be created", ex);
                ShowStartupError(ex.Message);
                return;
            }

            _overlay = new OverlayController(_store, _sessionInfo.Kind);
            var confirmation = new AvaloniaUserConfirmation(desktop.Windows);
            _session = new AgentSession(_store, desktop, confirmation, _overlay, Environment.ProcessPath!);
            var catalog = new ModelCatalog();
            var detector = new EnvironmentDetector(desktop.Screen, desktop.Windows) { Settings = () => _store.Current };

            _overlay.Attach(_session);
            _overlay.StopRequested += () => _ = StopSessionAsync("Stopped by user");

            _viewModel = new MainViewModel(_store, _session, catalog, detector, AvaloniaUiDispatcher.Instance, sessionKind: _sessionInfo.Kind);
            _main = new MainWindow(_viewModel);
            _main.ExitRequested += (_, _) => _ = ExitAsync();
            _main.HiddenToTray += (_, _) => OnHiddenToTray();

            _tray = CreateTray();
            _session.StateChanged += state => Dispatcher.UIThread.Post(() => _tray?.SetState(state));

            _hotkey = new StopHotkeyService(_sessionInfo.Kind);
            _hotkey.Pressed += () => Dispatcher.UIThread.Post(() => _ = StopSessionAsync("Stopped with the hotkey"));
            RegisterStopHotkey();

            _store.Changed += _ => Dispatcher.UIThread.Post(OnSettingsChanged);
            if (InstanceGuard != null) InstanceGuard.ActivationRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);

            if (_store.LoadError != null)
            {
                Log.Warn(_store.LoadError);
                _viewModel.Conversation.AddStatus(_store.LoadError, StatusLevel.Warning);
            }

            bool firstRun = !_store.Current.FirstRunCompleted;
            // The welcome dialog needs a visible owner, so a first run always shows the window.
            if (!startMinimized || firstRun) ShowMainWindow();
            else if (_tray == null) ShowMainWindow(); // no tray to come back from

            if (firstRun) await RunFirstRunAsync(detector);
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            await MessageWindow.ShowAsync(_main, "DeskPilot could not start",
                $"{ex.Message}\n\nDetails are in the log file:\n{Log.FilePath}", MessageKind.Error);
            await ExitAsync(askIfBusy: false);
        }
    }

    private void ShowStartupError(string message)
    {
        IReadOnlyList<string> notes;
        try
        {
            notes = LinuxDesktopFactory.DescribeMissingTools(_sessionInfo);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list missing tools: {ex.Message}");
            notes = Array.Empty<string>();
        }
        if (_sessionInfo.Kind == LinuxSessionKind.None && notes.Count == 0)
            notes = new[] { "No graphical session was found (neither WAYLAND_DISPLAY nor DISPLAY is set). Start DeskPilot from your desktop session." };

        string? logPath = null;
        try { logPath = Log.FilePath; }
        catch (Exception) { }

        var window = new StartupErrorWindow(message, notes, logPath);
        window.ExitRequested += (_, _) => _ = ExitAsync(askIfBusy: false);
        window.Show();
    }

    // ---------------------------------------------------------------- first run

    private async Task RunFirstRunAsync(IEnvironmentDetector detector)
    {
        EnvironmentReport report;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            report = await detector.DetectAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            Log.Error("Environment detection failed", ex);
            report = EmptyEnvironmentReport();
        }

        try
        {
            var copy = _store!.CloneCurrent();
            ProfileAutoConfig.ApplyDetected(copy, report);
            _store.Save(copy);
        }
        catch (Exception ex)
        {
            Log.Error("Adding detected providers failed", ex);
        }

        IReadOnlyList<string> notes;
        try { var session = _sessionInfo; notes = await Task.Run(() => LinuxDesktopFactory.DescribeMissingTools(session)); }
        catch (Exception ex)
        {
            Log.Warn($"Could not list missing tools: {ex.Message}");
            notes = Array.Empty<string>();
        }

        try
        {
            if (_main is { IsVisible: true })
            {
                var welcome = new WelcomeWindow(_store!, report, notes);
                await welcome.ShowDialog<bool>(_main);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Welcome window failed", ex);
        }

        _viewModel?.RefreshFromSettings();
    }

    /// <summary>Used when detection fails: nothing found, nothing assumed.</summary>
    public static EnvironmentReport EmptyEnvironmentReport()
    {
        static CliToolStatus Cli(string name) => new(name, null, null, null, "not checked");
        return new EnvironmentReport(
            Cli("claude"), Cli("gemini"), Cli("codex"),
            new LocalServerStatus("Ollama", ProviderPresets.Find(ProviderPresets.OllamaId)?.BaseUrl ?? "", false, Array.Empty<ModelInfo>()),
            new LocalServerStatus("LM Studio", ProviderPresets.Find(ProviderPresets.LmStudioId)?.BaseUrl ?? "", false, Array.Empty<ModelInfo>()),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<MonitorInfo>(), false);
    }

    // ---------------------------------------------------------------- windows, tray, hotkey

    public void ShowMainWindow()
    {
        if (_exiting || _main == null) return;
        _main.ShowAndActivate();
    }

    private void OnHiddenToTray()
    {
        Log.Info(_tray != null
            ? "Main window hidden to the tray"
            : "Main window hidden; no tray icon is available, start DeskPilot again to show it");
    }

    private TrayIconService? CreateTray()
    {
        try
        {
            var tray = new TrayIconService();
            // Posted so the tray menu closes before any window or dialog opens.
            tray.ShowRequested += () => Dispatcher.UIThread.Post(ShowMainWindow);
            tray.StopRequested += () => Dispatcher.UIThread.Post(() => _ = StopSessionAsync("Stopped from the tray icon"));
            tray.SettingsRequested += () => Dispatcher.UIThread.Post(() =>
            {
                ShowMainWindow();
                if (_main != null) _ = _main.OpenSettingsAsync();
            });
            tray.ExitRequested += () => Dispatcher.UIThread.Post(() => _ = ExitAsync());
            return tray;
        }
        catch (Exception ex)
        {
            Log.Error("Tray icon could not be created", ex);
            return null;
        }
    }

    private void RegisterStopHotkey()
    {
        if (_hotkey == null || _store == null || _viewModel == null) return;
        var text = _store.Current.Ui.StopHotkey;
        _registeredHotkey = text;

        if (!_hotkey.IsSupported)
        {
            _viewModel.SetHotkeyStatus(false, null);
            _overlay?.SetHotkeyActive(false);
            return;
        }

        bool ok = _hotkey.Register(text, out var error);
        _viewModel.SetHotkeyStatus(ok, error);
        _overlay?.SetHotkeyActive(ok);
        if (!ok && !string.IsNullOrWhiteSpace(text))
        {
            _viewModel.Conversation.AddStatus(
                $"The stop hotkey {StopHotkey.Display(text)} is not active: {error}. The Stop button, the overlay and the tray icon still work; choose another hotkey in Settings > Interface.",
                StatusLevel.Warning);
        }
    }

    private void OnSettingsChanged()
    {
        if (_exiting || _store == null) return;
        if (!string.Equals(_store.Current.Ui.StopHotkey, _registeredHotkey, StringComparison.Ordinal)) RegisterStopHotkey();
        _overlay?.ApplySettings();
    }

    private async Task StopSessionAsync(string reason)
    {
        if (_session == null) return;
        try
        {
            await _session.StopAsync(reason);
        }
        catch (Exception ex)
        {
            Log.Error("Stop failed", ex);
        }
    }

    // ---------------------------------------------------------------- shutdown

    /// <summary>Stops the agent, removes the tray icon and hotkey, disposes the session (bounded wait) and exits.</summary>
    public async Task ExitAsync(bool askIfBusy = true)
    {
        if (_exiting) return;
        if (askIfBusy && _viewModel is { IsBusy: true } && _main is { IsVisible: true })
        {
            bool stop = await MessageWindow.ShowAsync(_main, "DeskPilot is working on a task",
                "Stop it and exit?", MessageKind.Question, primary: "Stop and exit", secondary: "Keep working");
            if (!stop) return;
        }
        _exiting = true;
        Log.Info("DeskPilot exiting");

        try { _main?.SaveBounds(); }
        catch (Exception ex) { Log.Error("Saving the window position failed", ex); }

        _tray?.Dispose();
        _tray = null;
        _hotkey?.Dispose();
        _hotkey = null;
        _overlay?.Dispose();
        _main?.Hide();

        if (_session != null)
        {
            try
            {
                var dispose = _session.DisposeAsync().AsTask();
                var finished = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromSeconds(5)));
                if (finished == dispose) await dispose;
                else Log.Warn("The agent session did not shut down within 5 seconds");
            }
            catch (Exception ex)
            {
                Log.Error("Session shutdown failed", ex);
            }
        }

        _viewModel?.Dispose();
        if (_main != null)
        {
            _main.AllowClose = true;
            _main.Close();
        }
        _lifetime?.Shutdown();
    }

    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        if (_exiting) return;
        // The session is ending (log out, shutdown): no dialogs, but take the few seconds needed to stop the
        // agent's child processes; ExitAsync ends with an explicit Shutdown.
        e.Cancel = true;
        try { _main?.SaveBounds(); }
        catch (Exception) { }
        _tray?.Dispose();
        _tray = null;
        _ = ExitAsync(askIfBusy: false);
    }

    // ---------------------------------------------------------------- crash handling

    private void InstallExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log.Error("Unhandled UI exception", e.Exception);
            ReportError(e.Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);
        };
    }

    /// <summary>Shows at most a few dialogs per half minute so a repeating error can never become a dialog loop.</summary>
    private void ReportError(Exception ex)
    {
        var now = DateTime.UtcNow;
        if (now - _errorWindowStart > TimeSpan.FromSeconds(30))
        {
            _errorWindowStart = now;
            _errorsInWindow = 0;
        }
        _errorsInWindow++;
        if (_showingError || _errorsInWindow > 2 || _exiting) return;

        _showingError = true;
        _ = ShowErrorAsync(ex);
    }

    private async Task ShowErrorAsync(Exception ex)
    {
        try
        {
            await MessageWindow.ShowAsync(_main, "Something went wrong",
                $"{ex.Message}\n\nDeskPilot keeps running. Details were written to the log file:\n{Log.FilePath}", MessageKind.Warning);
        }
        catch (Exception)
        {
            // Showing the message failed too; it is in the log.
        }
        finally
        {
            _showingError = false;
        }
    }
}

using System.Windows;
using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Agent;
using DeskPilot.Core.Backends.Http;
using DeskPilot.Desktop.Windows;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using DeskPilot.Views;

namespace DeskPilot;

/// <summary>
/// App lifecycle: single instance, crash handling, service wiring, tray icon, global stop hotkey,
/// first-run welcome and an orderly shutdown. Program.Main handles --mcp-bridge before this exists.
/// </summary>
public partial class App : Application
{
    public const string MinimizedSwitch = "--minimized";
    /// <summary>Passed by "Restart as administrator": wait for the old instance to exit instead of handing over to it.</summary>
    public const string RestartSwitch = "--after-restart";

    private SingleInstanceGuard? _instance;
    private SettingsStore? _store;
    private OverlayController? _overlay;
    private IAgentSession? _session;
    private MainViewModel? _viewModel;
    private MainWindow? _main;
    private HotkeyService? _hotkey;
    private TrayIconService? _tray;
    private string? _registeredHotkey;
    private bool _exiting;
    private bool _trayHintShown;
    private bool _showingError;
    private int _errorsInWindow;
    private DateTime _errorWindowStart = DateTime.MinValue;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = new SingleInstanceGuard(SingleInstanceGuard.DefaultBaseName());
        bool acquired = _instance.TryAcquire();
        if (!acquired && e.Args.Any(a => string.Equals(a, RestartSwitch, StringComparison.OrdinalIgnoreCase)))
        {
            // The previous instance is still shutting down after asking for this restart.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!acquired && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(250);
                acquired = _instance.TryAcquire();
            }
        }
        if (!acquired)
        {
            // Another DeskPilot is running for this user: bring it forward and leave.
            _instance.SignalFirstInstance();
            _instance.Dispose();
            _instance = null;
            Shutdown(0);
            return;
        }

        InstallExceptionHandlers();
        _ = StartAsync(e.Args);
    }

    private async Task StartAsync(string[] args)
    {
        try
        {
            Log.Info($"DeskPilot {typeof(App).Assembly.GetName().Version} starting");
            bool startMinimized = args.Any(a => string.Equals(a, MinimizedSwitch, StringComparison.OrdinalIgnoreCase));

            _store = new SettingsStore();
            var desktop = DesktopFactory.CreateDefault();
            _overlay = new OverlayController(Dispatcher, _store);
            var confirmation = new WpfUserConfirmation(Dispatcher);
            _session = new AgentSession(_store, desktop, confirmation, _overlay, Environment.ProcessPath!);
            var catalog = new ModelCatalog();
            var detector = new EnvironmentDetector(desktop.Screen, desktop.Windows) { Settings = () => _store.Current };

            _overlay.Attach(_session);
            _overlay.StopRequested += () => _ = StopSessionAsync("Stopped by user");

            _viewModel = new MainViewModel(_store, _session, catalog, detector, new WpfUiDispatcher(Dispatcher));
            _main = new MainWindow(_viewModel);
            _main.ExitRequested += (_, _) => _ = ExitAsync();
            _main.HiddenToTray += (_, _) => OnHiddenToTray();
            MainWindow = _main;

            _tray = CreateTray();
            _session.StateChanged += state => Dispatcher.BeginInvoke(() => _tray?.SetState(state));

            _hotkey = new HotkeyService();
            _hotkey.Pressed += () => _ = StopSessionAsync("Stopped with the hotkey");
            RegisterStopHotkey();

            _store.Changed += _ => Dispatcher.BeginInvoke(OnSettingsChanged);
            _instance!.ActivationRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);

            if (_store.LoadError != null)
            {
                Log.Warn(_store.LoadError);
                _viewModel.Conversation.AddStatus(_store.LoadError, StatusLevel.Warning);
            }

            if (!startMinimized) ShowMainWindow();

            if (!_store.Current.FirstRunCompleted) await RunFirstRunAsync(detector);
        }
        catch (Exception ex)
        {
            Log.Error("Startup failed", ex);
            MessageBox.Show(
                $"DeskPilot could not start.\n\n{ex.Message}\n\nDetails are in the log file:\n{Log.FilePath}",
                "DeskPilot", MessageBoxButton.OK, MessageBoxImage.Error);
            await ExitAsync(askIfBusy: false);
        }
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

        try
        {
            var welcome = new WelcomeWindow(_store!, report);
            if (_main is { IsVisible: true }) welcome.Owner = _main;
            else welcome.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            NativeUi.Prepare(welcome);
            welcome.ShowDialog();
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
        if (_trayHintShown) return;
        _trayHintShown = true;
        _tray?.ShowBalloon("DeskPilot is still running", "Double-click the tray icon to open it again. Use Exit in its menu to quit.");
    }

    private TrayIconService? CreateTray()
    {
        try
        {
            var tray = new TrayIconService();
            // Deferred so the tray menu closes before any window or dialog opens.
            tray.ShowRequested += () => Dispatcher.BeginInvoke(ShowMainWindow);
            tray.StopRequested += () => Dispatcher.BeginInvoke(() => _ = StopSessionAsync("Stopped from the tray icon"));
            tray.SettingsRequested += () => Dispatcher.BeginInvoke(() =>
            {
                ShowMainWindow();
                _main?.OpenSettings();
            });
            tray.ExitRequested += () => Dispatcher.BeginInvoke(() => _ = ExitAsync());
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
        if (string.IsNullOrWhiteSpace(text))
        {
            _hotkey.Unregister();
            _viewModel.SetHotkeyStatus(false, "no hotkey is set");
            return;
        }

        bool ok = _hotkey.Register(text, out var error);
        _viewModel.SetHotkeyStatus(ok, error);
        if (!ok)
        {
            _viewModel.Conversation.AddStatus(
                $"The stop hotkey {text} is not active: {error}. The Stop button and the tray icon still work; choose another hotkey in Settings > Interface.",
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
            var answer = MessageBox.Show(_main, "DeskPilot is working on a task. Stop it and exit?", "DeskPilot",
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes) return;
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
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows is logging off: no dialogs, just leave cleanly.
        try { _main?.SaveBounds(); }
        catch (Exception) { }
        _tray?.Dispose();
        _tray = null;
        _ = ExitAsync(askIfBusy: false);
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hotkey?.Dispose();
        _instance?.Dispose();
        _instance = null;
        base.OnExit(e);
    }

    // ---------------------------------------------------------------- crash handling

    private void InstallExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error($"Unhandled exception (terminating: {e.IsTerminating})", e.ExceptionObject as Exception);
            // Do not leave a ghost tray icon behind if the process is going down.
            if (e.IsTerminating)
            {
                try { Dispatcher.Invoke(() => _tray?.Dispose(), DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(1)); }
                catch (Exception) { }
            }
        };
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log.Error("Unhandled UI exception", e.Exception);
        ReportError(e.Exception);
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
        try
        {
            MessageBox.Show(
                $"Something went wrong: {ex.Message}\n\nDeskPilot keeps running. Details were written to the log file:\n{Log.FilePath}",
                "DeskPilot", MessageBoxButton.OK, MessageBoxImage.Warning);
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

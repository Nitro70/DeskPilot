using System.Diagnostics;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Acp;
using DeskPilot.Core.Backends.Cli;
using DeskPilot.Core.Backends.Http;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;
using DeskPilot.Core.Vault;

namespace DeskPilot.Core.Agent;

/// <summary>
/// The agent session the UI drives: owns the tool stack (computer + vault tools behind the safety guard,
/// wrapped by ObservedToolHost), starts the backend for the active profile lazily, restarts it when the
/// relevant configuration changes, and turns every failure into a TurnResult instead of an exception.
/// </summary>
public sealed class AgentSession : IAgentSession
{
    internal const string BusyMessage = "DeskPilot is already working on a request";
    internal const string NewConversationMessage = "Started a new conversation";

    private static readonly TimeSpan InterruptTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackendDisposeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TurnEndTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReloadGateTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan StoppingPublishWait = TimeSpan.FromMilliseconds(100);

    private readonly SettingsStore _store;
    private readonly DesktopServices _desktop;
    private readonly string _bridgeExePath;
    private readonly Func<ProviderProfile, IAgentBackend> _backendFactory;
    private readonly AgentSessionHooks _hooks;
    private readonly AgentRunControl _control = new();
    private readonly ComputerToolHost? _computer;
    private readonly VaultToolHost? _vault;
    private readonly ObservedToolHost _tools;
    private readonly string? _toolInitError;

    // Held for the whole of a turn and for every backend replacement, so a backend is never
    // disposed or reset while a turn is using it.
    private readonly SemaphoreSlim _backendGate = new(1, 1);
    private readonly object _stateGate = new();
    // Serializes StateChanged so observers see transitions in the order they happened.
    private readonly object _statePublishGate = new();

    private IAgentBackend? _backend;
    private string? _backendFingerprint;
    private string? _lastSeenFingerprint;
    private volatile bool _forceRestart;
    private IMcpHost? _mcp;
    private bool _busy;
    private bool _turnEnding;
    // 1 once this turn's stop has been announced and its interrupt sent; whoever sees the stop first does both.
    private int _stopHandled;
    private int _disposed;
    private AgentState _state = AgentState.Idle;

    /// <param name="bridgeExePath">Path of DeskPilot.exe (Environment.ProcessPath) used to launch the MCP bridge.</param>
    /// <param name="backendFactory">Optional override for tests; default creates the backend for the profile's ProviderKind.</param>
    public AgentSession(SettingsStore settings, DesktopServices desktop, IUserConfirmation? confirmation, IInputActionObserver? observer,
        string bridgeExePath, Func<ProviderProfile, IAgentBackend>? backendFactory = null)
        : this(settings, desktop, confirmation, observer, bridgeExePath, backendFactory, null)
    {
    }

    internal AgentSession(SettingsStore settings, DesktopServices desktop, IUserConfirmation? confirmation, IInputActionObserver? observer,
        string bridgeExePath, Func<ProviderProfile, IAgentBackend>? backendFactory, AgentSessionHooks? hooks)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(desktop);
        _store = settings;
        _desktop = desktop;
        _bridgeExePath = string.IsNullOrWhiteSpace(bridgeExePath) ? Environment.ProcessPath ?? "" : bridgeExePath;
        _backendFactory = backendFactory ?? CreateDefaultBackend;
        _hooks = hooks ?? new AgentSessionHooks();

        IToolHost inner;
        if (_hooks.Tools != null)
        {
            inner = new GuardedToolHost(_hooks.Tools, "Injected");
        }
        else
        {
            ISafetyGuard guard;
            try
            {
                guard = new SafetyGuard(() => _store.Current, desktop.Windows, desktop.Ui);
            }
            catch (Exception ex)
            {
                Log.Error("The safety guard could not be created; every computer action will be denied", ex);
                guard = new DenyAllSafetyGuard("DeskPilot's safety guard failed to start, so actions are disabled. Restart DeskPilot.");
            }

            IToolHost computerTools;
            try
            {
                _computer = new ComputerToolHost(desktop, guard, () => _store.Current, _control, confirmation, observer);
                computerTools = new GuardedToolHost(_computer, "Computer");
            }
            catch (Exception ex)
            {
                Log.Error("The computer tools could not be created", ex);
                _toolInitError = $"the computer tools could not start ({ex.Message})";
                computerTools = new UnavailableToolHost(_toolInitError);
            }

            IToolHost vaultTools;
            try
            {
                _vault = new VaultToolHost(() => _store.Current);
                vaultTools = new GuardedToolHost(_vault, "Vault");
            }
            catch (Exception ex)
            {
                Log.Error("The vault tools could not be created", ex);
                vaultTools = new UnavailableToolHost("the vault tools could not start");
            }

            inner = new CompositeToolHost(computerTools, vaultTools);
        }

        _tools = new ObservedToolHost(inner, _control, () => _store.Current.Safety?.MaxStepsPerTurn ?? 80, Raise);
        _control.StopRequested += OnStopRequested;

        try
        {
            var s = _store.Current;
            _lastSeenFingerprint = SessionConfig.ComputeFingerprint(s, s.ActiveProfile, IsVaultAvailable());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not fingerprint the initial settings: {ex.Message}");
        }
    }

    public event Action<AgentEvent>? EventRaised;
    public event Action<AgentState>? StateChanged;

    public AgentState State
    {
        get { lock (_stateGate) return _state; }
    }

    public string ActiveDescription => SessionConfig.DescribeProfile(_store.Current.ActiveProfile);

    /// <summary>The tools exactly as backends and the MCP server see them (observed, guarded).</summary>
    internal IToolHost Tools => _tools;

    internal AgentRunControl Control => _control;

    private bool IsBusy
    {
        get { lock (_stateGate) return _busy; }
    }

    /// <summary>The backend each provider kind uses.</summary>
    internal static IAgentBackend CreateDefaultBackend(ProviderProfile profile) => profile.Kind switch
    {
        ProviderKind.ClaudeCli => new ClaudeCliBackend(),
        ProviderKind.AcpAgent => new AcpBackend(),
        ProviderKind.AnthropicApi or ProviderKind.OpenAiCompatible or ProviderKind.Ollama => new HttpAgentBackend(),
        _ => throw new NotSupportedException($"Unsupported provider kind '{profile.Kind}'"),
    };

    public async Task<TurnResult> SendAsync(string message, CancellationToken ct = default)
    {
        message ??= "";
        if (Volatile.Read(ref _disposed) != 0) return Rejected("DeskPilot is closing");
        if (string.IsNullOrWhiteSpace(message)) return Rejected("Type a message first");

        lock (_stateGate)
        {
            if (_busy) return Rejected(BusyMessage);
            _busy = true;
            _turnEnding = false;
            _stopHandled = 0;
            // Reset the stop signal while holding the lock: StopAsync only acts when _busy is set, so a stop
            // that arrives at any point of this turn (even during the backend start) is never lost.
            _control.BeginTurn();
        }

        var sw = Stopwatch.StartNew();
        var settings = _store.Current;
        var profile = settings.ActiveProfile;
        TurnResult result;
        var gateHeld = false;
        CancellationTokenSource? linked = null;
        CancellationTokenRegistration callerStop = default;
        CancellationTokenRegistration controlStop = default;

        try
        {
            // Raised before the backend starts so the user's message shows at once, even when the start is slow.
            Raise(new UserMessageEvent(message));

            linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _control.Token);
            // Registered after the linked source, so it runs before the turn sees the cancellation (callbacks run
            // last-registered first). StopRequested is raised only after the cancellation, when the turn may
            // already be over.
            controlStop = _control.Token.Register(OnControlTokenCancelled);
            // Cancelling the caller's token is a stop like any other: tools are refused and the model is interrupted.
            if (ct.CanBeCanceled) callerStop = ct.Register(StopFromCallerToken);

            try
            {
                await _backendGate.WaitAsync(linked.Token).ConfigureAwait(false);
                gateHeld = true;

                var (backend, failure) = await EnsureBackendAsync(settings, profile, sw, linked.Token).ConfigureAwait(false);
                result = failure ?? await RunBackendTurnAsync(backend!, message, profile, sw, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (linked.IsCancellationRequested)
            {
                result = Cancelled(profile, sw);
            }
            catch (Exception ex)
            {
                Log.Error("The turn failed unexpectedly", ex);
                var text = $"Something went wrong: {ex.Message}";
                Raise(new StatusEvent(text, StatusLevel.Error));
                result = Failed(text, profile, sw);
                // The backend broke its contract by throwing; start a fresh one next time rather than reuse it.
                _forceRestart = true;
            }

            // From here on a late stop must not announce "Stopping" after the turn has already ended.
            lock (_stateGate) _turnEnding = true;
            result = Normalize(result, profile, sw);
            // Still holding the gate, so a NewConversation waiting on this turn announces itself after this event.
            Raise(new TurnCompletedEvent(result));
            LogTurn(message, profile, result, sw.Elapsed);
        }
        finally
        {
            callerStop.Dispose();
            controlStop.Dispose();
            linked?.Dispose();
            if (gateHeld) _backendGate.Release();
            // Idle is announced while still busy so observers never see it after the next turn's Starting/Running.
            SetState(AgentState.Idle);
            lock (_stateGate) _busy = false;
        }
        return result;
    }

    public async Task StopAsync(string reason = "Stopped by user")
    {
        if (!IsBusy) return;
        if (string.IsNullOrWhiteSpace(reason)) reason = "Stopped by user";

        RequestStopAnnounced(reason);
        var backend = Volatile.Read(ref _backend);
        if (backend != null) await InterruptSafeAsync(backend).ConfigureAwait(false);
    }

    public async Task NewConversationAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        if (IsBusy) await StopAsync("New conversation").ConfigureAwait(false);

        // The running turn holds the gate until it has ended.
        var gotGate = await _backendGate.WaitAsync(TurnEndTimeout).ConfigureAwait(false);
        try
        {
            if (!gotGate)
            {
                Log.Warn("The previous turn did not end in time; the model will restart before the next message");
                _forceRestart = true;
            }
            else if (Volatile.Read(ref _backend) is { } backend)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TurnEndTimeout);
                    await backend.ResetConversationAsync(cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // A fresh backend is a fresh conversation too.
                    Log.Error("Resetting the conversation failed; restarting the model instead", ex);
                    await DisposeBackendAsync(backend).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (gotGate) _backendGate.Release();
        }

        Raise(new StatusEvent(NewConversationMessage));
    }

    public async Task ReloadSettingsAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var settings = _store.Current;
        var fingerprint = SessionConfig.ComputeFingerprint(settings, settings.ActiveProfile, IsVaultAvailable());
        if (fingerprint == _lastSeenFingerprint) return;
        // A running turn keeps its backend; SendAsync compares fingerprints and restarts before the next turn.
        if (IsBusy) return;

        // Only a turn that started a moment ago (or a reset) can hold the gate now; that turn applies the change.
        if (!await _backendGate.WaitAsync(ReloadGateTimeout).ConfigureAwait(false)) return;
        try
        {
            _lastSeenFingerprint = fingerprint;
            if (Volatile.Read(ref _backend) is { } backend && fingerprint != _backendFingerprint)
                await DisposeBackendAsync(backend).ConfigureAwait(false);
        }
        finally
        {
            _backendGate.Release();
        }

        Raise(new StatusEvent($"Settings changed: now using {ActiveDescription}. It starts with your next message."));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            await StopAsync("DeskPilot is closing").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Stopping the session on close failed", ex);
        }

        var gotGate = await _backendGate.WaitAsync(TurnEndTimeout).ConfigureAwait(false);
        try
        {
            if (Interlocked.Exchange(ref _backend, null) is { } backend)
                await DisposeWithTimeoutAsync(backend, "the model backend").ConfigureAwait(false);
            if (Interlocked.Exchange(ref _mcp, null) is { } mcp)
                await DisposeWithTimeoutAsync(mcp, "the MCP server").ConfigureAwait(false);
        }
        finally
        {
            if (gotGate) _backendGate.Release();
        }

        _control.StopRequested -= OnStopRequested;
    }

    // ---- turn internals ----

    private async Task<(IAgentBackend? Backend, TurnResult? Failure)> EnsureBackendAsync(
        AppSettings settings, ProviderProfile? profile, Stopwatch sw, CancellationToken ct)
    {
        if (profile == null)
            return StartFailure("No model profile is set up. Add one in Settings.", null, sw);

        var vaultAvailable = IsVaultAvailable();
        var fingerprint = SessionConfig.ComputeFingerprint(settings, profile, vaultAvailable);
        _lastSeenFingerprint = fingerprint;

        var current = Volatile.Read(ref _backend);
        if (current != null && !_forceRestart && fingerprint == _backendFingerprint)
            return (current, null);

        if (current != null)
        {
            Log.Info("The configuration changed; restarting the model backend");
            await DisposeBackendAsync(current).ConfigureAwait(false);
        }
        _forceRestart = false;

        string apiKey;
        try
        {
            apiKey = SecretProtector.ResolveApiKey(profile);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading the API key for '{profile.Name}' failed: {ex.GetType().Name}");
            apiKey = "";
        }
        if (apiKey.Length == 0 && SessionConfig.RequiresApiKey(profile))
            return StartFailure(SessionConfig.MissingKeyMessage(profile), profile, sw);

        SetState(AgentState.Starting);
        var description = SessionConfig.DescribeProfile(profile);
        Raise(new StatusEvent($"Starting {description}"));
        if (_toolInitError != null)
            Raise(new StatusEvent($"Warning: {_toolInitError}. The model cannot control the computer.", StatusLevel.Warning));

        IAgentBackend backend;
        try
        {
            backend = _backendFactory(profile) ?? throw new InvalidOperationException("the backend factory returned nothing");
        }
        catch (Exception ex)
        {
            Log.Error($"Creating the backend for '{profile.Name}' failed", ex);
            return StartFailure($"Could not start {description}: {ex.Message}", profile, sw);
        }

        try
        {
            McpEndpointInfo? endpoint = backend.UsesMcp ? EnsureMcpEndpoint() : null;
            var prompt = PromptBuilder.Build(new PromptContext(
                settings, profile, _tools.GetTools(), DescribeScreen(settings), vaultAvailable, DateTime.Now));
            var context = new AgentBackendContext
            {
                Settings = settings,
                Profile = profile,
                SystemPrompt = prompt,
                Tools = _tools,
                Emit = Raise,
                Control = _control,
                WorkingDirectory = WorkingDirectory(),
                Mcp = endpoint,
                ApiKey = apiKey,
            };

            // Published before StartAsync so a stop during a slow start can interrupt it.
            Volatile.Write(ref _backend, backend);
            await backend.StartAsync(context, ct).ConfigureAwait(false);
            _backendFingerprint = fingerprint;
            Log.Info($"Started {description} ({backend.GetType().Name})");
            return (backend, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await DisposeBackendAsync(backend).ConfigureAwait(false);
            return (null, Cancelled(profile, sw));
        }
        catch (Exception ex)
        {
            Log.Error($"Starting '{profile.Name}' failed", ex);
            await DisposeBackendAsync(backend).ConfigureAwait(false);
            return StartFailure($"Could not start {description}: {ex.Message}", profile, sw);
        }
    }

    private async Task<TurnResult> RunBackendTurnAsync(IAgentBackend backend, string message, ProviderProfile? profile, Stopwatch sw, CancellationToken ct)
    {
        if (_control.IsStopRequested || ct.IsCancellationRequested) return Cancelled(profile, sw);
        SetState(AgentState.Running);
        // A stop that arrived between the check above and SetState was announced as Stopping and then overwritten.
        if (_control.IsStopRequested) SetState(AgentState.Stopping);

        try
        {
            return await backend.RunTurnAsync(new UserTurn(message), ct).ConfigureAwait(false)
                ?? Failed("The model backend returned no result.", profile, sw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Cancelled(profile, sw);
        }
    }

    private McpEndpointInfo EnsureMcpEndpoint()
    {
        var mcp = _mcp;
        if (mcp == null)
        {
            var factory = _hooks.McpHostFactory ?? (tools => new McpPipeServerHost(tools));
            mcp = factory(_tools) ?? throw new InvalidOperationException("the MCP server could not be created");
            _mcp = mcp;
        }

        try
        {
            mcp.Start();
        }
        catch
        {
            // Let the next start build a fresh server instead of reusing a broken one.
            _mcp = null;
            _ = DisposeWithTimeoutAsync(mcp, "the MCP server");
            throw;
        }
        return mcp.GetEndpoint(_bridgeExePath);
    }

    private TurnResult Normalize(TurnResult result, ProviderProfile? profile, Stopwatch sw)
    {
        if (result.Stats == null) result = result with { Stats = Stats(profile, sw) };
        if (result.Outcome == TurnOutcome.Cancelled && _control.StopReason is { } reason)
        {
            // Why the turn stopped (failsafe corner, step limit, the user) says more than a backend's "interrupted",
            // and a stop may not have had its own status line if the turn ended inside the cancellation.
            // The step limit stops through the same signal as the Stop button; report it as its own outcome.
            var outcome = reason.StartsWith("Step limit", StringComparison.OrdinalIgnoreCase) ? TurnOutcome.StepLimit : TurnOutcome.Cancelled;
            result = result with { Outcome = outcome, Error = reason };
        }
        return result;
    }

    private (IAgentBackend?, TurnResult?) StartFailure(string message, ProviderProfile? profile, Stopwatch sw)
    {
        Raise(new StatusEvent(message, StatusLevel.Error));
        return (null, Failed(message, profile, sw));
    }

    private TurnStats Stats(ProviderProfile? profile, Stopwatch sw) =>
        new(0, 0, _control.Steps, null, sw.Elapsed, string.IsNullOrWhiteSpace(profile?.Model) ? null : profile!.Model);

    private TurnResult Failed(string error, ProviderProfile? profile, Stopwatch sw) =>
        new(TurnOutcome.Failed, null, Stats(profile, sw), error);

    private TurnResult Cancelled(ProviderProfile? profile, Stopwatch sw) =>
        new(TurnOutcome.Cancelled, null, Stats(profile, sw), _control.StopReason ?? "Cancelled");

    private static TurnResult Rejected(string error) =>
        new(TurnOutcome.Failed, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), error);

    private static void LogTurn(string message, ProviderProfile? profile, TurnResult result, TimeSpan elapsed)
    {
        var s = result.Stats;
        var line = $"Turn {result.Outcome} in {elapsed.TotalSeconds:F1}s with '{profile?.Name}' ({s?.Model ?? profile?.Model}): " +
                   $"{s?.Steps ?? 0} steps, {s?.InputTokens ?? 0} in / {s?.OutputTokens ?? 0} out tokens. " +
                   $"Message: \"{SessionConfig.Clip(message, 200)}\"";
        if (!string.IsNullOrEmpty(result.Error)) line += $" Error: {SessionConfig.Clip(result.Error, 300)}";
        Log.Info(line);
    }

    // ---- stop / backend lifetime ----

    // Ordering note: AgentRunControl.RequestStop cancels the turn's token before it raises StopRequested, and
    // the turn can finish inside that cancellation. A stop is therefore announced before RequestStop when the
    // session itself stops the turn, and from a control-token callback that runs ahead of the turn's own
    // cancellation for stops raised in the tool layer. ClaimStop makes sure only the first of these acts.

    /// <summary>StopAsync: announce, then signal. The caller sends the interrupt itself.</summary>
    private void RequestStopAnnounced(string reason)
    {
        if (ClaimStop()) AnnounceStopping(reason);
        _control.RequestStop(reason);
    }

    private void StopFromCallerToken()
    {
        var claimed = ClaimStop();
        if (claimed) AnnounceStopping("Cancelled");
        _control.RequestStop("Cancelled");
        if (claimed && Volatile.Read(ref _backend) is { } backend) _ = InterruptSafeAsync(backend);
    }

    /// <summary>Stops raised in the tool layer: failsafe corner, user mouse movement, step limit.</summary>
    private void OnControlTokenCancelled()
    {
        if (!ClaimStop()) return;
        AnnounceStopping(_control.StopReason ?? "Stopped");
        if (Volatile.Read(ref _backend) is { } backend) _ = InterruptSafeAsync(backend);
    }

    /// <summary>Fallback for a stop whose token callback did not run; normally the stop is already handled.</summary>
    private void OnStopRequested(string reason)
    {
        Log.Info($"Stop requested: {SessionConfig.Clip(reason, 200)}");
        if (!ClaimStop()) return;
        AnnounceStopping(reason);
        if (Volatile.Read(ref _backend) is { } backend) _ = InterruptSafeAsync(backend);
    }

    /// <summary>True for the first stop of a running turn.</summary>
    private bool ClaimStop()
    {
        lock (_stateGate)
        {
            if (!_busy || _stopHandled != 0) return false;
            _stopHandled = 1;
            return true;
        }
    }

    private void AnnounceStopping(string reason)
    {
        lock (_stateGate)
        {
            if (!_busy || _turnEnding) return;
        }
        TrySetStopping();
        Raise(new StatusEvent($"Stopping: {reason}"));
    }

    private static async Task InterruptSafeAsync(IAgentBackend backend)
    {
        try
        {
            var task = backend.InterruptAsync() ?? Task.CompletedTask;
            if (await Task.WhenAny(task, Task.Delay(InterruptTimeout)).ConfigureAwait(false) != task)
            {
                Log.Warn("The model backend did not confirm the interrupt in time");
                Observe(task);
                return;
            }
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("Interrupting the model failed", ex);
        }
    }

    private async Task DisposeBackendAsync(IAgentBackend backend)
    {
        if (Interlocked.CompareExchange(ref _backend, null, backend) == backend) _backendFingerprint = null;
        await DisposeWithTimeoutAsync(backend, "the model backend").ConfigureAwait(false);
    }

    private static async Task DisposeWithTimeoutAsync(IAsyncDisposable disposable, string what)
    {
        try
        {
            var task = disposable.DisposeAsync().AsTask();
            if (await Task.WhenAny(task, Task.Delay(BackendDisposeTimeout)).ConfigureAwait(false) != task)
            {
                Log.Warn($"Closing {what} did not finish in time");
                Observe(task);
                return;
            }
            await task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error($"Closing {what} failed", ex);
        }
    }

    private static void Observe(Task task) =>
        task.ContinueWith(t => Log.Error("Background task failed", t.Exception), CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    // ---- environment for the prompt ----

    private bool IsVaultAvailable()
    {
        try
        {
            if (_hooks.VaultAvailable != null) return _hooks.VaultAvailable();
            return _vault?.IsAvailable ?? false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Vault availability check failed: {ex.Message}");
            return false;
        }
    }

    private string DescribeScreen(AppSettings settings)
    {
        try
        {
            if (_hooks.ScreenDescription != null) return _hooks.ScreenDescription();
            if (_computer != null)
            {
                var mapper = _computer.CurrentMapper();
                IReadOnlyList<MonitorInfo> monitors;
                try
                {
                    monitors = _desktop.Screen.GetMonitors();
                }
                catch (Exception)
                {
                    monitors = Array.Empty<MonitorInfo>();
                }
                return SessionConfig.DescribeScreen(mapper, monitors, settings.Screen ?? new ScreenSettings());
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not describe the screen for the prompt: {ex.Message}");
        }
        return SessionConfig.FallbackScreenDescription(settings.Screen);
    }

    private string WorkingDirectory()
    {
        if (!string.IsNullOrWhiteSpace(_hooks.WorkingDirectory)) return _hooks.WorkingDirectory;
        try
        {
            return AppPaths.AgentWorkDirectory;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create the agent working directory: {ex.Message}");
            return Path.GetTempPath();
        }
    }

    // ---- events ----

    private void SetState(AgentState state)
    {
        lock (_statePublishGate)
        {
            lock (_stateGate)
            {
                if (_state == state) return;
                _state = state;
            }
            PublishState(state);
        }
    }

    /// <summary>
    /// Moves a starting or running turn to Stopping. Stops can come from the UI thread while another thread is
    /// publishing a state to a subscriber that synchronously waits for the UI thread, so this never blocks for
    /// long: if the publish gate stays busy, the Stopping state is skipped (the turn's Idle follows shortly).
    /// </summary>
    private void TrySetStopping()
    {
        if (!Monitor.TryEnter(_statePublishGate, StoppingPublishWait)) return;
        try
        {
            lock (_stateGate)
            {
                if (!_busy || _turnEnding || _state is not (AgentState.Starting or AgentState.Running)) return;
                _state = AgentState.Stopping;
            }
            PublishState(AgentState.Stopping);
        }
        finally
        {
            Monitor.Exit(_statePublishGate);
        }
    }

    private void PublishState(AgentState state)
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<AgentState>>())
        {
            try
            {
                handler(state);
            }
            catch (Exception ex)
            {
                Log.Error("A StateChanged subscriber failed", ex);
            }
        }
    }

    private void Raise(AgentEvent e)
    {
        var handlers = EventRaised;
        if (handlers == null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<AgentEvent>>())
        {
            try
            {
                handler(e);
            }
            catch (Exception ex)
            {
                Log.Error($"An EventRaised subscriber failed on {e.GetType().Name}", ex);
            }
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Acp;

/// <summary>Why an ACP agent could not start or run, written to be shown to the user as is.</summary>
public sealed class AcpAgentException : Exception
{
    public AcpAgentException(string message, Exception? inner = null) : base(message, inner) { }
}

/// <summary>
/// Agent Client Protocol client (agentclientprotocol.com). Runs an ACP agent such as "gemini --acp" as a child
/// process and speaks JSON-RPC over its stdio. The agent runs the model loop itself and reaches DeskPilot's
/// tools through the MCP server passed in session/new; its own tools are refused.
/// </summary>
public sealed class AcpBackend : IAgentBackend
{
    private const int SupportedProtocolVersion = 1;
    private const int AuthRequiredCode = -32000;

    private static readonly Regex AuthErrorText = new(@"auth|log\s?in|sign\s?in|credential|api[\s_-]?key|unauthori[sz]ed|not logged", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ApiKeyMethodId = new(@"api[\s_-]?key", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SecretVariableName = new("KEY|TOKEN|SECRET|PASSWORD", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Func<AcpLaunchSpec, IAcpTransport> _transportFactory;
    private readonly Func<string, string?> _resolveExecutable;
    private readonly string? _tempDirectory;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<string> _tempFiles = new();

    private AgentBackendContext? _context;
    private AcpLaunchSpec? _spec;
    private IAcpTransport? _transport;
    private JsonRpcConnection? _connection;
    private volatile string? _sessionId;
    private string _agentName = "The ACP agent";
    private bool _imageSupported;
    private bool _sessionCloseSupported;
    private List<AuthMethod> _authMethods = new();
    private string? _currentModelId;
    private bool _systemPromptPending;
    private bool _expectFreshStart;
    private double? _sessionCostUsd;
    private TurnState? _turn;
    private string? _lastStderrTail;
    private int _disposed;

    public AcpBackend() : this(AcpProcessTransport.Start) { }

    /// <summary>Tests: an in-memory agent instead of a process, a fake executable lookup, a private temp folder.</summary>
    internal AcpBackend(Func<AcpLaunchSpec, IAcpTransport> transportFactory, Func<string, string?>? resolveExecutable = null, string? tempDirectory = null)
    {
        _transportFactory = transportFactory;
        _resolveExecutable = resolveExecutable ?? (name => ExecutableLocator.Find(name));
        _tempDirectory = tempDirectory;
    }

    /// <summary>How long to wait for the prompt to end after session/cancel before killing the agent.</summary>
    internal TimeSpan CancelGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(90);
    internal TimeSpan SessionTimeout { get; set; } = TimeSpan.FromSeconds(180);
    internal Action<string> Logger { get; set; } = Log.Info;
    internal AcpLaunchSpec? LaunchSpec => _spec;
    /// <summary>Diagnostics: stderr of the running agent, or of the last one that was stopped.</summary>
    internal string? StderrTail => _transport?.StderrTail ?? _lastStderrTail;
    internal string? SessionId => _sessionId;

    public bool UsesMcp => true;

    private string TempDirectory => _tempDirectory ?? AppPaths.TempDirectory;
    private string ServerName => _spec?.ServerName ?? AcpLaunchBuilder.DefaultServerName;

    public async Task StartAsync(AgentBackendContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopAgentAsync().ConfigureAwait(false);
            _context = context;
            // If this start fails, a later turn retries it; that is not a "lost conversation".
            _expectFreshStart = true;
            await LaunchAsync(ct).ConfigureAwait(false);
            _expectFreshStart = false;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(turn);
        var sw = Stopwatch.StartNew();
        var ctx = _context;
        if (ctx == null) return Result(TurnOutcome.Failed, null, sw, "The ACP agent has not been started.");
        if (Volatile.Read(ref _disposed) != 0) return Result(TurnOutcome.Failed, null, sw, $"{_agentName} has been shut down.");

        var stopToken = ctx.Control.Token;
        if (ct.IsCancellationRequested || stopToken.IsCancellationRequested) return CancelledResult(null, sw);

        var state = new TurnState();
        if (Interlocked.CompareExchange(ref _turn, state, null) != null)
            return Result(TurnOutcome.Failed, null, sw, $"{_agentName} is still working on another request.");
        try
        {
            if (!IsAgentReady) await RestartForTurnAsync(ct, stopToken).ConfigureAwait(false);

            JsonRpcConnection? connection;
            IAcpTransport? transport;
            string? sessionId;
            lock (_stateGate)
            {
                connection = _connection;
                transport = _transport;
                sessionId = _sessionId;
            }
            if (connection == null || transport == null || sessionId == null)
                return Result(TurnOutcome.Failed, state, sw, $"{_agentName} is not running.");
            if (state.CancelRequested) return CancelledResult(state, sw);

            state.SessionId = sessionId;
            state.CostAtStart = _sessionCostUsd;
            state.ToolNames = SafeToolNames(ctx);

            var promptTask = connection.SendRequestAsync("session/prompt", BuildPromptParams(turn, sessionId, ctx));
            _systemPromptPending = false;

            using var userCancel = ct.Register(state.RequestCancel);
            using var stopCancel = stopToken.Register(state.RequestCancel);

            await Task.WhenAny(promptTask, state.CancelRequestedTask, transport.Exited).ConfigureAwait(false);
            if (!promptTask.IsCompleted && state.CancelRequested)
                return await CancelTurnAsync(state, connection, transport, promptTask, sw).ConfigureAwait(false);
            if (!promptTask.IsCompleted)
            {
                // The process exited; let the reader deliver whatever it had already written.
                await Task.WhenAny(promptTask, Task.Delay(1000)).ConfigureAwait(false);
                if (!promptTask.IsCompleted)
                {
                    Observe(promptTask);
                    return await AgentStoppedAsync(state, transport, sw).ConfigureAwait(false);
                }
            }
            return await FinishTurnAsync(state, transport, promptTask, sw).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested || stopToken.IsCancellationRequested || state.CancelRequested)
        {
            return CancelledResult(state, sw);
        }
        catch (AcpAgentException ex)
        {
            return Result(TurnOutcome.Failed, state, sw, ex.Message);
        }
        catch (ObjectDisposedException)
        {
            return state.CancelRequested ? CancelledResult(state, sw) : Result(TurnOutcome.Failed, state, sw, $"{_agentName} has been shut down.");
        }
        catch (Exception ex)
        {
            Logger($"ACP: turn failed: {ex}");
            return Result(TurnOutcome.Failed, state, sw, $"{_agentName} failed: {Redact(ex.Message)}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _turn, null, state);
        }
    }

    public Task InterruptAsync()
    {
        Volatile.Read(ref _turn)?.RequestCancel();
        return Task.CompletedTask;
    }

    public async Task ResetConversationAsync(CancellationToken ct)
    {
        if (_context == null || Volatile.Read(ref _disposed) != 0) return;
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsAgentReady)
            {
                try
                {
                    var old = _sessionId;
                    if (_sessionCloseSupported && old != null)
                    {
                        try { await StartupRequestAsync("session/close", new JsonObject { ["sessionId"] = old }, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
                        catch (JsonRpcException) { }
                    }
                    await OpenSessionAsync(ct).ConfigureAwait(false);
                    return;
                }
                catch (AcpAgentException ex)
                {
                    Logger($"ACP: could not open a new session in the running agent ({ex.Message}); restarting it.");
                }
            }

            await StopAgentAsync().ConfigureAwait(false);
            _expectFreshStart = true;
            try
            {
                await LaunchAsync(ct).ConfigureAwait(false);
                _expectFreshStart = false;
            }
            catch (AcpAgentException ex)
            {
                // The next turn tries again and reports the problem as its result.
                Emit(new StatusEvent(ex.Message, StatusLevel.Warning));
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Volatile.Read(ref _turn)?.RequestCancel();
        await StopAgentAsync().ConfigureAwait(false);
        DeleteTempFiles();
    }

    // ---------------------------------------------------------------- process + handshake

    private bool IsAgentReady
    {
        get
        {
            lock (_stateGate)
                return _transport is { } t && !t.Exited.IsCompleted && _connection is { IsClosed: false } && _sessionId != null;
        }
    }

    private async Task RestartForTurnAsync(CancellationToken ct, CancellationToken stopToken)
    {
        using var startCts = CancellationTokenSource.CreateLinkedTokenSource(ct, stopToken);
        await _lifecycle.WaitAsync(startCts.Token).ConfigureAwait(false);
        try
        {
            if (IsAgentReady) return;
            var fresh = _expectFreshStart;
            _expectFreshStart = true;
            await StopAgentAsync().ConfigureAwait(false);
            await LaunchAsync(startCts.Token).ConfigureAwait(false);
            _expectFreshStart = false;
            if (!fresh)
                Emit(new StatusEvent($"{_agentName} had stopped, so DeskPilot started it again. It does not remember the earlier conversation.", StatusLevel.Warning));
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Starts the process, then initialize and session/new (with authentication when needed).</summary>
    private async Task LaunchAsync(CancellationToken ct)
    {
        var ctx = _context ?? throw new InvalidOperationException("StartAsync has not been called.");
        DeleteTempFiles();
        var spec = AcpLaunchBuilder.Build(ctx, _resolveExecutable, TempDirectory);
        lock (_tempFiles) _tempFiles.AddRange(spec.TempFiles);
        _spec = spec;
        _agentName = spec.DisplayName;
        _imageSupported = false;
        _sessionCloseSupported = false;
        _authMethods = new List<AuthMethod>();
        _currentModelId = null;
        Logger($"ACP: starting {spec.DisplayCommandLine} in {spec.WorkingDirectory}");

        IAcpTransport transport;
        try
        {
            transport = _transportFactory(spec);
        }
        catch (Exception ex) when (ex is not AcpAgentException)
        {
            throw new AcpAgentException($"Could not start {spec.DisplayName} ({spec.ExecutablePath}): {ex.Message}", ex);
        }

        var connection = new JsonRpcConnection(transport.FromAgent, transport.ToAgent, HandleAgentRequestAsync, HandleAgentNotification)
        {
            Trace = Logger,
        };
        bool disposed;
        lock (_stateGate)
        {
            disposed = Volatile.Read(ref _disposed) != 0;
            if (!disposed)
            {
                _transport = transport;
                _connection = connection;
                _sessionId = null;
            }
        }
        if (disposed)
        {
            transport.Kill();
            await transport.DisposeAsync().ConfigureAwait(false);
            throw new ObjectDisposedException(nameof(AcpBackend));
        }

        connection.Start();
        try
        {
            JsonElement init;
            try
            {
                init = await StartupRequestAsync("initialize", BuildInitializeParams(), InitializeTimeout, ct).ConfigureAwait(false);
            }
            catch (JsonRpcException ex)
            {
                throw new AcpAgentException($"{_agentName} rejected the ACP handshake: {Clip(Redact(ex.Message), 400)}", ex);
            }
            ReadInitializeResult(init);
            await OpenSessionWithAuthenticationAsync(ct).ConfigureAwait(false);
            Logger($"ACP: {_agentName} is ready (image prompts: {_imageSupported}).");
        }
        catch
        {
            await StopAgentAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopAgentAsync()
    {
        IAcpTransport? transport;
        JsonRpcConnection? connection;
        lock (_stateGate)
        {
            transport = _transport;
            connection = _connection;
            _transport = null;
            _connection = null;
            _sessionId = null;
        }
        transport?.Kill();
        if (transport != null) _lastStderrTail = transport.StderrTail;
        if (connection != null) await connection.DisposeAsync().ConfigureAwait(false);
        if (transport != null)
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Logger($"ACP: disposing the agent process failed: {ex.Message}"); }
        }
    }

    /// <summary>A request made while starting or resetting: bounded by a timeout and by the process exiting.</summary>
    private async Task<JsonElement> StartupRequestAsync(string method, JsonNode parameters, TimeSpan timeout, CancellationToken ct)
    {
        JsonRpcConnection? connection;
        IAcpTransport? transport;
        lock (_stateGate)
        {
            connection = _connection;
            transport = _transport;
        }
        if (connection == null || transport == null) throw new AcpAgentException($"{_agentName} is not running.");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var request = connection.SendRequestAsync(method, parameters, timeoutCts.Token);
        // An agent that dies without closing its output must not leave us waiting for the timeout.
        if (await Task.WhenAny(request, transport.Exited).ConfigureAwait(false) != request)
        {
            await Task.WhenAny(request, Task.Delay(500)).ConfigureAwait(false);
            if (!request.IsCompleted)
            {
                Observe(request);
                throw await StoppedWhileStartingAsync(transport).ConfigureAwait(false);
            }
        }

        try
        {
            return await request.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new AcpAgentException($"{_agentName} did not answer the ACP '{method}' request within {timeout.TotalSeconds:0} seconds.{StderrSuffix(transport)}");
        }
        catch (JsonRpcConnectionClosedException)
        {
            throw await StoppedWhileStartingAsync(transport).ConfigureAwait(false);
        }
    }

    private async Task<AcpAgentException> StoppedWhileStartingAsync(IAcpTransport transport)
    {
        await Task.WhenAny(transport.Exited, Task.Delay(1000)).ConfigureAwait(false);
        var code = transport.ExitCode is int c ? $" (exit code {c})" : "";
        return new AcpAgentException($"{_agentName} stopped while starting{code}.{StderrSuffix(transport)}");
    }

    private static JsonObject BuildInitializeParams()
    {
        var version = typeof(AcpBackend).Assembly.GetName().Version;
        return new JsonObject
        {
            ["protocolVersion"] = SupportedProtocolVersion,
            ["clientCapabilities"] = new JsonObject
            {
                ["fs"] = new JsonObject { ["readTextFile"] = false, ["writeTextFile"] = false },
                ["terminal"] = false,
            },
            ["clientInfo"] = new JsonObject
            {
                ["name"] = "deskpilot",
                ["title"] = "DeskPilot",
                ["version"] = version == null ? "1.0.0" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}",
            },
        };
    }

    private void ReadInitializeResult(JsonElement init)
    {
        if (init.ValueKind != JsonValueKind.Object) return;
        if (init.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var version) && version != SupportedProtocolVersion)
            throw new AcpAgentException($"{_agentName} uses Agent Client Protocol version {version}, but DeskPilot supports version {SupportedProtocolVersion}. Use an agent release that supports protocol version {SupportedProtocolVersion}.");

        var caps = Obj(init, "agentCapabilities");
        _imageSupported = Bool(Obj(caps, "promptCapabilities"), "image") == true;
        _sessionCloseSupported = Obj(Obj(caps, "sessionCapabilities"), "close").ValueKind == JsonValueKind.Object;

        if (_spec is { IsGemini: false })
        {
            var info = Obj(init, "agentInfo");
            var title = Str(info, "title") ?? Str(info, "name");
            if (!string.IsNullOrWhiteSpace(title)) _agentName = title.Trim();
        }

        var methods = new List<AuthMethod>();
        if (init.TryGetProperty("authMethods", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in list.EnumerateArray())
            {
                var id = Str(m, "id");
                if (string.IsNullOrWhiteSpace(id)) continue;
                var vars = new List<AuthVariable>();
                if (m.TryGetProperty("vars", out var vs) && vs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in vs.EnumerateArray())
                        if (Str(e, "name") is { Length: > 0 } name) vars.Add(new AuthVariable(name, Bool(e, "optional") == true));
                }
                methods.Add(new AuthMethod(id, Str(m, "type"), vars));
            }
        }
        _authMethods = methods;
    }

    /// <summary>session/new; when the agent needs a login and an API key for it is in the environment, authenticate and retry once.</summary>
    private async Task OpenSessionWithAuthenticationAsync(CancellationToken ct)
    {
        JsonRpcException first;
        try
        {
            await NewSessionAsync(ct).ConfigureAwait(false);
            return;
        }
        catch (JsonRpcException ex)
        {
            first = ex;
        }

        var authError = IsAuthError(first);
        if (!authError && _authMethods.Count == 0) throw SessionFailed(first);
        var method = FindEnvironmentAuthMethod();
        if (method == null) throw authError ? NotLoggedIn(first.Message) : SessionFailed(first);

        Logger($"ACP: session/new failed (code {first.Code}); authenticating with method '{method.Id}' from the environment.");
        try
        {
            await StartupRequestAsync("authenticate", new JsonObject { ["methodId"] = method.Id }, InitializeTimeout, ct).ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            throw NotLoggedIn(ex.Message);
        }

        try
        {
            await NewSessionAsync(ct).ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            throw IsAuthError(ex) ? NotLoggedIn(ex.Message) : SessionFailed(ex);
        }
    }

    /// <summary>session/new in the running agent (used by reset); errors become AcpAgentException.</summary>
    private async Task OpenSessionAsync(CancellationToken ct)
    {
        try
        {
            await NewSessionAsync(ct).ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            throw IsAuthError(ex) ? NotLoggedIn(ex.Message) : SessionFailed(ex);
        }
    }

    private async Task NewSessionAsync(CancellationToken ct)
    {
        var result = await StartupRequestAsync("session/new", BuildNewSessionParams(), SessionTimeout, ct).ConfigureAwait(false);
        var id = Str(result, "sessionId");
        if (string.IsNullOrWhiteSpace(id)) throw new AcpAgentException($"{_agentName} did not return a session id.");
        _currentModelId = Str(Obj(result, "models"), "currentModelId");
        _sessionCostUsd = null;
        _systemPromptPending = _spec is { IsGemini: false };
        _sessionId = id;
    }

    private JsonObject BuildNewSessionParams()
    {
        var ctx = _context!;
        var servers = new JsonArray();
        if (ctx.Mcp is { } mcp)
        {
            var args = new JsonArray();
            foreach (var a in mcp.Args) args.Add(a);
            servers.Add(new JsonObject
            {
                ["name"] = ServerName,
                ["command"] = mcp.Command,
                ["args"] = args,
                ["env"] = new JsonArray(),
            });
        }
        return new JsonObject { ["cwd"] = ctx.WorkingDirectory, ["mcpServers"] = servers };
    }

    private AuthMethod? FindEnvironmentAuthMethod()
    {
        foreach (var m in _authMethods)
        {
            if (m.Variables.Count > 0)
            {
                var required = m.Variables.Where(v => !v.Optional).ToList();
                if (required.Count > 0 && required.All(v => HasChildVariable(v.Name))) return m;
                continue;
            }
            if (!ApiKeyMethodId.IsMatch(m.Id)) continue;
            if (CandidateKeyVariables(m.Id).Any(HasChildVariable)) return m;
        }
        return null;
    }

    /// <summary>"gemini-api-key" means GEMINI_API_KEY; the profile's own key variable counts too.</summary>
    private IEnumerable<string> CandidateKeyVariables(string methodId)
    {
        var derived = Regex.Replace(methodId.ToUpperInvariant(), "[^A-Z0-9]+", "_").Trim('_');
        if (derived.Length > 0) yield return derived;
        if (_context?.Profile.ApiKeyEnvVar is { Length: > 0 } configured) yield return configured.Trim();
    }

    private bool HasChildVariable(string name)
    {
        if (_spec != null && _spec.Environment.TryGetValue(name, out var overridden)) return !string.IsNullOrEmpty(overridden);
        return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name));
    }

    private static bool IsAuthError(JsonRpcException ex) => ex.Code == AuthRequiredCode || AuthErrorText.IsMatch(ex.Message);

    private AcpAgentException SessionFailed(JsonRpcException ex) =>
        new($"{_agentName} could not start a session: {Clip(Redact(ex.Message), 400)}", ex);

    private AcpAgentException NotLoggedIn(string? detail)
    {
        var message = _spec is { IsGemini: true }
            ? "Gemini CLI is not logged in. Run 'gemini' in a terminal once and sign in (or set GEMINI_API_KEY)."
            : $"{_agentName} is not logged in. Run it in a terminal once and sign in, then try again.";
        var d = Clip(Redact(detail ?? "").Trim(), 300);
        if (d.Length > 0 && !d.TrimEnd('.').Equals("Authentication required", StringComparison.OrdinalIgnoreCase))
            message += $" ({_agentName} said: {d})";
        return new AcpAgentException(message);
    }

    // ---------------------------------------------------------------- turns

    private JsonObject BuildPromptParams(UserTurn turn, string sessionId, AgentBackendContext ctx)
    {
        var text = turn.Text ?? "";
        if (_systemPromptPending) text = WithSystemPrompt(ctx.SystemPrompt, text, ServerName);
        var blocks = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } };

        var images = turn.Images ?? Array.Empty<ToolImage>();
        if (images.Count > 0)
        {
            if (_imageSupported)
            {
                foreach (var image in images)
                    blocks.Add(new JsonObject { ["type"] = "image", ["data"] = image.Base64Data, ["mimeType"] = image.MediaType });
            }
            else
            {
                Emit(new StatusEvent($"{_agentName} does not accept images, so {images.Count} attached image(s) were not sent.", StatusLevel.Info));
            }
        }
        return new JsonObject { ["sessionId"] = sessionId, ["prompt"] = blocks };
    }

    /// <summary>Agents without a system-prompt option get DeskPilot's prompt in front of the first message of each session.</summary>
    internal static string WithSystemPrompt(string systemPrompt, string userText, string serverName) =>
        "<deskpilot_instructions>\n" +
        "These are DeskPilot's standing instructions for this whole conversation. Treat them as your system prompt.\n\n" +
        systemPrompt.Trim() + "\n\n" + AcpLaunchBuilder.ToolNamingNote(serverName) + "\n" +
        "</deskpilot_instructions>\n\n" + userText;

    private async Task<TurnResult> FinishTurnAsync(TurnState state, IAcpTransport transport, Task<JsonElement> promptTask, Stopwatch sw)
    {
        JsonElement response;
        try
        {
            response = await promptTask.ConfigureAwait(false);
        }
        catch (JsonRpcConnectionClosedException)
        {
            if (state.CancelRequested) return CancelledResult(state, sw);
            return await AgentStoppedAsync(state, transport, sw).ConfigureAwait(false);
        }
        catch (JsonRpcException ex)
        {
            if (state.CancelRequested) return CancelledResult(state, sw);
            var message = IsAuthError(ex) ? NotLoggedIn(ex.Message).Message : $"{_agentName} reported an error: {Clip(Redact(ex.Message), 600)}";
            return Result(TurnOutcome.Failed, state, sw, message);
        }

        ReadUsage(response, state);
        var stopReason = Str(response, "stopReason") ?? "end_turn";
        if (state.CancelRequested || stopReason == "cancelled") return CancelledResult(state, sw);
        switch (stopReason)
        {
            case "max_tokens":
                Emit(new StatusEvent($"{_agentName} stopped because the model reached its output limit.", StatusLevel.Warning));
                return Result(TurnOutcome.Completed, state, sw, null);
            case "max_turn_requests":
                Emit(new StatusEvent($"{_agentName} stopped because it reached its limit of model requests for one turn.", StatusLevel.Warning));
                return Result(TurnOutcome.StepLimit, state, sw, null);
            case "refusal":
                Emit(new StatusEvent("The model refused to continue with this request.", StatusLevel.Warning));
                return Result(TurnOutcome.Completed, state, sw, null);
            default:
                return Result(TurnOutcome.Completed, state, sw, null);
        }
    }

    /// <summary>session/cancel, then wait for the prompt to end; an agent that does not stop in time is killed.</summary>
    private async Task<TurnResult> CancelTurnAsync(TurnState state, JsonRpcConnection connection, IAcpTransport transport, Task<JsonElement> promptTask, Stopwatch sw)
    {
        try
        {
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await connection.SendNotificationAsync("session/cancel", new JsonObject { ["sessionId"] = state.SessionId }, sendTimeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonRpcConnectionClosedException or OperationCanceledException)
        {
        }

        await Task.WhenAny(promptTask, transport.Exited, Task.Delay(CancelGracePeriod)).ConfigureAwait(false);
        if (promptTask.IsCompletedSuccessfully)
        {
            ReadUsage(promptTask.Result, state);
        }
        else if (!promptTask.IsCompleted)
        {
            Observe(promptTask);
            if (!transport.Exited.IsCompleted)
            {
                Logger($"ACP: {_agentName} did not end the turn within {CancelGracePeriod.TotalSeconds:0.#} s of session/cancel; killing it.");
                transport.Kill();
                _expectFreshStart = true;
                Emit(new StatusEvent($"{_agentName} did not stop in time, so DeskPilot ended it. The next request starts a new conversation.", StatusLevel.Warning));
            }
            _sessionId = null;
        }
        else
        {
            Observe(promptTask);
        }
        return CancelledResult(state, sw);
    }

    private async Task<TurnResult> AgentStoppedAsync(TurnState state, IAcpTransport transport, Stopwatch sw)
    {
        await Task.WhenAny(transport.Exited, Task.Delay(1000)).ConfigureAwait(false);
        _sessionId = null;
        if (state.CancelRequested) return CancelledResult(state, sw);
        var code = transport.ExitCode is int c ? $" (exit code {c})" : "";
        var message = $"{_agentName} stopped unexpectedly{code}.{StderrSuffix(transport)}";
        Logger("ACP: " + message);
        return Result(TurnOutcome.Failed, state, sw, message);
    }

    private static void ReadUsage(JsonElement response, TurnState state)
    {
        var usage = Obj(response, "usage");
        if (usage.ValueKind == JsonValueKind.Object)
        {
            if (Int(usage, "inputTokens") is int input) state.InputTokens = input;
            if (Int(usage, "outputTokens") is int output) state.OutputTokens = output;
        }

        // Gemini CLI reports usage in _meta.quota instead.
        var quota = Obj(Obj(response, "_meta"), "quota");
        var count = Obj(quota, "token_count");
        if (usage.ValueKind != JsonValueKind.Object)
        {
            if (Int(count, "input_tokens") is int input) state.InputTokens = input;
            if (Int(count, "output_tokens") is int output) state.OutputTokens = output;
        }
        if (quota.TryGetMember("model_usage", out var models) && models.ValueKind == JsonValueKind.Array && models.GetArrayLength() == 1)
            state.Model = Str(models[0], "model");
    }

    private TurnResult CancelledResult(TurnState? state, Stopwatch sw)
    {
        var reason = _context?.Control.StopReason;
        var outcome = reason != null && reason.StartsWith("Step limit", StringComparison.OrdinalIgnoreCase) ? TurnOutcome.StepLimit : TurnOutcome.Cancelled;
        return Result(outcome, state, sw, null);
    }

    private TurnResult Result(TurnOutcome outcome, TurnState? state, Stopwatch sw, string? error)
    {
        var text = state?.Text ?? "";
        var finalText = text.Length > 0 ? text : outcome == TurnOutcome.Completed ? "" : null;
        double? cost = state?.CostNow is double now ? Math.Max(0, now - (state.CostAtStart ?? 0)) : null;
        var model = _context?.Profile.Model is { Length: > 0 } configured ? configured : state?.Model ?? _currentModelId;
        var stats = new TurnStats(state?.InputTokens ?? 0, state?.OutputTokens ?? 0, _context?.Control.Steps ?? 0, cost, sw.Elapsed, model);
        return new TurnResult(outcome, finalText, stats, error);
    }

    // ---------------------------------------------------------------- messages from the agent

    private void HandleAgentNotification(string method, JsonElement parameters)
    {
        if (method != "session/update") return;
        var update = Obj(parameters, "update");
        var kind = Str(update, "sessionUpdate");
        var sessionId = Str(parameters, "sessionId");
        var state = Volatile.Read(ref _turn);

        if (kind == "usage_update" && sessionId != null && sessionId == _sessionId)
        {
            var cost = Obj(update, "cost");
            if (string.Equals(Str(cost, "currency"), "USD", StringComparison.OrdinalIgnoreCase) && Num(cost, "amount") is double amount)
            {
                _sessionCostUsd = amount;
                if (state != null && state.SessionId == sessionId) state.CostNow = amount;
            }
            return;
        }

        if (state == null || state.SessionId == null || sessionId != state.SessionId) return;
        switch (kind)
        {
            case "agent_message_chunk":
                if (ChunkText(update) is { Length: > 0 } text)
                {
                    state.AppendText(text);
                    Emit(new AssistantTextEvent(text, IsPartial: true));
                }
                break;
            case "agent_thought_chunk":
                if (ChunkText(update) is { Length: > 0 } thought) Emit(new ThinkingEvent(thought, IsPartial: true));
                break;
            case "tool_call":
            case "tool_call_update":
                ReportAgentTool(state, update);
                break;
            case "plan":
                ReportPlan(update);
                break;
        }
    }

    private static string? ChunkText(JsonElement update)
    {
        var content = Obj(update, "content");
        return Str(content, "type") == "text" ? Str(content, "text") : null;
    }

    /// <summary>DeskPilot's own tool calls are already logged by ObservedToolHost; only the agent's other tools get a status line.</summary>
    private void ReportAgentTool(TurnState state, JsonElement update)
    {
        var id = Str(update, "toolCallId") ?? "";
        if (state.IsHidden(id)) return;
        if (IsDeskPilotForDisplay(update, state))
        {
            state.Hide(id);
            return;
        }

        var title = Str(update, "title");
        var status = Str(update, "status");
        var messages = new List<string>(2);
        lock (state.Gate)
        {
            if (!string.IsNullOrWhiteSpace(title)) state.Titles[id] = title.Trim();
            if (!state.Titles.TryGetValue(id, out var known)) return;
            if (state.Announced.Add(id)) messages.Add($"Agent tool: {Clip(known, 120)}");
            if (status == "failed" && state.Failed.Add(id)) messages.Add($"Agent tool failed: {Clip(known, 120)}");
        }
        foreach (var m in messages) Emit(new StatusEvent(m, StatusLevel.Info));
    }

    private bool IsDeskPilotForDisplay(JsonElement toolCall, TurnState state)
    {
        if (AcpPermissions.IsDeskPilotToolCall(toolCall, default, ServerName, state.ToolNames ?? Array.Empty<string>(), mcpRestrictedToDeskPilot: false))
            return true;
        // With only DeskPilot's MCP server loaded, Gemini's "other" kind calls are DeskPilot's tools (titles
        // can be a run_command command line). Display only: permissions never rely on this.
        return _spec is { McpRestrictedToDeskPilot: true } && Str(toolCall, "kind") == "other";
    }

    private void ReportPlan(JsonElement update)
    {
        if (!update.TryGetMember("entries", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        var lines = new List<string>();
        foreach (var e in entries.EnumerateArray())
        {
            var content = Str(e, "content");
            if (string.IsNullOrWhiteSpace(content)) continue;
            var marker = Str(e, "status") switch
            {
                "completed" => "[x]",
                "in_progress" => "[>]",
                _ => "[ ]",
            };
            lines.Add($"{marker} {Clip(content.Trim(), 200)}");
        }
        if (lines.Count > 0) Emit(new StatusEvent("Plan:\n" + string.Join("\n", lines), StatusLevel.Info));
    }

    private Task<JsonNode?> HandleAgentRequestAsync(string method, JsonElement parameters, CancellationToken ct)
    {
        if (method == "session/request_permission") return Task.FromResult<JsonNode?>(HandlePermissionRequest(parameters));
        // fs/* and terminal/* were declared unsupported in initialize; anything else is unknown too.
        return Task.FromException<JsonNode?>(new JsonRpcException(JsonRpcConnection.MethodNotFound, $"Method not found: {method}"));
    }

    private JsonObject HandlePermissionRequest(JsonElement parameters)
    {
        var state = Volatile.Read(ref _turn);
        var ctx = _context;
        var toolCall = Obj(parameters, "toolCall");
        var options = parameters.TryGetMember("options", out var o) ? o : default;
        var sessionId = Str(parameters, "sessionId");

        if (state == null || ctx == null || state.SessionId == null || state.CancelRequested || ctx.Control.IsStopRequested
            || (sessionId != null && sessionId != state.SessionId))
            return CancelledOutcome();

        var toolCallId = Str(toolCall, "toolCallId");
        var names = state.ToolNames ?? SafeToolNames(ctx);
        if (AcpPermissions.IsDeskPilotToolCall(toolCall, options, ServerName, names, _spec is { McpRestrictedToDeskPilot: true }))
        {
            if (toolCallId != null) state.Hide(toolCallId);
            return AcpPermissions.PickAllowOption(options) is { } allow ? SelectedOutcome(allow) : CancelledOutcome();
        }

        if (toolCallId != null) state.Hide(toolCallId);
        var title = Str(toolCall, "title");
        Emit(new StatusEvent($"Blocked {_agentName}'s own tool \"{Clip(string.IsNullOrWhiteSpace(title) ? "unnamed tool" : title.Trim(), 120)}\": only DeskPilot's tools may run.", StatusLevel.Warning));
        return AcpPermissions.PickRejectOption(options) is { } reject ? SelectedOutcome(reject) : CancelledOutcome();
    }

    private static JsonObject SelectedOutcome(string optionId) =>
        new() { ["outcome"] = new JsonObject { ["outcome"] = "selected", ["optionId"] = optionId } };

    private static JsonObject CancelledOutcome() =>
        new() { ["outcome"] = new JsonObject { ["outcome"] = "cancelled" } };

    // ---------------------------------------------------------------- helpers

    private IReadOnlyCollection<string> SafeToolNames(AgentBackendContext ctx)
    {
        try
        {
            return ctx.Tools.GetTools().Select(t => t.Name).ToArray();
        }
        catch (Exception ex)
        {
            Logger($"ACP: listing DeskPilot's tools failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private void Emit(AgentEvent e)
    {
        try
        {
            _context?.Emit(e);
        }
        catch (Exception ex)
        {
            Logger($"ACP: an event handler failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private string StderrSuffix(IAcpTransport? transport)
    {
        var tail = Redact(transport?.StderrTail ?? "").Trim();
        if (tail.Length == 0) return "";
        if (tail.Length > 1500) tail = "..." + tail[^1500..];
        return "\nLast output from the agent:\n" + tail;
    }

    /// <summary>Agent messages can echo configuration; keys never reach the UI or the log.</summary>
    private string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var secrets = new List<string>();
        if (_context?.ApiKey is { Length: >= 8 } key) secrets.Add(key);
        if (_spec != null)
        {
            foreach (var (name, value) in _spec.Environment)
                if (value is { Length: >= 8 } && SecretVariableName.IsMatch(name)) secrets.Add(value);
        }
        foreach (var name in new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" })
            if (Environment.GetEnvironmentVariable(name) is { Length: >= 8 } v) secrets.Add(v);
        foreach (var s in secrets) text = text.Replace(s, "***", StringComparison.Ordinal);
        return text;
    }

    private void DeleteTempFiles()
    {
        string[] files;
        lock (_tempFiles)
        {
            files = _tempFiles.ToArray();
            _tempFiles.Clear();
        }
        foreach (var f in files) AcpLaunchBuilder.TryDelete(f);
    }

    private static void Observe(Task task) =>
        task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "...";

    private static JsonElement Obj(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static double? Num(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static int? Int(JsonElement e, string name) =>
        Num(e, name) is double d ? (int)Math.Clamp(d, 0, int.MaxValue) : null;

    private sealed record AuthVariable(string Name, bool Optional);

    private sealed record AuthMethod(string Id, string? Type, IReadOnlyList<AuthVariable> Variables);

    private sealed class TurnState
    {
        private readonly TaskCompletionSource _cancel = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly StringBuilder _text = new();
        private readonly HashSet<string> _hidden = new();

        public readonly object Gate = new();
        public readonly Dictionary<string, string> Titles = new();
        public readonly HashSet<string> Announced = new();
        public readonly HashSet<string> Failed = new();
        public volatile string? SessionId;
        public IReadOnlyCollection<string>? ToolNames;
        public double? CostAtStart;
        public double? CostNow;
        public int InputTokens;
        public int OutputTokens;
        public string? Model;

        public Task CancelRequestedTask => _cancel.Task;
        public bool CancelRequested => _cancel.Task.IsCompleted;
        public void RequestCancel() => _cancel.TrySetResult();

        public string Text
        {
            get { lock (Gate) return _text.ToString(); }
        }

        public void AppendText(string s)
        {
            lock (Gate) _text.Append(s);
        }

        public void Hide(string id)
        {
            lock (Gate) _hidden.Add(id);
        }

        public bool IsHidden(string id)
        {
            lock (Gate) return _hidden.Contains(id);
        }
    }
}

internal static class AcpJsonExtensions
{
    public static bool TryGetMember(this JsonElement e, string name, out JsonElement value)
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out value)) return true;
        value = default;
        return false;
    }
}

using System.Diagnostics;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

/// <summary>Runs the agent loop itself against Anthropic API, OpenAI-compatible and Ollama endpoints.</summary>
public sealed class HttpAgentBackend : IAgentBackend
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private AgentBackendContext? _context;
    private IChatProvider? _provider;
    private HttpTransport? _transport;
    private CancellationTokenSource? _turnCts;
    private int _busy;

    public HttpAgentBackend(HttpClient? http = null)
    {
        if (http == null)
        {
            // Per-request timeouts come from the profile, so the client itself never times out.
            _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            _ownsHttp = true;
        }
        else
        {
            _http = http;
        }
    }

    public bool UsesMcp => false;

    /// <summary>Waits between retries. Tests replace it to run instantly.</summary>
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    /// <summary>Where failures are logged. Messages never contain the API key.</summary>
    internal Action<string> LogWarning { get; set; } = Log.Warn;

    internal IChatProvider? Provider => _provider;

    public Task StartAsync(AgentBackendContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ct.ThrowIfCancellationRequested();
        var extraBody = Validate(context);
        var profile = context.Profile;
        var name = ProviderDisplayName(profile);
        var baseUrl = profile.Kind switch
        {
            ProviderKind.AnthropicApi => AnthropicProvider.ResolveBaseUrl(profile),
            ProviderKind.Ollama => OllamaProvider.ResolveBaseUrl(profile.BaseUrl),
            _ => Endpoints.TrimBase(profile.BaseUrl),
        };
        var transport = new HttpTransport(_http, name, baseUrl, context.ApiKey, TimeSpan.FromSeconds(Math.Max(5, profile.RequestTimeoutSeconds)),
            profile.ExtraHeaders, context.Emit, (d, token) => Delay(d, token));

        _provider = profile.Kind switch
        {
            ProviderKind.AnthropicApi => new AnthropicProvider(transport, profile, context.SystemPrompt, context.ApiKey, extraBody, context.Emit),
            ProviderKind.OpenAiCompatible => new OpenAiCompatProvider(transport, profile, context.SystemPrompt, context.ApiKey, extraBody, context.Emit),
            _ => new OllamaProvider(transport, profile, context.SystemPrompt, extraBody, context.Emit),
        };
        _transport = transport;
        _context = context;
        return Task.CompletedTask;
    }

    /// <summary>Checks the profile before any request; throws InvalidOperationException with a readable message.</summary>
    internal static System.Text.Json.Nodes.JsonObject? Validate(AgentBackendContext context)
    {
        var p = context.Profile;
        var name = ProviderDisplayName(p);
        if (p.Kind is not (ProviderKind.AnthropicApi or ProviderKind.OpenAiCompatible or ProviderKind.Ollama))
            throw new InvalidOperationException($"'{name}' is a {p.Kind} profile, which DeskPilot's HTTP backend cannot run.");

        string baseUrl = p.Kind switch
        {
            ProviderKind.AnthropicApi => AnthropicProvider.ResolveBaseUrl(p),
            ProviderKind.Ollama => OllamaProvider.ResolveBaseUrl(p.BaseUrl),
            _ => Endpoints.TrimBase(p.BaseUrl),
        };
        if (baseUrl.Length == 0)
            throw new InvalidOperationException($"No base URL is set for '{name}'. Enter the server address in Settings (for example http://127.0.0.1:1234/v1).");
        if (!Endpoints.TryParseHttpUrl(baseUrl, out _))
            throw new InvalidOperationException($"The base URL '{baseUrl}' for '{name}' is not a valid http:// or https:// address.");
        if (baseUrl.Contains("YOUR-RESOURCE", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Replace YOUR-RESOURCE in the base URL of '{name}' with your Azure resource name.");

        bool local = Endpoints.IsLocal(baseUrl);
        bool needsModel = p.Kind != ProviderKind.OpenAiCompatible || !local;
        if (needsModel && string.IsNullOrWhiteSpace(p.Model))
            throw new InvalidOperationException($"No model is selected for '{name}'. Pick one in the model box or in Settings.");

        bool needsKey = p.Kind switch
        {
            ProviderKind.AnthropicApi => true,
            ProviderKind.OpenAiCompatible => ProviderPresets.Find(p.PresetId)?.NeedsApiKey == true,
            _ => false,
        };
        if (needsKey && string.IsNullOrWhiteSpace(context.ApiKey))
        {
            var env = string.IsNullOrWhiteSpace(p.ApiKeyEnvVar) ? "" : $" or set the {p.ApiKeyEnvVar.Trim()} environment variable";
            throw new InvalidOperationException($"No API key for '{name}'. Add one in Settings{env}.");
        }

        try
        {
            return JsonUtil.ParseExtraBody(p.ExtraBodyJson);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"'{name}': {ex.Message}.");
        }
    }

    internal static string ProviderDisplayName(ProviderProfile p)
    {
        if (!string.IsNullOrWhiteSpace(p.Name)) return p.Name.Trim();
        return p.Kind switch
        {
            ProviderKind.AnthropicApi => "Anthropic API",
            ProviderKind.Ollama => "Ollama",
            _ => "OpenAI-compatible server",
        };
    }

    public async Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var context = _context;
        var provider = _provider;
        if (context == null || provider == null)
            return new TurnResult(TurnOutcome.Failed, null, new TurnStats(0, 0, 0, null, sw.Elapsed, null), "The HTTP backend has not been started.");
        if (Interlocked.Exchange(ref _busy, 1) == 1)
            return new TurnResult(TurnOutcome.Failed, null, new TurnStats(0, 0, 0, null, sw.Elapsed, context.Profile.Model), "A request is already running.");

        var control = context.Control;
        var settings = context.Settings;
        int maxSteps = Math.Max(1, settings.Safety.MaxStepsPerTurn);
        int maxModelCalls = maxSteps + 5;
        int keepImages = Math.Max(1, settings.Screen.ScreenshotsToKeep);
        int inputTokens = 0, outputTokens = 0, steps = 0, modelCalls = 0;
        string? model = string.IsNullOrWhiteSpace(context.Profile.Model) ? null : context.Profile.Model.Trim();
        string? lastText = null;
        int historyAtStart = provider.MessageCount;

        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct, control.Token);
        _turnCts = turnCts;
        var token = turnCts.Token;

        TurnResult Result(TurnOutcome outcome, string? text, string? error) =>
            new(outcome, text, new TurnStats(inputTokens, outputTokens, steps, null, sw.Elapsed, model), error);

        TurnResult Stopped()
        {
            bool stepLimit = control.IsStopRequested &&
                (steps > maxSteps || (control.StopReason?.StartsWith("Step limit", StringComparison.OrdinalIgnoreCase) ?? false));
            return stepLimit
                ? Result(TurnOutcome.StepLimit, lastText, control.StopReason ?? $"Step limit ({maxSteps}) reached")
                : Result(TurnOutcome.Cancelled, lastText, null);
        }

        try
        {
            provider.AddUserTurn(turn);
            while (true)
            {
                if (token.IsCancellationRequested || control.IsStopRequested) return Stopped();
                if (modelCalls >= maxModelCalls)
                {
                    var msg = $"Stopped after {modelCalls} model calls without finishing (step limit {maxSteps}).";
                    context.Emit(new StatusEvent(msg, StatusLevel.Warning));
                    return Result(TurnOutcome.StepLimit, lastText, msg);
                }
                modelCalls++;

                var response = await provider.CompleteAsync(context.Tools.GetTools(), token).ConfigureAwait(false);
                inputTokens += response.InputTokens;
                outputTokens += response.OutputTokens;
                if (!string.IsNullOrWhiteSpace(response.Model)) model = response.Model;
                if (!string.IsNullOrWhiteSpace(response.Thinking)) context.Emit(new ThinkingEvent(response.Thinking));
                if (!string.IsNullOrWhiteSpace(response.Text))
                {
                    context.Emit(new AssistantTextEvent(response.Text));
                    lastText = response.Text;
                }

                if (response.Stop == ProviderStop.Refusal)
                {
                    // Forget the declined request so the next message starts clean.
                    provider.TruncateTo(historyAtStart);
                    return Result(TurnOutcome.Failed, null, "The model declined this request.");
                }

                if (response.ToolCalls.Count == 0)
                {
                    if (response.Stop == ProviderStop.PauseTurn) continue;
                    if (response.Stop == ProviderStop.MaxTokens)
                        context.Emit(new StatusEvent($"The reply was cut off at the output limit ({context.Profile.MaxOutputTokens} tokens). Raise Max output tokens in Settings if this keeps happening.", StatusLevel.Warning));
                    return Result(TurnOutcome.Completed, string.IsNullOrWhiteSpace(response.Text) ? null : response.Text, null);
                }

                // Every call of this step runs (one at a time) and all results go back together.
                var results = new List<(ProviderToolCall Call, ToolResult Result)>(response.ToolCalls.Count);
                bool interrupted = false;
                foreach (var call in response.ToolCalls)
                {
                    if (interrupted || token.IsCancellationRequested)
                    {
                        interrupted = true;
                        results.Add((call, ToolResult.Error(HttpText.NotExecutedNote)));
                        continue;
                    }
                    try
                    {
                        var result = await context.Tools.ExecuteAsync(call.Name, JsonArgs.ParseArguments(call.ArgumentsJson), token).ConfigureAwait(false);
                        steps++;
                        results.Add((call, result));
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        interrupted = true;
                        results.Add((call, ToolResult.Error(HttpText.NotExecutedNote)));
                    }
                }
                // Results are appended even when stopped, so the history stays valid for the next turn.
                provider.AddToolResults(results);
                provider.PruneImages(keepImages);
                if (interrupted) return Stopped();
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return Stopped();
        }
        catch (ProviderException ex)
        {
            LogWarning($"HTTP backend ({context.Profile.Kind}): {ex.Message}");
            return Result(TurnOutcome.Failed, lastText, ex.Message);
        }
        catch (Exception ex)
        {
            var message = _transport?.Scrub(ex.Message) ?? ex.GetType().Name;
            LogWarning($"HTTP backend ({context.Profile.Kind}) unexpected {ex.GetType().Name}: {message}");
            return Result(TurnOutcome.Failed, lastText, $"Unexpected error talking to {provider.ProviderName}: {message}");
        }
        finally
        {
            _turnCts = null;
            Volatile.Write(ref _busy, 0);
        }
    }

    public Task InterruptAsync()
    {
        try
        {
            _turnCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The turn ended while we were interrupting it.
        }
        return Task.CompletedTask;
    }

    public Task ResetConversationAsync(CancellationToken ct)
    {
        _provider?.Reset();
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await InterruptAsync().ConfigureAwait(false);
        _provider = null;
        _context = null;
        if (_ownsHttp) _http.Dispose();
    }
}

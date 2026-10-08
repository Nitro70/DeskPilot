using System.Security.Principal;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Cli;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Vault;

namespace DeskPilot.Core.Agent;

/// <summary>
/// Finds what this PC offers (CLI agents, local model servers, API keys in the environment, Obsidian vaults,
/// monitors). Read-only: it never changes anything, runs every probe in parallel with short timeouts, and never throws.
/// </summary>
public sealed class EnvironmentDetector : IEnvironmentDetector
{
    public const string ClaudeName = "Claude Code";
    public const string GeminiName = "Gemini CLI";
    public const string CodexName = "Codex CLI";
    public const string OllamaBaseUrl = "http://127.0.0.1:11434";
    public const string LmStudioBaseUrl = "http://127.0.0.1:1234/v1";

    /// <summary>Environment variables that hold provider API keys. Only their names are ever reported.</summary>
    public static readonly IReadOnlyList<string> ApiKeyEnvVars = new[]
    {
        "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "OPENROUTER_API_KEY", "GEMINI_API_KEY", "GOOGLE_API_KEY", "GROQ_API_KEY",
        "XAI_API_KEY", "DEEPSEEK_API_KEY", "MISTRAL_API_KEY", "TOGETHER_API_KEY", "AZURE_OPENAI_API_KEY",
    };

    private static readonly Lazy<HttpClient> SharedHttp = new(() => new HttpClient(new SocketsHttpHandler
    {
        // Only loopback servers are probed; a system proxy must not see or slow these requests.
        UseProxy = false,
        ConnectTimeout = TimeSpan.FromSeconds(2),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    });

    private readonly IScreenCapture? _screen;
    private readonly IWindowManager? _windows;
    private readonly HttpClient _http;

    public EnvironmentDetector(IScreenCapture? screen = null, IWindowManager? windows = null, HttpClient? http = null)
    {
        _screen = screen;
        _windows = windows;
        _http = http ?? SharedHttp.Value;
    }

    /// <summary>Optional: current settings, so a configured Claude/Gemini CLI path is probed instead of the auto-detected one.</summary>
    public Func<AppSettings>? Settings { get; init; }

    // ---- seams for tests (defaults are the real probes) ----
    internal Func<string?, CancellationToken, Task<CliToolStatus>> ClaudeProbe { get; init; } = ClaudeCliProbe.ProbeAsync;
    internal Func<string, string?, string?> FindExecutable { get; init; } = (command, configured) => ExecutableLocator.Find(command, configured);
    internal Func<string, string, TimeSpan, CancellationToken, Task<string?>> RunTool { get; init; } = ProcessProbe.RunAsync;
    internal Func<string, bool> IsEnvVarSet { get; init; } = DefaultIsEnvVarSet;
    internal Func<IReadOnlyList<string>> FindVaults { get; init; } = ObsidianVaultDetector.FindVaults;
    internal Func<bool> IsProcessElevated { get; init; } = DefaultIsProcessElevated;
    internal TimeSpan HttpTimeout { get; init; } = TimeSpan.FromSeconds(1.5);
    internal TimeSpan CliTimeout { get; init; } = TimeSpan.FromSeconds(10);
    internal TimeSpan ClaudeProbeTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public async Task<EnvironmentReport> DetectAsync(CancellationToken ct)
    {
        var settings = TryGetSettings();
        var claudePath = ConfiguredPath(settings, p => p.Kind == ProviderKind.ClaudeCli);
        var geminiPath = ConfiguredPath(settings, p => p.PresetId == ProviderPresets.GeminiCliId);

        // Every probe below catches its own failures and has its own time limit; WhenAll cannot fault.
        var claude = Guard(() => ProbeClaudeAsync(claudePath, ct), NotFound(ClaudeName), ClaudeProbeTimeout + CliTimeout + TimeSpan.FromSeconds(5));
        var gemini = Guard(() => ProbeCliAsync(GeminiName, "gemini", geminiPath, ct), NotFound(GeminiName), CliTimeout + TimeSpan.FromSeconds(5));
        var codex = Guard(() => ProbeCliAsync(CodexName, "codex", null, ct), NotFound(CodexName), CliTimeout + TimeSpan.FromSeconds(5));
        var ollama = Guard(() => ProbeOllamaAsync(ct), NotRunning("Ollama", OllamaBaseUrl), HttpTimeout + TimeSpan.FromSeconds(2));
        var lmStudio = Guard(() => ProbeLmStudioAsync(ct), NotRunning("LM Studio", LmStudioBaseUrl), HttpTimeout + TimeSpan.FromSeconds(2));
        var keys = Guard(() => Task.Run(() => FindApiKeyEnvVars()), (IReadOnlyList<string>)Array.Empty<string>(), TimeSpan.FromSeconds(5));
        var vaults = Guard(() => Task.Run(() => FindVaults() ?? Array.Empty<string>()), (IReadOnlyList<string>)Array.Empty<string>(), TimeSpan.FromSeconds(5));
        var monitors = Guard(() => Task.Run(() => _screen?.GetMonitors() ?? Array.Empty<MonitorInfo>()), (IReadOnlyList<MonitorInfo>)Array.Empty<MonitorInfo>(), TimeSpan.FromSeconds(5));
        var elevated = Guard(() => Task.Run(() => _windows?.IsCurrentProcessElevated ?? IsProcessElevated()), false, TimeSpan.FromSeconds(5));

        await Task.WhenAll(claude, gemini, codex, ollama, lmStudio, keys, vaults, monitors, elevated).ConfigureAwait(false);

        return new EnvironmentReport(
            claude.Result, gemini.Result, codex.Result, ollama.Result, lmStudio.Result,
            keys.Result, vaults.Result, monitors.Result, elevated.Result);
    }

    private async Task<CliToolStatus> ProbeClaudeAsync(string? configuredPath, CancellationToken ct)
    {
        try
        {
            var probe = ClaudeProbe(configuredPath, ct);
            if (await Task.WhenAny(probe, Task.Delay(ClaudeProbeTimeout, ct)).ConfigureAwait(false) == probe)
            {
                var status = await probe.ConfigureAwait(false);
                if (status != null) return status;
            }
            else
            {
                Log.Warn("The Claude CLI probe did not finish in time; falling back to a version check");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log.Warn($"The Claude CLI probe failed ({ex.GetType().Name}); falling back to a version check");
        }
        return await ProbeCliAsync(ClaudeName, "claude", configuredPath, ct).ConfigureAwait(false);
    }

    private async Task<CliToolStatus> ProbeCliAsync(string name, string command, string? configuredPath, CancellationToken ct)
    {
        var path = await Task.Run(() => FindExecutable(command, configuredPath), ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path)) return NotFound(name);

        var output = await RunTool(path, "--version", CliTimeout, ct).ConfigureAwait(false);
        if (output == null) return new CliToolStatus(name, path, null, null, "Found, but it did not answer --version");
        var (version, line) = ProcessProbe.ParseVersion(output);
        return new CliToolStatus(name, path, version, null, version == null ? line : null);
    }

    private async Task<LocalServerStatus> ProbeOllamaAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(OllamaBaseUrl + "/api/tags", ct).ConfigureAwait(false);
        if (doc == null || !TryGetArray(doc.RootElement, "models", out var models)) return NotRunning("Ollama", OllamaBaseUrl);

        var list = new List<ModelInfo>();
        foreach (var m in models.EnumerateArray())
        {
            var name = GetString(m, "name") ?? GetString(m, "model");
            if (string.IsNullOrWhiteSpace(name)) continue;
            bool? vision = ProfileAutoConfig.LooksLikeVisionModel(name) || HasVisionFamily(m) ? true : null;
            list.Add(new ModelInfo(name, name, vision, null, null));
        }
        return new LocalServerStatus("Ollama", OllamaBaseUrl, true, list);
    }

    private async Task<LocalServerStatus> ProbeLmStudioAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(LmStudioBaseUrl + "/models", ct).ConfigureAwait(false);
        if (doc == null || !TryGetArray(doc.RootElement, "data", out var models)) return NotRunning("LM Studio", LmStudioBaseUrl);

        var list = new List<ModelInfo>();
        foreach (var m in models.EnumerateArray())
        {
            var id = GetString(m, "id");
            if (string.IsNullOrWhiteSpace(id)) continue;
            list.Add(new ModelInfo(id, id, ProfileAutoConfig.LooksLikeVisionModel(id) ? true : null, null, null));
        }
        return new LocalServerStatus("LM Studio", LmStudioBaseUrl, true, list);
    }

    /// <summary>GETs a small JSON document; null when nothing answers in time or the answer is not JSON.</summary>
    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HttpTimeout);
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private IReadOnlyList<string> FindApiKeyEnvVars()
    {
        var found = new List<string>();
        foreach (var name in ApiKeyEnvVars)
        {
            try
            {
                if (IsEnvVarSet(name)) found.Add(name);
            }
            catch (Exception)
            {
                // One unreadable variable must not hide the others.
            }
        }
        return found;
    }

    private static bool DefaultIsEnvVarSet(string name)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name))) return true;
        try
        {
            // Keys saved with setx after DeskPilot started are only in the user environment.
            return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User));
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool DefaultIsProcessElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private AppSettings? TryGetSettings()
    {
        try
        {
            return Settings?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn($"EnvironmentDetector could not read settings: {ex.Message}");
            return null;
        }
    }

    private static string? ConfiguredPath(AppSettings? settings, Func<ProviderProfile, bool> match)
    {
        var path = settings?.Profiles?.FirstOrDefault(p => p != null && match(p))?.CliPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    private static bool HasVisionFamily(JsonElement model)
    {
        if (model.ValueKind != JsonValueKind.Object || !model.TryGetProperty("details", out var details) ||
            details.ValueKind != JsonValueKind.Object || !TryGetArray(details, "families", out var families))
            return false;
        // Ollama lists the vision encoder ("clip", "mllama") among a multimodal model's families.
        return families.EnumerateArray().Any(f => f.ValueKind == JsonValueKind.String &&
            f.GetString() is { } s && (s.Equals("clip", StringComparison.OrdinalIgnoreCase) || s.Equals("mllama", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool TryGetArray(JsonElement obj, string name, out JsonElement array)
    {
        array = default;
        if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return false;
        array = value;
        return true;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static CliToolStatus NotFound(string name) => new(name, null, null, null, "Not found");

    private static LocalServerStatus NotRunning(string name, string baseUrl) => new(name, baseUrl, false, Array.Empty<ModelInfo>());

    /// <summary>Runs a probe so that neither an exception nor a hang can escape: either becomes the fallback.</summary>
    private static async Task<T> Guard<T>(Func<Task<T>> probe, T fallback, TimeSpan limit)
    {
        try
        {
            var task = probe();
            if (await Task.WhenAny(task, Task.Delay(limit)).ConfigureAwait(false) != task)
            {
                _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return fallback;
            }
            var result = await task.ConfigureAwait(false);
            return result is null ? fallback : result;
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) Log.Warn($"An environment probe failed: {ex.GetType().Name}: {ex.Message}");
            return fallback;
        }
    }
}

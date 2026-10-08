using System.Globalization;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Http;

/// <summary>Lists models for a profile: live from the provider when possible, otherwise the preset's suggestions.</summary>
public sealed class ModelCatalog : IModelCatalog
{
    private const int MaxConcurrentShows = 6;

    // Ids from OpenAI-style /models listings that are not chat models.
    private static readonly string[] NonChatMarkers =
    {
        "embed", "whisper", "tts", "dall-e", "moderation", "transcribe", "realtime", "-audio", "babbage", "davinci", "gpt-image", "sora", "rerank",
    };

    private readonly HttpClient _http;

    public ModelCatalog(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>Timeout for a listing request.</summary>
    internal TimeSpan ListTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Timeout for one Ollama /api/show request.</summary>
    internal TimeSpan ShowTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
    {
        if (profile == null) return new ModelListResult(Array.Empty<ModelInfo>(), "No profile selected.");
        apiKey ??= "";
        try
        {
            return profile.Kind switch
            {
                ProviderKind.ClaudeCli => new ModelListResult(ClaudeModels(ProviderPresets.ClaudeModels), null),
                ProviderKind.AcpAgent => new ModelListResult(Suggestions(profile), null),
                ProviderKind.AnthropicApi => await ListAnthropicAsync(profile, apiKey, ct).ConfigureAwait(false),
                ProviderKind.OpenAiCompatible => await ListOpenAiAsync(profile, apiKey, ct).ConfigureAwait(false),
                ProviderKind.Ollama => await ListOllamaAsync(profile, ct).ConfigureAwait(false),
                _ => new ModelListResult(Array.Empty<ModelInfo>(), $"Unknown provider kind {profile.Kind}."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ModelListResult(Array.Empty<ModelInfo>(), "Cancelled.");
        }
        catch (Exception ex)
        {
            var message = string.IsNullOrEmpty(apiKey) || apiKey.Length < 4 ? ex.Message : ex.Message.Replace(apiKey, "[redacted]", StringComparison.Ordinal);
            return new ModelListResult(SafeSuggestions(profile), $"Could not list models: {message}");
        }
    }

    // ---------------- Claude (CLI and API) ----------------

    internal static IReadOnlyList<ModelInfo> ClaudeModels(IEnumerable<string> ids) =>
        ids.Select(id => new ModelInfo(id, ClaudeDisplayName(id), true, true, true)).ToList();

    /// <summary>"haiku" becomes "haiku (newest Haiku)"; "claude-sonnet-5-5" becomes "Claude Sonnet 5.5".</summary>
    internal static string ClaudeDisplayName(string id)
    {
        var lower = id.Trim().ToLowerInvariant();
        if (lower is "haiku" or "sonnet" or "opus" or "fable")
            return $"{lower} (newest {CultureInfo.InvariantCulture.TextInfo.ToTitleCase(lower)})";
        if (!lower.StartsWith("claude-", StringComparison.Ordinal)) return id;
        var parts = lower["claude-".Length..].Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].All(char.IsLetter) || !parts[1].All(char.IsDigit)) return id;
        var family = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(parts[0]);
        // Version numbers are short; a long digit run is a date snapshot suffix.
        var version = parts.Skip(1).TakeWhile(p => p.All(char.IsDigit) && p.Length <= 2).ToList();
        var rest = parts.Skip(1 + version.Count).ToList();
        var name = $"Claude {family} {string.Join(".", version)}";
        return rest.Count == 0 ? name : $"{name} ({string.Join("-", rest)})";
    }

    private async Task<ModelListResult> ListAnthropicAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
    {
        var fallback = Suggestions(profile);
        if (string.IsNullOrWhiteSpace(apiKey))
            return new ModelListResult(fallback, "No API key is set, so these are suggested models. Add a key to list the models your account can use.");

        var baseUrl = AnthropicProvider.ResolveBaseUrl(profile);
        if (!Endpoints.TryParseHttpUrl(baseUrl, out _)) return new ModelListResult(fallback, $"The base URL '{baseUrl}' is not a valid http:// or https:// address.");
        var transport = Transport(profile, baseUrl, apiKey);
        HttpReply reply;
        try
        {
            reply = await transport.SendAsync(() => transport.CreateRequest(HttpMethod.Get, baseUrl + "/v1/models?limit=1000", null, new[]
            {
                new KeyValuePair<string, string>("x-api-key", apiKey),
                new KeyValuePair<string, string>("anthropic-version", AnthropicProvider.ApiVersion),
            }), ct).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return new ModelListResult(fallback, ex.Message);
        }
        if (!reply.IsSuccess) return new ModelListResult(fallback, transport.ErrorFor(reply, null).Message);

        var models = new List<ModelInfo>();
        if (reply.Json?["data"] is JsonArray data)
        {
            foreach (var item in data.OfType<JsonObject>())
            {
                var id = JsonUtil.Str(item["id"]);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var display = JsonUtil.Str(item["display_name"]);
                models.Add(new ModelInfo(id, string.IsNullOrWhiteSpace(display) ? ClaudeDisplayName(id) : display, true, true, null));
            }
        }
        return models.Count > 0 ? new ModelListResult(models, null) : new ModelListResult(fallback, "The provider returned no models; showing suggested models.");
    }

    // ---------------- OpenAI-compatible ----------------

    private async Task<ModelListResult> ListOpenAiAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
    {
        var suggestions = Suggestions(profile);
        var baseUrl = Endpoints.StripSuffix(Endpoints.TrimBase(profile.BaseUrl), "/chat/completions");
        if (baseUrl.Length == 0) return new ModelListResult(suggestions, "No base URL is set for this profile.");
        if (!Endpoints.TryParseHttpUrl(baseUrl, out _)) return new ModelListResult(suggestions, $"The base URL '{baseUrl}' is not a valid http:// or https:// address.");

        var transport = Transport(profile, baseUrl, apiKey);
        var headers = new List<KeyValuePair<string, string>>();
        if (!string.IsNullOrWhiteSpace(apiKey)) headers.Add(new("Authorization", "Bearer " + apiKey));
        HttpReply reply;
        try
        {
            reply = await transport.SendAsync(() => transport.CreateRequest(HttpMethod.Get, baseUrl + "/models", null, headers), ct).ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return new ModelListResult(suggestions, ex.Message);
        }
        if (!reply.IsSuccess) return new ModelListResult(suggestions, transport.ErrorFor(reply, null).Message);

        if (reply.Json is null)
            return new ModelListResult(suggestions, $"{transport.ProviderName} returned a model list DeskPilot could not read.");
        var live = ParseOpenAiModels(reply.Json);
        return new ModelListResult(Merge(suggestions, live), null);
    }

    internal static List<ModelInfo> ParseOpenAiModels(JsonNode? json)
    {
        JsonArray? items = json as JsonArray ?? json?["data"] as JsonArray ?? json?["models"] as JsonArray;
        var result = new List<ModelInfo>();
        if (items == null) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items.OfType<JsonObject>())
        {
            var id = JsonUtil.Str(item["id"]) ?? JsonUtil.Str(item["name"]);
            if (string.IsNullOrWhiteSpace(id)) continue;
            if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];   // Gemini
            var lower = id.ToLowerInvariant();
            if (NonChatMarkers.Any(lower.Contains) || !seen.Add(id)) continue;

            bool? vision = null, tools = null, thinking = null;
            if (item["architecture"]?["input_modalities"] is JsonArray modalities)   // OpenRouter
                vision = modalities.Any(m => string.Equals(JsonUtil.Str(m), "image", StringComparison.OrdinalIgnoreCase));
            if (item["supported_parameters"] is JsonArray parameters)               // OpenRouter
            {
                var names = parameters.Select(JsonUtil.Str).Where(s => s != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                tools = names.Contains("tools");
                thinking = names.Contains("reasoning") || names.Contains("include_reasoning");
            }
            var name = JsonUtil.Str(item["name"]);
            var display = !string.IsNullOrWhiteSpace(name) && !string.Equals(name, id, StringComparison.Ordinal) && JsonUtil.Str(item["id"]) != null ? $"{name} ({id})" : id;
            result.Add(new ModelInfo(id, display, vision, tools, thinking));
        }
        return result;
    }

    /// <summary>Preset suggestions first (with live details when the server knows them), then the rest of the live list.</summary>
    internal static IReadOnlyList<ModelInfo> Merge(IReadOnlyList<ModelInfo> suggestions, IReadOnlyList<ModelInfo> live)
    {
        var byId = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in live) byId.TryAdd(m.Id, m);
        var result = new List<ModelInfo>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in suggestions)
        {
            if (!used.Add(s.Id)) continue;
            result.Add(byId.TryGetValue(s.Id, out var l) ? l : s);
        }
        result.AddRange(live.Where(m => used.Add(m.Id)).OrderBy(m => m.Id, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    // ---------------- Ollama ----------------

    private async Task<ModelListResult> ListOllamaAsync(ProviderProfile profile, CancellationToken ct)
    {
        var suggestions = Suggestions(profile);
        var baseUrl = OllamaProvider.ResolveBaseUrl(profile.BaseUrl);
        if (!Endpoints.TryParseHttpUrl(baseUrl, out _)) return new ModelListResult(suggestions, $"The base URL '{baseUrl}' is not a valid http:// or https:// address.");

        var transport = Transport(profile, baseUrl, "");
        HttpReply reply;
        try
        {
            reply = await transport.SendAsync(() => transport.CreateRequest(HttpMethod.Get, baseUrl + "/api/tags", null, Array.Empty<KeyValuePair<string, string>>()), ct)
                .ConfigureAwait(false);
        }
        catch (ProviderException ex)
        {
            return new ModelListResult(suggestions, ex.Message);
        }
        if (!reply.IsSuccess) return new ModelListResult(suggestions, transport.ErrorFor(reply, null).Message);

        var names = new List<string>();
        if (reply.Json?["models"] is JsonArray models)
        {
            foreach (var m in models.OfType<JsonObject>())
            {
                var name = JsonUtil.Str(m["name"]) ?? JsonUtil.Str(m["model"]);
                if (!string.IsNullOrWhiteSpace(name) && !names.Contains(name, StringComparer.Ordinal)) names.Add(name);
            }
        }
        if (names.Count == 0)
            return new ModelListResult(suggestions, "Ollama has no models installed yet. Download one, for example: ollama pull " + (suggestions.FirstOrDefault()?.Id ?? "qwen2.5vl:7b"));

        using var gate = new SemaphoreSlim(MaxConcurrentShows);
        var showTransport = new HttpTransport(_http, ProfileName(profile), baseUrl, "", ShowTimeout, profile.ExtraHeaders, null, null, maxRetries: 0);
        var tasks = names.Select(async name =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                return await ShowAsync(showTransport, baseUrl, name, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        var infos = await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ModelListResult(infos, null);
    }

    private static async Task<ModelInfo> ShowAsync(HttpTransport transport, string baseUrl, string name, CancellationToken ct)
    {
        try
        {
            var body = new JsonObject { ["model"] = name };
            var reply = await transport.SendAsync(() => transport.CreateRequest(HttpMethod.Post, baseUrl + "/api/show", body, Array.Empty<KeyValuePair<string, string>>()), ct)
                .ConfigureAwait(false);
            if (reply.IsSuccess && reply.Json?["capabilities"] is JsonArray caps)
            {
                var set = caps.Select(JsonUtil.Str).Where(s => s != null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                return new ModelInfo(name, name, set.Contains("vision"), set.Contains("tools"), set.Contains("thinking"));
            }
        }
        catch (ProviderException)
        {
            // Capabilities are optional details; the model is still listed.
        }
        return new ModelInfo(name, name, null, null, null);
    }

    // ---------------- Helpers ----------------

    private HttpTransport Transport(ProviderProfile profile, string baseUrl, string apiKey) =>
        new(_http, ProfileName(profile), baseUrl, apiKey, ListTimeout, profile.ExtraHeaders, null, null, maxRetries: 0);

    private static string ProfileName(ProviderProfile profile) => HttpAgentBackend.ProviderDisplayName(profile);

    /// <summary>The preset's suggested models (Claude ids get friendly names).</summary>
    internal static IReadOnlyList<ModelInfo> Suggestions(ProviderProfile profile)
    {
        var preset = ProviderPresets.Find(profile.PresetId);
        if (preset == null)
        {
            preset = profile.Kind switch
            {
                ProviderKind.AnthropicApi => ProviderPresets.Find(ProviderPresets.AnthropicApiId),
                ProviderKind.Ollama => ProviderPresets.Find(ProviderPresets.OllamaId),
                ProviderKind.ClaudeCli => ProviderPresets.Find(ProviderPresets.ClaudeSubscriptionId),
                _ => null,
            };
        }
        if (preset == null) return Array.Empty<ModelInfo>();
        if (preset.Kind is ProviderKind.AnthropicApi or ProviderKind.ClaudeCli) return ClaudeModels(preset.SuggestedModels);
        return preset.SuggestedModels.Select(id => new ModelInfo(id, id, null, null, null)).ToList();
    }

    private static IReadOnlyList<ModelInfo> SafeSuggestions(ProviderProfile profile)
    {
        try
        {
            return Suggestions(profile);
        }
        catch (Exception)
        {
            return Array.Empty<ModelInfo>();
        }
    }
}

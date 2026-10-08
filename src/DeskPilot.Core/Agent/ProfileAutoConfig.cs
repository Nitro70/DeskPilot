using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Agent;

/// <summary>Turns an EnvironmentReport into provider profiles (first run and the settings "add detected" action).</summary>
public static class ProfileAutoConfig
{
    /// <summary>Model name fragments of vision-capable local model families (compared without separators).</summary>
    internal static readonly IReadOnlyList<string> VisionModelHints = new[]
    {
        "qwen2.5vl", "qwen3-vl", "qwen-vl", "llava", "gemma3", "llama3.2-vision", "llama4", "minicpm-v",
        "mistral-small3", "granite3.2-vision", "moondream", "pixtral", "internvl",
    };

    /// <summary>API key environment variables and the preset each one enables, in the order they are added.</summary>
    internal static readonly IReadOnlyList<(string PresetId, string[] EnvVars)> KeyPresets = new[]
    {
        (ProviderPresets.AnthropicApiId, new[] { "ANTHROPIC_API_KEY" }),
        (ProviderPresets.OpenAiId, new[] { "OPENAI_API_KEY" }),
        (ProviderPresets.OpenRouterId, new[] { "OPENROUTER_API_KEY" }),
        (ProviderPresets.GeminiApiId, new[] { "GEMINI_API_KEY", "GOOGLE_API_KEY" }),
        (ProviderPresets.GroqId, new[] { "GROQ_API_KEY" }),
        (ProviderPresets.XaiId, new[] { "XAI_API_KEY" }),
        (ProviderPresets.DeepSeekId, new[] { "DEEPSEEK_API_KEY" }),
        (ProviderPresets.MistralId, new[] { "MISTRAL_API_KEY" }),
        (ProviderPresets.TogetherId, new[] { "TOGETHER_API_KEY" }),
        // AZURE_OPENAI_API_KEY is detected but not auto-added: Azure also needs the resource's base URL.
    };

    private static readonly string[] NormalizedVisionHints = VisionModelHints.Select(Compact).ToArray();

    /// <summary>Adds profiles for providers found on this PC (Ollama, LM Studio, API keys in env, Gemini CLI) that are not configured yet. Returns true if anything changed.</summary>
    public static bool ApplyDetected(AppSettings settings, EnvironmentReport report)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(report);
        settings.Profiles ??= new List<ProviderProfile>();

        var added = new List<ProviderProfile>();
        void Add(ProviderProfile profile)
        {
            settings.Profiles.Add(profile);
            added.Add(profile);
        }

        if (report.Ollama is { Running: true } ollama && !IsConfigured(settings, ProviderPresets.OllamaId))
            Add(CreateLocalProfile(ProviderPresets.OllamaId, ollama));

        if (report.LmStudio is { Running: true } lmStudio && !IsConfigured(settings, ProviderPresets.LmStudioId))
            Add(CreateLocalProfile(ProviderPresets.LmStudioId, lmStudio));

        var envFound = new HashSet<string>(report.ApiKeyEnvVarsFound ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (presetId, envVars) in KeyPresets)
        {
            var found = envVars.FirstOrDefault(envFound.Contains);
            if (found == null || IsConfigured(settings, presetId)) continue;
            var profile = ProviderPresets.CreateProfile(presetId);
            profile.ApiKeyEnvVar = found;
            Add(profile);
        }

        if (!string.IsNullOrWhiteSpace(report.GeminiCli?.Path) && !IsConfigured(settings, ProviderPresets.GeminiCliId))
            Add(ProviderPresets.CreateProfile(ProviderPresets.GeminiCliId));

        // The Claude subscription stays the default; only move away from it when its CLI is missing,
        // because then it cannot work at all.
        var switched = false;
        if (added.Count > 0 && string.IsNullOrWhiteSpace(report.ClaudeCli?.Path) &&
            settings.ActiveProfile is { Kind: ProviderKind.ClaudeCli })
        {
            settings.ActiveProfileId = added[0].Id;
            switched = true;
        }

        return added.Count > 0 || switched;
    }

    /// <summary>True when a model name suggests a vision-capable model (e.g. "qwen2.5vl:7b", "llava:13b", "Qwen/Qwen2.5-VL-7B").</summary>
    public static bool LooksLikeVisionModel(string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName)) return false;
        var compact = Compact(modelName);
        if (NormalizedVisionHints.Any(compact.Contains)) return true;
        var tokens = modelName.ToLowerInvariant().Split(new[] { '-', '_', ':', '/', '.', ' ', '@' }, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Any(t => t is "vl" or "vision");
    }

    /// <summary>The model to preselect: the first vision model, else the first that is not an embedding model, else the first.</summary>
    internal static ModelInfo? PickModel(IReadOnlyList<ModelInfo>? models)
    {
        if (models == null || models.Count == 0) return null;
        return models.FirstOrDefault(m => LooksLikeVisionModel(m.Id))
            ?? models.FirstOrDefault(m => m.SupportsVision == true)
            ?? models.FirstOrDefault(m => !LooksLikeEmbeddingModel(m.Id))
            ?? models[0];
    }

    private static ProviderProfile CreateLocalProfile(string presetId, LocalServerStatus server)
    {
        var profile = ProviderPresets.CreateProfile(presetId);
        if (!string.IsNullOrWhiteSpace(server.BaseUrl)) profile.BaseUrl = server.BaseUrl.Trim().TrimEnd('/');
        var model = PickModel(server.Models);
        if (model != null)
        {
            profile.Model = model.Id;
            if (model.SupportsVision == false && !LooksLikeVisionModel(model.Id)) profile.SupportsVision = false;
        }
        return profile;
    }

    private static bool IsConfigured(AppSettings settings, string presetId)
    {
        if (settings.Profiles.Any(p => string.Equals(p.PresetId, presetId, StringComparison.OrdinalIgnoreCase))) return true;
        // A hand-made profile pointing at the same endpoint counts too, so the same provider never appears twice.
        var preset = ProviderPresets.Find(presetId);
        if (preset == null || string.IsNullOrWhiteSpace(preset.BaseUrl)) return false;
        var url = NormalizeUrl(preset.BaseUrl);
        return settings.Profiles.Any(p => p.Kind == preset.Kind && NormalizeUrl(p.BaseUrl) == url);
    }

    private static string NormalizeUrl(string? url) =>
        (url ?? "").Trim().TrimEnd('/').ToLowerInvariant().Replace("://localhost", "://127.0.0.1");

    private static bool LooksLikeEmbeddingModel(string? name) =>
        !string.IsNullOrEmpty(name) && (name.Contains("embed", StringComparison.OrdinalIgnoreCase) ||
                                        name.Contains("rerank", StringComparison.OrdinalIgnoreCase));

    private static string Compact(string s) =>
        new(s.ToLowerInvariant().Where(c => c is not ('-' or '_' or '.' or ' ')).ToArray());
}

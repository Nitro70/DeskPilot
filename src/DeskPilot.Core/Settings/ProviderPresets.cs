namespace DeskPilot.Core.Settings;

/// <summary>A template for creating a provider profile. Users can edit everything afterwards.</summary>
public sealed record ProviderPreset(
    string Id,
    string DisplayName,
    ProviderKind Kind,
    string BaseUrl,
    string DefaultModel,
    string ApiKeyEnvVar,
    bool NeedsApiKey,
    ReasoningStyle ReasoningStyle,
    IReadOnlyList<string> SuggestedModels,
    string Notes,
    string CliCommand = "",             // ClaudeCli / AcpAgent: executable name to auto-detect
    string CliArgs = "",                // AcpAgent: arguments that start ACP mode
    bool IsLocal = false);

public static class ProviderPresets
{
    public const string ClaudeSubscriptionId = "claude-cli";
    public const string AnthropicApiId = "anthropic-api";
    public const string OpenAiId = "openai";
    public const string OpenRouterId = "openrouter";
    public const string GeminiApiId = "gemini-api";
    public const string GroqId = "groq";
    public const string XaiId = "xai";
    public const string DeepSeekId = "deepseek";
    public const string MistralId = "mistral";
    public const string TogetherId = "together";
    public const string AzureOpenAiId = "azure-openai";
    public const string OllamaId = "ollama";
    public const string LmStudioId = "lmstudio";
    public const string LocalOpenAiId = "local-openai";
    public const string GeminiCliId = "gemini-cli";
    public const string CustomAcpId = "custom-acp";

    public static readonly IReadOnlyList<string> ClaudeModels = new[]
    {
        "haiku", "sonnet", "opus", "fable",
        "claude-haiku-5-5", "claude-sonnet-5-5", "claude-opus-5-5", "claude-fable-5-1",
        "claude-haiku-4-5", "claude-sonnet-5", "claude-opus-5",
    };

    public static readonly IReadOnlyList<ProviderPreset> All = new[]
    {
        new ProviderPreset(ClaudeSubscriptionId, "Claude (your subscription via Claude Code)", ProviderKind.ClaudeCli,
            "", "haiku", "", false, ReasoningStyle.None, ClaudeModels,
            "Uses the Claude Code CLI you are already logged in to (claude auth login). No API key; usage counts against your Claude plan.",
            CliCommand: "claude"),

        new ProviderPreset(AnthropicApiId, "Anthropic API (console key)", ProviderKind.AnthropicApi,
            "https://api.anthropic.com", "claude-haiku-5-5", "ANTHROPIC_API_KEY", true, ReasoningStyle.None,
            ClaudeModels.Where(m => m.StartsWith("claude-")).ToArray(),
            "Pay-as-you-go API key from console.anthropic.com."),

        new ProviderPreset(OpenAiId, "OpenAI API", ProviderKind.OpenAiCompatible,
            "https://api.openai.com/v1", "gpt-5-mini", "OPENAI_API_KEY", true, ReasoningStyle.ReasoningEffort,
            new[] { "gpt-5", "gpt-5-mini", "gpt-5-nano", "gpt-4.1", "gpt-4.1-mini", "o4-mini", "gpt-4o" },
            "API key from platform.openai.com."),

        new ProviderPreset(OpenRouterId, "OpenRouter", ProviderKind.OpenAiCompatible,
            "https://openrouter.ai/api/v1", "anthropic/claude-haiku-4.5", "OPENROUTER_API_KEY", true, ReasoningStyle.OpenRouter,
            new[] { "anthropic/claude-sonnet-4.5", "openai/gpt-5-mini", "google/gemini-2.5-flash", "qwen/qwen2.5-vl-72b-instruct", "meta-llama/llama-4-maverick" },
            "One key for hundreds of models. Pick a vision-capable model."),

        new ProviderPreset(GeminiApiId, "Google Gemini API", ProviderKind.OpenAiCompatible,
            "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash", "GEMINI_API_KEY", true, ReasoningStyle.ReasoningEffort,
            new[] { "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.5-flash-lite" },
            "Key from aistudio.google.com (OpenAI-compatible endpoint). Try Normalized 0-1000 coordinates if clicks miss."),

        new ProviderPreset(GroqId, "Groq", ProviderKind.OpenAiCompatible,
            "https://api.groq.com/openai/v1", "meta-llama/llama-4-scout-17b-16e-instruct", "GROQ_API_KEY", true, ReasoningStyle.None,
            new[] { "meta-llama/llama-4-scout-17b-16e-instruct", "meta-llama/llama-4-maverick-17b-128e-instruct" },
            "Very fast inference. Use a vision model."),

        new ProviderPreset(XaiId, "xAI (Grok)", ProviderKind.OpenAiCompatible,
            "https://api.x.ai/v1", "grok-4", "XAI_API_KEY", true, ReasoningStyle.None,
            new[] { "grok-4", "grok-4-fast" }, "Key from console.x.ai."),

        new ProviderPreset(DeepSeekId, "DeepSeek", ProviderKind.OpenAiCompatible,
            "https://api.deepseek.com/v1", "deepseek-chat", "DEEPSEEK_API_KEY", true, ReasoningStyle.None,
            new[] { "deepseek-chat", "deepseek-reasoner" },
            "Text-only models: DeskPilot uses UI Automation text instead of screenshots."),

        new ProviderPreset(MistralId, "Mistral", ProviderKind.OpenAiCompatible,
            "https://api.mistral.ai/v1", "mistral-medium-latest", "MISTRAL_API_KEY", true, ReasoningStyle.None,
            new[] { "mistral-medium-latest", "pixtral-large-latest", "mistral-small-latest" }, "Key from console.mistral.ai."),

        new ProviderPreset(TogetherId, "Together AI", ProviderKind.OpenAiCompatible,
            "https://api.together.xyz/v1", "Qwen/Qwen2.5-VL-72B-Instruct", "TOGETHER_API_KEY", true, ReasoningStyle.None,
            new[] { "Qwen/Qwen2.5-VL-72B-Instruct", "meta-llama/Llama-4-Maverick-17B-128E-Instruct-FP8" }, "Key from api.together.ai."),

        new ProviderPreset(AzureOpenAiId, "Azure OpenAI", ProviderKind.OpenAiCompatible,
            "https://YOUR-RESOURCE.openai.azure.com/openai/v1", "gpt-4.1", "AZURE_OPENAI_API_KEY", true, ReasoningStyle.ReasoningEffort,
            new[] { "gpt-4.1", "gpt-5-mini" },
            "Set the base URL to your resource's /openai/v1 endpoint; the model is your deployment name."),

        new ProviderPreset(OllamaId, "Ollama (local)", ProviderKind.Ollama,
            "http://127.0.0.1:11434", "qwen2.5vl:7b", "", false, ReasoningStyle.None,
            new[] { "qwen2.5vl:7b", "qwen2.5vl:32b", "gemma3:12b", "llama3.2-vision:11b", "mistral-small3.2:24b" },
            "Runs on your PC. Use a vision model (the model list marks them). Thinking can be switched off for speed.",
            IsLocal: true),

        new ProviderPreset(LmStudioId, "LM Studio (local)", ProviderKind.OpenAiCompatible,
            "http://127.0.0.1:1234/v1", "", "", false, ReasoningStyle.None, Array.Empty<string>(),
            "Start the LM Studio server first. Pick a vision model.", IsLocal: true),

        new ProviderPreset(LocalOpenAiId, "Other OpenAI-compatible server (vLLM, llama.cpp, Jan, KoboldCpp...)", ProviderKind.OpenAiCompatible,
            "http://127.0.0.1:8000/v1", "", "", false, ReasoningStyle.None, Array.Empty<string>(),
            "Any server that speaks /v1/chat/completions with tool calling.", IsLocal: true),

        new ProviderPreset(GeminiCliId, "Gemini CLI (experimental, ACP)", ProviderKind.AcpAgent,
            "", "", "", false, ReasoningStyle.None, new[] { "gemini-2.5-pro", "gemini-2.5-flash" },
            "Uses the Gemini CLI's own login through the Agent Client Protocol.",
            CliCommand: "gemini", CliArgs: "--acp"),

        new ProviderPreset(CustomAcpId, "Other ACP agent (experimental)", ProviderKind.AcpAgent,
            "", "", "", false, ReasoningStyle.None, Array.Empty<string>(),
            "Any agent that speaks the Agent Client Protocol over stdio. Set the command and its arguments."),
    };

    public static ProviderPreset? Find(string id) => All.FirstOrDefault(p => p.Id == id);

    public static ProviderProfile CreateProfile(string presetId)
    {
        var p = Find(presetId) ?? throw new ArgumentException($"Unknown preset '{presetId}'", nameof(presetId));
        return new ProviderProfile
        {
            Name = p.DisplayName,
            Kind = p.Kind,
            PresetId = p.Id,
            Model = p.DefaultModel,
            BaseUrl = p.BaseUrl,
            ApiKeyEnvVar = p.ApiKeyEnvVar,
            ReasoningStyle = p.ReasoningStyle,
            CliPath = "",
            ExtraCliArgs = p.Kind == ProviderKind.AcpAgent ? p.CliArgs : "",
            SupportsVision = p.Id is not DeepSeekId,
        };
    }
}

using System.Text.Json.Serialization;

namespace DeskPilot.Core.Settings;

// Settings are plain mutable POCOs serialized with System.Text.Json (camelCase, enums as strings).
// Every property has a safe default so an older or hand-edited settings.json still loads.
// Nothing user-specific is hard-coded anywhere: paths, keys and models all live here.

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool FirstRunCompleted { get; set; }
    public string ActiveProfileId { get; set; } = "";
    public List<ProviderProfile> Profiles { get; set; } = new();
    public VaultSettings Vault { get; set; } = new();
    public SafetySettings Safety { get; set; } = new();
    public ScreenSettings Screen { get; set; } = new();
    public UiSettings Ui { get; set; } = new();
    public PromptSettings Prompt { get; set; } = new();

    [JsonIgnore]
    public ProviderProfile? ActiveProfile =>
        Profiles.FirstOrDefault(p => p.Id == ActiveProfileId) ?? Profiles.FirstOrDefault();
}

public enum ProviderKind
{
    /// <summary>Claude Code CLI using the user's own Claude subscription login (default).</summary>
    ClaudeCli,
    /// <summary>Anthropic Messages API with an API key (console.anthropic.com).</summary>
    AnthropicApi,
    /// <summary>Any OpenAI-compatible /chat/completions endpoint: OpenAI, OpenRouter, Groq, Gemini, xAI, DeepSeek, Mistral, LM Studio, vLLM, llama.cpp, Azure...</summary>
    OpenAiCompatible,
    /// <summary>Ollama native /api/chat (local models, think on/off).</summary>
    Ollama,
    /// <summary>Any Agent Client Protocol agent over stdio, e.g. `gemini --acp` (experimental).</summary>
    AcpAgent,
}

/// <summary>Auto = do not send any thinking parameter (provider/model default).</summary>
public enum ThinkingMode { Auto, On, Off }

/// <summary>How an OpenAI-compatible endpoint expects reasoning settings.</summary>
public enum ReasoningStyle { None, ReasoningEffort, OpenRouter }

public sealed class ProviderProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public ProviderKind Kind { get; set; } = ProviderKind.ClaudeCli;
    /// <summary>Preset id this profile was created from (see ProviderPresets), informational.</summary>
    public string PresetId { get; set; } = "";
    public string Model { get; set; } = "";

    // ---- HTTP providers ----
    public string BaseUrl { get; set; } = "";
    /// <summary>API key encrypted with Windows DPAPI (CurrentUser), base64. Never stored in plain text.</summary>
    public string ApiKeyProtected { get; set; } = "";
    /// <summary>Environment variable to read the key from when no key is stored, e.g. OPENAI_API_KEY.</summary>
    public string ApiKeyEnvVar { get; set; } = "";
    /// <summary>Extra HTTP headers (e.g. Azure "api-key", OpenRouter "HTTP-Referer").</summary>
    public Dictionary<string, string> ExtraHeaders { get; set; } = new();
    /// <summary>Raw JSON object merged into every request body (advanced).</summary>
    public string ExtraBodyJson { get; set; } = "";
    public ReasoningStyle ReasoningStyle { get; set; } = ReasoningStyle.ReasoningEffort;
    public int MaxOutputTokens { get; set; } = 8192;
    public double? Temperature { get; set; }
    /// <summary>False for text-only models: screenshots are replaced by UI Automation text.</summary>
    public bool SupportsVision { get; set; } = true;
    /// <summary>Anthropic API: use the context-management beta to clear old tool results server-side.</summary>
    public bool UseContextEditing { get; set; } = true;
    /// <summary>Anthropic API: use prompt caching.</summary>
    public bool UsePromptCaching { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 300;

    // ---- Thinking / reasoning ----
    public ThinkingMode Thinking { get; set; } = ThinkingMode.Auto;
    /// <summary>"" (provider default), "none", "minimal", "low", "medium", "high", "xhigh", "max".</summary>
    public string Effort { get; set; } = "";
    /// <summary>Thinking budget for models that still use budget_tokens (e.g. Claude Haiku 4.5).</summary>
    public int ThinkingBudgetTokens { get; set; } = 4096;

    // ---- CLI / ACP agents ----
    /// <summary>Path to the CLI executable; empty = auto-detect.</summary>
    public string CliPath { get; set; } = "";
    /// <summary>Extra command-line arguments appended verbatim (advanced).</summary>
    public string ExtraCliArgs { get; set; } = "";
    /// <summary>ClaudeCli: remove ANTHROPIC_API_KEY/ANTHROPIC_AUTH_TOKEN from the child env so the subscription login is used.</summary>
    public bool ForceSubscriptionLogin { get; set; } = true;
    /// <summary>Extra environment variables for CLI/ACP agents (advanced).</summary>
    public Dictionary<string, string> ExtraEnv { get; set; } = new();
}

public sealed class VaultSettings
{
    /// <summary>Optional notes folder (e.g. an Obsidian vault). Empty = no vault.</summary>
    public string Path { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool AllowWrites { get; set; }
    public int MaxSearchResults { get; set; } = 8;
    public int MaxReadLines { get; set; } = 400;
    public List<string> IncludeExtensions { get; set; } = new() { ".md", ".txt", ".markdown", ".org", ".csv", ".json", ".yaml", ".yml" };
    public List<string> ExcludeFolders { get; set; } = new() { ".obsidian", ".git", ".trash", "node_modules", ".stfolder" };
}

public enum ConfirmMode { Never, RiskyOnly, Always }

public sealed class SafetySettings
{
    /// <summary>Off by default: the agent may not touch elevated (administrator) windows or start elevated programs.</summary>
    public bool AllowAdmin { get; set; }
    public ConfirmMode Confirm { get; set; } = ConfirmMode.Never;
    public bool AllowAppLaunch { get; set; } = true;
    /// <summary>Lets the agent run PowerShell/cmd commands (never elevated unless AllowAdmin). Off by default.</summary>
    public bool AllowShellCommands { get; set; }
    public bool AllowClipboard { get; set; } = true;
    /// <summary>Log actions without moving the mouse or pressing keys.</summary>
    public bool DryRun { get; set; }
    /// <summary>Slam the mouse into the top-left corner of the primary screen to stop the agent.</summary>
    public bool FailsafeCorner { get; set; } = true;
    /// <summary>Stop the agent when the user moves the mouse while it works.</summary>
    public bool StopOnUserMouseMove { get; set; }
    public int MaxStepsPerTurn { get; set; } = 80;
    /// <summary>Process names (without .exe) the agent must never interact with, e.g. a password manager.</summary>
    public List<string> BlockedProcesses { get; set; } = new();
    /// <summary>Regex patterns that block typed text / commands when admin mode is off.</summary>
    public List<string> ElevationTextPatterns { get; set; } = new()
    {
        @"\brunas\b",
        @"-verb\s+['""]?runas",
        @"\bsudo\b",
        @"\bgsudo\b",
        @"\bpsexec\b",
        @"\bnircmd(c)?\s+elevate\b",
    };
    /// <summary>Programs that always trigger UAC; launching them is blocked when admin mode is off.</summary>
    public List<string> ElevatedLaunchTargets { get; set; } = new()
    {
        "regedit", "regedt32", "gpedit.msc", "secpol.msc", "diskmgmt.msc", "lusrmgr.msc", "wf.msc", "compmgmt.msc", "netplwiz", "msconfig", "UserAccountControlSettings",
    };
}

public enum MonitorSelection { Primary, AllMonitors, Specific }

public enum CoordinateMode
{
    /// <summary>Coordinates are pixels of the (possibly downscaled) screenshot the model sees.</summary>
    ScreenshotPixels,
    /// <summary>Coordinates are 0-1000 on both axes (works better for some local VLMs, e.g. Qwen-VL, Gemini).</summary>
    Normalized1000,
}

public sealed class ScreenSettings
{
    public MonitorSelection Monitor { get; set; } = MonitorSelection.Primary;
    public int MonitorIndex { get; set; }
    /// <summary>Screenshots are downscaled to fit inside this box (aspect kept). 1280x800 is a good fit for most vision models.</summary>
    public int MaxImageWidth { get; set; } = 1280;
    public int MaxImageHeight { get; set; } = 800;
    public string Format { get; set; } = "jpeg";
    public int JpegQuality { get; set; } = 75;
    public bool DrawCursor { get; set; } = true;
    /// <summary>0 = off. Draw a labelled grid every N image pixels (helps weaker local models).</summary>
    public int GridSpacing { get; set; }
    public CoordinateMode Coordinates { get; set; } = CoordinateMode.ScreenshotPixels;
    /// <summary>Return a fresh screenshot after each action so the model sees the result immediately.</summary>
    public bool ScreenshotAfterAction { get; set; } = true;
    /// <summary>Delay before that screenshot, so the UI has time to react.</summary>
    public int ActionSettleDelayMs { get; set; } = 450;
    /// <summary>HTTP providers: keep at most this many screenshots in the conversation (older ones become text).</summary>
    public int ScreenshotsToKeep { get; set; } = 3;
    public int TypingDelayMs { get; set; } = 4;
}

public enum OverlayCorner { TopLeft, TopCenter, TopRight, BottomLeft, BottomCenter, BottomRight }

public sealed class UiSettings
{
    public bool AdvancedMode { get; set; }
    public bool MinimizeWhileWorking { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public OverlayCorner OverlayPosition { get; set; } = OverlayCorner.BottomRight;
    public string StopHotkey { get; set; } = "Ctrl+Alt+X";
    public bool CloseToTray { get; set; }
    public bool ShowThinking { get; set; } = true;
    public bool ShowScreenshots { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 720;
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}

public sealed class PromptSettings
{
    /// <summary>Advanced mode: full replacement for the built-in computer-use system prompt. Empty = built-in.</summary>
    public string CustomSystemPrompt { get; set; } = "";
    /// <summary>Extra standing instructions appended to the prompt (preferences, context about the user's setup).</summary>
    public string UserInstructions { get; set; } = "";
}

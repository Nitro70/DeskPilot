using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;

namespace DeskPilot.Core.Agent;

/// <summary>Pure helpers behind AgentSession: configuration fingerprint, API key rules, descriptions.</summary>
internal static class SessionConfig
{
    /// <summary>
    /// A hash of everything that requires a backend restart when it changes: the active profile (all fields
    /// except its display name) and the settings that end up in the rendered system prompt.
    /// </summary>
    public static string ComputeFingerprint(AppSettings settings, ProviderProfile? profile, bool vaultAvailable)
    {
        var sb = new StringBuilder();
        if (profile != null)
        {
            var node = JsonSerializer.SerializeToNode(profile, SettingsStore.JsonOptions) as JsonObject;
            // Renaming a profile changes nothing the model sees, so it must not cost the conversation.
            node?.Remove("name");
            sb.Append(node?.ToJsonString() ?? "");
        }
        else
        {
            sb.Append("(no profile)");
        }

        var vault = settings.Vault ?? new VaultSettings();
        var safety = settings.Safety ?? new SafetySettings();
        var screen = settings.Screen ?? new ScreenSettings();
        var prompt = settings.Prompt ?? new PromptSettings();
        void Add(object? value)
        {
            sb.Append('\u001F');
            sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
        }

        Add(vaultAvailable);
        Add(vault.Path?.Trim());
        Add(vault.Enabled);
        Add(vault.AllowWrites);
        Add(safety.AllowAdmin);
        Add(safety.DryRun);
        Add(safety.MaxStepsPerTurn);
        Add(safety.AllowShellCommands);
        Add(safety.AllowAppLaunch);
        Add(safety.AllowClipboard);
        Add(screen.Coordinates);
        Add(screen.Monitor);
        Add(screen.MonitorIndex);
        Add(screen.MaxImageWidth);
        Add(screen.MaxImageHeight);
        Add(prompt.CustomSystemPrompt);
        Add(prompt.UserInstructions);

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Whether the profile cannot work without an API key.</summary>
    public static bool RequiresApiKey(ProviderProfile profile)
    {
        switch (profile.Kind)
        {
            case ProviderKind.AnthropicApi:
                return !HasAuthHeader(profile);
            case ProviderKind.OpenAiCompatible:
                if (HasAuthHeader(profile)) return false;
                var preset = ProviderPresets.Find(profile.PresetId ?? "");
                if (preset != null) return preset.NeedsApiKey;
                // A custom profile that names a key variable expects a key; one without is a keyless (local) server.
                return !string.IsNullOrWhiteSpace(profile.ApiKeyEnvVar);
            default:
                return false;
        }
    }

    private static bool HasAuthHeader(ProviderProfile profile) =>
        profile.ExtraHeaders != null && profile.ExtraHeaders.Any(h =>
            !string.IsNullOrWhiteSpace(h.Value) &&
            (h.Key.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
             h.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase) ||
             h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase)));

    public static string MissingKeyMessage(ProviderProfile profile)
    {
        var env = profile.ApiKeyEnvVar?.Trim();
        return string.IsNullOrEmpty(env)
            ? $"No API key found. Add an API key for {profile.Name} in Settings."
            : $"No API key found. Add an API key for {profile.Name} in Settings, or set the {env} environment variable.";
    }

    /// <summary>"Name (model)", or "Name (default model)" when the profile leaves the model empty.</summary>
    public static string DescribeProfile(ProviderProfile? profile)
    {
        if (profile == null) return "No model profile";
        var model = string.IsNullOrWhiteSpace(profile.Model) ? "default model" : profile.Model.Trim();
        return $"{profile.Name} ({model})";
    }

    /// <summary>The {{SCREEN}} text: what the screenshots show and how big they are.</summary>
    public static string DescribeScreen(CoordinateMapper mapper, IReadOnlyList<MonitorInfo> monitors, ScreenSettings screen)
    {
        monitors ??= Array.Empty<MonitorInfo>();
        var match = monitors.FirstOrDefault(m => m.Bounds == mapper.Source);
        string what;
        if (match != null)
            what = match.IsPrimary ? "the primary monitor" : $"monitor {match.Index + 1}";
        else if (screen.Monitor == MonitorSelection.AllMonitors)
            what = monitors.Count > 1 ? $"all {monitors.Count} monitors (the whole virtual desktop)" : "all monitors (the whole virtual desktop)";
        else
            what = DescribeSelection(screen);

        var text = $"screenshots are {mapper.ImageWidth}x{mapper.ImageHeight} pixels and show {what} ({mapper.Source.Width}x{mapper.Source.Height} physical pixels)";
        if (match != null && monitors.Count > 1) text += $"; the PC has {monitors.Count} monitors and only this one is captured";
        return text;
    }

    /// <summary>Used when the coordinate mapping cannot be computed (no monitors reported, tool host failure).</summary>
    public static string FallbackScreenDescription(ScreenSettings? screen)
    {
        screen ??= new ScreenSettings();
        return $"screenshots show {DescribeSelection(screen)}, scaled down to fit within {screen.MaxImageWidth}x{screen.MaxImageHeight} pixels";
    }

    private static string DescribeSelection(ScreenSettings screen) => screen.Monitor switch
    {
        MonitorSelection.AllMonitors => "all monitors (the whole virtual desktop)",
        MonitorSelection.Specific => $"monitor {screen.MonitorIndex + 1}",
        _ => "the primary monitor",
    };

    /// <summary>One line, at most max characters, for the log.</summary>
    public static string Clip(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ");
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "...";
    }
}

using System.Collections.ObjectModel;
using System.IO;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.ViewModels;

public enum WelcomeClaudeState { LoggedIn, NotLoggedIn, LoginUnknown, NotFound }

/// <summary>An entry of the first-run vault picker. Path "" = no vault.</summary>
public sealed record SettingsWelcomeVaultOption(string Label, string Path, string Detail)
{
    public bool IsSkip => Path.Length == 0;
}

/// <summary>State of the first-run welcome window, built from the environment report.</summary>
public sealed class SettingsWelcomeViewModel : SettingsBindableBase
{
    private SettingsWelcomeVaultOption? _selectedVault;

    public SettingsWelcomeViewModel(AppSettings current, EnvironmentReport report)
    {
        report = SettingsViewModel.Sanitize(report);
        Report = report;
        StopHotkey = string.IsNullOrWhiteSpace(current.Ui.StopHotkey) ? SettingsHotkey.DefaultHotkey : current.Ui.StopHotkey.Trim();
        FailsafeCorner = current.Safety.FailsafeCorner;
        AdminModeOn = current.Safety.AllowAdmin;

        var claude = report.ClaudeCli;
        ClaudeState = claude.Path == null ? WelcomeClaudeState.NotFound
            : claude.LoggedIn switch
            {
                true => WelcomeClaudeState.LoggedIn,
                false => WelcomeClaudeState.NotLoggedIn,
                _ => WelcomeClaudeState.LoginUnknown,
            };
        (ClaudeTitle, ClaudeDetail, ClaudeCommand) = DescribeClaude(ClaudeState, claude);

        OtherProviders = DescribeOtherProviders(report);

        SkipOption = new SettingsWelcomeVaultOption("No vault for now", "", "You can pick a notes folder later in Settings.");
        VaultOptions.Add(SkipOption);
        foreach (var v in report.ObsidianVaults ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(v) || VaultOptions.Any(o => string.Equals(o.Path, v, StringComparison.OrdinalIgnoreCase))) continue;
            VaultOptions.Add(new SettingsWelcomeVaultOption(Path.GetFileName(v.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : v, v, "Obsidian vault: " + v));
        }

        // A vault that is already configured stays selected; otherwise the vault is opt-in.
        var existing = current.Vault.Path?.Trim() ?? "";
        if (existing.Length > 0)
            _selectedVault = VaultOptions.FirstOrDefault(o => string.Equals(o.Path, existing, StringComparison.OrdinalIgnoreCase)) ?? AddCustomVault(existing);
        else
            _selectedVault = SkipOption;
    }

    public EnvironmentReport Report { get; }

    public WelcomeClaudeState ClaudeState { get; }

    public bool ClaudeReady => ClaudeState is WelcomeClaudeState.LoggedIn or WelcomeClaudeState.LoginUnknown;

    public string ClaudeTitle { get; }

    public string ClaudeDetail { get; }

    /// <summary>A command the user should run, or "" when nothing is needed.</summary>
    public string ClaudeCommand { get; }

    public IReadOnlyList<string> OtherProviders { get; }

    public bool HasOtherProviders => OtherProviders.Count > 0;

    public string StopHotkey { get; }

    public bool FailsafeCorner { get; }

    public bool AdminModeOn { get; }

    public string StopText => FailsafeCorner
        ? $"Press {StopHotkey} at any time to stop the agent, or slam the mouse into the top-left corner of your main screen."
        : $"Press {StopHotkey} at any time to stop the agent. The Stop button and the tray icon work too.";

    public string AdminText => AdminModeOn
        ? "Administrator mode is ON: the agent may operate elevated windows. You can turn it off in Settings, Safety."
        : "Administrator mode is off: the agent cannot touch elevated (administrator) windows or start programs as administrator. You can change this in Settings, Safety.";

    public SettingsWelcomeVaultOption SkipOption { get; }

    public ObservableCollection<SettingsWelcomeVaultOption> VaultOptions { get; } = new();

    public SettingsWelcomeVaultOption? SelectedVault
    {
        get => _selectedVault;
        set
        {
            if (value != null) Set(ref _selectedVault, value);
        }
    }

    /// <summary>Adds a folder picked with Browse and selects it.</summary>
    public SettingsWelcomeVaultOption AddCustomVault(string path)
    {
        var trimmed = path.Trim();
        var existing = VaultOptions.FirstOrDefault(o => string.Equals(o.Path, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            SelectedVault = existing;
            return existing;
        }
        var name = Path.GetFileName(trimmed.TrimEnd('\\', '/'));
        var option = new SettingsWelcomeVaultOption(name.Length > 0 ? name : trimmed, trimmed, "Folder: " + trimmed);
        VaultOptions.Add(option);
        SelectedVault = option;
        return option;
    }

    /// <summary>What "Get started" saves: the vault choice and FirstRunCompleted.</summary>
    public void Apply(AppSettings settings)
    {
        if (_selectedVault is { IsSkip: false } vault)
        {
            settings.Vault.Path = vault.Path;
            settings.Vault.Enabled = true;
        }
        settings.FirstRunCompleted = true;
    }

    private static (string Title, string Detail, string Command) DescribeClaude(WelcomeClaudeState state, CliToolStatus claude)
    {
        var version = string.IsNullOrWhiteSpace(claude.Version) ? "" : " " + claude.Version.Trim();
        return state switch
        {
            WelcomeClaudeState.LoggedIn => (
                $"Claude Code{version} is installed and logged in",
                "DeskPilot will use your Claude subscription" + (string.IsNullOrWhiteSpace(claude.Detail) ? "." : $" ({claude.Detail.Trim()}).") +
                " No API key is needed; usage counts against your Claude plan.",
                ""),
            WelcomeClaudeState.NotLoggedIn => (
                $"Claude Code{version} is installed but not logged in",
                "Open a terminal, run the command below and follow the browser login. Then come back and send your first request.",
                "claude auth login"),
            WelcomeClaudeState.LoginUnknown => (
                $"Claude Code{version} is installed",
                "DeskPilot could not check the login. If the first request fails, run the command below in a terminal.",
                "claude auth login"),
            _ => (
                "Claude Code was not found",
                "Install Claude Code (see claude.com/claude-code), then log in once with the command below. Or add another provider in Settings: an API key, Ollama, LM Studio and more.",
                "claude auth login"),
        };
    }

    private static IReadOnlyList<string> DescribeOtherProviders(EnvironmentReport report)
    {
        var lines = new List<string>();
        if (report.Ollama.Running)
        {
            var models = report.Ollama.Models ?? Array.Empty<ModelInfo>();
            var vision = models.Count(m => m.SupportsVision == true);
            lines.Add(models.Count == 0
                ? "Ollama is running (no models installed yet)."
                : $"Ollama is running with {models.Count} model{(models.Count == 1 ? "" : "s")}" + (vision > 0 ? $", {vision} with vision." : "."));
        }
        if (report.LmStudio.Running)
        {
            var count = (report.LmStudio.Models ?? Array.Empty<ModelInfo>()).Count;
            lines.Add($"LM Studio's server is running with {count} model{(count == 1 ? "" : "s")} loaded.");
        }
        if (report.GeminiCli.Path != null)
            lines.Add("Gemini CLI is installed (experimental, through the Agent Client Protocol).");
        if (report.CodexCli.Path != null)
            lines.Add("Codex CLI is installed (needs an Agent Client Protocol adapter).");
        var keys = report.ApiKeyEnvVarsFound ?? Array.Empty<string>();
        if (keys.Count > 0)
            lines.Add("API keys in your environment: " + string.Join(", ", keys) + ".");
        return lines;
    }
}

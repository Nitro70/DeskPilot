using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Input;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Vault;
using DeskPilot.Desktop.Linux;

namespace DeskPilot.Linux.ViewModels;

public sealed record SettingsSectionItem(string Key, string Title, string Description)
{
    public override string ToString() => Title;
}

public sealed record SettingsPresetGroup(string Name, IReadOnlyList<ProviderPreset> Presets);

public sealed record SettingsPlaceholderItem(string Name, string Description);

public sealed record SettingsMonitorOption(int Index, string Label)
{
    public override string ToString() => Label;
}

/// <summary>One row of the "Detected on this computer" panel.</summary>
public sealed class SettingsDetectedItem
{
    public SettingsDetectedItem(string title, string detail, bool isFound)
    {
        Title = title;
        Detail = detail;
        IsFound = isFound;
    }

    public string Title { get; }
    public string Detail { get; }
    public bool IsFound { get; }
    /// <summary>Preset used by the Add button; null = nothing to add.</summary>
    public string? PresetId { get; init; }
    public string? Model { get; init; }
    public string? BaseUrl { get; init; }
    public string? ProfileName { get; init; }
    public string AddTooltip { get; init; } = "";
    public bool CanAdd => PresetId != null;
    public SettingsCommand? AddCommand { get; set; }
}

/// <summary>
/// State and rules of the settings window. Works on a deep copy of the settings: plain values are
/// written through immediately, text fields (numbers, lists, JSON, patterns) are validated and
/// written by <see cref="TryCommit"/>. Nothing here touches the UI, the autostart file or the store,
/// so it is fully testable.
/// </summary>
public sealed class SettingsViewModel : SettingsBindableBase, IDisposable
{
    public const string SectionModels = "models";
    public const string SectionInstructions = "instructions";
    public const string SectionVault = "vault";
    public const string SectionSafety = "safety";
    public const string SectionScreen = "screen";
    public const string SectionInterface = "interface";
    public const string SectionAdvanced = "advanced";

    // Ranges match SettingsStore.Normalize where it clamps.
    public const int MaxStepsMin = 1, MaxStepsMax = 1000;
    public const int ImageWidthMin = 320, ImageWidthMax = 3840;
    public const int ImageHeightMin = 240, ImageHeightMax = 2160;
    public const int GridSpacingMin = 20, GridSpacingMax = 2000;
    public const int SettleDelayMax = 10_000;
    public const int ScreenshotsToKeepMin = 1, ScreenshotsToKeepMax = 50;
    public const int TypingDelayMax = 1000;
    public const int VaultResultsMin = 1, VaultResultsMax = 100;
    public const int VaultReadLinesMin = 10, VaultReadLinesMax = 20_000;

    private static readonly IReadOnlyList<SettingsSectionItem> AllSections = new[]
    {
        new SettingsSectionItem(SectionModels, "Models & providers", "Profiles, API keys, model choice"),
        new SettingsSectionItem(SectionInstructions, "Your instructions", "Standing instructions for every request"),
        new SettingsSectionItem(SectionVault, "Vault", "Optional notes folder"),
        new SettingsSectionItem(SectionSafety, "Safety", "Administrator mode, confirmations, limits"),
        new SettingsSectionItem(SectionScreen, "Screen", "Monitors, screenshots, coordinates"),
        new SettingsSectionItem(SectionInterface, "Interface", "Overlay, stop hotkey, startup"),
        new SettingsSectionItem(SectionAdvanced, "Advanced", "System prompt and raw provider options"),
    };

    private static readonly Dictionary<string, string> FieldLabels = new(StringComparer.Ordinal)
    {
        [nameof(VaultMaxResultsText)] = "Vault: max search results",
        [nameof(VaultMaxReadLinesText)] = "Vault: max lines per read",
        [nameof(VaultExtensionsText)] = "Vault: file extensions",
        [nameof(VaultExcludeText)] = "Vault: excluded folders",
        [nameof(MaxStepsText)] = "Safety: max actions per request",
        [nameof(BlockedProcessesText)] = "Safety: blocked apps",
        [nameof(ElevationPatternsText)] = "Safety: elevation text patterns",
        [nameof(ElevatedTargetsText)] = "Safety: elevated launch targets",
        [nameof(MaxImageWidthText)] = "Screen: max image width",
        [nameof(MaxImageHeightText)] = "Screen: max image height",
        [nameof(GridSpacingText)] = "Screen: grid spacing",
        [nameof(SettleDelayText)] = "Screen: settle delay",
        [nameof(ScreenshotsToKeepText)] = "Screen: screenshots kept",
        [nameof(TypingDelayText)] = "Screen: typing delay",
        [nameof(MonitorIndex)] = "Screen: monitor",
        [nameof(StopHotkey)] = "Interface: stop hotkey",
        [nameof(ProfileViewModel.Name)] = "Profile name",
        [nameof(ProfileViewModel.BaseUrl)] = "Base URL",
        [nameof(ProfileViewModel.MaxOutputTokensText)] = "Max output tokens",
        [nameof(ProfileViewModel.TemperatureText)] = "Temperature",
        [nameof(ProfileViewModel.RequestTimeoutText)] = "Request timeout",
        [nameof(ProfileViewModel.ThinkingBudgetText)] = "Thinking budget tokens",
        [nameof(ProfileViewModel.ExtraEnvText)] = "Extra environment",
        [nameof(ProfileViewModel.ExtraHeadersText)] = "Extra headers",
        [nameof(ProfileViewModel.ExtraBodyJson)] = "Extra body JSON",
    };

    private static readonly HashSet<string> AdvancedProfileFields = new(StringComparer.Ordinal)
    {
        nameof(ProfileViewModel.ThinkingBudgetText), nameof(ProfileViewModel.ExtraEnvText),
        nameof(ProfileViewModel.ExtraHeadersText), nameof(ProfileViewModel.ExtraBodyJson),
    };

    private readonly IModelCatalog? _catalog;
    private readonly IEnvironmentDetector? _detector;
    private readonly CancellationTokenSource _lifetime = new();

    private ProfileViewModel? _selectedProfile;
    private SettingsSectionItem? _selectedSection;
    private bool _started;
    private bool _isDetecting;
    private string _detectionStatus = "";
    private EnvironmentReport? _report;
    private string _errorSummary = "";
    private bool _isRunningAsRoot;
    private string _hotkeyHint = "";
    private string? _selectedDetectedVault;
    private string _vaultTestQuery = "";
    private string _vaultTestResult = "";
    private bool _isVaultTesting;
    private LinuxSessionInfo _session = new(LinuxSessionKind.None, null, null, null, null);

    private string _vaultMaxResultsText = "";
    private string _vaultMaxReadLinesText = "";
    private string _vaultExtensionsText = "";
    private string _vaultExcludeText = "";
    private string _maxStepsText = "";
    private string _blockedProcessesText = "";
    private string _elevationPatternsText = "";
    private string _elevatedTargetsText = "";
    private string _maxImageWidthText = "";
    private string _maxImageHeightText = "";
    private string _gridSpacingText = "";
    private string _settleDelayText = "";
    private string _screenshotsToKeepText = "";
    private string _typingDelayText = "";

    public SettingsViewModel(AppSettings settings, IModelCatalog? catalog = null, IEnvironmentDetector? detector = null)
    {
        SettingsStore.Normalize(settings);
        Settings = settings;
        _catalog = catalog;
        _detector = detector;
        _isRunningAsRoot = Environment.IsPrivilegedProcess;

        foreach (var p in settings.Profiles) Profiles.Add(CreateProfileViewModel(p));
        _selectedProfile = Profiles.FirstOrDefault(p => p.Id == settings.ActiveProfileId) ?? Profiles.FirstOrDefault();
        UpdateActiveFlags();

        LoadTextFields();
        RebuildMonitorOptions();
        UpdateVisibleSections();
        _selectedSection = VisibleSections[0];

        Placeholders = PromptBuilder.Placeholders.Select(p => new SettingsPlaceholderItem(p.Name, p.Description)).ToList();

        AddPresetCommand = new SettingsCommand(p => { if (p is string id) AddPreset(id); }, p => p is string id && ProviderPresets.Find(id) != null);
        DuplicateCommand = new SettingsCommand(() => DuplicateSelected(), () => SelectedProfile != null);
        DeleteCommand = new SettingsCommand(() => DeleteSelected(), () => SelectedProfile != null && Profiles.Count > 1);
        SetActiveCommand = new SettingsCommand(SetSelectedActive, () => SelectedProfile is { IsActive: false });
        ClearVaultCommand = new SettingsCommand(() => VaultPath = "");
        VaultTestCommand = new SettingsAsyncCommand(() => RunVaultTestAsync(_lifetime.Token));
        LoadBuiltInPromptCommand = new SettingsCommand(() => CustomSystemPrompt = DefaultPrompts.ComputerUse);
        ResetPromptCommand = new SettingsCommand(() => CustomSystemPrompt = "");
        ResetHotkeyCommand = new SettingsCommand(() => { StopHotkey = SettingsHotkey.DefaultHotkey; HotkeyHint = ""; });
        RedetectCommand = new SettingsAsyncCommand(DetectEnvironmentAsync, () => _detector != null && !IsDetecting);
    }

    /// <summary>The edited deep copy. Save it with SettingsStore.Save after <see cref="TryCommit"/> succeeds.</summary>
    public AppSettings Settings { get; }

    public SettingsErrorBag Errors { get; } = new();

    /// <summary>Builds the vault tool host used by "Test search". Tests replace it with a fake.</summary>
    public Func<Func<AppSettings>, IToolHost> VaultHostFactory { get; set; } = s => new VaultToolHost(s);

    public string SettingsFilePath { get; set; } = "";

    public string LogFolderPath { get; set; } = "";

    // ---- Session (X11 or Wayland) ----

    /// <summary>The graphical session DeskPilot runs in; decides which notes about the stop hotkey are shown.</summary>
    public LinuxSessionInfo Session
    {
        get => _session;
        set
        {
            if (value == null || !Set(ref _session, value)) return;
            Raise(nameof(IsWayland), nameof(SessionText), nameof(HotkeySessionNote));
        }
    }

    public bool IsWayland => _session.Kind == LinuxSessionKind.Wayland;

    public string SessionText => DescribeSession(_session);

    /// <summary>"Wayland session (sway)", "X11 session (GNOME)" or "No graphical session detected".</summary>
    public static string DescribeSession(LinuxSessionInfo session)
    {
        var desktop = string.IsNullOrWhiteSpace(session.Desktop) ? "" : $" ({session.Desktop.Replace(":", ", ")})";
        return session.Kind switch
        {
            LinuxSessionKind.X11 => "X11 session" + desktop,
            LinuxSessionKind.Wayland => "Wayland session" + desktop,
            _ => "No graphical session detected" + desktop,
        };
    }

    /// <summary>Under Wayland no app may grab a global key, so the stop hotkey only works in X11 sessions.</summary>
    public string HotkeySessionNote => !IsWayland ? ""
        : "This is a Wayland session: Wayland does not let apps register global hotkeys, so the stop hotkey is not available. Stop the agent with the Stop button on the overlay " +
          (FailsafeCorner ? "or the tray icon, or by pushing the mouse into the top-left corner of the screen." : "or the tray icon (or turn on the failsafe corner under Safety).");

    // ---- Navigation ----

    public ObservableCollection<SettingsSectionItem> VisibleSections { get; } = new();

    public SettingsSectionItem? SelectedSection
    {
        get => _selectedSection;
        set
        {
            if (value == null) return;
            if (Set(ref _selectedSection, value)) Raise(nameof(SelectedSectionKey));
        }
    }

    public string SelectedSectionKey => _selectedSection?.Key ?? SectionModels;

    public void SelectSection(string key)
    {
        var item = VisibleSections.FirstOrDefault(s => s.Key == key);
        if (item != null) SelectedSection = item;
    }

    public string ErrorSummary
    {
        get => _errorSummary;
        private set => Set(ref _errorSummary, value);
    }

    /// <summary>Shows a problem that is not tied to one field (for example the settings file could not be written).</summary>
    public void ReportError(string message) => ErrorSummary = message ?? "";

    // ---- Profiles ----

    public ObservableCollection<ProfileViewModel> Profiles { get; } = new();

    public ProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (value == null || !Set(ref _selectedProfile, value)) return;
            RaiseProfileCommands();
            if (_started && !value.ModelsLoadedOnce) _ = value.RefreshModelsAsync(_lifetime.Token);
        }
    }

    public static IReadOnlyList<SettingsPresetGroup> PresetGroups { get; } = new[]
    {
        new SettingsPresetGroup("Cloud", ProviderPresets.All.Where(p => !p.IsLocal && p.Kind is ProviderKind.AnthropicApi or ProviderKind.OpenAiCompatible or ProviderKind.Ollama).ToList()),
        new SettingsPresetGroup("Local", ProviderPresets.All.Where(p => p.IsLocal).ToList()),
        new SettingsPresetGroup("Agents", ProviderPresets.All.Where(p => !p.IsLocal && p.Kind is ProviderKind.ClaudeCli or ProviderKind.AcpAgent).ToList()),
    };

    public SettingsCommand AddPresetCommand { get; }
    public SettingsCommand DuplicateCommand { get; }
    public SettingsCommand DeleteCommand { get; }
    public SettingsCommand SetActiveCommand { get; }

    public ProfileViewModel AddPreset(string presetId, string? model = null, string? baseUrl = null, string? name = null)
    {
        var profile = ProviderPresets.CreateProfile(presetId);
        if (!string.IsNullOrWhiteSpace(model)) profile.Model = model.Trim();
        if (!string.IsNullOrWhiteSpace(baseUrl)) profile.BaseUrl = baseUrl.Trim();
        profile.Name = UniqueProfileName(string.IsNullOrWhiteSpace(name) ? profile.Name : name.Trim());
        return AddProfile(profile);
    }

    public ProfileViewModel? DuplicateSelected()
    {
        if (SelectedProfile is not { } source) return null;
        var copy = source.BuildEffectiveProfile();
        copy.Id = Guid.NewGuid().ToString("N");
        copy.Name = UniqueProfileName(source.Name.Trim() + " (copy)");
        return AddProfile(copy);
    }

    public bool DeleteSelected()
    {
        if (SelectedProfile is not { } victim || Profiles.Count <= 1) return false;
        var index = Profiles.IndexOf(victim);
        // Select the neighbour first, so the editor never shows a profile that is no longer in the list.
        SelectedProfile = Profiles[index + 1 < Profiles.Count ? index + 1 : index - 1];
        Profiles.Remove(victim);
        Settings.Profiles.RemoveAll(p => p.Id == victim.Id);
        if (Settings.ActiveProfileId == victim.Id) Settings.ActiveProfileId = Profiles[0].Id;
        UpdateActiveFlags();
        RaiseProfileCommands();
        return true;
    }

    public void SetSelectedActive()
    {
        if (SelectedProfile is not { } p) return;
        Settings.ActiveProfileId = p.Id;
        UpdateActiveFlags();
        RaiseProfileCommands();
    }

    public ProfileViewModel? ActiveProfile => Profiles.FirstOrDefault(p => p.IsActive);

    // ---- Detection ----

    public bool IsDetecting
    {
        get => _isDetecting;
        private set
        {
            if (Set(ref _isDetecting, value)) RedetectCommand.RaiseCanExecuteChanged();
        }
    }

    public string DetectionStatus
    {
        get => _detectionStatus;
        private set => Set(ref _detectionStatus, value);
    }

    public EnvironmentReport? Report => _report;

    public ObservableCollection<SettingsDetectedItem> DetectedItems { get; } = new();

    public SettingsAsyncCommand RedetectCommand { get; }

    /// <summary>Called when the window opens: detects the environment and lists models for the selected profile.</summary>
    public Task StartAsync()
    {
        _started = true;
        var tasks = new List<Task> { DetectEnvironmentAsync() };
        if (SelectedProfile is { ModelsLoadedOnce: false } p) tasks.Add(p.RefreshModelsAsync(_lifetime.Token));
        return Task.WhenAll(tasks);
    }

    public async Task DetectEnvironmentAsync()
    {
        if (_detector == null || IsDetecting) return;
        IsDetecting = true;
        DetectionStatus = "Looking for Claude Code, other agents, local model servers and API keys...";
        try
        {
            var detector = _detector;
            var token = _lifetime.Token;
            // Task.Run so a detector that does slow synchronous work first can never freeze the window.
            var report = await Task.Run(() => detector.DetectAsync(token), token);
            ApplyReport(report);
            DetectionStatus = "";
        }
        catch (OperationCanceledException)
        {
            DetectionStatus = "";
        }
        catch (Exception ex)
        {
            DetectionStatus = "Detection failed: " + ex.Message;
        }
        finally
        {
            IsDetecting = false;
        }
    }

    public void ApplyReport(EnvironmentReport report)
    {
        report = Sanitize(report);
        _report = report;
        Raise(nameof(Report));

        DetectedItems.Clear();
        foreach (var item in BuildDetectedItems(report))
        {
            var captured = item;
            item.AddCommand = new SettingsCommand(() => AddDetected(captured), () => captured.CanAdd);
            DetectedItems.Add(item);
        }

        var keep = _selectedDetectedVault;
        DetectedVaults.Clear();
        foreach (var v in report.ObsidianVaults ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(v) && !DetectedVaults.Contains(v, StringComparer.Ordinal)) DetectedVaults.Add(v);
        _selectedDetectedVault = keep != null && DetectedVaults.Contains(keep) ? keep : null;
        Raise(nameof(HasDetectedVaults), nameof(SelectedDetectedVault));

        IsRunningAsRoot = report.IsElevated;
        RebuildMonitorOptions();
        foreach (var p in Profiles) p.DetectedCliPath = DetectedCliPathFor(p.Profile);
    }

    public ProfileViewModel? AddDetected(SettingsDetectedItem item)
    {
        if (item.PresetId == null) return null;
        return AddPreset(item.PresetId, item.Model, item.BaseUrl, item.ProfileName);
    }

    /// <summary>Rows for the "Detected on this computer" panel, each with what its Add button creates.</summary>
    public static IReadOnlyList<SettingsDetectedItem> BuildDetectedItems(EnvironmentReport report)
    {
        report = Sanitize(report);
        var items = new List<SettingsDetectedItem>();

        var claude = report.ClaudeCli;
        items.Add(new SettingsDetectedItem("Claude Code", DescribeCli(claude, "claude", "Not found. Install Claude Code, then run: claude auth login"), claude.Path != null)
        {
            PresetId = claude.Path != null ? ProviderPresets.ClaudeSubscriptionId : null,
            AddTooltip = ProviderPresets.Find(ProviderPresets.ClaudeSubscriptionId)?.Notes ?? "",
        });

        var gemini = report.GeminiCli;
        items.Add(new SettingsDetectedItem("Gemini CLI", DescribeCli(gemini, "gemini", "Not found."), gemini.Path != null)
        {
            PresetId = gemini.Path != null ? ProviderPresets.GeminiCliId : null,
            AddTooltip = ProviderPresets.Find(ProviderPresets.GeminiCliId)?.Notes ?? "",
        });

        var codex = report.CodexCli;
        items.Add(new SettingsDetectedItem("Codex CLI", DescribeCli(codex, "codex", "Not found."), codex.Path != null)
        {
            PresetId = codex.Path != null ? ProviderPresets.CustomAcpId : null,
            ProfileName = "Codex CLI (ACP adapter)",
            AddTooltip = "Adds an 'Other ACP agent' profile. Codex needs an Agent Client Protocol adapter: set the adapter's path under CLI path after adding.",
        });

        items.Add(DescribeServer(report.Ollama, ProviderPresets.OllamaId));
        items.Add(DescribeServer(report.LmStudio, ProviderPresets.LmStudioId));

        foreach (var name in report.ApiKeyEnvVarsFound ?? Array.Empty<string>())
        {
            var preset = ProviderPresets.All.FirstOrDefault(p => p.NeedsApiKey && string.Equals(p.ApiKeyEnvVar, name, StringComparison.OrdinalIgnoreCase));
            items.Add(new SettingsDetectedItem(name, preset != null ? $"API key found in the environment ({preset.DisplayName})." : "API key found in the environment.", true)
            {
                PresetId = preset?.Id,
                AddTooltip = preset != null ? $"Adds a {preset.DisplayName} profile that reads the key from {name}. {preset.Notes}" : "",
            });
        }
        return items;
    }

    /// <summary>Prefers a vision model, then the preset's default, then the first model.</summary>
    public static string? PickModel(IReadOnlyList<ModelInfo> models, string presetDefault)
    {
        if (models.Count == 0) return string.IsNullOrEmpty(presetDefault) ? null : presetDefault;
        var vision = models.FirstOrDefault(m => m.SupportsVision == true && m.SupportsTools != false)
                     ?? models.FirstOrDefault(m => m.SupportsVision == true);
        if (vision != null) return vision.Id;
        var preferred = models.FirstOrDefault(m => string.Equals(m.Id, presetDefault, StringComparison.OrdinalIgnoreCase));
        return (preferred ?? models[0]).Id;
    }

    // ---- Your instructions ----

    public string UserInstructions
    {
        get => Settings.Prompt.UserInstructions;
        set => Through(Settings.Prompt.UserInstructions, value ?? "", v => Settings.Prompt.UserInstructions = v);
    }

    // ---- Vault ----

    public string VaultPath
    {
        get => Settings.Vault.Path;
        set
        {
            if (Through(Settings.Vault.Path, value ?? "", v => Settings.Vault.Path = v)) Raise(nameof(VaultPathStatus), nameof(HasVaultPath));
        }
    }

    public bool HasVaultPath => !string.IsNullOrWhiteSpace(VaultPath);

    public string VaultPathStatus
    {
        get
        {
            var path = SettingsParsing.ExpandHome(VaultPath);
            if (path.Length == 0) return "No vault: the agent will not search any notes.";
            if (!Path.IsPathFullyQualified(path)) return "Use a full folder path, for example one picked with Browse.";
            return Directory.Exists(path) ? "" : "This folder does not exist (yet).";
        }
    }

    public ObservableCollection<string> DetectedVaults { get; } = new();

    public bool HasDetectedVaults => DetectedVaults.Count > 0;

    /// <summary>Picking a detected Obsidian vault fills the path.</summary>
    public string? SelectedDetectedVault
    {
        get => _selectedDetectedVault;
        set
        {
            if (!Set(ref _selectedDetectedVault, value)) return;
            if (!string.IsNullOrWhiteSpace(value)) VaultPath = value;
        }
    }

    public bool VaultEnabled
    {
        get => Settings.Vault.Enabled;
        set => Through(Settings.Vault.Enabled, value, v => Settings.Vault.Enabled = v);
    }

    public bool VaultAllowWrites
    {
        get => Settings.Vault.AllowWrites;
        set => Through(Settings.Vault.AllowWrites, value, v => Settings.Vault.AllowWrites = v);
    }

    public string VaultMaxResultsText { get => _vaultMaxResultsText; set => Set(ref _vaultMaxResultsText, value ?? ""); }
    public string VaultMaxReadLinesText { get => _vaultMaxReadLinesText; set => Set(ref _vaultMaxReadLinesText, value ?? ""); }
    public string VaultExtensionsText { get => _vaultExtensionsText; set => Set(ref _vaultExtensionsText, value ?? ""); }
    public string VaultExcludeText { get => _vaultExcludeText; set => Set(ref _vaultExcludeText, value ?? ""); }

    public SettingsCommand ClearVaultCommand { get; }

    public string VaultTestQuery { get => _vaultTestQuery; set => Set(ref _vaultTestQuery, value ?? ""); }

    public string VaultTestResult { get => _vaultTestResult; private set => Set(ref _vaultTestResult, value); }

    public bool IsVaultTesting { get => _isVaultTesting; private set => Set(ref _isVaultTesting, value); }

    public SettingsAsyncCommand VaultTestCommand { get; }

    /// <summary>Runs vault_search against the edited (unsaved) vault settings.</summary>
    public async Task RunVaultTestAsync(CancellationToken ct)
    {
        var query = VaultTestQuery.Trim();
        if (query.Length == 0)
        {
            VaultTestResult = "Type a few words to search for.";
            return;
        }
        var settings = BuildEffectiveSettings();
        settings.Vault.Enabled = true; // test even when the vault is switched off for the agent
        var path = settings.Vault.Path.Trim();
        if (path.Length == 0)
        {
            VaultTestResult = "Choose a vault folder first.";
            return;
        }
        if (!Directory.Exists(path))
        {
            VaultTestResult = "The vault folder does not exist.";
            return;
        }

        IsVaultTesting = true;
        VaultTestResult = "Searching...";
        try
        {
            var args = JsonSerializer.SerializeToElement(new Dictionary<string, object>
            {
                ["query"] = query,
                ["max_results"] = settings.Vault.MaxSearchResults,
            });
            var factory = VaultHostFactory;
            var result = await Task.Run(() => factory(() => settings).ExecuteAsync("vault_search", args, ct), ct);
            VaultTestResult = result.IsError ? "Error: " + result.Text : (result.Text.Length == 0 ? "No results." : result.Text);
        }
        catch (OperationCanceledException)
        {
            VaultTestResult = "";
        }
        catch (Exception ex)
        {
            VaultTestResult = "Search failed: " + ex.Message;
        }
        finally
        {
            IsVaultTesting = false;
        }
    }

    // ---- Safety ----

    public bool AllowAdmin
    {
        get => Settings.Safety.AllowAdmin;
        set => Through(Settings.Safety.AllowAdmin, value, v => Settings.Safety.AllowAdmin = v);
    }

    /// <summary>DeskPilot itself runs as root (effective user id 0), which is not supported.</summary>
    public bool IsRunningAsRoot
    {
        get => _isRunningAsRoot;
        set => Set(ref _isRunningAsRoot, value);
    }

    public ConfirmMode Confirm
    {
        get => Settings.Safety.Confirm;
        set => Through(Settings.Safety.Confirm, value, v => Settings.Safety.Confirm = v);
    }

    public bool AllowAppLaunch
    {
        get => Settings.Safety.AllowAppLaunch;
        set => Through(Settings.Safety.AllowAppLaunch, value, v => Settings.Safety.AllowAppLaunch = v);
    }

    public bool AllowShellCommands
    {
        get => Settings.Safety.AllowShellCommands;
        set => Through(Settings.Safety.AllowShellCommands, value, v => Settings.Safety.AllowShellCommands = v);
    }

    public bool AllowClipboard
    {
        get => Settings.Safety.AllowClipboard;
        set => Through(Settings.Safety.AllowClipboard, value, v => Settings.Safety.AllowClipboard = v);
    }

    public bool DryRun
    {
        get => Settings.Safety.DryRun;
        set => Through(Settings.Safety.DryRun, value, v => Settings.Safety.DryRun = v);
    }

    public bool FailsafeCorner
    {
        get => Settings.Safety.FailsafeCorner;
        set
        {
            if (Through(Settings.Safety.FailsafeCorner, value, v => Settings.Safety.FailsafeCorner = v)) Raise(nameof(HotkeySessionNote));
        }
    }

    public bool StopOnUserMouseMove
    {
        get => Settings.Safety.StopOnUserMouseMove;
        set => Through(Settings.Safety.StopOnUserMouseMove, value, v => Settings.Safety.StopOnUserMouseMove = v);
    }

    public string MaxStepsText { get => _maxStepsText; set => Set(ref _maxStepsText, value ?? ""); }
    public string BlockedProcessesText { get => _blockedProcessesText; set => Set(ref _blockedProcessesText, value ?? ""); }
    public string ElevationPatternsText { get => _elevationPatternsText; set => Set(ref _elevationPatternsText, value ?? ""); }
    public string ElevatedTargetsText { get => _elevatedTargetsText; set => Set(ref _elevatedTargetsText, value ?? ""); }

    // ---- Screen ----

    public MonitorSelection MonitorSelection
    {
        get => Settings.Screen.Monitor;
        set
        {
            if (Through(Settings.Screen.Monitor, value, v => Settings.Screen.Monitor = v)) Raise(nameof(IsSpecificMonitor));
        }
    }

    public bool IsSpecificMonitor => MonitorSelection == MonitorSelection.Specific;

    public int MonitorIndex
    {
        get => Settings.Screen.MonitorIndex;
        set
        {
            if (!Through(Settings.Screen.MonitorIndex, Math.Max(0, value), v => Settings.Screen.MonitorIndex = v)) return;
            if (MonitorOptions.All(o => o.Index != Settings.Screen.MonitorIndex)) RebuildMonitorOptions();
            else Raise(nameof(SelectedMonitorOption));
        }
    }

    public ObservableCollection<SettingsMonitorOption> MonitorOptions { get; } = new();

    /// <summary>The monitor picker's item. A null from the picker (its list being rebuilt) is ignored.</summary>
    public SettingsMonitorOption? SelectedMonitorOption
    {
        get => MonitorOptions.FirstOrDefault(o => o.Index == MonitorIndex);
        set
        {
            if (value != null) MonitorIndex = value.Index;
        }
    }

    public string MaxImageWidthText { get => _maxImageWidthText; set => Set(ref _maxImageWidthText, value ?? ""); }
    public string MaxImageHeightText { get => _maxImageHeightText; set => Set(ref _maxImageHeightText, value ?? ""); }

    /// <summary>"jpeg" or "png".</summary>
    public string ImageFormat
    {
        get => string.Equals(Settings.Screen.Format, "png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
        set
        {
            var v = string.Equals(value, "png", StringComparison.OrdinalIgnoreCase) ? "png" : "jpeg";
            if (Through(Settings.Screen.Format, v, x => Settings.Screen.Format = x)) Raise(nameof(IsJpeg));
        }
    }

    public bool IsJpeg => ImageFormat == "jpeg";

    public int JpegQuality
    {
        get => Settings.Screen.JpegQuality;
        set => Through(Settings.Screen.JpegQuality, Math.Clamp(value, 20, 100), v => Settings.Screen.JpegQuality = v);
    }

    /// <summary>The slider works in doubles; this keeps the stored quality a whole number from 20 to 100.</summary>
    public double JpegQualitySlider
    {
        get => JpegQuality;
        set
        {
            var before = JpegQuality;
            JpegQuality = (int)Math.Round(double.IsNaN(value) ? before : value);
            if (JpegQuality != before) Raise(nameof(JpegQualitySlider));
        }
    }

    public bool DrawCursor
    {
        get => Settings.Screen.DrawCursor;
        set => Through(Settings.Screen.DrawCursor, value, v => Settings.Screen.DrawCursor = v);
    }

    public string GridSpacingText { get => _gridSpacingText; set => Set(ref _gridSpacingText, value ?? ""); }

    public CoordinateMode Coordinates
    {
        get => Settings.Screen.Coordinates;
        set => Through(Settings.Screen.Coordinates, value, v => Settings.Screen.Coordinates = v);
    }

    public bool ScreenshotAfterAction
    {
        get => Settings.Screen.ScreenshotAfterAction;
        set => Through(Settings.Screen.ScreenshotAfterAction, value, v => Settings.Screen.ScreenshotAfterAction = v);
    }

    public string SettleDelayText { get => _settleDelayText; set => Set(ref _settleDelayText, value ?? ""); }
    public string ScreenshotsToKeepText { get => _screenshotsToKeepText; set => Set(ref _screenshotsToKeepText, value ?? ""); }
    public string TypingDelayText { get => _typingDelayText; set => Set(ref _typingDelayText, value ?? ""); }

    // ---- Interface ----

    public bool AdvancedMode
    {
        get => Settings.Ui.AdvancedMode;
        set
        {
            if (Through(Settings.Ui.AdvancedMode, value, v => Settings.Ui.AdvancedMode = v)) UpdateVisibleSections();
        }
    }

    public bool MinimizeWhileWorking
    {
        get => Settings.Ui.MinimizeWhileWorking;
        set => Through(Settings.Ui.MinimizeWhileWorking, value, v => Settings.Ui.MinimizeWhileWorking = v);
    }

    public bool ShowOverlay
    {
        get => Settings.Ui.ShowOverlay;
        set => Through(Settings.Ui.ShowOverlay, value, v => Settings.Ui.ShowOverlay = v);
    }

    public static IReadOnlyList<SettingsOption<OverlayCorner>> OverlayPositions { get; } = new[]
    {
        new SettingsOption<OverlayCorner>(OverlayCorner.TopLeft, "Top left"),
        new SettingsOption<OverlayCorner>(OverlayCorner.TopCenter, "Top center"),
        new SettingsOption<OverlayCorner>(OverlayCorner.TopRight, "Top right"),
        new SettingsOption<OverlayCorner>(OverlayCorner.BottomLeft, "Bottom left"),
        new SettingsOption<OverlayCorner>(OverlayCorner.BottomCenter, "Bottom center"),
        new SettingsOption<OverlayCorner>(OverlayCorner.BottomRight, "Bottom right"),
    };

    /// <summary>Instance access to <see cref="OverlayPositions"/> for compiled bindings.</summary>
    public IReadOnlyList<SettingsOption<OverlayCorner>> OverlayPositionOptions => OverlayPositions;

    public OverlayCorner OverlayPosition
    {
        get => Settings.Ui.OverlayPosition;
        set
        {
            if (Through(Settings.Ui.OverlayPosition, value, v => Settings.Ui.OverlayPosition = v)) Raise(nameof(SelectedOverlayPosition));
        }
    }

    /// <summary>The overlay-position picker's item. A null from the picker is ignored.</summary>
    public SettingsOption<OverlayCorner>? SelectedOverlayPosition
    {
        get => OverlayPositions.FirstOrDefault(o => o.Value == OverlayPosition);
        set
        {
            if (value != null) OverlayPosition = value.Value;
        }
    }

    public string StopHotkey
    {
        get => Settings.Ui.StopHotkey;
        set => Through(Settings.Ui.StopHotkey, value ?? "", v => Settings.Ui.StopHotkey = v);
    }

    /// <summary>Feedback while capturing a hotkey ("Ctrl+Alt+... now press the main key").</summary>
    public string HotkeyHint
    {
        get => _hotkeyHint;
        private set => Set(ref _hotkeyHint, value);
    }

    public SettingsCommand ResetHotkeyCommand { get; }

    /// <summary>
    /// Handles a key press in the hotkey box. Backspace or Delete alone restores the default.
    /// Returns true when the hotkey changed.
    /// </summary>
    public bool CaptureHotkey(KeyModifiers modifiers, Key key)
    {
        if (modifiers == KeyModifiers.None && key is Key.Back or Key.Delete)
        {
            StopHotkey = SettingsHotkey.DefaultHotkey;
            HotkeyHint = "Restored the default.";
            Errors.Remove(nameof(StopHotkey));
            return true;
        }
        if (SettingsHotkey.IsModifierOnly(key))
        {
            var held = SettingsHotkey.FormatModifiers(modifiers);
            HotkeyHint = held.Length > 0 ? held + "... now press the main key." : "";
            return false;
        }
        var text = SettingsHotkey.Format(modifiers, key);
        if (text == null)
        {
            HotkeyHint = "That key cannot be used. Use a letter, a digit, a function key or a key such as Pause or Insert.";
            return false;
        }
        if (!SettingsHotkey.TryValidate(text, out var error))
        {
            HotkeyHint = $"{text}: {error}";
            return false;
        }
        StopHotkey = text;
        HotkeyHint = "";
        Errors.Remove(nameof(StopHotkey));
        return true;
    }

    public bool CloseToTray
    {
        get => Settings.Ui.CloseToTray;
        set => Through(Settings.Ui.CloseToTray, value, v => Settings.Ui.CloseToTray = v);
    }

    public bool ShowThinking
    {
        get => Settings.Ui.ShowThinking;
        set => Through(Settings.Ui.ShowThinking, value, v => Settings.Ui.ShowThinking = v);
    }

    public bool ShowScreenshots
    {
        get => Settings.Ui.ShowScreenshots;
        set => Through(Settings.Ui.ShowScreenshots, value, v => Settings.Ui.ShowScreenshots = v);
    }

    /// <summary>"Start when I log in": stored in UiSettings.StartWithWindows, applied as an XDG autostart entry on save.</summary>
    public bool StartAtLogin
    {
        get => Settings.Ui.StartWithWindows;
        set => Through(Settings.Ui.StartWithWindows, value, v => Settings.Ui.StartWithWindows = v);
    }

    /// <summary>Shows the first-run welcome window again on the next start.</summary>
    public bool ShowWelcomeAgain
    {
        get => !Settings.FirstRunCompleted;
        set => Through(!Settings.FirstRunCompleted, value, v => Settings.FirstRunCompleted = !v);
    }

    // ---- Advanced: system prompt ----

    public string CustomSystemPrompt
    {
        get => Settings.Prompt.CustomSystemPrompt;
        set
        {
            if (Through(Settings.Prompt.CustomSystemPrompt, value ?? "", v => Settings.Prompt.CustomSystemPrompt = v))
                Raise(nameof(PromptStatus));
        }
    }

    public string PromptStatus => string.IsNullOrWhiteSpace(CustomSystemPrompt)
        ? "Empty: DeskPilot uses its built-in prompt."
        : $"Custom prompt, {CustomSystemPrompt.Length.ToString("N0", CultureInfo.InvariantCulture)} characters. Clear it to go back to the built-in prompt.";

    public IReadOnlyList<SettingsPlaceholderItem> Placeholders { get; }

    public SettingsCommand LoadBuiltInPromptCommand { get; }
    public SettingsCommand ResetPromptCommand { get; }

    /// <summary>Inserts a placeholder at the caret of the prompt editor; returns the new caret position.</summary>
    public int InsertIntoPrompt(string text, int selectionStart, int selectionLength)
    {
        CustomSystemPrompt = SettingsParsing.InsertAt(CustomSystemPrompt, text, selectionStart, selectionLength, out var caret);
        return caret;
    }

    /// <summary>The prompt the active profile would get with the edited settings (no tools listed).</summary>
    public string BuildPreviewPrompt(DateTime now)
    {
        var settings = BuildEffectiveSettings();
        var profile = settings.ActiveProfile ?? ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
        var vaultPath = settings.Vault.Path.Trim();
        var vaultAvailable = settings.Vault.Enabled && vaultPath.Length > 0 && Path.IsPathFullyQualified(vaultPath) && Directory.Exists(vaultPath);
        var context = new PromptContext(settings, profile, Array.Empty<ToolSpec>(), DescribeSampleScreen(settings, _report?.Monitors), vaultAvailable, now);
        return PromptBuilder.Build(context);
    }

    /// <summary>A screen description like the one the session builds, from the detected monitors (or a 1920x1080 sample).</summary>
    public static string DescribeSampleScreen(AppSettings settings, IReadOnlyList<MonitorInfo>? monitors)
    {
        var list = monitors ?? Array.Empty<MonitorInfo>();
        ScreenRect bounds;
        string which;
        switch (settings.Screen.Monitor)
        {
            case MonitorSelection.AllMonitors when list.Count > 0:
                var x1 = list.Min(m => m.Bounds.X);
                var y1 = list.Min(m => m.Bounds.Y);
                var x2 = list.Max(m => m.Bounds.Right);
                var y2 = list.Max(m => m.Bounds.Bottom);
                bounds = new ScreenRect(x1, y1, x2 - x1, y2 - y1);
                which = "all monitors";
                break;
            case MonitorSelection.Specific when settings.Screen.MonitorIndex < list.Count:
                bounds = list[settings.Screen.MonitorIndex].Bounds;
                which = $"monitor {settings.Screen.MonitorIndex + 1}";
                break;
            default:
                var primary = list.FirstOrDefault(m => m.IsPrimary) ?? list.FirstOrDefault();
                bounds = primary?.Bounds ?? new ScreenRect(0, 0, 1920, 1080);
                which = settings.Screen.Monitor == MonitorSelection.AllMonitors ? "all monitors" : "the primary monitor";
                break;
        }
        var (w, h) = FitInside(bounds.Width, bounds.Height, settings.Screen.MaxImageWidth, settings.Screen.MaxImageHeight);
        return $"screenshots are {w}x{h} and show {which} ({bounds.Width}x{bounds.Height} physical pixels)";
    }

    // ---- Commit ----

    /// <summary>
    /// Validates every field and writes the text fields into <see cref="Settings"/>. On failure the
    /// errors are shown inline, <see cref="ErrorSummary"/> names the first one and the page with it is
    /// selected.
    /// </summary>
    public bool TryCommit()
    {
        Errors.Clear();
        var ok = ApplyTo(Settings, Errors);
        foreach (var p in Profiles)
            ok &= p.Commit();

        if (ok)
        {
            ErrorSummary = "";
            SettingsStore.Normalize(Settings);
            return true;
        }
        ErrorSummary = BuildErrorSummaryAndNavigate();
        return false;
    }

    /// <summary>A copy of the settings with every valid pending edit applied (nothing is validated or stored).</summary>
    public AppSettings BuildEffectiveSettings()
    {
        var copy = SettingsStore.Clone(Settings);
        ApplyTo(copy, null);
        foreach (var p in Profiles)
        {
            var target = copy.Profiles.FirstOrDefault(x => x.Id == p.Id);
            if (target != null) p.ApplyTo(target, null);
        }
        return copy;
    }

    /// <summary>
    /// A report with every missing part replaced by "not found", so a detector that returns partial data
    /// can never break the settings or welcome window.
    /// </summary>
    public static EnvironmentReport Sanitize(EnvironmentReport report) => report with
    {
        ClaudeCli = report.ClaudeCli ?? new CliToolStatus("claude", null, null, null, null),
        GeminiCli = report.GeminiCli ?? new CliToolStatus("gemini", null, null, null, null),
        CodexCli = report.CodexCli ?? new CliToolStatus("codex", null, null, null, null),
        Ollama = report.Ollama is { } o ? o with { Models = o.Models ?? Array.Empty<ModelInfo>(), BaseUrl = o.BaseUrl ?? "" }
            : new LocalServerStatus("Ollama", "", false, Array.Empty<ModelInfo>()),
        LmStudio = report.LmStudio is { } l ? l with { Models = l.Models ?? Array.Empty<ModelInfo>(), BaseUrl = l.BaseUrl ?? "" }
            : new LocalServerStatus("LM Studio", "", false, Array.Empty<ModelInfo>()),
        ApiKeyEnvVarsFound = report.ApiKeyEnvVarsFound ?? Array.Empty<string>(),
        ObsidianVaults = report.ObsidianVaults ?? Array.Empty<string>(),
        Monitors = report.Monitors ?? Array.Empty<MonitorInfo>(),
    };

    /// <summary>Cancels running model listings, connection tests and detection. Tokens already handed out stay usable.</summary>
    public void Dispose()
    {
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
    }

    private bool ApplyTo(AppSettings target, SettingsErrorBag? errors)
    {
        var ok = true;
        void Fail(string key, string message)
        {
            ok = false;
            errors?.Set(key, message);
        }

        // A text field is parsed only when it no longer shows the target's current value, so a value
        // the user never touched (even an odd hand-edited one) is left exactly as it is.
        void Int(string field, string text, int current, int min, int max, Action<int> assign)
        {
            if (!Differs(text, SettingsParsing.FormatInt(current))) return;
            if (SettingsParsing.TryParseInt(text, min, max, out var v, out var e)) assign(v);
            else Fail(field, e);
        }

        target.Vault.Path = SettingsParsing.ExpandHome(VaultPath);
        Int(nameof(VaultMaxResultsText), _vaultMaxResultsText, target.Vault.MaxSearchResults, VaultResultsMin, VaultResultsMax, v => target.Vault.MaxSearchResults = v);
        Int(nameof(VaultMaxReadLinesText), _vaultMaxReadLinesText, target.Vault.MaxReadLines, VaultReadLinesMin, VaultReadLinesMax, v => target.Vault.MaxReadLines = v);
        if (Differs(_vaultExtensionsText, SettingsParsing.FormatList(target.Vault.IncludeExtensions)))
        {
            if (SettingsParsing.TryParseExtensions(_vaultExtensionsText, out var ext, out var e)) target.Vault.IncludeExtensions = ext;
            else Fail(nameof(VaultExtensionsText), e);
        }
        if (Differs(_vaultExcludeText, SettingsParsing.FormatList(target.Vault.ExcludeFolders)))
            target.Vault.ExcludeFolders = SettingsParsing.ParseFolderNames(_vaultExcludeText);

        Int(nameof(MaxStepsText), _maxStepsText, target.Safety.MaxStepsPerTurn, MaxStepsMin, MaxStepsMax, v => target.Safety.MaxStepsPerTurn = v);
        if (Differs(_blockedProcessesText, SettingsParsing.FormatList(target.Safety.BlockedProcesses)))
            target.Safety.BlockedProcesses = SettingsParsing.ParseProcessNames(_blockedProcessesText);
        if (Differs(_elevationPatternsText, SettingsParsing.FormatLines(target.Safety.ElevationTextPatterns)))
        {
            if (SettingsParsing.TryParseRegexLines(_elevationPatternsText, out var patterns, out var e)) target.Safety.ElevationTextPatterns = patterns;
            else Fail(nameof(ElevationPatternsText), e);
        }
        if (Differs(_elevatedTargetsText, SettingsParsing.FormatLines(target.Safety.ElevatedLaunchTargets)))
            target.Safety.ElevatedLaunchTargets = SettingsParsing.ParseLines(_elevatedTargetsText);

        Int(nameof(MaxImageWidthText), _maxImageWidthText, target.Screen.MaxImageWidth, ImageWidthMin, ImageWidthMax, v => target.Screen.MaxImageWidth = v);
        Int(nameof(MaxImageHeightText), _maxImageHeightText, target.Screen.MaxImageHeight, ImageHeightMin, ImageHeightMax, v => target.Screen.MaxImageHeight = v);
        if (Differs(_gridSpacingText, SettingsParsing.FormatInt(target.Screen.GridSpacing)))
        {
            if (SettingsParsing.TryParseInt(_gridSpacingText, 0, GridSpacingMax, out var g, out var e) && (g == 0 || g >= GridSpacingMin))
                target.Screen.GridSpacing = g;
            else
                Fail(nameof(GridSpacingText), e.Length > 0 ? e : $"Use 0 for no grid, or {GridSpacingMin} to {GridSpacingMax} pixels.");
        }
        Int(nameof(SettleDelayText), _settleDelayText, target.Screen.ActionSettleDelayMs, 0, SettleDelayMax, v => target.Screen.ActionSettleDelayMs = v);
        Int(nameof(ScreenshotsToKeepText), _screenshotsToKeepText, target.Screen.ScreenshotsToKeep, ScreenshotsToKeepMin, ScreenshotsToKeepMax, v => target.Screen.ScreenshotsToKeep = v);
        Int(nameof(TypingDelayText), _typingDelayText, target.Screen.TypingDelayMs, 0, TypingDelayMax, v => target.Screen.TypingDelayMs = v);

        if (target.Screen.Monitor == MonitorSelection.Specific && _report?.Monitors is { Count: > 0 } monitors &&
            target.Screen.MonitorIndex >= monitors.Count)
            Fail(nameof(MonitorIndex), $"Monitor {target.Screen.MonitorIndex + 1} is not connected. Pick one from the list.");

        var hotkey = StopHotkey.Trim();
        if (SettingsHotkey.TryValidate(hotkey, out var hotkeyError)) target.Ui.StopHotkey = hotkey;
        else Fail(nameof(StopHotkey), hotkeyError);

        return ok;
    }

    private string BuildErrorSummaryAndNavigate()
    {
        var messages = new List<(string Label, string Message, string Section, ProfileViewModel? Profile)>();
        foreach (var key in Errors.Keys)
            messages.Add((FieldLabels.GetValueOrDefault(key, key), Errors[key], SectionForField(key), null));
        foreach (var p in Profiles)
            foreach (var key in p.Errors.Keys)
            {
                var section = AdvancedProfileFields.Contains(key) && AdvancedMode ? SectionAdvanced : SectionModels;
                var label = $"{FieldLabels.GetValueOrDefault(key, key)} ({p.Name.Trim()})";
                var message = p.Errors[key];
                if (AdvancedProfileFields.Contains(key) && !AdvancedMode) message += " Turn on Advanced mode (Interface) to edit it.";
                messages.Add((label, message, section, p));
            }
        if (messages.Count == 0) return "";

        var first = messages[0];
        if (first.Profile != null) SelectedProfile = first.Profile;
        SelectSection(first.Section);
        var more = messages.Count > 1 ? $" ({messages.Count - 1} more)" : "";
        return $"{first.Label}: {first.Message}{more}";
    }

    private static string SectionForField(string field) => field switch
    {
        nameof(VaultMaxResultsText) or nameof(VaultMaxReadLinesText) or nameof(VaultExtensionsText) or nameof(VaultExcludeText) => SectionVault,
        nameof(MaxStepsText) or nameof(BlockedProcessesText) or nameof(ElevationPatternsText) or nameof(ElevatedTargetsText) => SectionSafety,
        nameof(StopHotkey) => SectionInterface,
        _ => SectionScreen,
    };

    private void LoadTextFields()
    {
        var s = Settings;
        _vaultMaxResultsText = SettingsParsing.FormatInt(s.Vault.MaxSearchResults);
        _vaultMaxReadLinesText = SettingsParsing.FormatInt(s.Vault.MaxReadLines);
        _vaultExtensionsText = SettingsParsing.FormatList(s.Vault.IncludeExtensions);
        _vaultExcludeText = SettingsParsing.FormatList(s.Vault.ExcludeFolders);
        _maxStepsText = SettingsParsing.FormatInt(s.Safety.MaxStepsPerTurn);
        _blockedProcessesText = SettingsParsing.FormatList(s.Safety.BlockedProcesses);
        _elevationPatternsText = SettingsParsing.FormatLines(s.Safety.ElevationTextPatterns);
        _elevatedTargetsText = SettingsParsing.FormatLines(s.Safety.ElevatedLaunchTargets);
        _maxImageWidthText = SettingsParsing.FormatInt(s.Screen.MaxImageWidth);
        _maxImageHeightText = SettingsParsing.FormatInt(s.Screen.MaxImageHeight);
        _gridSpacingText = SettingsParsing.FormatInt(s.Screen.GridSpacing);
        _settleDelayText = SettingsParsing.FormatInt(s.Screen.ActionSettleDelayMs);
        _screenshotsToKeepText = SettingsParsing.FormatInt(s.Screen.ScreenshotsToKeep);
        _typingDelayText = SettingsParsing.FormatInt(s.Screen.TypingDelayMs);
    }

    private static bool Differs(string text, string current) =>
        !string.Equals(text.Replace("\r\n", "\n"), current, StringComparison.Ordinal);

    private void UpdateVisibleSections()
    {
        var wanted = AllSections.Where(s => s.Key != SectionAdvanced || AdvancedMode).ToList();
        if (VisibleSections.SequenceEqual(wanted)) return;
        var selectedKey = _selectedSection?.Key;
        if (selectedKey == SectionAdvanced && !AdvancedMode)
        {
            // Move off the page before it disappears, so the navigation list never loses its selection.
            _selectedSection = VisibleSections.FirstOrDefault(s => s.Key == SectionInterface) ?? VisibleSections[0];
            selectedKey = _selectedSection.Key;
            Raise(nameof(SelectedSection), nameof(SelectedSectionKey));
        }
        VisibleSections.Clear();
        foreach (var s in wanted) VisibleSections.Add(s);
        var keep = VisibleSections.FirstOrDefault(s => s.Key == selectedKey);
        if (keep != null)
        {
            _selectedSection = keep;
            Raise(nameof(SelectedSection));
        }
        else if (selectedKey != null)
        {
            SelectedSection = VisibleSections.FirstOrDefault(s => s.Key == SectionInterface) ?? VisibleSections[0];
        }
    }

    private void RebuildMonitorOptions()
    {
        var index = MonitorIndex;
        MonitorOptions.Clear();
        var monitors = _report?.Monitors ?? Array.Empty<MonitorInfo>();
        if (monitors.Count > 0)
        {
            foreach (var m in monitors.OrderBy(m => m.Index))
                MonitorOptions.Add(new SettingsMonitorOption(m.Index, DescribeMonitor(m)));
        }
        if (MonitorOptions.All(o => o.Index != index))
        {
            // Not detected (yet): keep the configured index selectable.
            for (var i = MonitorOptions.Count; i <= index; i++)
                if (MonitorOptions.All(o => o.Index != i)) MonitorOptions.Add(new SettingsMonitorOption(i, $"Monitor {i + 1}" + (monitors.Count > 0 ? " (not connected)" : "")));
        }
        Raise(nameof(MonitorIndex), nameof(SelectedMonitorOption));
    }

    public static string DescribeMonitor(MonitorInfo m)
    {
        var scale = (m.Scale <= 0 ? 1 : m.Scale).ToString("P0", CultureInfo.InvariantCulture).Replace(" ", "");
        var name = string.IsNullOrWhiteSpace(m.DeviceName) ? "" : $" ({m.DeviceName.Trim()})";
        return $"Monitor {m.Index + 1}{name}: {m.Bounds.Width} x {m.Bounds.Height}, {scale} scale{(m.IsPrimary ? ", primary" : "")}";
    }

    private ProfileViewModel AddProfile(ProviderProfile profile)
    {
        Settings.Profiles.Add(profile);
        var vm = CreateProfileViewModel(profile);
        Profiles.Add(vm);
        UpdateActiveFlags();
        SelectedProfile = vm;
        RaiseProfileCommands();
        return vm;
    }

    private ProfileViewModel CreateProfileViewModel(ProviderProfile profile) =>
        new(profile, _catalog)
        {
            LifetimeToken = _lifetime.Token,
            DetectedCliPath = DetectedCliPathFor(profile),
        };

    private string? DetectedCliPathFor(ProviderProfile profile)
    {
        if (_report == null) return null;
        return profile.Kind switch
        {
            ProviderKind.ClaudeCli => _report.ClaudeCli.Path,
            ProviderKind.AcpAgent when profile.PresetId == ProviderPresets.GeminiCliId => _report.GeminiCli.Path,
            _ => null,
        };
    }

    private string UniqueProfileName(string baseName)
    {
        var name = baseName;
        for (var i = 2; Profiles.Any(p => string.Equals(p.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)); i++)
            name = $"{baseName} {i}";
        return name;
    }

    private void UpdateActiveFlags()
    {
        var activeId = Settings.ActiveProfileId;
        if (Profiles.All(p => p.Id != activeId) && Profiles.Count > 0)
        {
            activeId = Profiles[0].Id;
            Settings.ActiveProfileId = activeId;
        }
        foreach (var p in Profiles) p.IsActive = p.Id == activeId;
        Raise(nameof(ActiveProfile));
    }

    private void RaiseProfileCommands()
    {
        DuplicateCommand?.RaiseCanExecuteChanged();
        DeleteCommand?.RaiseCanExecuteChanged();
        SetActiveCommand?.RaiseCanExecuteChanged();
    }

    private static (int Width, int Height) FitInside(int width, int height, int maxWidth, int maxHeight)
    {
        if (width <= 0 || height <= 0) return (maxWidth, maxHeight);
        var scale = Math.Min(1.0, Math.Min(maxWidth / (double)width, maxHeight / (double)height));
        return (Math.Max(1, (int)Math.Round(width * scale)), Math.Max(1, (int)Math.Round(height * scale)));
    }

    private static string DescribeCli(CliToolStatus status, string command, string notFound)
    {
        if (status.Path == null) return notFound;
        var parts = new List<string>();
        parts.Add(string.IsNullOrWhiteSpace(status.Version) ? $"Found at {status.Path}." : $"Version {status.Version.Trim()} at {status.Path}.");
        switch (status.LoggedIn)
        {
            case true:
                parts.Add(string.IsNullOrWhiteSpace(status.Detail) ? "Logged in." : $"Logged in ({status.Detail.Trim()}).");
                break;
            case false:
                parts.Add(command == "claude" ? "Not logged in: run claude auth login in a terminal." : $"Not logged in: start {command} once in a terminal and log in.");
                break;
            default:
                if (!string.IsNullOrWhiteSpace(status.Detail)) parts.Add(status.Detail.Trim());
                break;
        }
        return string.Join(" ", parts);
    }

    private static SettingsDetectedItem DescribeServer(LocalServerStatus server, string presetId)
    {
        var preset = ProviderPresets.Find(presetId)!;
        var at = string.IsNullOrWhiteSpace(server.BaseUrl) ? "" : " at " + server.BaseUrl.Trim();
        if (!server.Running)
            return new SettingsDetectedItem(server.Name, $"Not running{at}.", false);
        var models = server.Models ?? Array.Empty<ModelInfo>();
        var vision = models.Count(m => m.SupportsVision == true);
        var detail = models.Count switch
        {
            0 => $"Running{at}, no models installed yet.",
            1 => $"Running{at}, 1 model{(vision > 0 ? " (vision)" : "")}.",
            _ => $"Running{at}, {models.Count} models" + (vision > 0 ? $" ({vision} with vision)." : "."),
        };
        var baseUrl = string.IsNullOrWhiteSpace(server.BaseUrl) ? null : server.BaseUrl.Trim();
        // LM Studio and other OpenAI-compatible servers are reached under /v1.
        if (baseUrl != null && preset.Kind == ProviderKind.OpenAiCompatible && !baseUrl.TrimEnd('/').EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            baseUrl = baseUrl.TrimEnd('/') + "/v1";
        return new SettingsDetectedItem(server.Name, detail, true)
        {
            PresetId = presetId,
            Model = PickModel(models, preset.DefaultModel),
            BaseUrl = baseUrl,
            AddTooltip = preset.Notes,
        };
    }
}

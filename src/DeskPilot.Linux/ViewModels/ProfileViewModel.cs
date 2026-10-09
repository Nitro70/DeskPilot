using System.Collections.ObjectModel;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Linux.ViewModels;

/// <summary>A model in the model picker, with a short capability hint such as "vision, thinking".</summary>
public sealed record SettingsModelOption(string Id, string Hint)
{
    public string Display => Hint.Length == 0 ? Id : $"{Id}  ({Hint})";

    public override string ToString() => Id;

    public static SettingsModelOption From(ModelInfo model) => new(model.Id, DescribeCapabilities(model));

    public static string DescribeCapabilities(ModelInfo model)
    {
        var parts = new List<string>();
        if (model.SupportsVision == true) parts.Add("vision");
        else if (model.SupportsVision == false) parts.Add("text only");
        if (model.SupportsThinking == true) parts.Add("thinking");
        if (model.SupportsTools == false) parts.Add("no tool calling");
        return string.Join(", ", parts);
    }
}

/// <summary>
/// Editor state for one provider profile. Plain values (strings, flags, enums) are written straight
/// through to the profile object of the edited settings copy; numbers, JSON and line lists are kept as
/// text and only written by <see cref="ApplyTo"/>, which validates them. A text field that still shows
/// the stored value is never rewritten, so a hand-edited settings file cannot block saving unrelated changes.
/// </summary>
public sealed class ProfileViewModel : SettingsBindableBase
{
    public const int MinOutputTokens = 16;
    public const int MaxOutputTokensLimit = 1_000_000;
    public const int MinTimeoutSeconds = 5;
    public const int MaxTimeoutSeconds = 3600;
    public const int MinThinkingBudget = 1024;
    public const int MaxThinkingBudget = 1_000_000;
    public const double MaxTemperature = 2.0;

    public static readonly IReadOnlyList<SettingsOption<ReasoningStyle>> ReasoningStyles = new[]
    {
        new SettingsOption<ReasoningStyle>(ReasoningStyle.None, "Send no reasoning settings"),
        new SettingsOption<ReasoningStyle>(ReasoningStyle.ReasoningEffort, "reasoning_effort (OpenAI style)"),
        new SettingsOption<ReasoningStyle>(ReasoningStyle.OpenRouter, "reasoning object (OpenRouter style)"),
    };

    /// <summary>
    /// Shared by every profile whose effort is one of these, so switching profiles in the editor only moves the
    /// selection and never swaps the list under it.
    /// </summary>
    public static readonly IReadOnlyList<SettingsOption<string>> StandardEffortOptions = new[]
    {
        new SettingsOption<string>("", "Provider default"), new SettingsOption<string>("none", "None"),
        new SettingsOption<string>("minimal", "Minimal"), new SettingsOption<string>("low", "Low"),
        new SettingsOption<string>("medium", "Medium"), new SettingsOption<string>("high", "High"),
        new SettingsOption<string>("xhigh", "Extra high"), new SettingsOption<string>("max", "Max"),
    };

    private readonly IModelCatalog? _catalog;
    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Dictionary<string, ModelInfo> _knownModels = new(StringComparer.OrdinalIgnoreCase);

    private bool _isActive;
    private string _maxOutputTokensText = "";
    private string _temperatureText = "";
    private string _requestTimeoutText = "";
    private string _thinkingBudgetText = "";
    private string _extraEnvText = "";
    private string _extraHeadersText = "";
    private string _extraBodyJson = "";
    private string _newApiKey = "";
    private bool _isReplacingKey;
    private bool _clearKeyRequested;
    private bool _isLoadingModels;
    private string _modelStatus = "";
    private bool _isTesting;
    private bool? _connectionOk;
    private string _connectionStatus = "";
    private string? _detectedCliPath;
    private bool _modelsLoadedOnce;

    public ProfileViewModel(ProviderProfile profile, IModelCatalog? catalog = null, Func<string, string?>? getEnvironmentVariable = null)
    {
        Profile = profile;
        _catalog = catalog;
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;

        profile.ExtraEnv ??= new();
        profile.ExtraHeaders ??= new();
        profile.Effort ??= "";
        LoadTextFields();

        EffortOptions = BuildEffortOptions(profile.Effort);
        ModelOptions = new ObservableCollection<SettingsModelOption>(SuggestedModels().Select(m => new SettingsModelOption(m, "")));

        ReplaceKeyCommand = new SettingsCommand(ReplaceKey);
        ClearKeyCommand = new SettingsCommand(ClearKey);
        CancelKeyChangeCommand = new SettingsCommand(CancelKeyChange);
        RefreshModelsCommand = new SettingsAsyncCommand(() => RefreshModelsAsync(CancellationToken.None), () => _catalog != null);
        TestConnectionCommand = new SettingsAsyncCommand(() => TestConnectionAsync(CancellationToken.None), () => _catalog != null);
    }

    /// <summary>The profile object inside the edited settings copy.</summary>
    public ProviderProfile Profile { get; }

    public SettingsErrorBag Errors { get; } = new();

    /// <summary>Cancels model listing / connection tests when the window closes.</summary>
    public CancellationToken LifetimeToken { get; set; }

    public string Id => Profile.Id;

    public ProviderKind Kind => Profile.Kind;

    public ProviderPreset? Preset => ProviderPresets.Find(Profile.PresetId);

    public string PresetNotes => Preset?.Notes ?? "";

    public string KindText => DescribeKind(Kind);

    public string KindBadge => BadgeFor(Kind);

    public bool IsHttp => Kind is ProviderKind.AnthropicApi or ProviderKind.OpenAiCompatible or ProviderKind.Ollama;

    public bool IsCliOrAcp => Kind is ProviderKind.ClaudeCli or ProviderKind.AcpAgent;

    public bool IsClaudeCli => Kind == ProviderKind.ClaudeCli;

    public bool IsAcp => Kind == ProviderKind.AcpAgent;

    public bool IsAnthropicApi => Kind == ProviderKind.AnthropicApi;

    public bool IsOpenAiCompatible => Kind == ProviderKind.OpenAiCompatible;

    public bool IsOllama => Kind == ProviderKind.Ollama;

    /// <summary>Thinking Auto/On/Off is translated for every provider except ACP agents, which manage it themselves.</summary>
    public bool ShowThinking => !IsAcp;

    /// <summary>Effort is understood by Claude Code, the Anthropic API and OpenAI-style reasoning_effort.</summary>
    public bool ShowEffort => Kind is ProviderKind.ClaudeCli or ProviderKind.AnthropicApi or ProviderKind.OpenAiCompatible;

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public string Name
    {
        get => Profile.Name;
        set => Through(Profile.Name, value ?? "", v => Profile.Name = v);
    }

    public string Model
    {
        get => Profile.Model;
        set
        {
            if (Through(Profile.Model, value ?? "", v => Profile.Model = v)) Raise(nameof(ModelHint));
        }
    }

    public ObservableCollection<SettingsModelOption> ModelOptions { get; }

    /// <summary>A pick from the model list: copies the id into <see cref="Model"/>. The list itself never holds a selection.</summary>
    public void PickModel(SettingsModelOption? option)
    {
        if (option != null && !string.IsNullOrWhiteSpace(option.Id)) Model = option.Id;
    }

    /// <summary>Capabilities of the chosen model when the catalog reported them.</summary>
    public string ModelHint
    {
        get
        {
            if (!_knownModels.TryGetValue(Model.Trim(), out var info)) return "";
            var caps = SettingsModelOption.DescribeCapabilities(info);
            if (info.SupportsVision == false && SupportsVision)
                return (caps.Length > 0 ? caps + ". " : "") + "This model cannot see images: turn off \"Supports vision\" below.";
            return caps;
        }
    }

    public bool IsLoadingModels
    {
        get => _isLoadingModels;
        private set => Set(ref _isLoadingModels, value);
    }

    public string ModelStatus
    {
        get => _modelStatus;
        private set => Set(ref _modelStatus, value);
    }

    // ---- API key ----

    public bool HasStoredKey => !_clearKeyRequested && !string.IsNullOrEmpty(Profile.ApiKeyProtected);

    /// <summary>"Saved key: sk-ab...wxyz". Never the full key.</summary>
    public string StoredKeyText
    {
        get
        {
            if (string.IsNullOrEmpty(Profile.ApiKeyProtected)) return "";
            var mask = SecretProtector.Mask(SecretProtector.Unprotect(Profile.ApiKeyProtected));
            return mask.Length == 0
                ? "A saved key exists but cannot be read with this user account's key file. Enter it again."
                : "Saved key: " + mask;
        }
    }

    public bool ShowKeyEntry => !HasStoredKey || _isReplacingKey;

    public bool IsReplacingKey
    {
        get => _isReplacingKey;
        private set
        {
            if (Set(ref _isReplacingKey, value)) RaiseKeyState();
        }
    }

    public bool ClearKeyRequested
    {
        get => _clearKeyRequested;
        private set
        {
            if (Set(ref _clearKeyRequested, value)) RaiseKeyState();
        }
    }

    /// <summary>A key typed into the password box, not saved yet. Encrypted on save; never displayed.</summary>
    public string NewApiKey
    {
        get => _newApiKey;
        set
        {
            if (Set(ref _newApiKey, value ?? "")) Raise(nameof(KeyStateText));
        }
    }

    public string KeyStateText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_newApiKey))
                return HasStoredKey || _clearKeyRequested
                    ? "The new key replaces the saved one when you save. It is stored encrypted."
                    : "The key is stored encrypted when you save, with a key file only your user account can read.";
            if (_clearKeyRequested) return "The saved key will be removed when you save.";
            if (HasStoredKey) return "Stored encrypted with a key file only your user account can read.";
            return IsHttp && Kind != ProviderKind.Ollama && Preset?.IsLocal != true
                ? "Paste a key, or leave empty to use the environment variable below."
                : "Usually not needed for a local server.";
        }
    }

    public SettingsCommand ReplaceKeyCommand { get; }
    public SettingsCommand ClearKeyCommand { get; }
    public SettingsCommand CancelKeyChangeCommand { get; }

    public void ReplaceKey()
    {
        IsReplacingKey = true;
    }

    public void ClearKey()
    {
        NewApiKey = "";
        IsReplacingKey = false;
        ClearKeyRequested = true;
    }

    public void CancelKeyChange()
    {
        NewApiKey = "";
        IsReplacingKey = false;
        ClearKeyRequested = false;
    }

    public string ApiKeyEnvVar
    {
        get => Profile.ApiKeyEnvVar;
        set
        {
            if (Through(Profile.ApiKeyEnvVar, value ?? "", v => Profile.ApiKeyEnvVar = v)) Raise(nameof(ApiKeyEnvStatus));
        }
    }

    /// <summary>Whether the variable is set for DeskPilot. Only the name is ever shown.</summary>
    public string ApiKeyEnvStatus
    {
        get
        {
            var name = ApiKeyEnvVar.Trim();
            if (name.Length == 0) return "Optional: the name of an environment variable that holds the key.";
            return string.IsNullOrWhiteSpace(_getEnvironmentVariable(name))
                ? $"{name} is not set for DeskPilot. Set it in your login environment (for example ~/.profile), then log out and back in."
                : $"{name} is set; it is used when no key is saved.";
        }
    }

    // ---- Connection ----

    public string BaseUrl
    {
        get => Profile.BaseUrl;
        set => Through(Profile.BaseUrl, value ?? "", v => Profile.BaseUrl = v);
    }

    public string CliPath
    {
        get => Profile.CliPath;
        set
        {
            if (Through(Profile.CliPath, value ?? "", v => Profile.CliPath = v)) Raise(nameof(CliHint));
        }
    }

    public string CliCommandName =>
        Preset?.CliCommand is { Length: > 0 } c ? c : Kind == ProviderKind.ClaudeCli ? "claude" : Kind == ProviderKind.AcpAgent ? "gemini" : "";

    /// <summary>Set by the settings window once environment detection has found the CLI.</summary>
    public string? DetectedCliPath
    {
        get => _detectedCliPath;
        set
        {
            if (Set(ref _detectedCliPath, value)) Raise(nameof(CliHint));
        }
    }

    public string CliHint
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CliPath)) return "Using this file. Clear the box to auto-detect again.";
            if (Preset?.Id == ProviderPresets.CustomAcpId) return "Set the agent's executable here and its ACP arguments under Advanced.";
            var hint = $"Leave empty to auto-detect '{CliCommandName}' (PATH, ~/.local/bin, npm and the usual install folders).";
            return string.IsNullOrEmpty(_detectedCliPath) ? hint : hint + " Found: " + _detectedCliPath;
        }
    }

    // ---- Thinking and output ----

    public ThinkingMode Thinking
    {
        get => Profile.Thinking;
        set => Through(Profile.Thinking, value, v => Profile.Thinking = v);
    }

    public IReadOnlyList<SettingsOption<string>> EffortOptions { get; }

    public string Effort
    {
        get => Profile.Effort;
        set
        {
            if (Through(Profile.Effort, value ?? "", v => Profile.Effort = v)) Raise(nameof(SelectedEffortOption));
        }
    }

    /// <summary>The effort picker's item. A null from the picker (its list being swapped) is ignored.</summary>
    public SettingsOption<string>? SelectedEffortOption
    {
        get => EffortOptions.FirstOrDefault(o => string.Equals(o.Value, Effort, StringComparison.Ordinal)) ?? EffortOptions[0];
        set
        {
            if (value != null) Effort = value.Value;
        }
    }

    public string MaxOutputTokensText
    {
        get => _maxOutputTokensText;
        set => Set(ref _maxOutputTokensText, value ?? "");
    }

    /// <summary>Empty = provider default.</summary>
    public string TemperatureText
    {
        get => _temperatureText;
        set => Set(ref _temperatureText, value ?? "");
    }

    public string RequestTimeoutText
    {
        get => _requestTimeoutText;
        set => Set(ref _requestTimeoutText, value ?? "");
    }

    public bool SupportsVision
    {
        get => Profile.SupportsVision;
        set
        {
            if (Through(Profile.SupportsVision, value, v => Profile.SupportsVision = v)) Raise(nameof(ModelHint));
        }
    }

    // ---- Advanced ----

    public string ExtraCliArgs
    {
        get => Profile.ExtraCliArgs;
        set => Through(Profile.ExtraCliArgs, value ?? "", v => Profile.ExtraCliArgs = v);
    }

    /// <summary>KEY=VALUE per line.</summary>
    public string ExtraEnvText
    {
        get => _extraEnvText;
        set => Set(ref _extraEnvText, value ?? "");
    }

    /// <summary>"Name: value" per line.</summary>
    public string ExtraHeadersText
    {
        get => _extraHeadersText;
        set => Set(ref _extraHeadersText, value ?? "");
    }

    public string ExtraBodyJson
    {
        get => _extraBodyJson;
        set => Set(ref _extraBodyJson, value ?? "");
    }

    public string ThinkingBudgetText
    {
        get => _thinkingBudgetText;
        set => Set(ref _thinkingBudgetText, value ?? "");
    }

    public bool ForceSubscriptionLogin
    {
        get => Profile.ForceSubscriptionLogin;
        set => Through(Profile.ForceSubscriptionLogin, value, v => Profile.ForceSubscriptionLogin = v);
    }

    public ReasoningStyle ReasoningStyle
    {
        get => Profile.ReasoningStyle;
        set
        {
            if (Through(Profile.ReasoningStyle, value, v => Profile.ReasoningStyle = v)) Raise(nameof(SelectedReasoningStyleOption));
        }
    }

    /// <summary>The reasoning-style picker's item. A null from the picker is ignored.</summary>
    public SettingsOption<ReasoningStyle>? SelectedReasoningStyleOption
    {
        get => ReasoningStyles.FirstOrDefault(o => o.Value == ReasoningStyle);
        set
        {
            if (value != null) ReasoningStyle = value.Value;
        }
    }

    public bool UsePromptCaching
    {
        get => Profile.UsePromptCaching;
        set => Through(Profile.UsePromptCaching, value, v => Profile.UsePromptCaching = v);
    }

    public bool UseContextEditing
    {
        get => Profile.UseContextEditing;
        set => Through(Profile.UseContextEditing, value, v => Profile.UseContextEditing = v);
    }

    // ---- Connection test ----

    public bool IsTesting
    {
        get => _isTesting;
        private set => Set(ref _isTesting, value);
    }

    /// <summary>null = not tested yet, true = OK, false = failed.</summary>
    public bool? ConnectionOk
    {
        get => _connectionOk;
        private set
        {
            if (Set(ref _connectionOk, value)) Raise(nameof(ConnectionSucceeded), nameof(ConnectionFailed));
        }
    }

    public bool ConnectionSucceeded => _connectionOk == true;

    public bool ConnectionFailed => _connectionOk == false;

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => Set(ref _connectionStatus, value);
    }

    public SettingsAsyncCommand RefreshModelsCommand { get; }
    public SettingsAsyncCommand TestConnectionCommand { get; }

    /// <summary>True once the model list was fetched (used to fetch it only once automatically).</summary>
    public bool ModelsLoadedOnce => _modelsLoadedOnce;

    // ---- Mapping ----

    /// <summary>
    /// Writes the text-backed fields and the key change into <paramref name="target"/> (the profile itself
    /// or a clone of it). Invalid fields are skipped and reported in <paramref name="errors"/> when given.
    /// Returns true when everything was valid.
    /// </summary>
    public bool ApplyTo(ProviderProfile target, SettingsErrorBag? errors)
    {
        var ok = true;
        void Fail(string key, string message)
        {
            ok = false;
            errors?.Set(key, message);
        }

        target.Name = Name.Trim();
        target.Model = Model.Trim();
        target.BaseUrl = BaseUrl.Trim();
        target.ApiKeyEnvVar = ApiKeyEnvVar.Trim();
        target.CliPath = SettingsParsing.ExpandHome(CliPath);

        if (target.Name.Length == 0) Fail(nameof(Name), "Give the profile a name.");
        if (IsHttp && !SettingsParsing.TryValidateHttpUrl(target.BaseUrl, out var urlError)) Fail(nameof(BaseUrl), urlError);

        if (Differs(_maxOutputTokensText, SettingsParsing.FormatInt(target.MaxOutputTokens)))
        {
            if (SettingsParsing.TryParseInt(_maxOutputTokensText, MinOutputTokens, MaxOutputTokensLimit, out var v, out var e)) target.MaxOutputTokens = v;
            else Fail(nameof(MaxOutputTokensText), e);
        }
        if (Differs(_temperatureText, SettingsParsing.FormatDouble(target.Temperature)))
        {
            if (SettingsParsing.TryParseOptionalDouble(_temperatureText, 0, MaxTemperature, out var t, out var e)) target.Temperature = t;
            else Fail(nameof(TemperatureText), e);
        }
        if (Differs(_requestTimeoutText, SettingsParsing.FormatInt(target.RequestTimeoutSeconds)))
        {
            if (SettingsParsing.TryParseInt(_requestTimeoutText, MinTimeoutSeconds, MaxTimeoutSeconds, out var v, out var e)) target.RequestTimeoutSeconds = v;
            else Fail(nameof(RequestTimeoutText), e);
        }
        if (Differs(_thinkingBudgetText, SettingsParsing.FormatInt(target.ThinkingBudgetTokens)))
        {
            if (SettingsParsing.TryParseInt(_thinkingBudgetText, MinThinkingBudget, MaxThinkingBudget, out var v, out var e)) target.ThinkingBudgetTokens = v;
            else Fail(nameof(ThinkingBudgetText), e);
        }
        if (Differs(_extraEnvText, SettingsParsing.FormatEnvLines(target.ExtraEnv)))
        {
            if (SettingsParsing.TryParseEnvLines(_extraEnvText, out var env, out var e)) target.ExtraEnv = env;
            else Fail(nameof(ExtraEnvText), e);
        }
        if (Differs(_extraHeadersText, SettingsParsing.FormatHeaderLines(target.ExtraHeaders)))
        {
            if (SettingsParsing.TryParseHeaderLines(_extraHeadersText, out var headers, out var e)) target.ExtraHeaders = headers;
            else Fail(nameof(ExtraHeadersText), e);
        }
        if (Differs(_extraBodyJson, target.ExtraBodyJson ?? ""))
        {
            if (SettingsParsing.TryValidateJsonObject(_extraBodyJson, out var e)) target.ExtraBodyJson = _extraBodyJson.Trim();
            else Fail(nameof(ExtraBodyJson), e);
        }

        if (!string.IsNullOrWhiteSpace(_newApiKey)) target.ApiKeyProtected = SecretProtector.Protect(_newApiKey.Trim());
        else if (_clearKeyRequested) target.ApiKeyProtected = "";

        return ok;
    }

    /// <summary>Validates and writes everything into <see cref="Profile"/>, filling <see cref="Errors"/>.</summary>
    public bool Commit()
    {
        Errors.Clear();
        return ApplyTo(Profile, Errors);
    }

    /// <summary>A copy of the profile with every valid pending edit applied (for previews, tests and duplicates).</summary>
    public ProviderProfile BuildEffectiveProfile()
    {
        var copy = CloneProfile(Profile);
        ApplyTo(copy, null);
        return copy;
    }

    /// <summary>The key a model request would use right now: a newly typed key, else the saved key, else the environment variable.</summary>
    public string ResolveKeyForRequests()
    {
        if (!string.IsNullOrWhiteSpace(_newApiKey)) return _newApiKey.Trim();
        return SecretProtector.ResolveApiKey(BuildEffectiveProfile());
    }

    public static ProviderProfile CloneProfile(ProviderProfile profile) =>
        JsonSerializer.Deserialize<ProviderProfile>(JsonSerializer.Serialize(profile, SettingsStore.JsonOptions), SettingsStore.JsonOptions)!;

    // ---- Model list ----

    public async Task RefreshModelsAsync(CancellationToken ct)
    {
        if (_catalog == null) return;
        _modelsLoadedOnce = true;
        IsLoadingModels = true;
        ModelStatus = "Loading models...";
        var key = "";
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken);
        try
        {
            key = ResolveKeyForRequests();
            var profile = BuildEffectiveProfile();
            var catalog = _catalog;
            var token = linked.Token;
            var result = await Task.Run(() => catalog.ListModelsAsync(profile, key, token), token);
            SetModels(result.Models);
            ModelStatus = result.Error != null
                ? "Could not list models: " + SettingsParsing.Redact(result.Error, key)
                : result.Models.Count == 1 ? "1 model available." : $"{result.Models.Count} models available.";
        }
        catch (OperationCanceledException)
        {
            ModelStatus = "";
        }
        catch (Exception ex)
        {
            ModelStatus = "Could not list models: " + SettingsParsing.Redact(ex.Message, key);
        }
        finally
        {
            IsLoadingModels = false;
        }
    }

    public async Task TestConnectionAsync(CancellationToken ct)
    {
        if (_catalog == null) return;
        IsTesting = true;
        ConnectionOk = null;
        ConnectionStatus = "Testing...";
        var key = "";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, LifetimeToken, timeout.Token);
        try
        {
            key = ResolveKeyForRequests();
            var profile = BuildEffectiveProfile();
            var catalog = _catalog;
            var token = linked.Token;
            var result = await Task.Run(() => catalog.ListModelsAsync(profile, key, token), token);
            if (result.Error != null)
            {
                ConnectionOk = false;
                ConnectionStatus = "Failed: " + SettingsParsing.Redact(result.Error, key);
            }
            else
            {
                ConnectionOk = true;
                ConnectionStatus = result.Models.Count == 1 ? "OK: 1 model" : $"OK: {result.Models.Count} models";
            }
            SetModels(result.Models);
            _modelsLoadedOnce = true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            ConnectionOk = false;
            ConnectionStatus = "Failed: no answer within 60 seconds.";
        }
        catch (OperationCanceledException)
        {
            ConnectionOk = null;
            ConnectionStatus = "Cancelled.";
        }
        catch (Exception ex)
        {
            ConnectionOk = false;
            ConnectionStatus = "Failed: " + SettingsParsing.Redact(ex.Message, key);
        }
        finally
        {
            IsTesting = false;
        }
    }

    /// <summary>Replaces the picker's list with live results; keeps the preset suggestions when the list is empty.</summary>
    public void SetModels(IReadOnlyList<ModelInfo> models)
    {
        if (models.Count == 0) return;
        _knownModels.Clear();
        ModelOptions.Clear();
        foreach (var m in models)
        {
            if (string.IsNullOrWhiteSpace(m.Id) || _knownModels.ContainsKey(m.Id)) continue;
            _knownModels[m.Id] = m;
            ModelOptions.Add(SettingsModelOption.From(m));
        }
        Raise(nameof(ModelHint));
    }

    // ---- Helpers ----

    public static string DescribeKind(ProviderKind kind) => kind switch
    {
        ProviderKind.ClaudeCli => "Claude Code CLI (your Claude subscription)",
        ProviderKind.AnthropicApi => "Anthropic Messages API",
        ProviderKind.OpenAiCompatible => "OpenAI-compatible chat completions API",
        ProviderKind.Ollama => "Ollama native API (local)",
        ProviderKind.AcpAgent => "Agent Client Protocol agent (experimental)",
        _ => kind.ToString(),
    };

    public static string BadgeFor(ProviderKind kind) => kind switch
    {
        ProviderKind.ClaudeCli => "Claude Code",
        ProviderKind.AnthropicApi => "Anthropic API",
        ProviderKind.OpenAiCompatible => "OpenAI-compatible",
        ProviderKind.Ollama => "Ollama",
        ProviderKind.AcpAgent => "ACP agent",
        _ => kind.ToString(),
    };

    private IEnumerable<string> SuggestedModels()
    {
        var list = new List<string>();
        if (Preset is { } preset) list.AddRange(preset.SuggestedModels);
        else if (Kind is ProviderKind.ClaudeCli or ProviderKind.AnthropicApi) list.AddRange(ProviderPresets.ClaudeModels);
        if (!string.IsNullOrWhiteSpace(Profile.Model) && !list.Contains(Profile.Model, StringComparer.OrdinalIgnoreCase))
            list.Insert(0, Profile.Model);
        return list.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<SettingsOption<string>> BuildEffortOptions(string current)
    {
        if (string.IsNullOrEmpty(current) || StandardEffortOptions.Any(o => string.Equals(o.Value, current, StringComparison.Ordinal)))
            return StandardEffortOptions;
        // A value from a hand-edited file stays selectable instead of being silently replaced.
        return StandardEffortOptions.Append(new SettingsOption<string>(current, current)).ToList();
    }

    private void LoadTextFields()
    {
        _maxOutputTokensText = SettingsParsing.FormatInt(Profile.MaxOutputTokens);
        _temperatureText = SettingsParsing.FormatDouble(Profile.Temperature);
        _requestTimeoutText = SettingsParsing.FormatInt(Profile.RequestTimeoutSeconds);
        _thinkingBudgetText = SettingsParsing.FormatInt(Profile.ThinkingBudgetTokens);
        _extraEnvText = SettingsParsing.FormatEnvLines(Profile.ExtraEnv);
        _extraHeadersText = SettingsParsing.FormatHeaderLines(Profile.ExtraHeaders);
        _extraBodyJson = Profile.ExtraBodyJson ?? "";
    }

    /// <summary>
    /// A text field is parsed only when it no longer shows the target's current value, so a value the
    /// user never touched (even an odd hand-edited one) is left exactly as it is.
    /// </summary>
    private static bool Differs(string text, string current) => !string.Equals(text, current, StringComparison.Ordinal);

    private void RaiseKeyState() =>
        Raise(nameof(HasStoredKey), nameof(ShowKeyEntry), nameof(KeyStateText));
}

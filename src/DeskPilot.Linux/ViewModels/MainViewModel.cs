using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.Services;
using AppLog = DeskPilot.Core.Runtime.Log;

namespace DeskPilot.Linux.ViewModels;

/// <summary>
/// State and commands of the main window. Session events arrive on arbitrary threads and are
/// marshalled through <see cref="IUiDispatcher"/>; everything else runs on the UI thread.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    public const string DefaultEffort = "Default";

    public static readonly IReadOnlyList<string> EffortChoices = new[] { DefaultEffort, "none", "minimal", "low", "medium", "high", "xhigh", "max" };

    public static readonly IReadOnlyList<string> ExamplePrompts = new[]
    {
        "Open the text editor and write a short haiku about autumn",
        "What windows are open right now? Summarize them",
        "Open the Downloads folder and sort it by date",
    };

    private readonly IUiDispatcher _ui;
    private readonly LinuxSessionKind _sessionKind;
    private ProfileOption? _selectedProfile;
    private ModelOption? _selectedModel;
    private string _modelText = "";
    private string? _modelsProfileId;
    private bool _isRefreshingModels;
    private string? _modelListError;
    private ThinkingMode _thinking;
    private string _effort = DefaultEffort;
    private AgentState _state = AgentState.Idle;
    private bool _sending;
    private bool _stopRequested;
    private CancellationTokenSource? _turnCts;
    private string _inputText = "";
    private bool _syncing;
    private bool _disposed;
    private bool _showThinking = true;
    private bool _showScreenshots = true;
    private string _profileModelText = "";
    private bool _vaultOn;
    private string _vaultTooltip = "";
    private bool _adminMode;
    private bool _dryRun;
    private string _stopHotkeyText = "";
    private bool _hotkeyOk = true;
    private string _hotkeyTooltip = "";

    public MainViewModel(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector, IUiDispatcher ui,
        ConversationLog? conversation = null, LinuxSessionKind sessionKind = LinuxSessionKind.X11)
    {
        Store = store;
        Session = session;
        Catalog = catalog;
        Detector = detector;
        _ui = ui;
        _sessionKind = sessionKind;
        Conversation = conversation ?? new ConversationLog();
        Conversation.Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasItems));
            OnPropertyChanged(nameof(IsEmpty));
            CopyLogCommand?.NotifyCanExecuteChanged();
        };

        SendOrStopCommand = new RelayCommand(SendOrStop, () => IsBusy || InputText.Trim().Length > 0);
        NewConversationCommand = new AsyncRelayCommand(NewConversationAsync, () => !IsBusy);
        CopyLogCommand = new RelayCommand(CopyLog, () => HasItems);
        RefreshModelsCommand = new AsyncRelayCommand(RefreshModelsAsync, () => !IsRefreshingModels);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        OpenImageCommand = new RelayCommand<object?>(p => { if (p is ToolStepItem { HasImage: true } step) ImageRequested?.Invoke(step); });
        UseExampleCommand = new RelayCommand<object?>(p =>
        {
            if (p is string text && !IsBusy)
            {
                InputText = text;
                FocusInputRequested?.Invoke();
            }
        });
        // Concurrent: a second Stop while the first is still pending cancels the request outright.
        StopCommand = new AsyncRelayCommand(StopAsync, () => IsBusy, AsyncRelayCommandOptions.AllowConcurrentExecutions);

        session.EventRaised += OnSessionEvent;
        session.StateChanged += OnSessionStateChanged;
        store.Changed += OnStoreChanged;

        try { _state = session.State; }
        catch (Exception) { _state = AgentState.Idle; }

        if (!StopHint.HotkeySupported(sessionKind)) SetHotkeyStatus(true, null);
        RefreshFromSettings();
    }

    public SettingsStore Store { get; }
    public IAgentSession Session { get; }
    public IModelCatalog Catalog { get; }
    public IEnvironmentDetector Detector { get; }
    public ConversationLog Conversation { get; }
    public ObservableCollection<LogItem> Items => Conversation.Items;
    public bool HasItems => Conversation.Items.Count > 0;
    public bool IsEmpty => !HasItems;
    public LinuxSessionKind SessionKind => _sessionKind;

    public ObservableCollection<ProfileOption> Profiles { get; } = new();
    public ObservableCollection<ModelOption> Models { get; } = new();
    public IReadOnlyList<string> Efforts => EffortChoices;
    public IReadOnlyList<string> Examples => ExamplePrompts;

    public RelayCommand SendOrStopCommand { get; }
    public AsyncRelayCommand NewConversationCommand { get; }
    public RelayCommand CopyLogCommand { get; }
    public AsyncRelayCommand RefreshModelsCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenImageCommand { get; }
    public ICommand UseExampleCommand { get; }
    public AsyncRelayCommand StopCommand { get; }

    /// <summary>A turn started (raised on the UI thread). The window minimizes itself when configured.</summary>
    public event Action? TurnStarted;
    /// <summary>A turn ended and its footer is in the log (UI thread).</summary>
    public event Action? TurnEnded;
    public event Action? SettingsRequested;
    public event Action<ToolStepItem>? ImageRequested;
    public event Action<string>? CopyRequested;
    public event Action? FocusInputRequested;

    // ---------------------------------------------------------------- top bar

    public ProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (!SetProperty(ref _selectedProfile, value) || _syncing || value == null) return;
            if (value.Id != Store.Current.ActiveProfileId) SwitchProfile(value.Id);
        }
    }

    public ModelOption? SelectedModel
    {
        get => _selectedModel;
        set
        {
            if (!SetProperty(ref _selectedModel, value) || _syncing || value == null) return;
            ModelText = value.Id;
        }
    }

    /// <summary>The model id of the active profile (free text: any id the provider accepts).</summary>
    public string ModelText
    {
        get => _modelText;
        set
        {
            value = (value ?? "").Trim();
            if (!SetProperty(ref _modelText, value) || _syncing) return;
            var profile = Store.Current.ActiveProfile;
            if (profile == null || string.Equals(profile.Model, value, StringComparison.Ordinal)) return;
            SaveActiveProfile(p => p.Model = value, "model");
            UpdateStatusBar();
        }
    }

    public bool IsRefreshingModels
    {
        get => _isRefreshingModels;
        private set
        {
            if (SetProperty(ref _isRefreshingModels, value)) RefreshModelsCommand.NotifyCanExecuteChanged();
        }
    }

    public string? ModelListError
    {
        get => _modelListError;
        private set
        {
            if (SetProperty(ref _modelListError, value)) OnPropertyChanged(nameof(RefreshModelsTooltip));
        }
    }

    public string RefreshModelsTooltip => ModelListError is { Length: > 0 } e
        ? $"Refresh the model list (last attempt failed: {e})"
        : "Refresh the model list from the provider";

    public ThinkingMode Thinking
    {
        get => _thinking;
        set
        {
            if (!SetProperty(ref _thinking, value)) return;
            OnPropertyChanged(nameof(IsThinkingAuto));
            OnPropertyChanged(nameof(IsThinkingOn));
            OnPropertyChanged(nameof(IsThinkingOff));
            if (!_syncing) SaveActiveProfile(p => p.Thinking = value, "thinking setting");
        }
    }

    public bool IsThinkingAuto { get => Thinking == ThinkingMode.Auto; set { if (value) Thinking = ThinkingMode.Auto; } }
    public bool IsThinkingOn { get => Thinking == ThinkingMode.On; set { if (value) Thinking = ThinkingMode.On; } }
    public bool IsThinkingOff { get => Thinking == ThinkingMode.Off; set { if (value) Thinking = ThinkingMode.Off; } }

    /// <summary>One of <see cref="EffortChoices"/>; "Default" means the provider default (stored as "").</summary>
    public string? Effort
    {
        get => _effort;
        set
        {
            // A combo box pushes null while its items are being replaced: that is not a user choice.
            if (value is null) return;
            value = value.Trim().Length == 0 ? DefaultEffort : value.Trim();
            if (!SetProperty(ref _effort, value) || _syncing) return;
            var stored = value == DefaultEffort ? "" : value;
            SaveActiveProfile(p => p.Effort = stored, "effort");
        }
    }

    // ---------------------------------------------------------------- state

    public AgentState State
    {
        get => _state;
        private set
        {
            if (SetProperty(ref _state, value)) UpdateBusy();
        }
    }

    /// <summary>What the status pill shows: the session state, or Starting while a send is pending.</summary>
    public AgentState DisplayState => _sending && State is AgentState.Idle or AgentState.Error ? AgentState.Starting : State;

    public string StatusText => StatusLabel(DisplayState);

    /// <summary>Status pill classes.</summary>
    public bool IsStateActive => DisplayState is AgentState.Starting or AgentState.Running;
    public bool IsStateStopping => DisplayState == AgentState.Stopping;
    public bool IsStateError => DisplayState == AgentState.Error;

    public bool IsBusy => _sending || State is AgentState.Starting or AgentState.Running or AgentState.Stopping;
    public bool IsIdle => !IsBusy;
    public string SendButtonText => IsBusy ? "Stop" : "Send";
    public string SendButtonIcon => IsBusy ? Icons.Stop : Icons.Send;
    public string SendButtonTooltip => IsBusy
        ? (_stopRequested ? "Stop now (force)" : $"Stop the current task ({StopShortcutText()})")
        : "Send (Enter)";

    private string StopShortcutText()
    {
        if (!StopHint.HotkeySupported(_sessionKind) || !HotkeyOk) return "Esc";
        var display = StopHotkey.Display(Store.Current.Ui.StopHotkey);
        return display.Length == 0 ? "Esc" : display;
    }

    public static string StatusLabel(AgentState state) => state switch
    {
        AgentState.Idle => "Idle",
        AgentState.Starting => "Starting",
        AgentState.Running => "Working",
        AgentState.Stopping => "Stopping",
        AgentState.Error => "Error",
        _ => state.ToString(),
    };

    // ---------------------------------------------------------------- input

    public string InputText
    {
        get => _inputText;
        set
        {
            if (SetProperty(ref _inputText, value ?? "")) SendOrStopCommand.NotifyCanExecuteChanged();
        }
    }

    // ---------------------------------------------------------------- settings-derived display

    public bool ShowThinking { get => _showThinking; private set => SetProperty(ref _showThinking, value); }
    public bool ShowScreenshots { get => _showScreenshots; private set => SetProperty(ref _showScreenshots, value); }
    public string ProfileModelText { get => _profileModelText; private set => SetProperty(ref _profileModelText, value); }
    public bool VaultOn { get => _vaultOn; private set { if (SetProperty(ref _vaultOn, value)) OnPropertyChanged(nameof(VaultText)); } }
    public string VaultText => VaultOn ? "Vault on" : "Vault off";
    public string VaultTooltip { get => _vaultTooltip; private set => SetProperty(ref _vaultTooltip, value); }
    public bool AdminMode { get => _adminMode; private set => SetProperty(ref _adminMode, value); }
    public bool DryRun { get => _dryRun; private set => SetProperty(ref _dryRun, value); }

    /// <summary>"Stop: Ctrl+Alt+X" under X11; the overlay/tray/corner hint under Wayland.</summary>
    public string StopHotkeyText { get => _stopHotkeyText; private set => SetProperty(ref _stopHotkeyText, value); }
    public bool HotkeyOk { get => _hotkeyOk; private set { if (SetProperty(ref _hotkeyOk, value)) OnPropertyChanged(nameof(HotkeyProblem)); } }
    public bool HotkeyProblem => !HotkeyOk;
    public string HotkeyTooltip { get => _hotkeyTooltip; private set => SetProperty(ref _hotkeyTooltip, value); }

    /// <summary>Called by the app after (re)registering the global stop hotkey.</summary>
    public void SetHotkeyStatus(bool registered, string? error)
    {
        if (!StopHint.HotkeySupported(_sessionKind))
        {
            HotkeyOk = true;
            HotkeyTooltip = StopHint.WaylandTooltip;
            return;
        }
        HotkeyOk = registered;
        HotkeyTooltip = registered
            ? "Press this anywhere to stop DeskPilot"
            : $"The stop hotkey is not active: {error}. Change it in Settings > Interface.";
        OnPropertyChanged(nameof(SendButtonTooltip));
    }

    // ---------------------------------------------------------------- actions

    private void SendOrStop()
    {
        if (IsBusy) _ = StopAsync();
        else _ = SendAsync();
    }

    /// <summary>Sends the input box text as a new turn. Returns when the turn has ended.</summary>
    public async Task SendAsync()
    {
        var text = InputText.Trim();
        if (text.Length == 0 || IsBusy || _disposed) return;

        InputText = "";
        Conversation.AddLocalUserMessage(text);
        _sending = true;
        _stopRequested = false;
        var cts = new CancellationTokenSource();
        _turnCts = cts;
        UpdateBusy();
        TurnStarted?.Invoke();

        TurnResult? result = null;
        try
        {
            result = await Session.SendAsync(text, cts.Token);
        }
        catch (OperationCanceledException)
        {
            result = new TurnResult(TurnOutcome.Cancelled, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), "The request was cancelled.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Sending a message failed", ex);
            result = new TurnResult(TurnOutcome.Failed, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), ex.Message);
        }
        finally
        {
            _sending = false;
            _stopRequested = false;
            _turnCts = null;
            cts.Dispose();
        }

        // Posted so session events that are still queued land before the footer.
        var final = result;
        _ui.Post(() =>
        {
            if (!Conversation.TurnFooterAdded) Conversation.AddTurnResult(final);
            UpdateBusy();
            TurnEnded?.Invoke();
        });
    }

    /// <summary>Stops the running turn. A second Stop while the first is pending cancels the request outright.</summary>
    public async Task StopAsync()
    {
        if (_stopRequested && _turnCts is { IsCancellationRequested: false } cts)
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { }
            return;
        }
        _stopRequested = IsBusy;
        UpdateBusy();
        try
        {
            await Session.StopAsync("Stopped by user");
        }
        catch (Exception ex)
        {
            AppLog.Error("Stopping failed", ex);
            Conversation.AddStatus($"Could not stop cleanly: {ex.Message}. Press Stop again to cancel the request.", StatusLevel.Warning);
        }
    }

    public async Task NewConversationAsync()
    {
        if (IsBusy) return;
        try
        {
            await Session.NewConversationAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("New conversation failed", ex);
            Conversation.AddStatus($"Could not start a new conversation: {ex.Message}", StatusLevel.Error);
            return;
        }
        Conversation.Clear();
    }

    private void CopyLog()
    {
        if (!HasItems) return;
        CopyRequested?.Invoke(Conversation.ToPlainText());
    }

    /// <summary>Lists models for the active profile from the provider (live query when possible).</summary>
    public async Task RefreshModelsAsync()
    {
        var profile = Store.Current.ActiveProfile;
        if (profile == null) return;
        IsRefreshingModels = true;
        ModelListError = null;
        try
        {
            var key = SecretProtector.ResolveApiKey(profile);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var result = await Catalog.ListModelsAsync(profile, key, timeout.Token);
            if (Store.Current.ActiveProfile?.Id != profile.Id) return; // switched profile meanwhile

            if (result.Models.Count > 0)
            {
                _syncing = true;
                try
                {
                    Models.Clear();
                    foreach (var m in result.Models.Where(m => !string.IsNullOrWhiteSpace(m.Id)).DistinctBy(m => m.Id))
                        Models.Add(ModelOption.From(m));
                    SelectedModel = Models.FirstOrDefault(m => m.Id == ModelText);
                }
                finally
                {
                    _syncing = false;
                }
                OnPropertyChanged(nameof(ModelText));
            }

            if (!string.IsNullOrWhiteSpace(result.Error))
            {
                ModelListError = result.Error;
                Conversation.AddStatus($"Could not list models for {profile.Name}: {result.Error}", StatusLevel.Warning);
            }
            else if (result.Models.Count == 0)
            {
                ModelListError = "the provider returned no models";
                Conversation.AddStatus($"{profile.Name} returned no models. You can still type a model id.", StatusLevel.Warning);
            }
        }
        catch (Exception ex)
        {
            ModelListError = ex.Message;
            Conversation.AddStatus($"Could not list models: {ex.Message}", StatusLevel.Warning);
        }
        finally
        {
            IsRefreshingModels = false;
        }
    }

    /// <summary>Re-reads everything shown from settings (after the settings dialog or any save).</summary>
    public void RefreshFromSettings()
    {
        if (_disposed) return;
        var s = Store.Current;
        var active = s.ActiveProfile;
        _syncing = true;
        try
        {
            if (!Profiles.Select(p => (p.Id, p.Name)).SequenceEqual(s.Profiles.Select(p => (p.Id, p.Name))))
            {
                Profiles.Clear();
                foreach (var p in s.Profiles) Profiles.Add(new ProfileOption(p.Id, p.Name));
            }
            SelectedProfile = Profiles.FirstOrDefault(p => p.Id == active?.Id);

            if (active?.Id != _modelsProfileId)
            {
                _modelsProfileId = active?.Id;
                LoadSuggestedModels(active);
            }
            ModelText = active?.Model ?? "";
            SelectedModel = Models.FirstOrDefault(m => m.Id == ModelText);
            Thinking = active?.Thinking ?? ThinkingMode.Auto;
            Effort = string.IsNullOrWhiteSpace(active?.Effort) ? DefaultEffort : active!.Effort;

            ShowThinking = s.Ui.ShowThinking;
            ShowScreenshots = s.Ui.ShowScreenshots;
            Conversation.ShowThinking = s.Ui.ShowThinking;
            Conversation.KeepImages = s.Ui.ShowScreenshots;
            if (!s.Ui.ShowScreenshots) Conversation.ReleaseImages();
        }
        finally
        {
            _syncing = false;
        }
        // Re-push the model text: replacing the combo's items can blank its text box.
        OnPropertyChanged(nameof(ModelText));
        UpdateStatusBar();
    }

    private void UpdateStatusBar()
    {
        var s = Store.Current;
        var active = s.ActiveProfile;
        var model = string.IsNullOrWhiteSpace(active?.Model) ? "default model" : active!.Model;
        ProfileModelText = active == null ? "No profile" : $"{active.Name} · {model}";

        bool vaultConfigured = !string.IsNullOrWhiteSpace(s.Vault.Path);
        bool vaultExists = vaultConfigured && SafeDirectoryExists(s.Vault.Path);
        VaultOn = s.Vault.Enabled && vaultExists;
        VaultTooltip = !vaultConfigured ? "No notes vault configured (optional, see Settings > Vault)"
            : !vaultExists ? $"The vault folder was not found: {s.Vault.Path}"
            : !s.Vault.Enabled ? $"Vault disabled: {s.Vault.Path}"
            : $"Vault: {s.Vault.Path}{(s.Vault.AllowWrites ? " (writes allowed)" : " (read-only)")}";

        AdminMode = s.Safety.AllowAdmin;
        DryRun = s.Safety.DryRun;
        StopHotkeyText = StopHint.StatusText(_sessionKind, s.Ui.StopHotkey);
        OnPropertyChanged(nameof(SendButtonTooltip));
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception) { return false; }
    }

    private void LoadSuggestedModels(ProviderProfile? profile)
    {
        Models.Clear();
        ModelListError = null;
        if (profile == null) return;
        var preset = ProviderPresets.Find(profile.PresetId);
        var ids = new List<string>();
        if (!string.IsNullOrWhiteSpace(profile.Model)) ids.Add(profile.Model);
        if (preset != null) ids.AddRange(preset.SuggestedModels);
        foreach (var id in ids.Distinct(StringComparer.Ordinal)) Models.Add(ModelOption.FromId(id));
    }

    private void SwitchProfile(string id)
    {
        try
        {
            Store.Update(s => s.ActiveProfileId = id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Conversation.AddStatus($"Could not switch profile: {ex.Message}", StatusLevel.Error);
            RefreshFromSettings();
            return;
        }
        RefreshFromSettings();
        _ = ReloadSessionAsync();
    }

    private void SaveActiveProfile(Action<ProviderProfile> mutate, string what)
    {
        try
        {
            Store.Update(s =>
            {
                var p = s.ActiveProfile;
                if (p != null) mutate(p);
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Conversation.AddStatus($"Could not save the {what}: {ex.Message}", StatusLevel.Error);
            return;
        }
        _ = ReloadSessionAsync();
    }

    private async Task ReloadSessionAsync()
    {
        try
        {
            await Session.ReloadSettingsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Reloading settings failed", ex);
            _ui.Post(() => Conversation.AddStatus($"Could not apply the new settings: {ex.Message}", StatusLevel.Warning));
        }
    }

    private void UpdateBusy()
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(DisplayState));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsStateActive));
        OnPropertyChanged(nameof(IsStateStopping));
        OnPropertyChanged(nameof(IsStateError));
        OnPropertyChanged(nameof(SendButtonText));
        OnPropertyChanged(nameof(SendButtonIcon));
        OnPropertyChanged(nameof(SendButtonTooltip));
        SendOrStopCommand.NotifyCanExecuteChanged();
        NewConversationCommand.NotifyCanExecuteChanged();
        StopCommand.NotifyCanExecuteChanged();
    }

    private void OnSessionEvent(AgentEvent e)
    {
        if (_disposed) return;
        _ui.Post(() =>
        {
            if (!_disposed) Conversation.Apply(e);
        });
    }

    private void OnSessionStateChanged(AgentState state)
    {
        if (_disposed) return;
        _ui.Post(() =>
        {
            if (!_disposed) State = state;
        });
    }

    private void OnStoreChanged(AppSettings _)
    {
        if (_disposed) return;
        _ui.Post(RefreshFromSettings);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Session.EventRaised -= OnSessionEvent;
        Session.StateChanged -= OnSessionStateChanged;
        Store.Changed -= OnStoreChanged;
        try { _turnCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}

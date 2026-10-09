using System.ComponentModel;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.Services;
using DeskPilot.Linux.ViewModels;

namespace DeskPilot.Linux.Views;

/// <summary>
/// Settings editor. Edits a clone of the settings; on Save it calls store.Save(clone) and
/// session.ReloadSettingsAsync() and closes with result true (ShowDialog&lt;bool&gt;).
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore? _store;
    private readonly IAgentSession? _session;
    private readonly SettingsViewModel? _vm;
    private readonly LinuxAutostart? _autostart;
    private bool _saving;
    private bool _saved;
    private bool _resultSet;

    /// <summary>Parameterless constructor for the XAML loader and designer only.</summary>
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector)
        : this(store, session, catalog, detector, LinuxAutostart.Default, LinuxSession.Detect())
    {
    }

    /// <summary>Full constructor: tests pass their own autostart folder and session.</summary>
    internal SettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector,
        LinuxAutostart autostart, LinuxSessionInfo linuxSession) : this()
    {
        _store = store;
        _session = session;
        _autostart = autostart;
        _vm = new SettingsViewModel(store.CloneCurrent(), catalog, detector)
        {
            SettingsFilePath = store.FilePath,
            // Not AppPaths.LogsDirectory: that creates the folder, and showing a path should not.
            LogFolderPath = Path.Combine(AppPaths.LocalRoot, "logs"),
            Session = linuxSession,
        };
        DataContext = _vm;

        _vm.PropertyChanged += Vm_PropertyChanged;
        HotkeyBox.AddHandler(KeyDownEvent, HotkeyBox_KeyDown, RoutingStrategies.Tunnel);
        ModelBox.AddHandler(KeyDownEvent, ModelBox_KeyDown, RoutingStrategies.Tunnel);
        Opened += OnOpened;
        Closed += OnClosed;
    }

    /// <summary>The editor state (exposed for tests and for callers that want to preselect a page).</summary>
    public SettingsViewModel ViewModel => _vm ?? throw new InvalidOperationException("Created by the XAML designer: no settings.");

    /// <summary>Opens the window on a given page, e.g. SettingsViewModel.SectionVault.</summary>
    public void ShowSection(string sectionKey) => _vm?.SelectSection(sectionKey);

    /// <summary>Starts a helper program (xdg-open). Tests replace it so nothing is launched.</summary>
    internal Func<string, IReadOnlyList<string>, bool> StartProcess { get; set; } = SettingsFolderOpener.DefaultStart;

    /// <summary>The message shown in the footer after a save that partly failed, "" otherwise.</summary>
    internal string SaveNoticeText => SaveNotice.Text ?? "";

    private async void OnOpened(object? sender, EventArgs e)
    {
        // Opened fires once per Show; detect only the first time.
        Opened -= OnOpened;
        if (_vm == null) return;
        try
        {
            await _vm.StartAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: environment detection failed: " + ex.Message);
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_vm == null) return;
        _vm.PropertyChanged -= Vm_PropertyChanged;
        _vm.Dispose();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // KeyDown bubbles, so an open drop-down that handles Escape itself never closes the window.
        if (e.Key == Key.Escape && !e.Handled && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            CloseWithResult(_saved);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // After a save that kept the window open (to show a warning), closing it with the title bar
        // must still tell the caller that the settings changed.
        if (!e.Cancel && _saved && !_resultSet && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Dispatcher.UIThread.Post(() => CloseWithResult(true));
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.SelectedSection)) ContentScroll.ScrollToHome();
    }

    // ---- Profiles ----

    private void AddProfile_Click(object? sender, RoutedEventArgs e) => BuildAddMenu().ShowAt(AddProfileButton);

    /// <summary>The Add menu: presets grouped Cloud / Local / Agents, each with its notes as a tooltip.</summary>
    public MenuFlyout BuildAddMenu()
    {
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var group in SettingsViewModel.PresetGroups)
        {
            if (group.Presets.Count == 0) continue;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = group.Name, IsEnabled = false, FontWeight = FontWeight.SemiBold });
            foreach (var preset in group.Presets)
            {
                var item = new MenuItem
                {
                    // A TextBlock header keeps underscores in names from turning into access keys.
                    Header = new TextBlock { Text = preset.DisplayName },
                    Command = _vm?.AddPresetCommand,
                    CommandParameter = preset.Id,
                };
                ToolTip.SetTip(item, preset.Notes);
                AutomationProperties.SetName(item, preset.DisplayName);
                menu.Items.Add(item);
            }
        }
        return menu;
    }

    private void ModelBox_KeyDown(object? sender, KeyEventArgs e)
    {
        // The combo-box keys: Alt+Down or F4 opens the model list.
        if ((e.Key == Key.Down && e.KeyModifiers == KeyModifiers.Alt) || (e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.None))
        {
            e.Handled = true;
            ModelPickButton.Flyout?.ShowAt(ModelPickButton);
        }
    }

    private void ModelFlyout_Opened(object? sender, EventArgs e)
    {
        ModelList.SelectedItem = null;
        Dispatcher.UIThread.Post(() => ModelList.Focus());
    }

    // A pick is a click or Enter, not a selection change, so arrow keys can move through the list first.
    private void ModelList_Tapped(object? sender, TappedEventArgs e) => PickFromModelList();

    private void ModelList_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Space)
        {
            e.Handled = true;
            PickFromModelList();
        }
    }

    /// <summary>
    /// Copies the highlighted model into the profile and closes the list. The list never keeps a selection, so a
    /// refreshed list or another profile can never write an empty or foreign model back.
    /// </summary>
    internal bool PickFromModelList()
    {
        if (ModelList.SelectedItem is not SettingsModelOption picked || ModelList.DataContext is not ProfileViewModel profile) return false;
        profile.PickModel(picked);
        ModelPickButton.Flyout?.Hide();
        ModelList.SelectedItem = null;
        ModelBox.Focus();
        return true;
    }

    private async void BrowseCli_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedProfile is not { } profile) return;
        try
        {
            var provider = StorageProvider;
            if (!provider.CanOpen)
            {
                _vm.ReportError("No file chooser is available here. Type the full path of the program instead.");
                return;
            }
            var options = new FilePickerOpenOptions
            {
                Title = $"Choose the {(profile.CliCommandName.Length > 0 ? profile.CliCommandName : "agent")} program",
                AllowMultiple = false,
            };
            var current = SettingsParsing.ExpandHome(profile.CliPath);
            if (current.Length > 0 && Path.IsPathFullyQualified(current) && Path.GetDirectoryName(current) is { } dir && Directory.Exists(dir))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(dir);
            var files = await provider.OpenFilePickerAsync(options);
            var path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
            if (!string.IsNullOrEmpty(path)) profile.CliPath = path;
        }
        catch (Exception ex)
        {
            Log.Warn("Settings: file chooser failed: " + ex.Message);
            _vm.ReportError("The file chooser could not be opened: " + ex.Message);
        }
    }

    // ---- Vault ----

    private async void BrowseVault_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        var path = await PickFolderAsync(this, "Choose your notes folder (vault)", SettingsParsing.ExpandHome(_vm.VaultPath), _vm.ReportError);
        if (path != null) _vm.VaultPath = path;
    }

    /// <summary>Shows the folder chooser (a desktop portal or GTK dialog); returns null when cancelled or unavailable.</summary>
    internal static async Task<string?> PickFolderAsync(TopLevel owner, string title, string current, Action<string> reportError)
    {
        try
        {
            var provider = owner.StorageProvider;
            if (!provider.CanPickFolder)
            {
                reportError("No folder chooser is available here. Type the full folder path instead.");
                return null;
            }
            var options = new FolderPickerOpenOptions { Title = title, AllowMultiple = false };
            if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(current))
                options.SuggestedStartLocation = await provider.TryGetFolderFromPathAsync(current);
            var folders = await provider.OpenFolderPickerAsync(options);
            var path = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            Log.Warn("Folder chooser failed: " + ex.Message);
            reportError("The folder chooser could not be opened: " + ex.Message);
            return null;
        }
    }

    // ---- Interface ----

    private void HotkeyBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        // Plain Tab moves focus and plain Escape closes the window, as everywhere else.
        if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        _vm.CaptureHotkey(e.KeyModifiers, e.Key);
    }

    private void OpenSettingsFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_store == null || _vm == null) return;
        if (Path.GetDirectoryName(_store.FilePath) is { } dir) OpenFolder(dir);
    }

    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        OpenFolder(_vm.LogFolderPath);
    }

    private void OpenFolder(string dir)
    {
        var error = SettingsFolderOpener.Open(dir, StartProcess);
        if (error != null) _vm?.ReportError(error);
    }

    // ---- Advanced ----

    private async void PreviewPrompt_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null) return;
        string text;
        try
        {
            text = _vm.BuildPreviewPrompt(DateTime.Now);
        }
        catch (Exception ex)
        {
            _vm.ReportError("Could not build the preview: " + ex.Message);
            return;
        }
        await new PromptPreviewWindow(text).ShowDialog(this);
    }

    private void Placeholder_Click(object? sender, RoutedEventArgs e)
    {
        if (_vm == null || sender is not Control { Tag: string placeholder }) return;
        var start = Math.Min(PromptEditor.SelectionStart, PromptEditor.SelectionEnd);
        var length = Math.Abs(PromptEditor.SelectionEnd - PromptEditor.SelectionStart);
        if (length == 0) start = PromptEditor.CaretIndex;
        var caret = _vm.InsertIntoPrompt(placeholder, start, length);
        PromptEditor.Focus();
        PromptEditor.SelectionStart = PromptEditor.SelectionEnd = PromptEditor.CaretIndex = Math.Min(caret, (PromptEditor.Text ?? "").Length);
    }

    // ---- Save / cancel ----

    private async void Save_Click(object? sender, RoutedEventArgs e) => await SaveAsync(closeOnSuccess: true);

    private void Cancel_Click(object? sender, RoutedEventArgs e) => CloseWithResult(_saved);

    /// <summary>
    /// Validates, saves a copy of the edited settings, reloads the session and applies "Start when I log in".
    /// Closes with true when everything worked; when the save worked but a later step did not, the window stays
    /// open with the warning in the footer (and still reports true when it closes).
    /// </summary>
    internal async Task<bool> SaveAsync(bool closeOnSuccess)
    {
        if (_saving || _vm == null || _store == null || _session == null) return false;
        if (!_vm.TryCommit()) return false;

        _saving = true;
        SaveButton.IsEnabled = false;
        SaveNotice.Text = "";
        try
        {
            try
            {
                // Save a copy so later edits in this window can never change the live settings.
                _store.Save(SettingsStore.Clone(_vm.Settings));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _vm.ReportError("Could not save the settings file: " + ex.Message);
                return false;
            }
            _saved = true;

            var warnings = new List<string>();
            try
            {
                await _session.ReloadSettingsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Reloading settings failed", ex);
                warnings.Add("Settings were saved, but the agent could not reload them: " + ex.Message);
            }
            if (ApplyAutostart(_vm.Settings.Ui.StartWithWindows) is { } autostartWarning) warnings.Add(autostartWarning);

            if (warnings.Count > 0)
            {
                SaveNotice.Text = string.Join(" ", warnings);
                CancelButton.Content = "Close";
                return true;
            }
            if (closeOnSuccess) CloseWithResult(true);
            return true;
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private string? ApplyAutostart(bool enabled)
    {
        if (_autostart == null) return null;
        try
        {
            _autostart.Sync(enabled);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException)
        {
            Log.Warn("Start when I log in could not be changed: " + ex.Message);
            return "Settings were saved, but \"Start when I log in\" could not be changed: " + ex.Message;
        }
    }

    private void CloseWithResult(bool result)
    {
        _resultSet = true;
        Close(result);
    }
}

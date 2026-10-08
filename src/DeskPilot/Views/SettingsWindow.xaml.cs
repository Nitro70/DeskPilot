using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using Microsoft.Win32;

namespace DeskPilot.Views;

/// <summary>
/// Modal settings editor. Edits a clone of the settings; on Save it calls store.Save(clone) and
/// session.ReloadSettingsAsync(), and ShowDialog() returns true.
/// </summary>
public partial class SettingsWindow : Window
{
    private const int ErrorCancelled = 1223; // ERROR_CANCELLED: the user said no to the UAC prompt.

    private readonly SettingsStore _store;
    private readonly IAgentSession _session;
    private readonly SettingsViewModel _vm;
    private readonly StartupRegistration _startup;
    private ProfileViewModel? _watchedProfile;
    private bool _syncingPassword;
    private bool _resettingModelSelection;
    private bool _saving;

    public SettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector)
    {
        InitializeComponent();
        NativeUi.Prepare(this);

        _store = store;
        _session = session;
        _startup = StartupRegistration.Default;
        _vm = new SettingsViewModel(store.CloneCurrent(), catalog, detector)
        {
            SettingsFilePath = store.FilePath,
            // Not AppPaths.LogsDirectory: that creates the folder, and showing a path should not.
            LogFolderPath = Path.Combine(AppPaths.LocalRoot, "logs"),
        };
        DataContext = _vm;

        _vm.PropertyChanged += Vm_PropertyChanged;
        WatchProfile(_vm.SelectedProfile);

        Loaded += OnLoaded;
        Closed += OnClosed;
        KeyDown += OnWindowKeyDown;
    }

    /// <summary>The editor state (exposed for tests and for callers that want to preselect a page).</summary>
    public SettingsViewModel ViewModel => _vm;

    /// <summary>Opens the window on a given page, e.g. SettingsViewModel.SectionVault.</summary>
    public void ShowSection(string sectionKey) => _vm.SelectSection(sectionKey);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded can fire again if the window is re-parented; detect only once.
        Loaded -= OnLoaded;
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
        _vm.PropertyChanged -= Vm_PropertyChanged;
        WatchProfile(null);
        _vm.Dispose();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        // KeyDown bubbles, so an open drop-down that handles Escape itself never closes the window.
        if (e.Key == Key.Escape && !e.Handled && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            Close();
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SettingsViewModel.SelectedSection):
                ContentScroll.ScrollToTop();
                break;
            case nameof(SettingsViewModel.SelectedProfile):
                WatchProfile(_vm.SelectedProfile);
                break;
        }
    }

    // ---- API key box: PasswordBox.Password cannot be bound, so it is synced by hand ----

    private void WatchProfile(ProfileViewModel? profile)
    {
        if (_watchedProfile != null) _watchedProfile.PropertyChanged -= Profile_PropertyChanged;
        _watchedProfile = profile;
        if (profile != null) profile.PropertyChanged += Profile_PropertyChanged;
        SyncPasswordBox();
    }

    private void Profile_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileViewModel.NewApiKey)) SyncPasswordBox();
    }

    private void SyncPasswordBox()
    {
        var wanted = _watchedProfile?.NewApiKey ?? "";
        if (ApiKeyBox.Password == wanted) return;
        _syncingPassword = true;
        try
        {
            ApiKeyBox.Password = wanted;
        }
        finally
        {
            _syncingPassword = false;
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingPassword || _vm.SelectedProfile is not { } profile) return;
        profile.NewApiKey = ApiKeyBox.Password;
    }

    // ---- Profiles ----

    /// <summary>
    /// An editable ComboBox sets its Text to "" (and so, through the binding, the profile's model) whenever
    /// its selected item leaves the list: after a model refresh or when another profile is selected. So the
    /// combo never keeps a selection: a pick is copied into the model and the selection is cleared at once.
    /// </summary>
    private void ModelCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_resettingModelSelection || sender is not ComboBox combo) return;
        if (combo.SelectedItem is not SettingsModelOption picked || combo.DataContext is not ProfileViewModel profile) return;
        // The ComboBox copies the selection into Text after this handler returns, so clear the selection
        // just after that (Normal priority runs before any further input can refresh or switch profiles).
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            if (!ReferenceEquals(combo.DataContext, profile) || combo.SelectedItem == null) return;
            _resettingModelSelection = true;
            try
            {
                combo.SelectedIndex = -1;
            }
            finally
            {
                _resettingModelSelection = false;
            }
            profile.Model = picked.Id;
        });
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var menu = BuildAddMenu();
        menu.PlacementTarget = AddProfileButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    /// <summary>The Add menu: presets grouped Cloud / Local / Agents, each with its notes as a tooltip.</summary>
    public ContextMenu BuildAddMenu()
    {
        var menu = new ContextMenu();
        foreach (var group in SettingsViewModel.PresetGroups)
        {
            if (group.Presets.Count == 0) continue;
            if (menu.Items.Count > 0) menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem { Header = group.Name, IsEnabled = false, FontWeight = FontWeights.SemiBold });
            foreach (var preset in group.Presets)
            {
                menu.Items.Add(new MenuItem
                {
                    // A TextBlock header keeps underscores in names from turning into access keys.
                    Header = new TextBlock { Text = preset.DisplayName },
                    ToolTip = preset.Notes,
                    Command = _vm.AddPresetCommand,
                    CommandParameter = preset.Id,
                });
            }
        }
        return menu;
    }

    private void BrowseCli_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.SelectedProfile is not { } profile) return;
        var dialog = new OpenFileDialog
        {
            Title = $"Choose the {(profile.CliCommandName.Length > 0 ? profile.CliCommandName : "agent")} executable",
            Filter = "Programs (*.exe;*.cmd;*.bat)|*.exe;*.cmd;*.bat|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        var current = profile.CliPath.Trim().Trim('"');
        if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(Path.GetDirectoryName(current)))
            dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) == true) profile.CliPath = dialog.FileName;
    }

    // ---- Vault ----

    private void BrowseVault_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose your notes folder (vault)" };
        var current = _vm.VaultPath.Trim().Trim('"');
        if (current.Length > 0 && Path.IsPathFullyQualified(current) && Directory.Exists(current)) dialog.InitialDirectory = current;
        if (dialog.ShowDialog(this) == true) _vm.VaultPath = dialog.FolderName;
    }

    private void VaultQueryBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_vm.VaultTestCommand.CanExecute(null)) _vm.VaultTestCommand.Execute(null);
    }

    // ---- Safety ----

    private async void RestartAsAdmin_Click(object sender, RoutedEventArgs e)
    {
        _vm.AdminRestartStatus = "";
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            _vm.AdminRestartStatus = "DeskPilot.exe could not be found, so it cannot restart itself. Start it with \"Run as administrator\" instead.";
            return;
        }
        if (!await SaveAsync(closeOnSuccess: false))
        {
            _vm.AdminRestartStatus = "Fix the highlighted settings first: they are saved before restarting.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(exe, App.RestartSwitch) { UseShellExecute = true, Verb = "runas" });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            _vm.AdminRestartStatus = "Restart cancelled: Windows did not get permission. Your settings were saved.";
            return;
        }
        catch (Exception ex)
        {
            _vm.AdminRestartStatus = "Could not restart as administrator: " + ex.Message;
            return;
        }
        Log.Info("Restarting elevated at the user's request");
        // Exit through the app so the agent session (and its CLI child process) is shut down cleanly.
        if (Application.Current is App app) await app.ExitAsync(askIfBusy: false);
        else if (Application.Current != null) Application.Current.Shutdown();
        else Close();
    }

    // ---- Interface ----

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key,
        };
        var modifiers = Keyboard.Modifiers;
        // Plain Tab moves focus and plain Escape cancels the dialog, as everywhere else.
        if (modifiers == ModifierKeys.None && key is Key.Tab or Key.Escape) return;
        e.Handled = true;
        _vm.CaptureHotkey(modifiers, key);
    }

    private void OpenSettingsFolder_Click(object sender, RoutedEventArgs e)
    {
        var file = _store.FilePath;
        if (File.Exists(file)) OpenInExplorer($"/select,\"{file}\"");
        else if (Path.GetDirectoryName(file) is { } dir) OpenFolder(dir);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.GetDirectoryName(Log.FilePath);
            if (dir != null) OpenFolder(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.ReportError("Could not open the log folder: " + ex.Message);
        }
    }

    private void OpenFolder(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            OpenInExplorer($"\"{dir}\"");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _vm.ReportError("Could not open the folder: " + ex.Message);
        }
    }

    private void OpenInExplorer(string arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _vm.ReportError("Could not open Explorer: " + ex.Message);
        }
    }

    // ---- Advanced ----

    private void PreviewPrompt_Click(object sender, RoutedEventArgs e)
    {
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
        var preview = new PromptPreviewWindow(text) { Owner = this };
        preview.ShowDialog();
    }

    private void Placeholder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string placeholder }) return;
        var caret = _vm.InsertIntoPrompt(placeholder, PromptEditor.SelectionStart, PromptEditor.SelectionLength);
        PromptEditor.Focus();
        PromptEditor.CaretIndex = Math.Min(caret, PromptEditor.Text.Length);
    }

    // ---- Save / cancel ----

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync(closeOnSuccess: true);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async Task<bool> SaveAsync(bool closeOnSuccess)
    {
        if (_saving) return false;
        if (!_vm.TryCommit()) return false;

        _saving = true;
        SaveButton.IsEnabled = false;
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

            var warning = ApplyStartWithWindows(_vm.Settings.Ui.StartWithWindows);

            try
            {
                await _session.ReloadSettingsAsync();
            }
            catch (Exception ex)
            {
                Log.Error("Reloading settings failed", ex);
                warning ??= "Settings were saved, but the agent could not reload them: " + ex.Message;
            }

            if (warning != null)
                MessageBox.Show(this, warning, "DeskPilot", MessageBoxButton.OK, MessageBoxImage.Warning);

            if (closeOnSuccess) CloseWithResult(true);
            return true;
        }
        finally
        {
            _saving = false;
            SaveButton.IsEnabled = true;
        }
    }

    private string? ApplyStartWithWindows(bool enabled)
    {
        try
        {
            _startup.Sync(enabled);
            return null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Log.Warn("Start with Windows could not be changed: " + ex.Message);
            return "Settings were saved, but \"Start with Windows\" could not be changed: " + ex.Message;
        }
    }

    private void CloseWithResult(bool result)
    {
        try
        {
            DialogResult = result;
        }
        catch (InvalidOperationException)
        {
            // Shown without ShowDialog: there is no dialog result to set.
            Close();
        }
    }
}

using Avalonia.Controls;
using Avalonia.Interactivity;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.ViewModels;

namespace DeskPilot.Linux.Views;

/// <summary>
/// First-run window: what was detected (Claude Code login, session type and missing helper tools, local servers,
/// API keys, Obsidian vaults), optional vault choice, stop controls; saves FirstRunCompleted = true and closes with true.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly SettingsStore? _store;

    /// <summary>Parameterless constructor for the XAML loader and designer only.</summary>
    public WelcomeWindow() => InitializeComponent();

    public WelcomeWindow(SettingsStore store, EnvironmentReport report, IReadOnlyList<string> setupNotes)
        : this(store, report, setupNotes, LinuxSession.Detect())
    {
    }

    /// <summary>Full constructor: tests pass the session to show.</summary>
    internal WelcomeWindow(SettingsStore store, EnvironmentReport report, IReadOnlyList<string> setupNotes, LinuxSessionInfo session) : this()
    {
        _store = store;
        ViewModel = new SettingsWelcomeViewModel(store.Current, report, setupNotes, session);
        DataContext = ViewModel;
    }

    public SettingsWelcomeViewModel? ViewModel { get; }

    /// <summary>The message shown when saving failed, "" otherwise.</summary>
    internal string ErrorMessage => ErrorText.Text ?? "";

    private async void BrowseVault_Click(object? sender, RoutedEventArgs e)
    {
        if (ViewModel == null) return;
        var path = await SettingsWindow.PickFolderAsync(this, "Choose your notes folder (vault)", "", m => ErrorText.Text = m);
        if (path != null) ViewModel.AddCustomVault(path);
    }

    private void GetStarted_Click(object? sender, RoutedEventArgs e) => GetStarted();

    /// <summary>Saves the vault choice and FirstRunCompleted, then closes with true. Returns false when saving failed.</summary>
    internal bool GetStarted()
    {
        if (_store == null || ViewModel == null)
        {
            Close(false);
            return false;
        }
        try
        {
            _store.Update(ViewModel.Apply);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ErrorText.Text = "Could not save your settings: " + ex.Message;
            return false;
        }
        Close(true);
        return true;
    }
}

using System.IO;
using System.Windows;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using Microsoft.Win32;

namespace DeskPilot.Views;

/// <summary>
/// First-run window: shows what was detected (Claude Code login, local servers, API keys, Obsidian vaults),
/// lets the user pick an optional vault folder, explains the stop hotkey, then saves FirstRunCompleted = true.
/// </summary>
public partial class WelcomeWindow : Window
{
    private readonly SettingsStore _store;

    public WelcomeWindow(SettingsStore store, EnvironmentReport report)
    {
        InitializeComponent();
        NativeUi.Prepare(this);

        _store = store;
        ViewModel = new SettingsWelcomeViewModel(store.Current, report);
        DataContext = ViewModel;
    }

    public SettingsWelcomeViewModel ViewModel { get; }

    private void BrowseVault_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose your notes folder (vault)" };
        if (dialog.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            ViewModel.AddCustomVault(dialog.FolderName);
    }

    private void GetStarted_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _store.Update(ViewModel.Apply);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ErrorText.Text = "Could not save your settings: " + ex.Message;
            return;
        }
        try
        {
            DialogResult = true;
        }
        catch (InvalidOperationException)
        {
            // Shown without ShowDialog: there is no dialog result to set.
            Close();
        }
    }
}

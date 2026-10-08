using System.Windows;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Views;

// STUB: owned by the Settings UI agent.
/// <summary>
/// First-run window: shows what was detected (Claude Code login, local servers, API keys, Obsidian vaults),
/// lets the user pick an optional vault folder, explains the stop hotkey, then saves FirstRunCompleted = true.
/// </summary>
public partial class WelcomeWindow : Window
{
    public WelcomeWindow(SettingsStore store, EnvironmentReport report)
    {
        InitializeComponent();
    }
}

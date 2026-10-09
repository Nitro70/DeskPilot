using Avalonia.Controls;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Linux.Views;

// STUB: owned by the Linux settings UI agent.
/// <summary>
/// First-run window: what was detected (Claude Code login, session type and missing helper tools, local servers,
/// API keys, Obsidian vaults), optional vault choice, stop controls; saves FirstRunCompleted = true and closes with true.
/// </summary>
public partial class WelcomeWindow : Window
{
    /// <summary>Parameterless constructor for the XAML loader and designer only.</summary>
    public WelcomeWindow() => InitializeComponent();

    public WelcomeWindow(SettingsStore store, EnvironmentReport report, IReadOnlyList<string> setupNotes) : this() { }
}

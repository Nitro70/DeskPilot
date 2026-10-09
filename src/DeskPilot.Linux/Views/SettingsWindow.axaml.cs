using Avalonia.Controls;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Linux.Views;

// STUB: owned by the Linux settings UI agent.
/// <summary>
/// Settings editor. Edits a clone of the settings; on Save it calls store.Save(clone) and
/// session.ReloadSettingsAsync() and closes with result true (ShowDialog&lt;bool&gt;).
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>Parameterless constructor for the XAML loader and designer only.</summary>
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector) : this() { }
}

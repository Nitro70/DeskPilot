using System.Windows;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Views;

// STUB: owned by the Settings UI agent.
/// <summary>
/// Modal settings editor. Edits a clone of the settings; on Save it calls store.Save(clone) and
/// session.ReloadSettingsAsync(), and ShowDialog() returns true.
/// </summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsStore store, IAgentSession session, IModelCatalog catalog, IEnvironmentDetector detector)
    {
        InitializeComponent();
    }
}

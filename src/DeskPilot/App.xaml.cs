using System.Windows;

namespace DeskPilot;

// STUB: owned by the UI module agent.
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var w = new MainWindow();
        w.Closed += (_, _) => Shutdown();
        w.Show();
    }
}

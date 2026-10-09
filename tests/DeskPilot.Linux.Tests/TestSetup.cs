using System.Runtime.CompilerServices;
using DeskPilot.Core.Settings;

namespace DeskPilot.Linux.Tests;

internal static class TestSetup
{
    /// <summary>Keeps test runs out of the real ~/.config and ~/.local/share DeskPilot folders.</summary>
    [ModuleInitializer]
    internal static void RedirectAppData()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskPilotLinuxTests", Environment.ProcessId.ToString());
        Directory.CreateDirectory(root);
        AppPaths.OverrideRoot(root);
    }
}

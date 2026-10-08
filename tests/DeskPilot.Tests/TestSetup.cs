using System.Runtime.CompilerServices;
using DeskPilot.Core.Settings;

namespace DeskPilot.Tests;

internal static class TestSetup
{
    /// <summary>Keeps test runs out of the real %APPDATA% / %LOCALAPPDATA% DeskPilot folders (logs, temp files, settings).</summary>
    [ModuleInitializer]
    internal static void RedirectAppData()
    {
        var root = Path.Combine(Path.GetTempPath(), "DeskPilotTests", Environment.ProcessId.ToString());
        Directory.CreateDirectory(root);
        AppPaths.OverrideRoot(root);
    }
}

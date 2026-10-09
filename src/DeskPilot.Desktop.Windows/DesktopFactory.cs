using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Windows;

public static class DesktopFactory
{
    /// <summary>The real Windows implementations of every desktop service.</summary>
    public static DesktopServices CreateDefault() => new(
        new WindowsScreenCapture(),
        new WindowsInputSimulator(),
        new WindowsWindowManager(),
        new UiAutomationInspector(),
        new WindowsAppLauncher(),
        new WindowsClipboard(),
        new PowerShellRunner());
}

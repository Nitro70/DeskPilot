using Avalonia;
using Avalonia.Headless;

// TEMPORARY (settings UI branch). The main UI branch adds LinuxUiTestApp.cs with its own
// [assembly: AvaloniaTestApplication(...)], and an assembly may carry only one. When both branches are merged,
// delete this file: LinuxUiSettingsTests does not refer to SettingsUiTestApp and runs under either app, since both
// start the real DeskPilot.Linux.App on the headless platform.
[assembly: AvaloniaTestApplication(typeof(DeskPilot.Linux.Tests.SettingsUiTestApp))]

namespace DeskPilot.Linux.Tests;

/// <summary>Headless Avalonia app for the UI tests: the real App (themes, resources) on a platform that shows nothing.</summary>
public static class SettingsUiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(DeskPilot.Linux.Tests.LinuxUiTestApp))]

namespace DeskPilot.Linux.Tests;

/// <summary>
/// The Avalonia app used by [AvaloniaFact] tests: the real App (theme, resources, styles) on the headless platform
/// with Skia drawing, so nothing is ever shown on a screen and frames can still be rendered.
/// </summary>
public sealed class LinuxUiTestApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        Program.BuildAvaloniaApp()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

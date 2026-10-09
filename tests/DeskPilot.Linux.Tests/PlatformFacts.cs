namespace DeskPilot.Linux.Tests;

// Tests that need a real Linux session declare it, so the same test project builds and runs anywhere:
// on Windows only the platform-neutral tests run; CI runs the X11 job under Xvfb and the Wayland job under headless sway.

/// <summary>Runs only on Linux.</summary>
public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
    }
}

/// <summary>Runs only on Linux with an X11 display (DISPLAY set and no WAYLAND_DISPLAY), e.g. under xvfb-run.</summary>
public sealed class X11FactAttribute : FactAttribute
{
    public X11FactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) ||
                 !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            Skip = "Needs an X11 display (DISPLAY set, no WAYLAND_DISPLAY)";
    }
}

/// <summary>Runs only on Linux inside a Wayland session (WAYLAND_DISPLAY set), e.g. headless sway in CI.</summary>
public sealed class WaylandFactAttribute : FactAttribute
{
    public WaylandFactAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            Skip = "Needs a Wayland session (WAYLAND_DISPLAY)";
    }
}

/// <summary>Theory variant of LinuxFact.</summary>
public sealed class LinuxTheoryAttribute : TheoryAttribute
{
    public LinuxTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux()) Skip = "Linux only";
    }
}

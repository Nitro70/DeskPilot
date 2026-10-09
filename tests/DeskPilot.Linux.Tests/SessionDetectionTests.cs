using DeskPilot.Desktop.Linux;

namespace DeskPilot.Linux.Tests;

public class SessionDetectionTests
{
    private static LinuxSessionInfo Detect(params (string Key, string Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value);
        return LinuxSession.Detect(k => map.TryGetValue(k, out var v) ? v : null);
    }

    [Fact]
    public void Wayland_wins_over_xwayland_display() =>
        Assert.Equal(LinuxSessionKind.Wayland, Detect(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0")).Kind);

    [Fact]
    public void X11_session() =>
        Assert.Equal(LinuxSessionKind.X11, Detect(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":0")).Kind);

    [Fact]
    public void No_session_type_falls_back_to_variables()
    {
        Assert.Equal(LinuxSessionKind.X11, Detect(("DISPLAY", ":99")).Kind);
        Assert.Equal(LinuxSessionKind.Wayland, Detect(("WAYLAND_DISPLAY", "wayland-1")).Kind);
        Assert.Equal(LinuxSessionKind.None, Detect().Kind);
    }

    [Fact]
    public void Override_forces_x11()
    {
        var s = Detect(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("DESKPILOT_SESSION", "x11"));
        Assert.Equal(LinuxSessionKind.X11, s.Kind);
    }

    [Fact]
    public void Desktop_flags()
    {
        Assert.True(Detect(("XDG_CURRENT_DESKTOP", "ubuntu:GNOME")).IsGnome);
        Assert.True(Detect(("XDG_CURRENT_DESKTOP", "KDE")).IsKde);
        Assert.True(Detect(("XDG_CURRENT_DESKTOP", "sway")).IsWlroots);
        Assert.True(Detect(("XDG_CURRENT_DESKTOP", "COSMIC")).IsCosmic);
    }
}

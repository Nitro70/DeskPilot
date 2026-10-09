using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.Services;
using DeskPilot.Desktop.Linux.Wayland;
using DeskPilot.Desktop.Linux.X11;

namespace DeskPilot.Linux.Tests;

public class FactoryTests
{
    private static LinuxSessionInfo Session(LinuxSessionKind kind, string? desktop = null) =>
        new(kind, kind == LinuxSessionKind.X11 ? ":0" : null, kind == LinuxSessionKind.Wayland ? "wayland-0" : null, desktop, "/run/user/1000");

    private static ToolProbe Probe(IEnumerable<string>? commands = null, IEnumerable<string>? libraries = null, IEnumerable<string>? busNames = null,
        IEnumerable<string>? globals = null, bool windowCalls = true, Dictionary<string, string>? env = null)
    {
        var cmd = new HashSet<string>(commands ?? Array.Empty<string>());
        var libs = new HashSet<string>(libraries ?? Array.Empty<string>());
        var bus = new HashSet<string>(busNames ?? Array.Empty<string>());
        var glob = globals?.ToHashSet();
        return new ToolProbe
        {
            HasCommand = cmd.Contains,
            CanLoadLibrary = libs.Contains,
            HasBusName = bus.Contains,
            HasWaylandGlobal = g => glob == null ? null : glob.Contains(g),
            HasGnomeWindowCalls = () => windowCalls,
            Env = k => env != null && env.TryGetValue(k, out var v) ? v : null,
        };
    }

    private static readonly string[] AllCommands = { "grim", "wtype", "swaymsg", "hyprctl", "wl-copy", "wl-paste", "xclip", "kdotool", "ydotool" };
    private static readonly string[] AllBusNames = { "org.a11y.Bus", "org.freedesktop.portal.Desktop" };

    [Fact]
    public void No_session_is_a_readable_error()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LinuxDesktopFactory.CreateFor(Session(LinuxSessionKind.None)));
        Assert.Equal("No graphical session found: DeskPilot needs an X11 or Wayland desktop (DISPLAY or WAYLAND_DISPLAY is not set).", ex.Message);
        Assert.Contains("No graphical session", Assert.Single(LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.None), Probe())));
    }

    [LinuxFact]
    public void Wayland_session_gets_the_wayland_classes()
    {
        // Constructing touches nothing: every Wayland class discovers the compositor on first use.
        var services = LinuxDesktopFactory.CreateFor(Session(LinuxSessionKind.Wayland, "sway"));
        Assert.IsType<WaylandScreenCapture>(services.Screen);
        Assert.IsType<WaylandInputSimulator>(services.Input);
        Assert.IsType<WaylandWindowManager>(services.Windows);
        Assert.IsType<AtSpiInspector>(services.Ui);
        Assert.IsType<LinuxAppLauncher>(services.Launcher);
        Assert.IsType<LinuxClipboard>(services.Clipboard);
        Assert.IsType<LinuxShellRunner>(services.Shell);
    }

    [X11Fact]
    public void X11_session_gets_the_x11_classes()
    {
        var services = LinuxDesktopFactory.CreateFor(Session(LinuxSessionKind.X11, "openbox"));
        Assert.IsType<X11ScreenCapture>(services.Screen);
        Assert.IsType<X11InputSimulator>(services.Input);
        Assert.IsType<X11WindowManager>(services.Windows);
        Assert.IsType<AtSpiInspector>(services.Ui);
        Assert.IsType<LinuxClipboard>(services.Clipboard);
    }

    [Fact]
    public void Nothing_missing_means_no_notes()
    {
        var libs = new[] { "libXtst.so.6", "libXrandr.so.2" };
        Assert.Empty(LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.X11), Probe(AllCommands, libs, AllBusNames)));
        foreach (var desktop in new[] { "sway", "Hyprland", "river", "GNOME", "KDE", "COSMIC" })
            Assert.Empty(LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, desktop),
                Probe(AllCommands, null, AllBusNames, new[] { VirtualPointerDevice.ManagerInterface })));
    }

    [Fact]
    public void X11_notes_name_the_packages()
    {
        var notes = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.X11), Probe());
        Assert.Equal(4, notes.Count);
        Assert.Contains(notes, n => n.Contains("libXtst") && n.Contains("sudo apt install libxtst6") && n.Contains("sudo dnf install libXtst") && n.Contains("sudo pacman -S libxtst"));
        Assert.Contains(notes, n => n.Contains("libXrandr") && n.Contains("libxrandr2"));
        Assert.Contains(notes, n => n.Contains("xclip or xsel"));
        Assert.Contains(notes, n => n.Contains("at-spi2-core"));

        // xsel alone is enough for the clipboard.
        var withXsel = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.X11), Probe(new[] { "xsel" }, new[] { "libXtst.so.6", "libXrandr.so.2" }, AllBusNames));
        Assert.Empty(withXsel);
    }

    [Fact]
    public void Wlroots_notes()
    {
        var notes = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "sway"), Probe(busNames: AllBusNames, globals: Array.Empty<string>()));
        Assert.Contains(notes, n => n.StartsWith("Screenshots on sway need grim") && n.Contains("sudo apt install grim") && n.Contains("sudo pacman -S grim"));
        Assert.Contains(notes, n => n.Contains("wtype"));
        Assert.Contains(notes, n => n.Contains("swaymsg"));
        Assert.Contains(notes, n => n.Contains("virtual pointer") && n.Contains("ydotool"));
        Assert.Contains(notes, n => n.Contains("wl-clipboard"));
        Assert.DoesNotContain(notes, n => n.Contains("xdg-desktop-portal"));

        var hypr = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "Hyprland"), Probe(new[] { "grim", "wtype", "wl-copy", "wl-paste" }, null, AllBusNames));
        Assert.Equal("Listing and focusing windows on Hyprland needs hyprctl, which comes with Hyprland. Make sure it is on PATH.", Assert.Single(hypr));

        var swayWithSocket = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "sway"),
            Probe(new[] { "grim", "wtype", "wl-copy", "wl-paste" }, null, AllBusNames, env: new() { ["SWAYSOCK"] = "/run/user/1000/sway-ipc.sock" }));
        Assert.Empty(swayWithSocket);
    }

    [Fact]
    public void Portal_desktop_notes()
    {
        var gnome = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "ubuntu:GNOME"), Probe(new[] { "wl-copy", "wl-paste" }, windowCalls: false));
        Assert.Contains(gnome, n => n.Contains("xdg-desktop-portal xdg-desktop-portal-gnome") && n.Contains("ydotool"));
        Assert.Contains(gnome, n => n.Contains("Window Calls"));
        Assert.Contains(gnome, n => n.Contains("at-spi2-core"));
        Assert.DoesNotContain(gnome, n => n.Contains("grim"));

        var kde = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "KDE"), Probe(new[] { "wl-copy", "wl-paste" }, null, AllBusNames));
        Assert.Equal("kdotool", Assert.Single(kde).Split(' ')[1]);

        var cosmic = LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, "COSMIC"), Probe(new[] { "wl-copy" }, null, new[] { "org.a11y.Bus" }));
        Assert.Contains(cosmic, n => n.Contains("xdg-desktop-portal-cosmic"));
        Assert.Contains(cosmic, n => n.Contains("wl-clipboard"));
    }

    [Fact]
    public void Notes_never_use_long_dashes()
    {
        var all = new List<string>();
        all.AddRange(LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.X11), Probe()));
        foreach (var d in new[] { "sway", "Hyprland", "GNOME", "KDE", "COSMIC", "river" })
            all.AddRange(LinuxDesktopFactory.DescribeMissingTools(Session(LinuxSessionKind.Wayland, d), Probe(globals: Array.Empty<string>(), windowCalls: false)));
        Assert.All(all, n => Assert.DoesNotContain((char)0x2014, n));
        Assert.All(all, n => Assert.DoesNotContain((char)0x2013, n));
    }

    [WaylandFact]
    public void Real_wayland_session_reports_what_it_lacks()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION") == "wayland", "Needs the CI headless sway session");
        var session = LinuxSession.Detect();
        var notes = LinuxDesktopFactory.DescribeMissingTools(session);
        // The CI image has grim, wtype, swaymsg and wl-clipboard; only the accessibility bus may be absent there.
        Assert.All(notes, n => Assert.Contains("at-spi2-core", n));
    }

    [WaylandFact]
    public void Default_services_in_the_real_wayland_session_share_one_layout()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION") == "wayland", "Needs the CI headless sway session");
        var services = LinuxDesktopFactory.CreateDefault();
        Assert.IsType<WaylandScreenCapture>(services.Screen);
        var monitor = Assert.Single(services.Screen.GetMonitors());
        Assert.Equal(new DeskPilot.Core.Abstractions.ScreenRect(0, 0, 1920, 1080), monitor.Bounds);
        Assert.NotNull(services.Windows.ListWindows());
        var frame = services.Screen.Capture(new DeskPilot.Core.Abstractions.CaptureRequest(monitor.Bounds, 640, 360,
            DeskPilot.Core.Abstractions.ImageFormatKind.Jpeg, 80, true, 0));
        Assert.Equal((640, 360), (frame.Width, frame.Height));
    }
}

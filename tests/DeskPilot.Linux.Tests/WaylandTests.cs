using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.Wayland;
using SkiaSharp;
using Tmds.DBus.Protocol;

namespace DeskPilot.Linux.Tests;

// ================================================================================================ fakes

internal sealed class FakeRunner : ICommandRunner
{
    public HashSet<string> Installed { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, Func<IReadOnlyList<string>, CommandResult>> Handlers { get; } = new(StringComparer.Ordinal);
    public List<(string Command, string[] Args, byte[]? Stdin)> Calls { get; } = new();
    public List<FakeProcess> Started { get; } = new();

    public FakeRunner(params string[] installed)
    {
        foreach (var c in installed) Installed.Add(c);
    }

    public FakeRunner Returns(string command, string stdout, int exitCode = 0)
    {
        Installed.Add(command);
        Handlers[command] = _ => new CommandResult(exitCode, Encoding.UTF8.GetBytes(stdout), "", false);
        return this;
    }

    public string? Find(string command) => Installed.Contains(command) ? "/usr/bin/" + command : null;

    public CommandResult Run(string command, IReadOnlyList<string> args, int timeoutMs, byte[]? stdin = null)
    {
        Calls.Add((command, args.ToArray(), stdin));
        if (!Installed.Contains(command)) return CommandResult.NotFound(command);
        return Handlers.TryGetValue(command, out var h) ? h(args) : new CommandResult(0, Array.Empty<byte>(), "", false);
    }

    public IRunningProcess? Start(string command, IReadOnlyList<string> args)
    {
        Calls.Add((command, args.ToArray(), null));
        if (!Installed.Contains(command)) return null;
        var p = new FakeProcess();
        Started.Add(p);
        return p;
    }

    public sealed class FakeProcess : IRunningProcess
    {
        public bool Stopped { get; private set; }
        public bool HasExited => Stopped;
        public void Stop() => Stopped = true;
        public void Dispose() => Stopped = true;
    }
}

internal sealed class FakePortalBus : IPortalBus
{
    public List<PortalCall> Requests { get; } = new();
    public List<PortalCall> Calls { get; } = new();
    public uint Version { get; set; } = 2;
    public Func<PortalCall, PortalResponse> Respond { get; set; } = _ => new PortalResponse(0, new Dictionary<string, object?>());

    public Task<string> GetUniqueNameAsync(CancellationToken ct) => Task.FromResult(":1.42");

    public Task<PortalResponse> RequestAsync(PortalCall call, TimeSpan timeout, CancellationToken ct)
    {
        Requests.Add(call);
        return Task.FromResult(Respond(call));
    }

    public Task CallAsync(PortalCall call, CancellationToken ct)
    {
        Calls.Add(call);
        return Task.CompletedTask;
    }

    public Task<uint> GetVersionAsync(string iface, CancellationToken ct) => Task.FromResult(Version);
}

internal sealed class RecordingPointer : IPointerBackend
{
    public List<string> Log { get; } = new();
    public string Name => "fake";
    public void Move(double lx, double ly, WaylandLayout layout) => Log.Add($"move {lx:0.##},{ly:0.##}");
    public void Button(MouseButton button, bool pressed) => Log.Add($"{(pressed ? "down" : "up")} {button}");
    public void Scroll(int dx, int dy) => Log.Add($"scroll {dx},{dy}");
    public void Dispose() { }
}

internal sealed class RecordingKeyboard : IKeyboardBackend
{
    public List<string> Log { get; } = new();
    public string Name => "fake";
    public void Type(string text, int delay) => Log.Add("type " + text);
    public void Combo(IReadOnlyList<string> modifiers, string? key) => Log.Add("combo " + string.Join("+", modifiers.Append(key ?? "")));
    public void KeyDown(string key) => Log.Add("down " + key);
    public void KeyUp(string key) => Log.Add("up " + key);
    public void ReleaseAll() => Log.Add("releaseall");
    public void Dispose() { }
}

internal static class WaylandSamples
{
    public static LinuxSessionInfo Session(string desktop = "sway") =>
        new(LinuxSessionKind.Wayland, null, "wayland-1", desktop, "/run/user/1000");

    public static WaylandContext Context(FakeRunner runner, string desktop = "sway", params (string Key, string Value)[] env)
    {
        var map = env.ToDictionary(e => e.Key, e => e.Value);
        return new WaylandContext(Session(desktop), runner, k => map.TryGetValue(k, out var v) ? v : null);
    }

    public const string SwayOutputs = """
        [{"id":3,"type":"output","name":"HEADLESS-1","active":true,"primary":false,"focused":true,"scale":1.0,
          "rect":{"x":0,"y":0,"width":1920,"height":1080},"current_mode":{"width":1920,"height":1080,"refresh":60000}},
         {"id":9,"type":"output","name":"DP-2","active":true,"focused":false,"scale":2.0,
          "rect":{"x":1920,"y":0,"width":1280,"height":720},"current_mode":{"width":2560,"height":1440,"refresh":60000}},
         {"id":10,"type":"output","name":"OFF-1","active":false,"rect":{"x":0,"y":0,"width":0,"height":0}}]
        """;

    public const string SwayTree = """
        {"id":1,"type":"root","name":"root","focus":[3],"nodes":[
          {"id":2,"type":"output","name":"__i3","focus":[5],"nodes":[
            {"id":5,"type":"workspace","name":"__i3_scratch","focus":[40],"nodes":[],"floating_nodes":[
              {"id":40,"type":"floating_con","name":"Scratch","app_id":"foot","pid":400,"visible":false,"focused":false,
               "rect":{"x":10,"y":10,"width":300,"height":200},"nodes":[],"floating_nodes":[],"focus":[]}]}]},
          {"id":3,"type":"output","name":"HEADLESS-1","focus":[4],"nodes":[
            {"id":4,"type":"workspace","name":"1","focus":[30,21,20],"nodes":[
              {"id":20,"type":"con","name":"Left editor","app_id":"org.gnome.TextEditor","pid":200,"visible":true,"focused":false,
               "rect":{"x":24,"y":24,"width":936,"height":1032},"nodes":[],"floating_nodes":[],"focus":[]},
              {"id":21,"type":"con","name":"Right terminal","app_id":null,"window_properties":{"class":"XTerm"},"pid":210,"visible":true,"focused":true,
               "rect":{"x":960,"y":24,"width":936,"height":1032},"nodes":[],"floating_nodes":[],"focus":[]}],
             "floating_nodes":[
              {"id":30,"type":"floating_con","name":"Dialog","app_id":"zenity","pid":300,"visible":true,"focused":false,
               "rect":{"x":700,"y":400,"width":500,"height":300},"nodes":[],"floating_nodes":[],"focus":[]}]}]}]}
        """;
}

// ================================================================================================ pure logic

public class WaylandLayoutTests
{
    [Fact]
    public void Sway_outputs_build_a_physical_layout_with_the_highest_scale()
    {
        var outputs = WaylandOutputParsers.ParseSway(WaylandSamples.SwayOutputs);
        Assert.Equal(2, outputs.Count);
        var layout = new WaylandLayout(outputs, "sway");
        Assert.Equal(2.0, layout.Factor);
        Assert.Equal(new ScreenRect(0, 0, 3840, 2160), layout.Monitors[0].Bounds);
        Assert.Equal(new ScreenRect(3840, 0, 2560, 1440), layout.Monitors[1].Bounds);
        Assert.Equal(new ScreenRect(0, 0, 6400, 2160), layout.VirtualScreen);
        Assert.True(layout.Monitors[0].IsPrimary);
        Assert.Equal(2.0, layout.Monitors[1].Scale);
        Assert.Equal((1000.0, 500.0), layout.ToLogical(2000, 1000));
        Assert.Equal(new ScreenPoint(2000, 1000), layout.ToPhysical(1000, 500));
        Assert.Equal((0.0, 0.0, 3200.0, 1080.0), layout.LogicalBox);
    }

    [Fact]
    public void Single_scale_layout_is_the_pixel_grid()
    {
        var layout = new WaylandLayout(new[] { new WaylandOutput("HEADLESS-1", 0, 0, 1920, 1080, 1.0) }, "test");
        Assert.Equal(1.0, layout.Factor);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), layout.VirtualScreen);
        Assert.Equal(new ScreenPoint(960, 540), layout.PrimaryCenter);
    }

    [Fact]
    public void Negative_positions_keep_the_layout_box()
    {
        var layout = new WaylandLayout(new[]
        {
            new WaylandOutput("A", -1280, 0, 1280, 1024, 1.0),
            new WaylandOutput("B", 0, 0, 1920, 1080, 1.0),
        }, "test");
        Assert.Equal((-1280.0, 0.0, 3200.0, 1080.0), layout.LogicalBox);
        Assert.Equal(new ScreenRect(-1280, 0, 3200, 1080), layout.VirtualScreen);
        Assert.Equal("B", layout.Monitors.Single(m => m.IsPrimary).DeviceName);
    }

    [Fact]
    public void Hyprland_monitors_are_scaled_to_logical_size()
    {
        const string json = """
            [{"id":0,"name":"eDP-1","width":2560,"height":1600,"x":0,"y":0,"scale":1.25,"transform":0,"focused":true,"disabled":false},
             {"id":1,"name":"DP-1","width":1080,"height":1920,"x":2048,"y":0,"scale":1.0,"transform":1,"focused":false,"disabled":false},
             {"id":2,"name":"OFF","width":1920,"height":1080,"x":0,"y":0,"scale":1.0,"disabled":true}]
            """;
        var o = WaylandOutputParsers.ParseHyprland(json);
        Assert.Equal(2, o.Count);
        Assert.Equal(("eDP-1", 2048.0, 1280.0, 1.25), (o[0].Name, o[0].Width, o[0].Height, o[0].Scale));
        Assert.Equal((1920.0, 1080.0), (o[1].Width, o[1].Height));
        Assert.True(o[0].Focused);
    }

    [Fact]
    public void Kscreen_doctor_json_is_parsed()
    {
        const string json = """
            {"outputs":[{"id":1,"name":"eDP-1","enabled":true,"connected":true,"priority":1,"pos":{"x":0,"y":0},"size":{"width":2880,"height":1800},"scale":2,"rotation":1},
                        {"id":2,"name":"HDMI-A-1","enabled":true,"connected":true,"priority":2,"pos":{"x":1440,"y":0},"size":{"width":1920,"height":1080},"scale":1,"rotation":8},
                        {"id":3,"name":"DP-1","enabled":false,"connected":true,"pos":{"x":0,"y":0},"size":{"width":1920,"height":1080},"scale":1}]}
            """;
        var o = WaylandOutputParsers.ParseKscreen(json);
        Assert.Equal(2, o.Count);
        Assert.Equal((1440.0, 900.0, true), (o[0].Width, o[0].Height, o[0].Primary));
        Assert.Equal((1080.0, 1920.0, false), (o[1].Width, o[1].Height, o[1].Primary));
    }

    [Fact]
    public void Wlr_randr_text_is_parsed()
    {
        const string text = "HEADLESS-1 \"Headless output 1\"\n  Enabled: yes\n  Modes:\n    1920x1080 px, 60.000000 Hz (current)\n" +
                            "  Position: 0,0\n  Transform: normal\n  Scale: 1.000000\n" +
                            "DP-1 \"Some Monitor\"\n  Physical size: 600x340 mm\n  Enabled: yes\n  Modes:\n    3840x2160 px, 30.000000 Hz\n    2560x1440 px, 59.951000 Hz (preferred, current)\n" +
                            "  Position: 1920,0\n  Transform: 90\n  Scale: 2.000000\n" +
                            "HDMI-A-1 \"Off\"\n  Enabled: no\n  Modes:\n    1920x1080 px, 60.000000 Hz\n";
        var o = WaylandOutputParsers.ParseWlrRandr(text);
        Assert.Equal(2, o.Count);
        Assert.Equal(("HEADLESS-1", 0.0, 0.0, 1920.0, 1080.0, 1.0), (o[0].Name, o[0].X, o[0].Y, o[0].Width, o[0].Height, o[0].Scale));
        Assert.Equal(("DP-1", 1920.0, 720.0, 1280.0, 2.0), (o[1].Name, o[1].X, o[1].Width, o[1].Height, o[1].Scale));
    }

    [Fact]
    public void Protocol_outputs_derive_fractional_scale()
    {
        var o = WaylandOutputQuery.Combine("eDP-1", 0, 0, 0, 2560, 1600, 2, 0, 0, 2048, 1280);
        Assert.Equal((2048, 1280, 1.25), (o.LogicalWidth, o.LogicalHeight, o.Scale));
        var rotated = WaylandOutputQuery.Combine(null, 100, 0, 1, 1920, 1080, 1, null, null, null, null);
        Assert.Equal((100, 1080, 1920, 1.0), (rotated.LogicalX, rotated.LogicalWidth, rotated.LogicalHeight, rotated.Scale));
    }

    [Fact]
    public void Unreadable_listings_give_no_outputs()
    {
        Assert.Empty(WaylandOutputParsers.ParseSway("not json"));
        Assert.Empty(WaylandOutputParsers.ParseHyprland("{}"));
        Assert.Empty(WaylandOutputParsers.ParseKscreen("[]"));
        Assert.Empty(WaylandOutputParsers.ParseWlrRandr(""));
    }

    [Fact]
    public void Desktop_detection_prefers_the_session_name_then_variables()
    {
        WaylandDesktop D(string? desktop, params (string, string)[] env)
        {
            var map = env.ToDictionary(e => e.Item1, e => e.Item2);
            return WaylandContext.DetectDesktop(new LinuxSessionInfo(LinuxSessionKind.Wayland, null, "wayland-0", desktop, "/run/user/1"), k => map.TryGetValue(k, out var v) ? v : null);
        }
        Assert.Equal(WaylandDesktop.Sway, D("sway"));
        Assert.Equal(WaylandDesktop.Hyprland, D("Hyprland"));
        Assert.Equal(WaylandDesktop.Wlroots, D("river"));
        Assert.Equal(WaylandDesktop.Gnome, D("ubuntu:GNOME"));
        Assert.Equal(WaylandDesktop.Kde, D("KDE"));
        Assert.Equal(WaylandDesktop.Cosmic, D("COSMIC"));
        Assert.Equal(WaylandDesktop.Hyprland, D(null, ("HYPRLAND_INSTANCE_SIGNATURE", "abc")));
        Assert.Equal(WaylandDesktop.Sway, D(null, ("SWAYSOCK", "/run/user/1/sway-ipc.sock")));
        Assert.Equal(WaylandDesktop.Unknown, D(null));
    }

    [Fact]
    public void Socket_path_resolution()
    {
        Assert.Equal("/run/user/1000/wayland-1".Replace('/', Path.DirectorySeparatorChar), WaylandConnection.ResolveSocketPath("wayland-1", "/run/user/1000")?.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal("/tmp/custom-socket", WaylandConnection.ResolveSocketPath("/tmp/custom-socket", null));
        Assert.EndsWith("wayland-0", WaylandConnection.ResolveSocketPath(null, "/run/user/1000"));
        Assert.Null(WaylandConnection.ResolveSocketPath("wayland-0", null));
    }

    [Fact]
    public void Context_reads_sway_outputs_through_swaymsg_when_the_socket_is_missing()
    {
        var runner = new FakeRunner().Returns("swaymsg", WaylandSamples.SwayOutputs);
        var ctx = WaylandSamples.Context(runner, "sway", ("SWAYSOCK", "/nonexistent/sway.sock"));
        var layout = ctx.TryGetLayout();
        Assert.NotNull(layout);
        Assert.Equal("sway", layout!.Source);
        Assert.Contains(runner.Calls, c => c.Command == "swaymsg" && c.Args.SequenceEqual(new[] { "-r", "-t", "get_outputs" }));
    }

    [Fact]
    public void Hyprland_cursor_position_is_parsed()
    {
        Assert.True(WaylandContext.TryParseCursorPos("1012, 433\n", out var x, out var y));
        Assert.Equal((1012.0, 433.0), (x, y));
        Assert.False(WaylandContext.TryParseCursorPos("error", out _, out _));
    }
}

public class WaylandWireTests
{
    [Fact]
    public void Strings_are_length_prefixed_nul_terminated_and_padded()
    {
        var bytes = new WireWriter().UInt(7).String("wl_seat").Fixed(1.5).Int(-2).ToArray();
        Assert.Equal(4 + 4 + 8 + 4 + 4, bytes.Length);
        var r = new WireReader(bytes);
        Assert.Equal(7u, r.UInt());
        Assert.Equal("wl_seat", r.String());
        Assert.Equal(1.5, r.Fixed());
        Assert.Equal(-2, r.Int());
    }

    [Fact]
    public void Null_string_is_zero_length()
    {
        var bytes = new WireWriter().String(null).UInt(1).ToArray();
        var r = new WireReader(bytes);
        Assert.Null(r.String());
        Assert.Equal(1u, r.UInt());
    }

    [Fact]
    public void Virtual_pointer_absolute_arguments_use_sub_pixels_and_stay_in_range()
    {
        Assert.Equal((15360u, 8640u, 30720u, 17280u), VirtualPointerDevice.AbsoluteArgs(960, 540, 1920, 1080));
        var edge = VirtualPointerDevice.AbsoluteArgs(5000, -10, 1920, 1080);
        Assert.Equal(30719u, edge.X);
        Assert.Equal(0u, edge.Y);
    }

    [Fact]
    public void Sway_ipc_messages_have_the_i3_header()
    {
        var msg = SwayIpc.Encode(SwayIpc.GetTree, "");
        Assert.Equal("i3-ipc", Encoding.ASCII.GetString(msg, 0, 6));
        Assert.Equal(0, BitConverter.ToInt32(msg, 6));
        Assert.Equal(4, BitConverter.ToInt32(msg, 10));
        Assert.True(SwayIpc.CommandSucceeded("""[{"success":true},{"success":true}]""", out _));
        Assert.False(SwayIpc.CommandSucceeded("""[{"success":false,"error":"No matching node"}]""", out var error));
        Assert.Equal("No matching node", error);
    }
}

public class WaylandKeyTests
{
    [Theory]
    [InlineData("enter", "Return", 0xff0d, 28)]
    [InlineData("Return", "Return", 0xff0d, 28)]
    [InlineData("esc", "Escape", 0xff1b, 1)]
    [InlineData("pgdn", "Next", 0xff56, 109)]
    [InlineData("f12", "F12", 0xffc9, 88)]
    [InlineData("f13", "F13", 0xffca, 183)]
    [InlineData("numpad7", "KP_7", 0xffb7, 71)]
    [InlineData("volumeup", "XF86AudioRaiseVolume", 0x1008ff13, 115)]
    [InlineData("a", "a", 0x61, 30)]
    [InlineData("A", "a", 0x61, 30)]
    [InlineData("5", "5", 0x35, 6)]
    [InlineData("plus", "U002B", 0x2b, 13)]
    [InlineData("slash", "U002F", 0x2f, 53)]
    public void Named_keys_map_to_keysyms_and_evdev_codes(string name, string keysymName, int keysym, int evdev)
    {
        var k = XkbKeys.Resolve(name);
        Assert.Equal((keysymName, keysym, evdev), (k.KeysymName, k.Keysym, k.EvdevCode));
    }

    [Fact]
    public void Characters_beyond_latin1_use_the_unicode_keysym_range()
    {
        Assert.Equal(0xe9, XkbKeys.KeysymForCodePoint('é'));
        Assert.Equal(0x010020ac, XkbKeys.KeysymForCodePoint('€'));
        Assert.Equal(0x01006587, XkbKeys.KeysymForCodePoint('文'));
        Assert.Equal(0x0101f600, XkbKeys.KeysymForCodePoint(0x1f600));
        Assert.Equal(0xff0d, XkbKeys.KeysymForCodePoint('\n'));
        Assert.Equal("U20AC", XkbKeys.KeysymNameForCodePoint('€'));
        Assert.Equal(0x010020ac, XkbKeys.Resolve("€").Keysym);
        Assert.True(XkbKeys.Modifier("win").IsModifier);
        Assert.False(XkbKeys.TryResolve("notakey", out _, out var error));
        Assert.Contains("Unknown key", error);
    }

    [Fact]
    public void Code_points_fold_line_endings_and_keep_surrogate_pairs()
    {
        Assert.Equal(new[] { 'a', '\n', 'b', '\n', 0x1f600 }, XkbKeys.CodePoints("a\r\nb\r\U0001F600").ToArray());
    }

    [Fact]
    public void Wtype_arguments()
    {
        // Every run starts with a short sleep so apps can bind the new keyboard before the first key.
        Assert.Equal(new[] { "-s", "50", "--", "-héllo\nwörld 日本" }, WtypeKeyboardBackend.TypeArgs("-héllo\r\nwörld 日本", 0));
        Assert.Equal(new[] { "-s", "50", "-d", "12", "--", "x" }, WtypeKeyboardBackend.TypeArgs("x\0", 12));
        Assert.Null(WtypeKeyboardBackend.TypeArgs("\0\u0001", 0));
        Assert.Equal(new[] { "-s", "50", "-M", "ctrl", "-M", "shift", "-k", "t", "-m", "shift", "-m", "ctrl" }, WtypeKeyboardBackend.ComboArgs(new[] { "ctrl", "shift" }, "t"));
        Assert.Equal(new[] { "-s", "50", "-k", "Return" }, WtypeKeyboardBackend.ComboArgs(Array.Empty<string>(), "enter"));
        Assert.Equal(new[] { "-s", "50", "-k", "Super_L" }, WtypeKeyboardBackend.ComboArgs(new[] { "win" }, null));
        Assert.Equal(new[] { "-s", "50", "-M", "ctrl", "-k", "Alt_L", "-m", "ctrl" }, WtypeKeyboardBackend.ComboArgs(new[] { "ctrl", "alt" }, null));
        Assert.Equal(new[] { "-s", "50", "-M", "logo", "-k", "U002B", "-m", "logo" }, WtypeKeyboardBackend.ComboArgs(new[] { "win" }, "+"));
        Assert.Equal(new[] { "-s", "50", "-M", "shift", "-s", "3600000" }, WtypeKeyboardBackend.HoldArgs("shift"));
        Assert.Equal(new[] { "-s", "50", "-P", "Delete", "-s", "3600000" }, WtypeKeyboardBackend.HoldArgs("delete"));
    }

    [Fact]
    public void Wtype_holds_keys_in_a_process_until_key_up()
    {
        var runner = new FakeRunner("wtype");
        var kb = new WtypeKeyboardBackend(runner);
        kb.KeyDown("shift");
        kb.KeyDown("shift");
        Assert.Single(runner.Started);
        kb.KeyUp("shift");
        Assert.True(runner.Started[0].Stopped);
        Assert.Equal(new[] { "-m", "shift" }, runner.Calls.Last().Args);
        kb.KeyDown("x");
        kb.ReleaseAll();
        Assert.True(runner.Started[1].Stopped);
    }

    [Fact]
    public void Ydotool_and_dotool_arguments()
    {
        Assert.Equal(new[] { "key", "29:1", "42:1", "20:1", "20:0", "42:0", "29:0" }, YdotoolKeyboardBackend.ComboArgs(new[] { "ctrl", "shift" }, "t"));
        Assert.Equal("0x40", YdotoolPointerBackend.ButtonCode(MouseButton.Left, true));
        Assert.Equal("0x81", YdotoolPointerBackend.ButtonCode(MouseButton.Right, false));
        Assert.Equal("0x42", YdotoolPointerBackend.ButtonCode(MouseButton.Middle, true));
        Assert.Equal("k:29+k:46", DotoolKeyboardBackend.KeySpec(new[] { "ctrl" }, "c"));
        Assert.True(XkbKeys.TryAsciiEvdev('A', out var code, out var shift));
        Assert.Equal((30, true), (code, shift));
    }

    [Fact]
    public void Portal_keysym_sequence_for_a_combo()
    {
        var seq = PortalKeyboardBackend.ComboSequence(new[] { "ctrl", "alt" }, "delete");
        Assert.Equal(new[] { (0xffe3, true), (0xffe9, true), (0xffff, true), (0xffff, false), (0xffe9, false), (0xffe3, false) }, seq);
    }

    [Fact]
    public void Utf8_locale_detection()
    {
        Assert.True(ProcessCommandRunner.HasUtf8Locale(n => n == "LANG" ? "en_US.UTF-8" : null));
        Assert.True(ProcessCommandRunner.HasUtf8Locale(n => n == "LC_CTYPE" ? "C.utf8" : n == "LANG" ? "C" : null));
        Assert.False(ProcessCommandRunner.HasUtf8Locale(n => n == "LC_ALL" ? "C" : n == "LANG" ? "en_US.UTF-8" : null));
        Assert.False(ProcessCommandRunner.HasUtf8Locale(_ => null));
    }
}

[Collection("Wayland cursor tracker")]
public class WaylandInputLogicTests
{
    private static WaylandLayout Layout(double scale = 1.0) =>
        new(new[] { new WaylandOutput("A", 0, 0, 1920, 1080, scale) }, "test");

    [Fact]
    public void Sway_seat_commands()
    {
        var layout = new WaylandLayout(new[] { new WaylandOutput("L", -1280, 0, 1280, 1024, 1), new WaylandOutput("M", 0, 0, 1920, 1080, 1) }, "t");
        Assert.Equal("seat - cursor set 1380.5 20", SwayPointerBackend.MoveCommand(100.5, 20, layout));
        Assert.Equal("seat - cursor press button1", SwayPointerBackend.ButtonCommand(MouseButton.Left, true));
        Assert.Equal("seat - cursor release button3", SwayPointerBackend.ButtonCommand(MouseButton.Right, false));
        Assert.Equal("seat - cursor press button2", SwayPointerBackend.ButtonCommand(MouseButton.Middle, true));
        Assert.Equal(new[] { "seat - cursor press button5", "seat - cursor press button5", "seat - cursor press button6" }, SwayPointerBackend.ScrollCommands(-1, 2));
    }

    [Fact]
    public void Route_orders_per_desktop()
    {
        Assert.Equal(new[] { "virtual-pointer", "sway", "wlrctl", "ydotool", "dotool" }, WaylandInputSimulator.PointerOrder(WaylandDesktop.Sway, true));
        Assert.Equal(new[] { "hyprland", "ydotool", "dotool" }, WaylandInputSimulator.PointerOrder(WaylandDesktop.Hyprland, false));
        Assert.Equal(new[] { "portal", "ydotool", "dotool" }, WaylandInputSimulator.PointerOrder(WaylandDesktop.Gnome, false));
        Assert.Equal(new[] { "wtype", "ydotool", "dotool" }, WaylandInputSimulator.KeyboardOrder(WaylandDesktop.Sway, true));
        Assert.Equal(new[] { "portal", "ydotool", "dotool" }, WaylandInputSimulator.KeyboardOrder(WaylandDesktop.Kde, false));
        Assert.Equal(new[] { "grim", "portal", "gnome-screenshot" }, WaylandScreenCapture.RouteOrder(WaylandDesktop.Sway, true, true));
        Assert.Equal(new[] { "spectacle", "portal", "gnome-screenshot" }, WaylandScreenCapture.RouteOrder(WaylandDesktop.Kde, false, true));
        Assert.Equal(new[] { "portal", "gnome-screenshot" }, WaylandScreenCapture.RouteOrder(WaylandDesktop.Gnome, false, false));
        Assert.Equal(new[] { "grim", "portal", "gnome-screenshot" }, WaylandScreenCapture.RouteOrder(WaylandDesktop.Cosmic, true, true));
    }

    [Fact]
    public void Simulator_converts_to_logical_and_tracks_what_it_holds()
    {
        var runner = new FakeRunner().Returns("swaymsg", """[{"name":"A","active":true,"scale":2.0,"rect":{"x":0,"y":0,"width":1920,"height":1080}}]""");
        var ctx = WaylandSamples.Context(runner, "sway");
        var pointer = new RecordingPointer();
        var keyboard = new RecordingKeyboard();
        var sim = new WaylandInputSimulator(ctx, pointer, keyboard);

        sim.MoveMouse(2000, 1000);
        Assert.Equal("move 1000,500", pointer.Log.Last());
        Assert.Equal(new ScreenPoint(2000, 1000), sim.GetCursorPosition());

        sim.MouseDown(MouseButton.Left);
        sim.KeyDown("Shift");
        Assert.Contains(MouseButton.Left, sim.PressedButtons);
        Assert.Contains("shift", sim.PressedKeys);
        sim.ReleaseAll();
        Assert.Empty(sim.PressedButtons);
        Assert.Empty(sim.PressedKeys);
        Assert.Contains("up Left", pointer.Log);
        Assert.Contains("releaseall", keyboard.Log);

        sim.Click(MouseButton.Right, 2);
        Assert.Equal(new[] { "down Right", "up Right", "down Right", "up Right" }, pointer.Log.TakeLast(4));
        sim.PressCombo(KeyCombo.Parse("ctrl+shift+t"));
        Assert.Equal("combo ctrl+shift+t", keyboard.Log.Last());
        Assert.Throws<ArgumentException>(() => sim.PressCombo(KeyCombo.Parse("ctrl+nosuchkey")));
        Assert.Throws<ArgumentException>(() => sim.KeyDown("nosuchkey"));
        sim.Scroll(0, 3);
        Assert.Equal("scroll 0,3", pointer.Log.Last());
    }

    [Fact]
    public void A_route_that_never_worked_is_replaced_by_the_next()
    {
        var socket = Path.GetTempFileName();
        try
        {
            bool ydotoolPointerWorks = true;
            var runner = new FakeRunner("swaymsg", "ydotool", "dotool");
            runner.Handlers["swaymsg"] = args => args.Contains("get_outputs")
                ? new CommandResult(0, Encoding.UTF8.GetBytes("""[{"name":"A","active":true,"scale":1.0,"rect":{"x":0,"y":0,"width":800,"height":600}}]"""), "", false)
                : new CommandResult(2, Encoding.UTF8.GetBytes("""[{"success":false,"error":"Seat has no pointer"}]"""), "", false);
            runner.Handlers["ydotool"] = args => args[0] == "mousemove" && ydotoolPointerWorks
                ? new CommandResult(0, Array.Empty<byte>(), "", false)
                : new CommandResult(1, Array.Empty<byte>(), "failed to connect socket", false);
            var ctx = WaylandSamples.Context(runner, "sway", ("YDOTOOL_SOCKET", socket));
            var sim = new WaylandInputSimulator(ctx);

            sim.MoveMouse(10, 10);
            Assert.Equal(InputRoutes.Ydotool, sim.PointerRoute);
            sim.TypeText("hi", 0);
            Assert.Equal(InputRoutes.Dotool, sim.KeyboardRoute);
            Assert.Equal("type hi\n", Encoding.UTF8.GetString(runner.Calls.Last().Stdin!));

            // Once a route has worked, its later errors are reported instead of switching routes.
            ydotoolPointerWorks = false;
            var ex = Assert.Throws<InvalidOperationException>(() => sim.MoveMouse(20, 20));
            Assert.Contains("ydotool", ex.Message);
            Assert.Equal(InputRoutes.Ydotool, sim.PointerRoute);
        }
        finally
        {
            File.Delete(socket);
        }
    }

    [Fact]
    public void No_working_route_explains_what_to_install()
    {
        var runner = new FakeRunner().Returns("swaymsg", """[{"name":"A","active":true,"scale":1.0,"rect":{"x":0,"y":0,"width":800,"height":600}}]""");
        var sim = new WaylandInputSimulator(WaylandSamples.Context(runner, "sway"));
        var ex = Assert.Throws<InvalidOperationException>(() => sim.TypeText("x", 0));
        Assert.Contains("wtype", ex.Message);
    }

    [Fact]
    public void Smooth_move_ends_on_the_target()
    {
        var runner = new FakeRunner().Returns("swaymsg", """[{"name":"A","active":true,"scale":1.0,"rect":{"x":0,"y":0,"width":1920,"height":1080}}]""");
        var pointer = new RecordingPointer();
        var sim = new WaylandInputSimulator(WaylandSamples.Context(runner), pointer, new RecordingKeyboard());
        sim.MoveMouse(100, 100);
        sim.MoveMouseSmooth(400, 300, 60);
        Assert.True(pointer.Log.Count >= 3);
        Assert.Equal("move 400,300", pointer.Log.Last());
    }

    [Fact]
    public void Missing_input_routes_explain_what_to_install()
    {
        var gnome = WaylandSamples.Context(new FakeRunner(), "GNOME");
        Assert.Contains("xdg-desktop-portal", WaylandInputSimulator.PointerHelp(gnome));
        var sway = WaylandSamples.Context(new FakeRunner(), "sway");
        Assert.Contains("wtype", WaylandInputSimulator.KeyboardHelp(sway));
        Assert.Contains("grim", WaylandScreenCapture.CaptureHelp(sway));
        Assert.DoesNotContain((char)0x2014, WaylandScreenCapture.CaptureHelp(gnome));
        Assert.DoesNotContain((char)0x2013, WaylandInputSimulator.PointerHelp(gnome));
    }

    [Fact]
    public void Portal_stream_space_rescales_screenshot_pixels()
    {
        var streams = new[] { new PortalStream(44, 0, 0, 1920, 1080) };
        var sameLayout = new WaylandLayout(new[] { new WaylandOutput("A", 0, 0, 1920, 1080, 1) }, "t");
        Assert.Equal((100.0, 50.0), PortalPointerBackend.ToStreamSpace(100, 50, sameLayout, streams));
        var screenshotLayout = WaylandLayout.FromImageSize(3840, 2160, "screenshot");
        Assert.Equal((1000.0, 500.0), PortalPointerBackend.ToStreamSpace(2000, 1000, screenshotLayout, streams));
    }
}

public class WaylandWindowParserTests
{
    [Fact]
    public void Sway_tree_lists_views_front_to_back()
    {
        var w = WaylandWindowParsers.ParseSwayTree(WaylandSamples.SwayTree);
        Assert.Equal(new[] { "Dialog", "Right terminal", "Left editor", "Scratch" }, w.Select(x => x.Title));
        var term = w.Single(x => x.Title == "Right terminal");
        Assert.Equal(((nint)21, "XTerm", 210, true, true), (term.Handle, term.ClassName, term.Pid, term.Focused, term.Visible));
        Assert.Equal((960.0, 24.0, 936.0, 1032.0), (term.X, term.Y, term.Width, term.Height));
        var scratch = w.Single(x => x.Title == "Scratch");
        Assert.False(scratch.Visible);
        Assert.True(scratch.Minimized);
        Assert.True(w.Single(x => x.Title == "Dialog").Floating);

        Assert.Equal("Dialog", WaylandWindowParsers.At(w, 800, 500)?.Title);
        Assert.Equal("Left editor", WaylandWindowParsers.At(w, 100, 100)?.Title);
        Assert.Equal("Right terminal", WaylandWindowParsers.At(w, 1500, 900)?.Title);
        Assert.Null(WaylandWindowParsers.At(w, 5, 5));
    }

    [Fact]
    public void Hyprland_clients()
    {
        const string clients = """
            [{"address":"0x55aa01","mapped":true,"hidden":false,"at":[10,40],"size":[940,1030],"workspace":{"id":1,"name":"1"},"floating":false,"class":"kitty","title":"shell","pid":11,"fullscreen":0,"focusHistoryID":1},
             {"address":"0x55aa02","mapped":true,"hidden":false,"at":[960,40],"size":[940,1030],"workspace":{"id":1,"name":"1"},"floating":false,"class":"firefox","title":"Mozilla Firefox","pid":12,"fullscreen":0,"focusHistoryID":0},
             {"address":"0x55aa03","mapped":true,"hidden":false,"at":[0,0],"size":[800,600],"workspace":{"id":2,"name":"2"},"floating":true,"class":"mpv","title":"video","pid":13,"fullscreen":0,"focusHistoryID":2}]
            """;
        const string monitors = """[{"id":0,"name":"DP-1","activeWorkspace":{"id":1,"name":"1"},"specialWorkspace":{"id":0,"name":""}}]""";
        var w = WaylandWindowParsers.ParseHyprlandClients(clients, monitors, """{"address":"0x55aa02","title":"Mozilla Firefox"}""");
        Assert.Equal(new[] { "Mozilla Firefox", "shell", "video" }, w.Select(x => x.Title));
        Assert.True(w[0].Focused);
        Assert.False(w[2].Visible);
        Assert.Equal((nint)0x55aa02, w[0].Handle);
        Assert.Equal("0x55aa02", WaylandWindowParsers.FormatAddress(w[0].Handle));
        Assert.False(WaylandWindowParsers.TryParseAddress("0x0", out _));
    }

    [Fact]
    public void Kwin_script_json_keeps_stacking_order()
    {
        var ids = new Dictionary<string, nint>();
        nint Handle(string id) => ids.TryGetValue(id, out var h) ? h : ids[id] = ids.Count + 1;
        const string output = """[{"id":"{a}","title":"Konsole","cls":"konsole","pid":5,"x":0,"y":0,"w":800,"h":600,"min":false,"active":false,"current":true},{"id":"{b}","title":"Dolphin","cls":"dolphin","pid":6,"x":100,"y":100,"w":800,"h":600,"min":false,"active":true,"current":true},{"id":"{c}","title":"Hidden","cls":"x","pid":7,"x":0,"y":0,"w":10,"h":10,"min":true,"active":false,"current":true}]""";
        var w = WaylandWindowParsers.ParseKwinJson("kdotool: " + output + "\n", Handle);
        Assert.Equal(new[] { "Dolphin", "Konsole", "Hidden" }, w.Select(x => x.Title));
        Assert.True(w[0].Focused);
        Assert.True(w[2].Minimized);
        Assert.Equal((100.0, 100.0, 800.0, 600.0), WaylandWindowParsers.ParseKdotoolGeometry("Window {b}\n  Position: 100,100 (screen: 0)\n  Geometry: 800x600\n"));
    }

    [Fact]
    public void Gnome_window_calls_json()
    {
        var list = WaylandWindowParsers.ParseGnomeList("""[{"in_current_workspace":true,"workspace":0,"wm_class":"org.gnome.Nautilus","title":"Home","pid":42,"id":12345,"frame_type":0,"window_type":0,"focus":true}]""");
        Assert.Single(list);
        Assert.Equal((12345u, "Home", 42, true), (list[0].Id, list[0].Title, list[0].Pid, list[0].Focus));
        var d = WaylandWindowParsers.ParseGnomeDetails("""{"x":10,"y":20,"width":640,"height":480,"minimized":false,"title":"Home"}""");
        Assert.Equal((10.0, 20.0, 640.0, 480.0, false), (d!.Value.X, d.Value.Y, d.Value.W, d.Value.H, d.Value.Minimized));
        Assert.Null(WaylandWindowParsers.ParseGnomeDetails("{}"));
    }

    [Fact]
    public void Window_manager_returns_nothing_where_no_source_exists()
    {
        var wm = new WaylandWindowManager(WaylandSamples.Context(new FakeRunner(), "COSMIC"));
        Assert.Empty(wm.ListWindows());
        Assert.Null(wm.GetForegroundWindow());
        Assert.False(wm.FocusWindow(1));
    }

    [Fact]
    public void Window_manager_maps_hyprland_windows_to_physical_pixels()
    {
        var runner = new FakeRunner();
        runner.Installed.Add("hyprctl");
        runner.Handlers["hyprctl"] = args => args[0] switch
        {
            "clients" => Ok("""[{"address":"0xabc","mapped":true,"hidden":false,"at":[100,50],"size":[400,300],"workspace":{"id":1},"floating":true,"class":"foot","title":"term","pid":1,"focusHistoryID":0}]"""),
            "monitors" => Ok("""[{"name":"eDP-1","width":3840,"height":2160,"x":0,"y":0,"scale":2.0,"activeWorkspace":{"id":1}}]"""),
            "activewindow" => Ok("""{"address":"0xabc"}"""),
            "dispatch" => Ok("ok"),
            _ => Ok(""),
        };
        var wm = new WaylandWindowManager(WaylandSamples.Context(runner, "Hyprland"));
        var w = Assert.Single(wm.ListWindows());
        Assert.Equal(new ScreenRect(200, 100, 800, 600), w.Bounds);
        Assert.True(w.IsForeground);
        Assert.Equal((nint)0xabc, wm.GetWindowAt(300, 300)?.Handle);
        Assert.Null(wm.GetWindowAt(10, 10));
        Assert.True(wm.FocusWindow(0xabc));
        Assert.Contains(runner.Calls, c => c.Command == "hyprctl" && c.Args.SequenceEqual(new[] { "dispatch", "focuswindow", "address:0xabc" }));

        static CommandResult Ok(string s) => new(0, Encoding.UTF8.GetBytes(s), "", false);
    }
}

public class WaylandPortalTests
{
    [Fact]
    public void Request_paths_are_predictable()
    {
        Assert.Equal("/org/freedesktop/portal/desktop/request/1_42/deskpilot_ab", PortalNames.RequestPath(":1.42", "deskpilot_ab"));
        Assert.Equal("/org/freedesktop/portal/desktop/session/1_7/s1", PortalNames.SessionPath(":1.7", "s1"));
        var token = PortalNames.NewToken();
        Assert.Matches("^[A-Za-z0-9_]+$", token);
        Assert.NotEqual(token, PortalNames.NewToken());
    }

    [Fact]
    public void Screenshot_request_and_response()
    {
        var call = PortalScreenshot.Build("tok1");
        Assert.Equal(("org.freedesktop.portal.Screenshot", "Screenshot", "sa{sv}", "tok1"), (call.Interface, call.Method, call.Signature, call.HandleToken));
        var options = (IReadOnlyDictionary<string, object>)call.Args[1].Value;
        Assert.Equal("tok1", options["handle_token"]);
        Assert.Equal(false, options["interactive"]);

        var path = PortalScreenshot.ParseFile(new PortalResponse(0, new Dictionary<string, object?> { ["uri"] = "file:///tmp/Screenshot%20from%20today.png" }));
        Assert.EndsWith("Screenshot from today.png", path);
        var denied = Assert.Throws<InvalidOperationException>(() => PortalScreenshot.ParseFile(new PortalResponse(1, new Dictionary<string, object?>())));
        Assert.Contains("cancelled", denied.Message);
        Assert.Throws<InvalidOperationException>(() => PortalScreenshot.ParseFile(new PortalResponse(0, new Dictionary<string, object?>())));
    }

    [Fact]
    public async Task Screenshot_file_is_read_and_deleted()
    {
        var file = Path.Combine(Path.GetTempPath(), $"dp-portal-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(file, new byte[] { 1, 2, 3 }, TestContext.Current.CancellationToken);
        var bus = new FakePortalBus { Respond = _ => new PortalResponse(0, new Dictionary<string, object?> { ["uri"] = new Uri(file).AbsoluteUri }) };
        var bytes = await PortalScreenshot.TakeAsync(bus, TimeSpan.FromSeconds(5), CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
        Assert.False(File.Exists(file));
    }

    private static PortalResponse StartResponse(string? restoreToken = "new-token") => new(0, new Dictionary<string, object?>
    {
        ["devices"] = 3u,
        ["restore_token"] = restoreToken,
        ["streams"] = new object?[]
        {
            new object?[] { 51u, new Dictionary<string, object?> { ["position"] = new object?[] { 0, 0 }, ["size"] = new object?[] { 1920, 1080 }, ["source_type"] = 1u } },
            new object?[] { 52u, new Dictionary<string, object?> { ["position"] = new object?[] { 1920, 0 }, ["size"] = new object?[] { 1280, 1024 } } },
        },
    });

    private static FakePortalBus RemoteDesktopBus() => new()
    {
        Respond = call => call.Method switch
        {
            "CreateSession" => new PortalResponse(0, new Dictionary<string, object?> { ["session_handle"] = "/org/freedesktop/portal/desktop/session/1_42/s" }),
            "Start" => StartResponse(),
            _ => new PortalResponse(0, new Dictionary<string, object?>()),
        },
    };

    [Fact]
    public async Task Remote_desktop_session_flow_saves_the_restore_token()
    {
        var tokenFile = Path.Combine(Path.GetTempPath(), $"dp-rd-{Guid.NewGuid():N}", "remote-desktop-token");
        Directory.CreateDirectory(Path.GetDirectoryName(tokenFile)!);
        await File.WriteAllTextAsync(tokenFile, "old-token", TestContext.Current.CancellationToken);
        var bus = RemoteDesktopBus();
        var rd = new PortalRemoteDesktop(bus, tokenFile);
        await rd.EnsureStartedAsync(CancellationToken.None);
        await rd.EnsureStartedAsync(CancellationToken.None);

        Assert.Equal(new[] { "CreateSession", "SelectDevices", "SelectSources", "Start" }, bus.Requests.Select(r => r.Method));
        Assert.Equal("org.freedesktop.portal.ScreenCast", bus.Requests[2].Interface);
        var create = (IReadOnlyDictionary<string, object>)bus.Requests[0].Args[0].Value;
        Assert.True(create.ContainsKey("session_handle_token"));
        var devices = (IReadOnlyDictionary<string, object>)bus.Requests[1].Args[1].Value;
        Assert.Equal(3u, devices["types"]);
        Assert.Equal(2u, devices["persist_mode"]);
        Assert.Equal("old-token", devices["restore_token"]);
        var sources = (IReadOnlyDictionary<string, object>)bus.Requests[2].Args[1].Value;
        Assert.Equal((1u, true), ((uint)sources["types"], (bool)sources["multiple"]));
        Assert.Equal("oa{sv}", bus.Requests[1].Signature);
        Assert.Equal("osa{sv}", bus.Requests[3].Signature);
        Assert.All(bus.Requests, r => Assert.Equal((string)((IReadOnlyDictionary<string, object>)r.Args.Last().Value)["handle_token"], r.HandleToken));

        Assert.Equal("new-token", await File.ReadAllTextAsync(tokenFile, TestContext.Current.CancellationToken));
        Assert.Equal(2, rd.Streams.Count);
        Assert.Equal(3u, rd.Devices);

        await rd.MoveAsync(2000, 100, CancellationToken.None);
        var move = bus.Calls.Last();
        Assert.Equal(("NotifyPointerMotionAbsolute", "oa{sv}udd"), (move.Method, move.Signature));
        Assert.Equal(52u, move.Args[2].Value);
        Assert.Equal((80.0, 100.0), ((double)move.Args[3].Value, (double)move.Args[4].Value));

        await rd.ButtonAsync(272, true, CancellationToken.None);
        Assert.Equal(("NotifyPointerButton", "oa{sv}iu", 272, 1u), (bus.Calls.Last().Method, bus.Calls.Last().Signature, (int)bus.Calls.Last().Args[2].Value, (uint)bus.Calls.Last().Args[3].Value));
        await rd.ScrollAsync(0, -2, CancellationToken.None);
        Assert.Equal(("NotifyPointerAxisDiscrete", 0u, -2), (bus.Calls.Last().Method, (uint)bus.Calls.Last().Args[2].Value, (int)bus.Calls.Last().Args[3].Value));
        await rd.KeysymAsync(0x010020ac, false, CancellationToken.None);
        Assert.Equal(("NotifyKeyboardKeysym", 0x010020ac, 0u), (bus.Calls.Last().Method, (int)bus.Calls.Last().Args[2].Value, (uint)bus.Calls.Last().Args[3].Value));
        Assert.Equal("/org/freedesktop/portal/desktop/session/1_42/s", bus.Calls.Last().Args[0].Value);
    }

    [Fact]
    public async Task Old_portals_get_no_persistence_and_refusals_are_explained()
    {
        var bus = RemoteDesktopBus();
        bus.Version = 1;
        var rd = new PortalRemoteDesktop(bus, Path.Combine(Path.GetTempPath(), $"dp-rd-{Guid.NewGuid():N}"));
        await rd.EnsureStartedAsync(CancellationToken.None);
        var devices = (IReadOnlyDictionary<string, object>)bus.Requests[1].Args[1].Value;
        Assert.False(devices.ContainsKey("persist_mode"));
        Assert.False(devices.ContainsKey("restore_token"));

        var refusing = RemoteDesktopBus();
        var inner = refusing.Respond;
        refusing.Respond = c => c.Method == "Start" ? new PortalResponse(1, new Dictionary<string, object?>()) : inner(c);
        var rd2 = new PortalRemoteDesktop(refusing, Path.Combine(Path.GetTempPath(), $"dp-rd-{Guid.NewGuid():N}"));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => rd2.EnsureStartedAsync(CancellationToken.None));
        Assert.Contains("Approve", ex.Message);
        Assert.False(rd2.IsStarted);

        var none = new FakePortalBus { Version = 0 };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new PortalRemoteDesktop(none, "x").EnsureStartedAsync(CancellationToken.None));
    }

    [Fact]
    public void Points_map_to_the_stream_under_them()
    {
        var streams = PortalRemoteDesktop.ParseStreams(StartResponse());
        Assert.Equal(new[] { new PortalStream(51, 0, 0, 1920, 1080), new PortalStream(52, 1920, 0, 1280, 1024) }, streams);
        var hit = PortalRemoteDesktop.MapPoint(streams, 1919.5, 1079)!.Value;
        Assert.Equal((51u, 1919.0, 1079.0), (hit.Stream.NodeId, hit.X, hit.Y));
        var outside = PortalRemoteDesktop.MapPoint(streams, 3500, 1100)!.Value;
        Assert.Equal((52u, 1279.0, 1023.0), (outside.Stream.NodeId, outside.X, outside.Y));
        Assert.Null(PortalRemoteDesktop.MapPoint(Array.Empty<PortalStream>(), 1, 1));
    }

    [Fact]
    public void Dbus_variants_convert_to_the_shapes_the_parsers_expect()
    {
        var props = new Dict<string, VariantValue>
        {
            ["position"] = Struct.Create(1920, 0).AsVariantValue(),
            ["size"] = Struct.Create(1280, 1024).AsVariantValue(),
        };
        var streams = new Tmds.DBus.Protocol.Array<Struct<uint, Dict<string, VariantValue>>> { Struct.Create(77u, props) };
        var plain = PortalValues.ToPlain(streams.AsVariantValue());
        var parsed = PortalRemoteDesktop.ParseStreams(new PortalResponse(0, new Dictionary<string, object?> { ["streams"] = plain }));
        Assert.Equal(new[] { new PortalStream(77, 1920, 0, 1280, 1024) }, parsed);

        Assert.Equal("file:///x.png", PortalValues.ToPlain(VariantValue.String("file:///x.png")));
        Assert.Equal(5u, PortalValues.ToPlain(VariantValue.Variant(VariantValue.UInt32(5))));
        var variants = PortalValues.ToVariants(new Dictionary<string, object> { ["a"] = "s", ["b"] = 2u, ["c"] = true, ["d"] = 3, ["e"] = 1.5 });
        Assert.Equal(VariantValueType.UInt32, variants["b"].Type);
        Assert.Equal(VariantValueType.Bool, variants["c"].Type);
    }
}

[Collection("Wayland cursor tracker")]
public class WaylandCaptureLogicTests
{
    [Fact]
    public void Grim_arguments_cover_the_region_at_the_layout_scale()
    {
        var one = new WaylandLayout(new[] { new WaylandOutput("A", 0, 0, 1920, 1080, 1) }, "t");
        var (args, captured) = WaylandScreenCapture.GrimArgs(new ScreenRect(10, 20, 300, 200), one);
        Assert.Equal(new[] { "-g", "10,20 300x200", "-t", "ppm", "-" }, args);
        Assert.Equal(new ScreenRect(10, 20, 300, 200), captured);

        var hidpi = new WaylandLayout(new[] { new WaylandOutput("A", 0, 0, 1280, 720, 1.5) }, "t");
        var (args2, captured2) = WaylandScreenCapture.GrimArgs(new ScreenRect(301, 0, 600, 300), hidpi);
        Assert.Equal(new[] { "-g", "200,0 401x200", "-s", "1.5", "-t", "ppm", "-" }, args2);
        Assert.Equal(new ScreenRect(300, 0, 602, 300), captured2);
    }

    [Fact]
    public void Ppm_images_decode_with_comments()
    {
        var header = Encoding.ASCII.GetBytes("P6\n# grim\n2 2\n255\n");
        var pixels = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 10, 20, 30 };
        using var bmp = WaylandScreenCapture.DecodeImage(header.Concat(pixels).ToArray());
        Assert.Equal((2, 2), (bmp.Width, bmp.Height));
        Assert.Equal(new SKColor(255, 0, 0), bmp.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 255), bmp.GetPixel(0, 1));
        Assert.Equal(new SKColor(10, 20, 30), bmp.GetPixel(1, 1));
        Assert.Throws<InvalidOperationException>(() => WaylandScreenCapture.DecodeImage(header.Concat(pixels.Take(5)).ToArray()));
    }

    [Fact]
    public void Full_desktop_images_are_cropped_at_their_own_resolution()
    {
        using var full = new SKBitmap(400, 200);
        full.Erase(SKColors.Blue);
        using (var c = new SKCanvas(full)) c.DrawRect(200, 0, 200, 200, new SKPaint { Color = SKColors.Red });
        // The image is twice the virtual screen's size (e.g. a scale-2 portal screenshot).
        using var crop = WaylandScreenCapture.CropFull(full, new ScreenRect(100, 0, 100, 100), new ScreenRect(0, 0, 200, 100));
        Assert.Equal((100, 100), (crop.Width, crop.Height));
        Assert.Equal(SKColors.Red, crop.GetPixel(50, 50));

        using var exact = WaylandScreenCapture.Crop(full, new SKRect(150, 10, 250, 60), 100, 50);
        Assert.Equal(SKColors.Blue, exact.GetPixel(10, 10));
        Assert.Equal(SKColors.Red, exact.GetPixel(90, 10));

        // Beyond the image: black, still the requested size.
        using var past = WaylandScreenCapture.Crop(full, new SKRect(350, 150, 450, 250), 100, 100);
        Assert.Equal((100, 100), (past.Width, past.Height));
        Assert.Equal(SKColors.Black, past.GetPixel(90, 90));
    }

    [Fact]
    public void Capture_uses_grim_output_and_draws_the_tracked_cursor()
    {
        var runner = new FakeRunner().Returns("swaymsg", """[{"name":"A","active":true,"scale":1.0,"rect":{"x":0,"y":0,"width":64,"height":32}}]""");
        runner.Installed.Add("grim");
        runner.Handlers["grim"] = args =>
        {
            var geo = args[1].Split(' ')[1].Split('x');
            int w = int.Parse(geo[0]), h = int.Parse(geo[1]);
            var data = Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n").Concat(Enumerable.Repeat(new byte[] { 0x2f, 0x6d, 0x8c }, w * h).SelectMany(b => b)).ToArray();
            return new CommandResult(0, data, "", false);
        };
        var capture = new WaylandScreenCapture(WaylandSamples.Context(runner), WaylandScreenCapture.RouteGrim);
        Assert.Equal(new ScreenRect(0, 0, 64, 32), capture.GetVirtualScreen());

        WaylandCursorTracker.Set(new ScreenPoint(10, 10));
        var frame = capture.Capture(new CaptureRequest(new ScreenRect(0, 0, 64, 32), 64, 32, ImageFormatKind.Png, 90, true, 0));
        Assert.Equal(("image/png", 64, 32), (frame.MediaType, frame.Width, frame.Height));
        using var bmp = SKBitmap.Decode(frame.Data);
        Assert.Equal(new SKColor(0x2f, 0x6d, 0x8c), bmp.GetPixel(40, 25));
        Assert.NotEqual(new SKColor(0x2f, 0x6d, 0x8c), bmp.GetPixel(12, 18));
        Assert.Equal(WaylandScreenCapture.RouteGrim, capture.Route);
    }

    [Fact]
    public void Capture_failure_explains_the_routes()
    {
        var runner = new FakeRunner().Returns("swaymsg", """[{"name":"A","active":true,"scale":1.0,"rect":{"x":0,"y":0,"width":64,"height":32}}]""");
        runner.Returns("grim", "", exitCode: 1);
        var capture = new WaylandScreenCapture(WaylandSamples.Context(runner), WaylandScreenCapture.RouteGrim);
        var ex = Assert.Throws<InvalidOperationException>(() => capture.Capture(new CaptureRequest(new ScreenRect(0, 0, 64, 32), 64, 32, ImageFormatKind.Jpeg, 80, false, 0)));
        Assert.Contains("grim", ex.Message);
        Assert.Contains("xdg-desktop-portal", ex.Message);
    }
}

// ================================================================================================ real D-Bus, fake portal

/// <summary>A private dbus-daemon for one test, so a fake portal can own the real portal's names safely.</summary>
internal sealed class PrivateBus : IDisposable
{
    private readonly Process _daemon;
    private readonly string _dir;

    public string Address { get; }

    private PrivateBus(Process daemon, string dir, string address)
    {
        _daemon = daemon;
        _dir = dir;
        Address = address;
    }

    public static PrivateBus Start()
    {
        var exe = DeskPilot.Core.Runtime.ExecutableLocator.Find("dbus-daemon");
        Assert.SkipWhen(exe == null, "dbus-daemon is not installed");
        var dir = Path.Combine(Path.GetTempPath(), "dp-bus-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var socket = Path.Combine(dir, "bus");
        var config = Path.Combine(dir, "bus.conf");
        File.WriteAllText(config,
            "<!DOCTYPE busconfig PUBLIC \"-//freedesktop//DTD D-Bus Bus Configuration 1.0//EN\" \"http://www.freedesktop.org/standards/dbus/1.0/busconfig.dtd\">\n" +
            $"<busconfig><type>session</type><listen>unix:path={socket}</listen><auth>EXTERNAL</auth>" +
            "<policy context=\"default\"><allow send_destination=\"*\" eavesdrop=\"true\"/><allow eavesdrop=\"true\"/><allow own=\"*\"/></policy></busconfig>\n");
        var psi = new ProcessStartInfo(exe!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add("--config-file=" + config);
        psi.ArgumentList.Add("--nofork");
        psi.ArgumentList.Add("--nopidfile");
        var p = Process.Start(psi)!;
        var sw = Stopwatch.StartNew();
        while (!File.Exists(socket) && sw.ElapsedMilliseconds < 5000 && !p.HasExited) Thread.Sleep(20);
        Assert.True(File.Exists(socket), "the private dbus-daemon did not start: " + (p.HasExited ? p.StandardError.ReadToEnd() : "timeout"));
        return new PrivateBus(p, dir, "unix:path=" + socket);
    }

    public void Dispose()
    {
        try { if (!_daemon.HasExited) _daemon.Kill(true); } catch (InvalidOperationException) { }
        _daemon.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

/// <summary>
/// Plays xdg-desktop-portal (Screenshot, RemoteDesktop, ScreenCast) and the GNOME "Window Calls" extension on a
/// private bus, answering like the real services: Request objects at the predictable path and Response signals.
/// </summary>
internal sealed class FakePortalService : IPathMethodHandler, IDisposable
{
    public const string WindowsPath = "/org/gnome/Shell/Extensions/Windows";

    private readonly DBusConnection _connection;
    private readonly FakePortalService? _root;
    private readonly List<string> _log;

    public string Path { get; }
    public bool HandlesChildPaths => false;
    public List<string> Log => _log;
    public string? ScreenshotFile { get; set; }
    public uint StartCode { get; set; }
    public uint FocusedWindow { get; set; } = 101;

    private FakePortalService(DBusConnection connection, string path, FakePortalService? root)
    {
        _connection = connection;
        Path = path;
        _root = root;
        _log = root?._log ?? new List<string>();
    }

    /// <summary>
    /// Connects on a thread-pool thread: the connection must not capture the test's synchronization context, or the
    /// product code blocking the test thread (it is synchronous) would starve the fake of the thread it answers on.
    /// </summary>
    public static Task<FakePortalService> StartAsync(string address) => Task.Run(async () =>
    {
        var c = new DBusConnection(address);
        await c.ConnectAsync().ConfigureAwait(false);
        var portal = new FakePortalService(c, PortalNames.ObjectPath, null);
        c.AddMethodHandler(portal);
        c.AddMethodHandler(new FakePortalService(c, WindowsPath, portal));
        await c.RequestNameAsync(PortalNames.Service, RequestNameOptions.Default).ConfigureAwait(false);
        await c.RequestNameAsync("org.gnome.Shell", RequestNameOptions.Default).ConfigureAwait(false);
        return portal;
    });

    private FakePortalService Root => _root ?? this;

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        string iface = request.InterfaceAsString ?? "", member = request.MemberAsString ?? "", sender = request.SenderAsString ?? "";
        var reader = request.GetBodyReader();

        if (Path == WindowsPath)
        {
            switch (member)
            {
                case "Introspect":
                    ReplyString(context, "<node><interface name=\"org.gnome.Shell.Extensions.Windows\"/></node>");
                    break;
                case "List":
                    // Stacking order, bottom to top, like Mutter.
                    ReplyString(context, $$"""[{"id":101,"title":"Back window","wm_class":"gedit","pid":{{Environment.ProcessId}},"focus":{{(Root.FocusedWindow == 101 ? "true" : "false")}},"in_current_workspace":true,"window_type":0},{"id":7,"title":"","wm_class":"gnome-shell","pid":1,"focus":false,"in_current_workspace":true,"window_type":2},{"id":202,"title":"Front window","wm_class":"nautilus","pid":{{Environment.ProcessId}},"focus":{{(Root.FocusedWindow == 202 ? "true" : "false")}},"in_current_workspace":true,"window_type":0}]""");
                    break;
                case "Details":
                    uint id = reader.ReadUInt32();
                    ReplyString(context, id == 101
                        ? """{"x":0,"y":0,"width":1000,"height":800,"minimized":false,"title":"Back window"}"""
                        : """{"x":500,"y":300,"width":600,"height":400,"minimized":false,"title":"Front window"}""");
                    break;
                case "Activate":
                    Root.FocusedWindow = reader.ReadUInt32();
                    _log.Add($"Activate {Root.FocusedWindow}");
                    ReplyEmpty(context);
                    break;
                default:
                    context.ReplyUnknownMethodError();
                    break;
            }
            return ValueTask.CompletedTask;
        }

        if (iface == "org.freedesktop.DBus.Properties" && member == "Get")
        {
            string target = reader.ReadString();
            uint version = target switch { PortalNames.Screenshot => 1, PortalNames.RemoteDesktop => 2, PortalNames.ScreenCast => 4, _ => 0 };
            if (version == 0) context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "No such interface");
            else
            {
                using var w = context.CreateReplyWriter("v");
                w.WriteVariant(VariantValue.UInt32(version));
                context.Reply(w.CreateMessage());
            }
            return ValueTask.CompletedTask;
        }

        switch (member)
        {
            case "Screenshot":
            {
                reader.ReadString();
                var o = reader.ReadDictionaryOfStringToVariantValue();
                var path = RequestPath(sender, o["handle_token"].GetString());
                _log.Add($"Screenshot interactive={o["interactive"].GetBool()}");
                // The Response may come before the method reply: callers must subscribe first.
                Respond(path, 0, new Dictionary<string, VariantValue> { ["uri"] = VariantValue.String(new Uri(ScreenshotFile!).AbsoluteUri) });
                ReplyPath(context, path);
                break;
            }
            case "CreateSession":
            {
                var o = reader.ReadDictionaryOfStringToVariantValue();
                var path = RequestPath(sender, o["handle_token"].GetString());
                var session = $"/org/freedesktop/portal/desktop/session/{sender.TrimStart(':').Replace('.', '_')}/{o["session_handle_token"].GetString()}";
                _log.Add("CreateSession");
                ReplyPath(context, path);
                Respond(path, 0, new Dictionary<string, VariantValue> { ["session_handle"] = VariantValue.String(session) });
                break;
            }
            case "SelectDevices":
            case "SelectSources":
            {
                reader.ReadObjectPathAsString();
                var o = reader.ReadDictionaryOfStringToVariantValue();
                var path = RequestPath(sender, o["handle_token"].GetString());
                var keys = string.Join(",", o.Keys.Where(k => k != "handle_token").OrderBy(k => k, StringComparer.Ordinal)
                    .Select(k => $"{k}={(PortalValues.ToPlain(o[k]) is bool b ? (b ? "true" : "false") : PortalValues.ToPlain(o[k]))}"));
                _log.Add($"{member} {keys}");
                ReplyPath(context, path);
                Respond(path, 0, new Dictionary<string, VariantValue>());
                break;
            }
            case "Start":
            {
                reader.ReadObjectPathAsString();
                reader.ReadString();
                var o = reader.ReadDictionaryOfStringToVariantValue();
                var path = RequestPath(sender, o["handle_token"].GetString());
                _log.Add("Start");
                ReplyPath(context, path);
                var props = new Dict<string, VariantValue>
                {
                    ["position"] = Struct.Create(0, 0).AsVariantValue(),
                    ["size"] = Struct.Create(1920, 1080).AsVariantValue(),
                    ["source_type"] = VariantValue.UInt32(1),
                };
                var streams = new Tmds.DBus.Protocol.Array<Struct<uint, Dict<string, VariantValue>>> { Struct.Create(51u, props) };
                Respond(path, StartCode, new Dictionary<string, VariantValue>
                {
                    ["devices"] = VariantValue.UInt32(3),
                    ["restore_token"] = VariantValue.String("tok-2"),
                    ["streams"] = streams.AsVariantValue(),
                });
                break;
            }
            case "NotifyPointerMotionAbsolute":
                reader.ReadObjectPathAsString(); reader.ReadDictionaryOfStringToVariantValue();
                _log.Add($"Motion {reader.ReadUInt32()} {reader.ReadDouble():0.#} {reader.ReadDouble():0.#}");
                ReplyEmpty(context);
                break;
            case "NotifyPointerButton":
                reader.ReadObjectPathAsString(); reader.ReadDictionaryOfStringToVariantValue();
                _log.Add($"Button {reader.ReadInt32()} {reader.ReadUInt32()}");
                ReplyEmpty(context);
                break;
            case "NotifyPointerAxisDiscrete":
                reader.ReadObjectPathAsString(); reader.ReadDictionaryOfStringToVariantValue();
                _log.Add($"Axis {reader.ReadUInt32()} {reader.ReadInt32()}");
                ReplyEmpty(context);
                break;
            case "NotifyKeyboardKeysym":
                reader.ReadObjectPathAsString(); reader.ReadDictionaryOfStringToVariantValue();
                _log.Add($"Keysym 0x{reader.ReadInt32():x} {reader.ReadUInt32()}");
                ReplyEmpty(context);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return ValueTask.CompletedTask;
    }

    private static string RequestPath(string sender, string token) =>
        $"/org/freedesktop/portal/desktop/request/{sender.TrimStart(':').Replace('.', '_')}/{token}";

    private void Respond(string path, uint code, Dictionary<string, VariantValue> results)
    {
        using var w = _connection.GetMessageWriter();
        w.WriteSignalHeader(null, path, "org.freedesktop.portal.Request", "Response", "ua{sv}");
        w.WriteUInt32(code);
        w.WriteDictionary(results);
        _connection.TrySendMessage(w.CreateMessage());
    }

    private static void ReplyPath(MethodContext context, string path)
    {
        using var w = context.CreateReplyWriter("o");
        w.WriteObjectPath(path);
        context.Reply(w.CreateMessage());
    }

    private static void ReplyString(MethodContext context, string s)
    {
        using var w = context.CreateReplyWriter("s");
        w.WriteString(s);
        context.Reply(w.CreateMessage());
    }

    private static void ReplyEmpty(MethodContext context)
    {
        using var w = context.CreateReplyWriter(null!);
        context.Reply(w.CreateMessage());
    }

    public void Dispose()
    {
        if (_root == null) _connection.Dispose();
    }
}

/// <summary>The GNOME/KDE portal code paths over a real D-Bus connection (dbus-daemon), with a fake portal behind it.</summary>
[Collection("Wayland cursor tracker")]
public class WaylandPortalBusTests
{
    /// <summary>A GNOME context whose portal is the private bus and whose layout is a 1920x1080 screenshot.</summary>
    private static WaylandContext GnomeContext(PrivateBus bus)
    {
        var session = new LinuxSessionInfo(LinuxSessionKind.Wayland, null, "/nonexistent/wayland-socket", "GNOME", Path.GetTempPath());
        var ctx = new WaylandContext(session, new FakeRunner(), _ => null)
        {
            CaptureLayout = WaylandLayout.FromImageSize(1920, 1080, "screenshot"),
        };
        ctx.Portal = new PortalBus(bus.Address);
        return ctx;
    }

    [LinuxFact]
    public async Task Screenshot_portal_round_trip()
    {
        using var bus = PrivateBus.Start();
        using var service = await FakePortalService.StartAsync(bus.Address);
        var file = Path.Combine(Path.GetTempPath(), $"dp-shot-{Guid.NewGuid():N}.png");
        using (var bmp = new SKBitmap(1920, 1080))
        {
            bmp.Erase(new SKColor(0x2f, 0x6d, 0x8c));
            using var canvas = new SKCanvas(bmp);
            canvas.DrawRect(1000, 500, 200, 100, new SKPaint { Color = SKColors.Red });
            canvas.Flush();
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 90);
            await File.WriteAllBytesAsync(file, data.ToArray(), TestContext.Current.CancellationToken);
        }
        service.ScreenshotFile = file;
        var expected = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        using var portal = new PortalBus(bus.Address);
        Assert.Equal(1u, await portal.GetVersionAsync(PortalNames.Screenshot, TestContext.Current.CancellationToken));
        Assert.Equal(0u, await portal.GetVersionAsync("org.freedesktop.portal.Nothing", TestContext.Current.CancellationToken));
        var bytes = await PortalScreenshot.TakeAsync(portal, TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(expected, bytes);
        Assert.False(File.Exists(file), "the portal's screenshot file must be deleted");
        Assert.Equal("Screenshot interactive=False", service.Log.Single());

        // The whole capture route: full-desktop screenshot, cropped to the request.
        await File.WriteAllBytesAsync(file, expected, TestContext.Current.CancellationToken);
        var ctx = GnomeContext(bus);
        var capture = new WaylandScreenCapture(ctx, WaylandScreenCapture.RoutePortal);
        var frame = capture.Capture(new CaptureRequest(new ScreenRect(990, 490, 100, 50), 100, 50, ImageFormatKind.Png, 90, false, 0));
        using var crop = SKBitmap.Decode(frame.Data);
        Assert.Equal(new SKColor(0x2f, 0x6d, 0x8c), crop.GetPixel(2, 2));
        Assert.Equal(SKColors.Red, crop.GetPixel(50, 30));
        Assert.Equal(WaylandScreenCapture.RoutePortal, capture.Route);
        Assert.False(File.Exists(file));
        ctx.Portal.Dispose();
    }

    [LinuxFact]
    public async Task Remote_desktop_input_round_trip()
    {
        using var bus = PrivateBus.Start();
        using var service = await FakePortalService.StartAsync(bus.Address);
        try { File.Delete(PortalRemoteDesktop.DefaultTokenFile); } catch (IOException) { }

        // Awaited directly first, then through the synchronous simulator.
        using (var direct = new PortalBus(bus.Address))
        {
            var rd = new PortalRemoteDesktop(direct, Path.Combine(Path.GetTempPath(), $"dp-rd-{Guid.NewGuid():N}"));
            await rd.EnsureStartedAsync(TestContext.Current.CancellationToken);
            await rd.MoveAsync(10, 20, TestContext.Current.CancellationToken);
            Assert.True(service.Log.Contains("Motion 51 10 20"), string.Join(" | ", service.Log));
        }
        service.Log.Clear();

        var ctx = GnomeContext(bus);
        var sim = new WaylandInputSimulator(ctx, InputRoutes.Portal, InputRoutes.Portal);
        try
        {
            sim.MoveMouse(100, 200);
        }
        catch (InvalidOperationException first)
        {
            Assert.Fail(first.Message + " / fake portal saw: " + string.Join(" | ", service.Log));
        }
        sim.Click(MouseButton.Left, 1);
        sim.Scroll(0, 2);
        sim.TypeText("é€\n", 0);
        sim.PressCombo(KeyCombo.Parse("ctrl+c"));
        sim.KeyDown("shift");
        sim.ReleaseAll();
        Assert.Equal(InputRoutes.Portal, sim.PointerRoute);
        Assert.Equal(InputRoutes.Portal, sim.KeyboardRoute);

        Assert.Equal(new[]
        {
            "CreateSession",
            "SelectDevices persist_mode=2,types=3",
            "SelectSources multiple=true,types=1",
            "Start",
            "Motion 51 100 200",
            "Button 272 1", "Button 272 0",
            "Axis 0 2",
            "Keysym 0xe9 1", "Keysym 0xe9 0", "Keysym 0x10020ac 1", "Keysym 0x10020ac 0", "Keysym 0xff0d 1", "Keysym 0xff0d 0",
            "Keysym 0xffe3 1", "Keysym 0x63 1", "Keysym 0x63 0", "Keysym 0xffe3 0",
            "Keysym 0xffe1 1", "Keysym 0xffe1 0",
        }, service.Log);
        Assert.Equal("tok-2", (await File.ReadAllTextAsync(PortalRemoteDesktop.DefaultTokenFile, TestContext.Current.CancellationToken)).Trim());

        // A later session presents the saved token, so the user is not asked again.
        service.Log.Clear();
        var again = new WaylandInputSimulator(GnomeContext(bus), InputRoutes.Portal, InputRoutes.Portal);
        again.MoveMouse(5, 5);
        Assert.Contains("SelectDevices persist_mode=2,restore_token=tok-2,types=3", service.Log);

        // A refused approval is a readable error.
        service.StartCode = 1;
        var refused = new WaylandInputSimulator(GnomeContext(bus), InputRoutes.Portal, InputRoutes.Portal);
        var ex = Assert.Throws<InvalidOperationException>(() => refused.MoveMouse(1, 1));
        Assert.Contains("Approve DeskPilot", ex.Message);
    }

    [LinuxFact]
    public async Task Gnome_window_calls_round_trip()
    {
        using var bus = PrivateBus.Start();
        using var service = await FakePortalService.StartAsync(bus.Address);
        using (var direct = new PortalBus(bus.Address))
        {
            var json = await direct.CallServiceAsync(WaylandWindowManager.GnomeService, WaylandWindowManager.GnomePath, WaylandWindowManager.GnomeInterface,
                "List", null, true, TestContext.Current.CancellationToken);
            Assert.Equal(3, WaylandWindowParsers.ParseGnomeList(json!).Count);
        }
        var wm = new WaylandWindowManager(GnomeContext(bus));
        var windows = wm.ListWindows();
        Assert.Equal(new[] { "Front window", "Back window" }, windows.Select(w => w.Title));
        Assert.Equal(new ScreenRect(500, 300, 600, 400), windows[0].Bounds);
        Assert.Equal(Environment.ProcessId, windows[0].ProcessId);
        Assert.Equal("Back window", wm.GetForegroundWindow()?.Title);
        Assert.Equal("Front window", wm.GetWindowAt(700, 500)?.Title);
        Assert.Equal("Back window", wm.GetWindowAt(100, 100)?.Title);
        Assert.True(wm.FocusWindow(202));
        Assert.Contains("Activate 202", service.Log);
        Assert.Equal("Front window", wm.GetForegroundWindow()?.Title);
    }
}

// ================================================================================================ real session (headless sway in CI)

/// <summary>A zenity window started for a test, with a unique title; killed on dispose.</summary>
internal sealed class ZenityApp : IDisposable
{
    private readonly Process _process;
    private readonly Task<string> _stdout;

    public string Title { get; }
    public int Pid => _process.Id;

    private ZenityApp(Process p, string title)
    {
        _process = p;
        Title = title;
        _stdout = p.StandardOutput.ReadToEndAsync();
        _ = p.StandardError.ReadToEndAsync();
    }

    public static ZenityApp Start(string kind, params string[] extra)
    {
        var title = "DP-wl-" + Guid.NewGuid().ToString("N")[..10];
        var psi = new ProcessStartInfo("zenity") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(kind);
        psi.ArgumentList.Add("--title=" + title);
        foreach (var e in extra) psi.ArgumentList.Add(e);
        psi.Environment["GDK_BACKEND"] = "wayland";
        psi.Environment["GSK_RENDERER"] = "cairo";
        psi.Environment["GTK_A11Y"] = "none";
        psi.Environment["NO_AT_BRIDGE"] = "1";
        psi.Environment["LC_ALL"] = "C.UTF-8";
        return new ZenityApp(Process.Start(psi)!, title);
    }

    public WindowInfo WaitForWindow(IWindowManager wm, int timeoutMs = 20000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            var w = wm.ListWindows().FirstOrDefault(x => x.Title == Title);
            if (w != null && !w.Bounds.IsEmpty) return w;
            if (_process.HasExited) throw new InvalidOperationException($"zenity exited early with {_process.ExitCode}");
            Thread.Sleep(100);
        }
        throw new TimeoutException($"Window '{Title}' did not appear");
    }

    public (int ExitCode, string Output)? WaitForExit(int timeoutMs = 8000)
    {
        if (!_process.WaitForExit(timeoutMs)) return null;
        _stdout.Wait(2000);
        return (_process.ExitCode, _stdout.Result.TrimEnd('\n'));
    }

    public bool HasExited => _process.HasExited;

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(true); } catch (InvalidOperationException) { }
        _process.Dispose();
    }
}

[CollectionDefinition("Wayland session", DisableParallelization = true)]
public sealed class WaylandSessionCollection { }

/// <summary>Runs against the headless sway session of the CI "wayland" job (wlroots, 1920x1080, no Xwayland).</summary>
[Collection("Wayland session")]
public class WaylandSessionTests
{
    private const string BackgroundHex = "2f6d8c";
    private static readonly SKColor Background = new(0x2f, 0x6d, 0x8c);

    private static WaylandContext Sway()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION") == "wayland", "Needs the CI headless sway session");
        var ctx = new WaylandContext(LinuxSession.Detect());
        Assert.Equal(WaylandDesktop.Sway, ctx.Desktop);
        return ctx;
    }

    private static void Log(string name, byte[] data)
    {
        var dir = Environment.GetEnvironmentVariable("CI_LOGS");
        if (string.IsNullOrEmpty(dir)) return;
        try { File.WriteAllBytes(Path.Combine(dir, name), data); } catch (IOException) { }
    }

    private static bool Near(SKColor a, SKColor b, int tolerance = 3) =>
        Math.Abs(a.Red - b.Red) <= tolerance && Math.Abs(a.Green - b.Green) <= tolerance && Math.Abs(a.Blue - b.Blue) <= tolerance;

    private static string? SwayCommand(WaylandContext ctx, string command)
    {
        ctx.Sway!.Command(command, out var error);
        return error.Length == 0 ? null : error;
    }

    [WaylandFact]
    public void Compositor_protocols_and_outputs()
    {
        var ctx = Sway();
        Assert.Contains("zwlr_virtual_pointer_manager_v1", ctx.Globals);
        Assert.Contains("zwp_virtual_keyboard_manager_v1", ctx.Globals);
        Assert.Contains("zwlr_screencopy_manager_v1", ctx.Globals);

        var layout = ctx.TryGetLayout();
        Assert.NotNull(layout);
        Assert.Equal("sway", layout!.Source);
        var m = Assert.Single(layout.Monitors);
        Assert.Equal((new ScreenRect(0, 0, 1920, 1080), true, 1.0), (m.Bounds, m.IsPrimary, m.Scale));

        // The Wayland wire client reads the same outputs through wl_output and xdg-output.
        var proto = WaylandOutputQuery.Query(ctx.SocketPath!);
        var o = Assert.Single(proto);
        Assert.Equal((0, 0, 1920, 1080, 1920, 1080, 1.0), (o.LogicalX, o.LogicalY, o.LogicalWidth, o.LogicalHeight, o.ModeWidth, o.ModeHeight, o.Scale));
        Assert.Equal("HEADLESS-1", o.Name);

        var randr = ctx.Runner.Run("wlr-randr", Array.Empty<string>(), 5000);
        if (randr.Ok)
        {
            var parsed = Assert.Single(WaylandOutputParsers.ParseWlrRandr(randr.Text));
            Assert.Equal((1920.0, 1080.0), (parsed.Width, parsed.Height));
        }
    }

    [WaylandFact]
    public void Grim_capture_shows_the_configured_background()
    {
        var ctx = Sway();
        var capture = new WaylandScreenCapture(ctx);
        Assert.Equal(new ScreenRect(0, 0, 1920, 1080), capture.GetVirtualScreen());

        var full = capture.Capture(new CaptureRequest(new ScreenRect(0, 0, 1920, 1080), 1920, 1080, ImageFormatKind.Png, 90, false, 0));
        Log("wayland-full.png", full.Data);
        Assert.Equal(WaylandScreenCapture.RouteGrim, capture.Route);
        using (var bmp = SKBitmap.Decode(full.Data))
        {
            Assert.Equal((1920, 1080), (bmp.Width, bmp.Height));
            // The outer gap strips are always background, whatever windows are open.
            foreach (var (x, y) in new[] { (4, 4), (1915, 4), (4, 1075), (1915, 1075), (960, 6), (6, 540) })
                Assert.True(Near(bmp.GetPixel(x, y), Background), $"pixel ({x},{y}) is {bmp.GetPixel(x, y)}, expected #{BackgroundHex}");
        }

        var small = capture.Capture(new CaptureRequest(new ScreenRect(0, 0, 1920, 1080), 960, 540, ImageFormatKind.Jpeg, 85, true, 100));
        Assert.Equal(("image/jpeg", 960, 540), (small.MediaType, small.Width, small.Height));

        var region = capture.Capture(new CaptureRequest(new ScreenRect(0, 1060, 300, 20), 300, 20, ImageFormatKind.Png, 90, false, 0));
        using var r = SKBitmap.Decode(region.Data);
        Assert.Equal((300, 20), (r.Width, r.Height));
        Assert.True(Near(r.GetPixel(5, 15), Background));
    }

    [WaylandFact]
    public void Lists_a_wayland_app_with_its_pid_and_title()
    {
        var ctx = Sway();
        var wm = new WaylandWindowManager(ctx);
        using var app = ZenityApp.Start("--entry", "--text=list test");
        var w = app.WaitForWindow(wm);
        Assert.Equal(app.Pid, w.ProcessId);
        Assert.Equal("zenity", w.ProcessName);
        Assert.True(w.IsVisible);
        Assert.False(w.IsElevated);
        Assert.False(w.IsUacPrompt);
        Assert.True(new ScreenRect(0, 0, 1920, 1080).Intersect(w.Bounds) == w.Bounds, $"bounds {w.Bounds}");
        Assert.True(wm.FocusWindow(w.Handle));
        Assert.Equal(w.Handle, wm.GetForegroundWindow()?.Handle);
        Assert.Equal(w.Handle, wm.GetWindowAt(w.Bounds.Center.X, w.Bounds.Center.Y)?.Handle);
        Assert.False(wm.IsUacPromptActive());
        Assert.False(wm.IsCurrentProcessElevated);
    }

    /// <summary>Types into a focused zenity entry, presses Enter and returns what zenity printed.</summary>
    private static string TypeIntoEntry(WaylandContext ctx, Action<WaylandInputSimulator> type)
    {
        var wm = new WaylandWindowManager(ctx);
        var input = new WaylandInputSimulator(ctx);
        using var app = ZenityApp.Start("--entry", "--text=type here");
        var w = app.WaitForWindow(wm);
        Assert.True(wm.FocusWindow(w.Handle));
        Thread.Sleep(300);
        type(input);
        input.PressCombo(KeyCombo.Parse("enter"));
        var result = app.WaitForExit();
        Assert.Equal(InputRoutes.Wtype, input.KeyboardRoute);
        Assert.True(result is { ExitCode: 0 }, $"zenity did not accept the entry (exit {result?.ExitCode})");
        return result!.Value.Output;
    }

    [WaylandFact]
    public void Types_unicode_text_with_wtype()
    {
        var ctx = Sway();
        const string text = "Hello wörld, ñandú 日本語 € ✓ -dash";
        Assert.Equal(text, TypeIntoEntry(ctx, i => i.TypeText(text, 0)));
    }

    [WaylandFact]
    public void Key_combos_and_named_keys()
    {
        var ctx = Sway();
        Assert.Equal("xyz", TypeIntoEntry(ctx, i =>
        {
            i.TypeText("abc def", 0);
            i.PressCombo(KeyCombo.Parse("ctrl+a"));
            i.TypeText("xyzq", 0);
            i.PressCombo(KeyCombo.Parse("backspace"));
        }));
        Assert.Equal("a1b", TypeIntoEntry(ctx, i =>
        {
            i.TypeText("ab", 5);
            i.PressCombo(KeyCombo.Parse("left"));
            i.PressCombo(KeyCombo.Parse("1"));
        }));
    }

    [WaylandFact]
    public void Held_key_is_released_by_key_up()
    {
        var ctx = Sway();
        Assert.Equal("q", TypeIntoEntry(ctx, i =>
        {
            i.KeyDown("q");
            i.KeyUp("q");
        }));
    }

    private static (ZenityApp A, WindowInfo WA, ZenityApp B, WindowInfo WB) TwoTiledWindows(WaylandContext ctx, WaylandWindowManager wm)
    {
        var a = ZenityApp.Start("--entry", "--text=left");
        var wa = a.WaitForWindow(wm);
        var b = ZenityApp.Start("--entry", "--text=right");
        var wb = b.WaitForWindow(wm);
        // Dialog-like windows may float; tiling them side by side makes their positions predictable.
        SwayCommand(ctx, $"[con_id={(long)wa.Handle}] floating disable");
        SwayCommand(ctx, $"[con_id={(long)wb.Handle}] floating disable");
        Thread.Sleep(400);
        wa = wm.ListWindows().First(w => w.Handle == wa.Handle);
        wb = wm.ListWindows().First(w => w.Handle == wb.Handle);
        Assert.False(wa.Bounds.Intersect(wb.Bounds) is { IsEmpty: false } overlap && overlap.Width * overlap.Height > wa.Bounds.Width * wa.Bounds.Height / 2,
            $"windows overlap: {wa.Bounds} {wb.Bounds}");
        return (a, wa, b, wb);
    }

    private static void ClickFocusTest(WaylandContext ctx, string? pointerRoute)
    {
        var wm = new WaylandWindowManager(ctx);
        var input = new WaylandInputSimulator(ctx, pointerRoute, null);
        var (a, wa, b, wb) = TwoTiledWindows(ctx, wm);
        using (a)
        using (b)
        {
            foreach (var target in new[] { wa, wb, wa })
            {
                var p = new ScreenPoint(target.Bounds.X + target.Bounds.Width / 2, target.Bounds.Y + target.Bounds.Height * 2 / 3);
                input.MoveMouse(p.X, p.Y);
                input.Click(MouseButton.Left, 1);
                var sw = Stopwatch.StartNew();
                while (wm.GetForegroundWindow()?.Handle != target.Handle && sw.ElapsedMilliseconds < 3000) Thread.Sleep(50);
                Assert.True(wm.GetForegroundWindow()?.Handle == target.Handle,
                    $"click at {p} via {input.PointerRoute} did not focus '{target.Title}' {target.Bounds}; foreground is '{wm.GetForegroundWindow()?.Title}'");
            }
            if (pointerRoute != null) Assert.Equal(pointerRoute, input.PointerRoute);
            else Assert.Equal(InputRoutes.VirtualPointer, input.PointerRoute);
        }
    }

    [WaylandFact]
    public void Clicks_focus_the_window_under_the_pointer() => ClickFocusTest(Sway(), null);

    [WaylandFact]
    public void Sway_ipc_pointer_route_clicks() => ClickFocusTest(Sway(), InputRoutes.Sway);

    [WaylandFact]
    public void Wlrctl_pointer_route_clicks()
    {
        var ctx = Sway();
        Assert.SkipWhen(ctx.Runner.Find("wlrctl") == null, "wlrctl is not installed");
        ClickFocusTest(ctx, InputRoutes.Wlrctl);
    }

    [WaylandFact]
    public void Focus_switches_between_windows()
    {
        var ctx = Sway();
        var wm = new WaylandWindowManager(ctx);
        var (a, wa, b, wb) = TwoTiledWindows(ctx, wm);
        using (a)
        using (b)
        {
            Assert.True(wm.FocusWindow(wa.Handle));
            Assert.Equal(wa.Handle, wm.GetForegroundWindow()?.Handle);
            Assert.True(wm.FocusWindow(wb.Handle));
            Assert.Equal(wb.Handle, wm.GetForegroundWindow()?.Handle);
            Assert.False(wm.FocusWindow(987654321));
        }
    }

    [WaylandFact]
    public void Clicking_a_dialog_button_answers_it()
    {
        var ctx = Sway();
        var wm = new WaylandWindowManager(ctx);
        var input = new WaylandInputSimulator(ctx);
        using var app = ZenityApp.Start("--question", "--text=Click the right button", "--ok-label=Yes", "--cancel-label=No");
        var w = app.WaitForWindow(wm);
        Thread.Sleep(500);
        var shot = new WaylandScreenCapture(ctx).Capture(new CaptureRequest(w.Bounds, w.Bounds.Width, w.Bounds.Height, ImageFormatKind.Png, 90, false, 0));
        Log("wayland-question.png", shot.Data);

        // The buttons sit along the bottom of the dialog, "Yes" on the right: walk up from the bottom edge until one answers.
        for (int dy = 12; dy <= 90 && !app.HasExited; dy += 8)
        {
            input.MoveMouse(w.Bounds.X + w.Bounds.Width * 3 / 4, w.Bounds.Bottom - dy);
            input.Click(MouseButton.Left, 1);
            Thread.Sleep(250);
        }
        var result = app.WaitForExit(3000);
        Assert.NotNull(result);
        Assert.Equal(0, result!.Value.ExitCode);
    }

    [WaylandFact]
    public void Pointer_lands_where_it_was_sent()
    {
        var ctx = Sway();
        var input = new WaylandInputSimulator(ctx);
        var capture = new WaylandScreenCapture(ctx);
        // The left outer gap (24 px of plain background) holds the cursor tip, so nothing else changes there.
        var region = new ScreenRect(0, 380, 22, 140);
        SKBitmap Shot() => SKBitmap.Decode(capture.Capture(new CaptureRequest(region, region.Width, region.Height, ImageFormatKind.Png, 90, false, 0)).Data);

        input.MoveMouse(1500, 900);
        Thread.Sleep(150);
        using var without = Shot();
        input.MoveMouse(6, 450);
        Thread.Sleep(150);
        using var with = Shot();
        int minX = int.MaxValue, minY = int.MaxValue;
        for (int y = 0; y < region.Height; y++)
            for (int x = 0; x < region.Width; x++)
                if (!Near(without.GetPixel(x, y), with.GetPixel(x, y), 8)) { minX = Math.Min(minX, x); minY = Math.Min(minY, y); }
        Assert.SkipWhen(minX == int.MaxValue, "The compositor does not draw the cursor into screenshots here");
        Assert.InRange(minX, 0, 7);
        Assert.InRange(minY + region.Y, 450 - 12, 452);
        Assert.Equal(new ScreenPoint(6, 450), input.GetCursorPosition());
    }

    [WaylandFact]
    public void Scrolling_moves_the_content_under_the_pointer()
    {
        var ctx = Sway();
        var wm = new WaylandWindowManager(ctx);
        var input = new WaylandInputSimulator(ctx);
        var file = Path.Combine(Path.GetTempPath(), $"dp-scroll-{Guid.NewGuid():N}.txt");
        File.WriteAllLines(file, Enumerable.Range(1, 400).Select(i => $"Line {i} of the scroll test"));
        try
        {
            using var app = ZenityApp.Start("--text-info", "--filename=" + file, "--width=600", "--height=500");
            var w = app.WaitForWindow(wm);
            Thread.Sleep(600);
            var capture = new WaylandScreenCapture(ctx);
            var inner = new ScreenRect(w.Bounds.X + 20, w.Bounds.Y + 60, Math.Max(50, w.Bounds.Width - 60), Math.Max(50, w.Bounds.Height / 2));
            byte[] Shot() => capture.Capture(new CaptureRequest(inner, inner.Width, inner.Height, ImageFormatKind.Png, 90, false, 0)).Data;

            input.MoveMouse(inner.Center.X, inner.Center.Y);
            Thread.Sleep(200);
            var before = Shot();
            input.Scroll(0, 6);
            Thread.Sleep(600);
            var after = Shot();
            Log("wayland-scroll-before.png", before);
            Log("wayland-scroll-after.png", after);
            Assert.False(before.SequenceEqual(after), "the text view did not scroll");
        }
        finally
        {
            File.Delete(file);
        }
    }
}

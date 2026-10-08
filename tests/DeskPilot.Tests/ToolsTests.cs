using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;

namespace DeskPilot.Tests;

/// <summary>Fakes for every desktop service, shared by ToolsTests and SafetyTests. Nothing here touches the real desktop.</summary>
internal static class ToolsTestFakes
{
    public static readonly WindowInfo Notepad = new(101, "Untitled - Notepad", "Notepad", "notepad", 4321,
        new ScreenRect(0, 0, 1280, 720), true, false, true, false, false);

    public static WindowInfo Window(string title, string process, int pid = 5000, ScreenRect? bounds = null, bool foreground = false,
        bool minimized = false, bool elevated = false, bool uac = false, nint handle = 0) =>
        new(handle == 0 ? pid : handle, title, "Class", process, pid, bounds ?? new ScreenRect(0, 0, 800, 600), true, minimized, foreground, elevated, uac);

    public sealed class Screen : IScreenCapture
    {
        public List<MonitorInfo> Monitors { get; } = new();
        public ScreenRect Virtual { get; set; }
        public List<CaptureRequest> Requests { get; } = new();
        public Exception? CaptureError { get; set; }

        public IReadOnlyList<MonitorInfo> GetMonitors() => Monitors;
        public ScreenRect GetVirtualScreen() => Virtual;

        public CapturedFrame Capture(CaptureRequest request)
        {
            if (CaptureError != null) throw CaptureError;
            Requests.Add(request);
            return new CapturedFrame(new byte[] { 1, 2, 3 }, request.Format == ImageFormatKind.Png ? "image/png" : "image/jpeg",
                request.TargetWidth, request.TargetHeight, request.Source);
        }

        public static Screen Single(int width = 2560, int height = 1440)
        {
            var s = new Screen { Virtual = new ScreenRect(0, 0, width, height) };
            s.Monitors.Add(new MonitorInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, width, height), new ScreenRect(0, 0, width, height - 40), true, 1.0));
            return s;
        }

        /// <summary>Primary 2560x1440 at (0,0) plus a 1920x1080 monitor to its left (negative coordinates).</summary>
        public static Screen Dual()
        {
            var s = new Screen { Virtual = new ScreenRect(-1920, 0, 4480, 1440) };
            s.Monitors.Add(new MonitorInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1400), true, 1.0));
            s.Monitors.Add(new MonitorInfo(1, @"\\.\DISPLAY2", new ScreenRect(-1920, 0, 1920, 1080), new ScreenRect(-1920, 0, 1920, 1040), false, 1.0));
            return s;
        }
    }

    public sealed class Input : IInputSimulator
    {
        public List<string> Calls { get; } = new();
        public ScreenPoint Cursor { get; set; } = new(500, 500);
        public bool FollowMoves { get; set; } = true;
        public Exception? ClickError { get; set; }

        public void MoveMouse(int x, int y) { Calls.Add($"move {x},{y}"); if (FollowMoves) Cursor = new ScreenPoint(x, y); }
        public void MoveMouseSmooth(int x, int y, int durationMs) { Calls.Add($"smooth {x},{y} {durationMs}"); if (FollowMoves) Cursor = new ScreenPoint(x, y); }
        public void MouseDown(MouseButton button) => Calls.Add($"down {button}");
        public void MouseUp(MouseButton button) => Calls.Add($"up {button}");
        public void Click(MouseButton button, int clicks)
        {
            if (ClickError != null) throw ClickError;
            Calls.Add($"click {button} {clicks}");
        }
        public void Scroll(int dx, int dy) => Calls.Add($"scroll {dx},{dy}");
        public void TypeText(string text, int delayMsPerChar) => Calls.Add($"type {text}|{delayMsPerChar}");
        public void PressCombo(KeyCombo combo) => Calls.Add($"keys {combo}");
        public void KeyDown(string key) => Calls.Add($"keydown {key}");
        public void KeyUp(string key) => Calls.Add($"keyup {key}");
        public void ReleaseAll() => Calls.Add("releaseall");
        public ScreenPoint GetCursorPosition() => Cursor;
    }

    public sealed class Windows : IWindowManager
    {
        public List<WindowInfo> List { get; } = new();
        public WindowInfo? Foreground { get; set; }
        public Func<int, int, WindowInfo?> At { get; set; } = (_, _) => null;
        public bool UacActive { get; set; }
        public List<nint> Focused { get; } = new();
        public bool FocusResult { get; set; } = true;
        public int WindowAtCalls { get; private set; }

        public IReadOnlyList<WindowInfo> ListWindows() => List;
        public WindowInfo? GetForegroundWindow() => Foreground;
        public WindowInfo? GetWindowAt(int x, int y) { WindowAtCalls++; return At(x, y); }
        public bool FocusWindow(nint handle) { Focused.Add(handle); return FocusResult; }
        public bool IsCurrentProcessElevated => false;
        public bool IsUacPromptActive() => UacActive;
    }

    public sealed class Ui : IUiInspector
    {
        public List<UiElementInfo> Elements { get; } = new();
        public List<(nint Window, int Max)> Requests { get; } = new();
        public int ElementAtCalls { get; private set; }
        public Func<int, int, CancellationToken, Task<UiElementInfo?>> At { get; set; } = (_, _, _) => Task.FromResult<UiElementInfo?>(null);

        public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct)
        {
            Requests.Add((window, maxElements));
            return Task.FromResult<IReadOnlyList<UiElementInfo>>(Elements.Take(maxElements).ToList());
        }

        public Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct)
        {
            ElementAtCalls++;
            return At(x, y, ct);
        }
    }

    public sealed class Launcher : IAppLauncher
    {
        public List<(string Target, string? Arguments, bool AllowElevation)> Calls { get; } = new();
        public LaunchResult Result { get; set; } = new(true, "", 1234);

        public LaunchResult Launch(string target, string? arguments, bool allowElevation)
        {
            Calls.Add((target, arguments, allowElevation));
            return Result;
        }
    }

    public sealed class Clipboard : IClipboardService
    {
        public string? Text { get; set; }
        public List<string> Sets { get; } = new();
        public string? GetText() => Text;
        public void SetText(string text) { Sets.Add(text); Text = text; }
    }

    public sealed class Shell : IShellRunner
    {
        public List<(string Command, string Shell, string WorkingDirectory, int TimeoutMs)> Calls { get; } = new();
        public ShellResult Result { get; set; } = new(0, "hello", "", false);

        public Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct)
        {
            Calls.Add((command, shell, workingDirectory, timeoutMs));
            return Task.FromResult(Result);
        }
    }

    public sealed class Guard : ISafetyGuard
    {
        public List<ProposedAction> Actions { get; } = new();
        public Func<ProposedAction, SafetyVerdict> Decide { get; set; } = _ => SafetyVerdict.Allowed;

        public Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct)
        {
            Actions.Add(action);
            return Task.FromResult(Decide(action));
        }
    }

    public sealed class Confirmation : IUserConfirmation
    {
        public ConfirmationChoice Choice { get; set; } = ConfirmationChoice.Allow;
        public List<ProposedAction> Asked { get; } = new();

        public Task<ConfirmationChoice> ConfirmAsync(ProposedAction action, CancellationToken ct)
        {
            Asked.Add(action);
            return Task.FromResult(Choice);
        }
    }

    public sealed class Observer : IInputActionObserver
    {
        public List<string> Events { get; } = new();
        public Action? OnBefore { get; set; }

        public Task BeforeInputAsync(ScreenPoint? target, CancellationToken ct)
        {
            Events.Add(target is { } t ? $"before {t.X},{t.Y}" : "before none");
            OnBefore?.Invoke();
            return Task.CompletedTask;
        }

        public void AfterInput() => Events.Add("after");
    }

    public sealed class Rig
    {
        public Screen Screen { get; init; } = Screen.Single();
        public Input Input { get; } = new();
        public Windows Windows { get; } = new();
        public Ui Ui { get; } = new();
        public Launcher Launcher { get; } = new();
        public Clipboard Clipboard { get; } = new();
        public Shell Shell { get; } = new();
        public Guard Guard { get; } = new();
        public Confirmation Confirmation { get; } = new();
        public Observer Observer { get; } = new();
        public AppSettings Settings { get; } = new();
        public AgentRunControl Control { get; } = new();
        public List<int> Delays { get; } = new();
        public ComputerToolHost Host { get; private set; } = null!;

        public DesktopServices Desktop => new(Screen, Input, Windows, Ui, Launcher, Clipboard, Shell);

        public Rig Build(ISafetyGuard? guard = null, bool withConfirmation = true, bool withObserver = true)
        {
            Host = new ComputerToolHost(Desktop, guard ?? Guard, () => Settings, Control,
                withConfirmation ? Confirmation : null, withObserver ? Observer : null)
            {
                Delay = (ms, ct) =>
                {
                    Delays.Add(ms);
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
            };
            return this;
        }

        public Task<ToolResult> Run(string tool, string json = "{}", CancellationToken ct = default) =>
            Host.ExecuteAsync(tool, JsonArgs.ParseArguments(json), ct);

        /// <summary>Calls that move the mouse, click or press keys (everything the fake input records).</summary>
        public IReadOnlyList<string> InputCalls => Input.Calls;
    }

    /// <summary>A rig with one 2560x1440 monitor, Notepad in front and under every point.</summary>
    public static Rig CreateRig(Action<Rig>? configure = null, ISafetyGuard? guard = null, bool withConfirmation = true, bool withObserver = true)
    {
        var rig = new Rig();
        rig.Windows.Foreground = Notepad;
        rig.Windows.At = (_, _) => Notepad;
        rig.Windows.List.Add(Notepad);
        configure?.Invoke(rig);
        return rig.Build(guard, withConfirmation, withObserver);
    }
}

public class ToolsTests
{
    private static ToolsTestFakes.Rig Rig(Action<ToolsTestFakes.Rig>? configure = null, ISafetyGuard? guard = null,
        bool withConfirmation = true, bool withObserver = true) =>
        ToolsTestFakes.CreateRig(configure, guard, withConfirmation, withObserver);

    // ------------------------------------------------------------ CoordinateMapper

    [Fact]
    public void Mapper_maps_screenshot_pixels_to_physical_pixels()
    {
        var m = new CoordinateMapper(new ScreenRect(0, 0, 2560, 1440), 1280, 720, CoordinateMode.ScreenshotPixels);
        Assert.Equal(new ScreenPoint(1280, 720), m.ToScreen(640, 360));
        Assert.Equal(new ScreenPoint(1024, 600), m.ToScreen(512, 300));
        Assert.Equal(new ScreenPoint(0, 0), m.ToScreen(0, 0));
        Assert.Equal(new ScreenPoint(1, 1), m.ToScreen(0.4, 0.4));
        Assert.Equal(1280, m.ModelWidth);
        Assert.Equal(720, m.ModelHeight);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    [InlineData(640, 360)]
    [InlineData(1279, 719)]
    public void Mapper_round_trips(int x, int y)
    {
        var m = new CoordinateMapper(new ScreenRect(0, 0, 2560, 1440), 1280, 720, CoordinateMode.ScreenshotPixels);
        var (bx, by) = m.FromScreen(m.ToScreen(x, y));
        Assert.Equal(x, bx, 3);
        Assert.Equal(y, by, 3);
    }

    [Fact]
    public void Mapper_clamps_into_source()
    {
        var m = new CoordinateMapper(new ScreenRect(0, 0, 2560, 1440), 1280, 720, CoordinateMode.ScreenshotPixels);
        Assert.Equal(new ScreenPoint(2559, 0), m.ToScreen(5000, -10));
        Assert.Equal(new ScreenPoint(0, 1439), m.ToScreen(-3, 99999));
        Assert.Equal(new ScreenPoint(2559, 1439), m.ToScreen(1280, 720));
        Assert.Equal(new ScreenPoint(0, 0), m.ToScreen(double.NaN, double.PositiveInfinity));
        Assert.False(m.IsInModelSpace(1280.5, 10));
        Assert.True(m.IsInModelSpace(1280, 720));
    }

    [Fact]
    public void Mapper_handles_negative_virtual_coordinates()
    {
        // Monitor left of the primary: its pixels have negative X.
        var m = new CoordinateMapper(new ScreenRect(-1920, 0, 1920, 1080), 1280, 720, CoordinateMode.ScreenshotPixels);
        Assert.Equal(new ScreenPoint(-1920, 0), m.ToScreen(0, 0));
        Assert.Equal(new ScreenPoint(-960, 540), m.ToScreen(640, 360));
        Assert.Equal(new ScreenPoint(-1, 1079), m.ToScreen(5000, 5000));
        var (x, y) = m.FromScreen(new ScreenPoint(-960, 540));
        Assert.Equal(640, x, 3);
        Assert.Equal(360, y, 3);
    }

    [Fact]
    public void Mapper_normalized_mode_uses_0_to_1000()
    {
        var m = new CoordinateMapper(new ScreenRect(0, 0, 2560, 1440), 1280, 720, CoordinateMode.Normalized1000);
        Assert.Equal(new ScreenPoint(1280, 720), m.ToScreen(500, 500));
        Assert.Equal(new ScreenPoint(256, 1296), m.ToScreen(100, 900));
        Assert.Equal(new ScreenPoint(2559, 1439), m.ToScreen(1000, 1000));
        var (x, y) = m.FromScreen(new ScreenPoint(1280, 360));
        Assert.Equal(500, x, 3);
        Assert.Equal(250, y, 3);
        Assert.Equal(1000, m.ModelWidth);
        Assert.Equal(1000, m.ModelHeight);
    }

    [Fact]
    public void Mapper_converts_rects_both_ways()
    {
        var m = new CoordinateMapper(new ScreenRect(-1920, 0, 1920, 1080), 1280, 720, CoordinateMode.ScreenshotPixels);
        var (x, y, w, h) = m.FromScreen(new ScreenRect(-1920 + 300, 150, 600, 300));
        Assert.Equal(200, x, 3);
        Assert.Equal(100, y, 3);
        Assert.Equal(400, w, 3);
        Assert.Equal(200, h, 3);

        Assert.Equal(new ScreenRect(-1620, 150, 600, 300), m.ToScreenRect(200, 100, 400, 200));
        Assert.Equal(new ScreenRect(-1620, 150, 1620, 930), m.ToScreenRect(200, 100, 5000, 5000));
        Assert.True(m.ToScreenRect(10, 10, 0, 5).IsEmpty);
    }

    [Theory]
    [InlineData(2560, 1440, 1280, 800, 1280, 720)]
    [InlineData(1920, 1080, 1280, 800, 1280, 720)]
    [InlineData(800, 600, 1280, 800, 800, 600)]
    [InlineData(1080, 1920, 1280, 800, 450, 800)]
    [InlineData(10000, 1, 100, 100, 100, 1)]
    [InlineData(0, 0, 1280, 800, 1, 1)]
    [InlineData(3000, 1000, 0, 0, 3000, 1000)]
    public void FitWithin_keeps_aspect_and_never_upscales(int sw, int sh, int mw, int mh, int ew, int eh)
    {
        Assert.Equal((ew, eh), CoordinateMapper.FitWithin(sw, sh, mw, mh));
    }

    [Fact]
    public void ResolveSource_follows_monitor_selection()
    {
        var screen = ToolsTestFakes.Screen.Dual();
        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), CoordinateMapper.ResolveSource(screen, new ScreenSettings { Monitor = MonitorSelection.Primary }));
        Assert.Equal(new ScreenRect(-1920, 0, 4480, 1440), CoordinateMapper.ResolveSource(screen, new ScreenSettings { Monitor = MonitorSelection.AllMonitors }));
        Assert.Equal(new ScreenRect(-1920, 0, 1920, 1080), CoordinateMapper.ResolveSource(screen, new ScreenSettings { Monitor = MonitorSelection.Specific, MonitorIndex = 1 }));
        // Out of range falls back to the primary monitor.
        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), CoordinateMapper.ResolveSource(screen, new ScreenSettings { Monitor = MonitorSelection.Specific, MonitorIndex = 7 }));
        Assert.Equal(new ScreenRect(0, 0, 2560, 1440), CoordinateMapper.ResolveSource(screen, new ScreenSettings { Monitor = MonitorSelection.Specific, MonitorIndex = -1 }));
    }

    [Fact]
    public void ForSettings_builds_mapper_from_settings()
    {
        var screen = ToolsTestFakes.Screen.Dual();
        var m = CoordinateMapper.ForSettings(screen, new ScreenSettings
        {
            Monitor = MonitorSelection.Specific, MonitorIndex = 1, MaxImageWidth = 1280, MaxImageHeight = 800, Coordinates = CoordinateMode.Normalized1000,
        });
        Assert.Equal(new ScreenRect(-1920, 0, 1920, 1080), m.Source);
        Assert.Equal(1280, m.ImageWidth);
        Assert.Equal(720, m.ImageHeight);
        Assert.Equal(CoordinateMode.Normalized1000, m.Mode);
    }

    [Fact]
    public void CurrentMapper_and_DescribeScreen_use_live_settings()
    {
        var rig = Rig();
        Assert.Equal(1280, rig.Host.CurrentMapper().ImageWidth);
        rig.Settings.Screen.MaxImageWidth = 640;
        rig.Settings.Screen.MaxImageHeight = 640;
        Assert.Equal(640, rig.Host.CurrentMapper().ImageWidth);
        Assert.Equal(360, rig.Host.CurrentMapper().ImageHeight);
        Assert.Equal("screenshots are 640x360 and show the primary monitor (2560x1440 physical pixels)", rig.Host.DescribeScreen());
    }

    // ------------------------------------------------------------ tool list

    [Fact]
    public void GetTools_filters_by_safety_settings()
    {
        var rig = Rig();
        var names = rig.Host.GetTools().Select(t => t.Name).ToList();
        Assert.Equal(new[]
        {
            "screenshot", "zoom", "click", "move_mouse", "drag", "scroll", "type_text", "press_keys", "wait",
            "list_windows", "focus_window", "launch", "ui_elements", "get_clipboard", "set_clipboard",
        }, names);

        rig.Settings.Safety.AllowAppLaunch = false;
        rig.Settings.Safety.AllowClipboard = false;
        rig.Settings.Safety.AllowShellCommands = true;
        names = rig.Host.GetTools().Select(t => t.Name).ToList();
        Assert.DoesNotContain("launch", names);
        Assert.DoesNotContain("get_clipboard", names);
        Assert.DoesNotContain("set_clipboard", names);
        Assert.Contains("run_command", names);
    }

    [Fact]
    public void GetTools_schemas_describe_every_parameter()
    {
        var rig = Rig(r => r.Settings.Safety.AllowShellCommands = true);
        foreach (var tool in rig.Host.GetTools())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Description), tool.Name);
            Assert.Equal("object", tool.InputSchema.GetProperty("type").GetString());
            var props = tool.InputSchema.GetProperty("properties");
            foreach (var p in props.EnumerateObject())
            {
                Assert.True(p.Value.TryGetProperty("description", out var d) && d.GetString()!.Length > 5, $"{tool.Name}.{p.Name} has no description");
                Assert.True(p.Value.TryGetProperty("type", out _), $"{tool.Name}.{p.Name} has no type");
            }
            if (tool.InputSchema.TryGetProperty("required", out var required))
                foreach (var r in required.EnumerateArray())
                    Assert.True(props.TryGetProperty(r.GetString()!, out _), $"{tool.Name}: required {r} missing from properties");
        }

        var click = rig.Host.GetTools().Single(t => t.Name == "click").InputSchema;
        Assert.Equal(new[] { "x", "y" }, click.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(new[] { "left", "right", "middle" }, click.GetProperty("properties").GetProperty("button").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void GetTools_descriptions_state_coordinate_space_and_screenshot_behavior()
    {
        var rig = Rig();
        string D(string name) => rig.Host.GetTools().Single(t => t.Name == name).Description;

        foreach (var name in new[] { "screenshot", "zoom", "click", "move_mouse", "drag", "scroll" })
            Assert.Contains("pixels of the full screenshot", D(name));
        foreach (var name in new[] { "click", "move_mouse", "drag", "scroll", "type_text", "press_keys", "focus_window", "launch" })
            Assert.Contains("fresh screenshot", D(name));
        Assert.Contains("full-screenshot coordinates", D("zoom"));

        rig.Settings.Screen.Coordinates = CoordinateMode.Normalized1000;
        foreach (var name in new[] { "screenshot", "zoom", "click", "move_mouse", "drag", "scroll" })
            Assert.Contains("0-1000", D(name));
        var xDesc = rig.Host.GetTools().Single(t => t.Name == "click").InputSchema.GetProperty("properties").GetProperty("x").GetProperty("description").GetString();
        Assert.Contains("0-1000", xDesc);

        rig.Settings.Screen.ScreenshotAfterAction = false;
        Assert.Contains("call screenshot", D("click"));
        Assert.DoesNotContain("fresh screenshot", D("click"));
    }

    [Fact]
    public async Task Unknown_and_disabled_tools_return_errors()
    {
        var rig = Rig();
        var unknown = await rig.Run("format_disk");
        Assert.True(unknown.IsError);
        Assert.Contains("Unknown tool 'format_disk'", unknown.Text);
        Assert.Contains("screenshot", unknown.Text);

        var shell = await rig.Run("run_command", "{\"command\":\"dir\"}");
        Assert.True(shell.IsError);
        Assert.Contains("turned off", shell.Text);
        Assert.Empty(rig.Shell.Calls);

        rig.Settings.Safety.AllowAppLaunch = false;
        var launch = await rig.Run("launch", "{\"target\":\"notepad\"}");
        Assert.True(launch.IsError);
        Assert.Empty(rig.Launcher.Calls);
    }

    // ------------------------------------------------------------ screenshot / zoom

    [Fact]
    public async Task Screenshot_returns_image_and_state_line()
    {
        var rig = Rig();
        var result = await rig.Run("screenshot");

        Assert.False(result.IsError);
        var image = Assert.Single(result.Images);
        Assert.Equal("image/jpeg", image.MediaType);
        Assert.Equal(1280, image.Width);
        Assert.Equal(720, image.Height);
        Assert.Equal("Screenshot 1280x720 of the primary monitor (2560x1440 px). Active window: 'Untitled - Notepad' (notepad). Mouse at (250, 250).", result.Text);

        var req = Assert.Single(rig.Screen.Requests);
        Assert.Equal(new CaptureRequest(new ScreenRect(0, 0, 2560, 1440), 1280, 720, ImageFormatKind.Jpeg, 75, true, 0), req);
        Assert.Empty(rig.InputCalls);
    }

    [Fact]
    public async Task Screenshot_request_follows_screen_settings()
    {
        var rig = Rig(r =>
        {
            r.Settings.Screen.Format = "PNG";
            r.Settings.Screen.JpegQuality = 150;
            r.Settings.Screen.DrawCursor = false;
            r.Settings.Screen.GridSpacing = 100;
            r.Settings.Screen.MaxImageWidth = 1024;
            r.Settings.Screen.MaxImageHeight = 1024;
        });
        var result = await rig.Run("screenshot");
        Assert.Equal(new CaptureRequest(new ScreenRect(0, 0, 2560, 1440), 1024, 576, ImageFormatKind.Png, 100, false, 100), rig.Screen.Requests.Single());
        Assert.Equal("image/png", result.Images.Single().MediaType);
    }

    [Fact]
    public async Task Screenshot_text_uses_normalized_coordinates_and_handles_other_monitors()
    {
        var rig = Rig(r =>
        {
            r.Settings.Screen.Coordinates = CoordinateMode.Normalized1000;
            r.Input.Cursor = new ScreenPoint(1280, 360);
        });
        var result = await rig.Run("screenshot");
        Assert.Contains("coordinates 0-1000 on both axes", result.Text);
        Assert.Contains("Mouse at (500, 250).", result.Text);
        Assert.Equal(0, rig.Screen.Requests.Single().GridSpacing);

        rig.Input.Cursor = new ScreenPoint(-500, 100);
        result = await rig.Run("screenshot");
        Assert.Contains("Mouse is outside the captured area.", result.Text);
    }

    [Fact]
    public async Task Screenshot_describes_specific_monitor()
    {
        var rig = new ToolsTestFakes.Rig { Screen = ToolsTestFakes.Screen.Dual() };
        rig.Settings.Screen.Monitor = MonitorSelection.Specific;
        rig.Settings.Screen.MonitorIndex = 1;
        rig.Build();
        var result = await rig.Run("screenshot");
        Assert.StartsWith("Screenshot 1280x720 of monitor 2 (1920x1080 px).", result.Text);
        Assert.Contains("Active window: none.", result.Text);
        Assert.Equal(new ScreenRect(-1920, 0, 1920, 1080), rig.Screen.Requests.Single().Source);
    }

    [Fact]
    public async Task Screenshot_for_text_only_model_returns_windows_and_elements()
    {
        var rig = Rig(r =>
        {
            r.Settings.Profiles.Add(new ProviderProfile { Id = "local", SupportsVision = false });
            r.Settings.ActiveProfileId = "local";
            r.Windows.List.Add(ToolsTestFakes.Window("Calculator", "calc", bounds: new ScreenRect(1280, 0, 640, 720)));
            r.Windows.List.Add(ToolsTestFakes.Window("DeskPilot", "DeskPilot", pid: Environment.ProcessId));
            r.Ui.Elements.Add(new UiElementInfo("Save", "Button", new ScreenRect(100, 100, 200, 60), "save", true, true, null));
            r.Ui.Elements.Add(new UiElementInfo("Search", "Edit", new ScreenRect(400, 200, 300, 40), null, false, true, "abc"));
        });

        var result = await rig.Run("screenshot");
        Assert.False(result.IsError);
        Assert.Empty(result.Images);
        Assert.Empty(rig.Screen.Requests);
        Assert.Contains("cannot see images", result.Text);
        Assert.Contains("- 'Untitled - Notepad' [notepad] at (0, 0) 640x360 (active)", result.Text);
        Assert.Contains("- 'Calculator' [calc] at (640, 0) 320x360", result.Text);
        Assert.DoesNotContain("DeskPilot", result.Text);
        Assert.Contains("[1] Button 'Save' at (100, 65) size 100x30", result.Text);
        Assert.Contains("[2] Edit 'Search' at (275, 110) size 150x20 (disabled) value \"abc\"", result.Text);
        Assert.Equal((ToolsTestFakes.Notepad.Handle, 80), rig.Ui.Requests.Single());

        var zoom = await rig.Run("zoom", "{\"x\":0,\"y\":0,\"width\":100,\"height\":100}");
        Assert.True(zoom.IsError);
        Assert.Contains("ui_elements", zoom.Text);

        var afterClick = await rig.Run("click", "{\"x\":10,\"y\":10}");
        Assert.Empty(afterClick.Images);
        Assert.Contains("[1] Button 'Save'", afterClick.Text);
    }

    [Fact]
    public async Task Zoom_captures_region_at_native_resolution()
    {
        var rig = Rig();
        var result = await rig.Run("zoom", "{\"x\":100,\"y\":100,\"width\":200,\"height\":100}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new CaptureRequest(new ScreenRect(200, 200, 400, 200), 400, 200, ImageFormatKind.Jpeg, 75, true, 0), rig.Screen.Requests.Single());
        var image = Assert.Single(result.Images);
        Assert.Equal(400, image.Width);
        Assert.Contains("(2x the detail of the screenshot)", result.Text);
        Assert.Contains("full-screenshot coordinates", result.Text);
        Assert.Contains("(100 + u * 0.5, 100 + v * 0.5)", result.Text);
        Assert.Empty(rig.InputCalls);
    }

    [Fact]
    public async Task Zoom_large_region_is_fitted_and_clipped()
    {
        var rig = Rig();
        var result = await rig.Run("zoom", "{\"x\":640,\"y\":0,\"width\":1000,\"height\":720}");
        Assert.False(result.IsError, result.Text);
        var req = rig.Screen.Requests.Single();
        Assert.Equal(new ScreenRect(1280, 0, 1280, 1440), req.Source);
        Assert.Equal((711, 800), (req.TargetWidth, req.TargetHeight));
        Assert.Contains("clipped to the screen", result.Text);
    }

    [Theory]
    [InlineData("{\"x\":10,\"y\":10,\"width\":0,\"height\":10}", "greater than 0")]
    [InlineData("{\"x\":10,\"y\":10,\"width\":50}", "Missing or invalid region")]
    [InlineData("{\"x\":1500,\"y\":10,\"width\":50,\"height\":50}", "outside the screen")]
    [InlineData("{\"x\":-5,\"y\":10,\"width\":50,\"height\":50}", "outside the screen")]
    [InlineData("{\"x\":10,\"y\":10,\"width\":0.2,\"height\":0.2}", "too small")]
    public async Task Zoom_validates_region(string json, string expected)
    {
        var rig = Rig();
        var result = await rig.Run("zoom", json);
        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Contains("zoom takes x, y, width and height", result.Text);
        Assert.Empty(rig.Screen.Requests);
    }

    // ------------------------------------------------------------ click and the action pipeline

    [Fact]
    public async Task Click_lands_on_the_right_physical_pixel()
    {
        var rig = Rig();
        var result = await rig.Run("click", "{\"x\":512,\"y\":300}");

        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "move 1024,600", "click Left 1" }, rig.InputCalls);
        var action = Assert.Single(rig.Guard.Actions);
        Assert.Equal("click", action.Tool);
        Assert.Equal("Click left at (512, 300) on 'Untitled - Notepad' (notepad)", action.Summary);
        Assert.Equal(ActionRisk.Medium, action.Risk);
        Assert.Equal(new ScreenPoint(1024, 600), action.Target);
        Assert.StartsWith("Clicked left at (512, 300) on 'Untitled - Notepad' (notepad).", result.Text);
        Assert.Contains("Screenshot 1280x720", result.Text);
        Assert.Single(result.Images);
        Assert.Equal(new[] { ComputerToolHost.ClickPauseMs, 450 }, rig.Delays);
        Assert.Equal(new[] { "before 1024,600", "after" }, rig.Observer.Events);
    }

    [Fact]
    public async Task Click_in_normalized_mode_maps_0_to_1000()
    {
        var rig = Rig(r => r.Settings.Screen.Coordinates = CoordinateMode.Normalized1000);
        var result = await rig.Run("click", "{\"x\":500,\"y\":250}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal("move 1280,360", rig.InputCalls[0]);

        var outside = await rig.Run("click", "{\"x\":1001,\"y\":250}");
        Assert.True(outside.IsError);
        Assert.Contains("x and y from 0 to 1000", outside.Text);
    }

    [Fact]
    public async Task Click_on_monitor_left_of_primary_uses_negative_coordinates()
    {
        var rig = new ToolsTestFakes.Rig { Screen = ToolsTestFakes.Screen.Dual() };
        rig.Settings.Screen.Monitor = MonitorSelection.Specific;
        rig.Settings.Screen.MonitorIndex = 1;
        rig.Build();
        await rig.Run("click", "{\"x\":0,\"y\":0}");
        await rig.Run("click", "{\"x\":640,\"y\":360}");
        Assert.Equal(new[] { "move -1920,0", "click Left 1", "move -960,540", "click Left 1" }, rig.InputCalls);
    }

    [Fact]
    public async Task Click_holds_modifiers_and_supports_double_click()
    {
        var rig = Rig();
        var result = await rig.Run("click", "{\"x\":10,\"y\":20,\"button\":\"right\",\"clicks\":2,\"modifiers\":[\"Shift\",\"control\"]}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "move 20,40", "keydown ctrl", "keydown shift", "click Right 2", "keyup shift", "keyup ctrl" }, rig.InputCalls);
        Assert.Equal("Double-click right with ctrl+shift at (10, 20) on 'Untitled - Notepad' (notepad)", rig.Guard.Actions.Single().Summary);
    }

    [Theory]
    [InlineData("{\"x\":10}", "'x' and 'y' must be numbers")]
    [InlineData("{\"x\":\"abc\",\"y\":5}", "'x' and 'y' must be numbers")]
    [InlineData("{\"x\":1300,\"y\":5}", "outside the screen")]
    [InlineData("{\"x\":10,\"y\":10,\"button\":\"back\"}", "Unknown button 'back'")]
    [InlineData("{\"x\":10,\"y\":10,\"clicks\":4}", "'clicks' must be 1, 2")]
    [InlineData("{\"x\":10,\"y\":10,\"clicks\":1.5}", "'clicks' must be 1, 2")]
    [InlineData("{\"x\":10,\"y\":10,\"modifiers\":[\"hyper\"]}", "Unknown modifier 'hyper'")]
    public async Task Click_validates_arguments(string json, string expected)
    {
        var rig = Rig();
        var result = await rig.Run("click", json);
        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Contains("click takes x and y", result.Text);
        Assert.Empty(rig.InputCalls);
        Assert.Empty(rig.Guard.Actions);
    }

    [Fact]
    public async Task Click_accepts_lenient_numbers()
    {
        var rig = Rig();
        var result = await rig.Run("click", "{\"x\":\"100px\",\"y\":\"50.4\",\"clicks\":\"3\"}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "move 200,101", "click Left 3" }, rig.InputCalls);
    }

    [Fact]
    public async Task Dry_run_performs_no_input_but_keeps_screenshots()
    {
        var rig = Rig(r => r.Settings.Safety.DryRun = true);
        var results = new[]
        {
            await rig.Run("click", "{\"x\":512,\"y\":300}"),
            await rig.Run("move_mouse", "{\"x\":5,\"y\":5}"),
            await rig.Run("drag", "{\"from_x\":1,\"from_y\":1,\"to_x\":50,\"to_y\":50}"),
            await rig.Run("scroll", "{\"direction\":\"down\"}"),
            await rig.Run("type_text", "{\"text\":\"hello\",\"press_enter\":true}"),
            await rig.Run("press_keys", "{\"keys\":\"ctrl+s\"}"),
            await rig.Run("focus_window", "{\"title\":\"notepad\"}"),
            await rig.Run("launch", "{\"target\":\"calc\"}"),
            await rig.Run("set_clipboard", "{\"text\":\"x\"}"),
        };

        Assert.All(results, r => Assert.False(r.IsError, r.Text));
        Assert.All(results, r => Assert.StartsWith("DRY RUN: would ", r.Text));
        Assert.StartsWith("DRY RUN: would click left at (512, 300) on 'Untitled - Notepad' (notepad)", results[0].Text);
        Assert.Empty(rig.InputCalls);
        Assert.Empty(rig.Windows.Focused);
        Assert.Empty(rig.Launcher.Calls);
        Assert.Empty(rig.Clipboard.Sets);
        Assert.Empty(rig.Observer.Events);
        Assert.Single(results[0].Images);
        Assert.Empty(rig.Delays);

        rig.Settings.Safety.AllowShellCommands = true;
        var cmd = await rig.Run("run_command", "{\"command\":\"Get-Date\"}");
        Assert.StartsWith("DRY RUN: would run this powershell command", cmd.Text);
        Assert.Empty(rig.Shell.Calls);
    }

    [Fact]
    public async Task Safety_denial_blocks_the_action()
    {
        var rig = Rig();
        rig.Guard.Decide = _ => SafetyVerdict.Denied("not today.");
        var result = await rig.Run("click", "{\"x\":10,\"y\":10}");
        Assert.True(result.IsError);
        Assert.Equal("Blocked by DeskPilot safety: not today.", result.Text);
        Assert.Empty(rig.InputCalls);
        Assert.Empty(rig.Observer.Events);
        Assert.Empty(rig.Confirmation.Asked);
    }

    [Fact]
    public async Task Confirmation_allow_and_deny()
    {
        var rig = Rig();
        rig.Guard.Decide = _ => SafetyVerdict.Confirm("risky");

        rig.Confirmation.Choice = ConfirmationChoice.Allow;
        var allowed = await rig.Run("click", "{\"x\":10,\"y\":10}");
        Assert.False(allowed.IsError, allowed.Text);
        Assert.Single(rig.Confirmation.Asked);
        Assert.Equal(2, rig.InputCalls.Count);

        rig.Confirmation.Choice = ConfirmationChoice.Deny;
        var denied = await rig.Run("click", "{\"x\":10,\"y\":10}");
        Assert.True(denied.IsError);
        Assert.StartsWith("The user declined this action", denied.Text);
        Assert.Equal(2, rig.Confirmation.Asked.Count);
        Assert.Equal(2, rig.InputCalls.Count);
        Assert.False(rig.Control.AllowAllThisTurn);
    }

    [Fact]
    public async Task Confirmation_allow_all_skips_later_prompts_this_turn()
    {
        var rig = Rig();
        rig.Guard.Decide = _ => SafetyVerdict.Confirm("risky");
        rig.Confirmation.Choice = ConfirmationChoice.AllowAllThisTurn;

        Assert.False((await rig.Run("click", "{\"x\":10,\"y\":10}")).IsError);
        Assert.True(rig.Control.AllowAllThisTurn);
        rig.Confirmation.Choice = ConfirmationChoice.Deny;
        Assert.False((await rig.Run("press_keys", "{\"keys\":\"enter\"}")).IsError);
        Assert.Single(rig.Confirmation.Asked);

        rig.Control.BeginTurn();
        Assert.True((await rig.Run("press_keys", "{\"keys\":\"enter\"}")).IsError);
        Assert.Equal(2, rig.Confirmation.Asked.Count);
    }

    [Fact]
    public async Task Confirmation_without_a_prompt_is_denied()
    {
        var rig = Rig(withConfirmation: false);
        rig.Guard.Decide = _ => SafetyVerdict.Confirm("This high-risk action needs the user's confirmation.");
        var result = await rig.Run("press_keys", "{\"keys\":\"enter\"}");
        Assert.True(result.IsError);
        Assert.StartsWith("Blocked by DeskPilot safety:", result.Text);
        Assert.Contains("no confirmation prompt", result.Text);
        Assert.Empty(rig.InputCalls);
    }

    [Fact]
    public async Task Failsafe_corner_stops_the_agent()
    {
        var rig = Rig(r => r.Input.Cursor = new ScreenPoint(2, 1));
        var result = await rig.Run("click", "{\"x\":600,\"y\":300}");
        Assert.True(result.IsError);
        Assert.StartsWith("STOPPED: Failsafe: mouse moved to the top-left corner", result.Text);
        Assert.True(rig.Control.IsStopRequested);
        Assert.Equal("Failsafe: mouse moved to the top-left corner", rig.Control.StopReason);
        Assert.Empty(rig.InputCalls);
        Assert.Empty(rig.Guard.Actions);

        // Observation tools are not input actions.
        Assert.False((await rig.Run("screenshot")).IsError);
    }

    [Fact]
    public async Task Failsafe_ignores_the_corner_when_DeskPilot_put_the_cursor_there()
    {
        var rig = Rig();
        Assert.False((await rig.Run("click", "{\"x\":0,\"y\":0}")).IsError);
        Assert.Equal(new ScreenPoint(0, 0), rig.Input.Cursor);
        var second = await rig.Run("click", "{\"x\":0,\"y\":0}");
        Assert.False(second.IsError, second.Text);
        Assert.False(rig.Control.IsStopRequested);
    }

    [Fact]
    public async Task Failsafe_can_be_turned_off()
    {
        var rig = Rig(r =>
        {
            r.Input.Cursor = new ScreenPoint(0, 0);
            r.Settings.Safety.FailsafeCorner = false;
        });
        Assert.False((await rig.Run("click", "{\"x\":600,\"y\":300}")).IsError);
        Assert.False(rig.Control.IsStopRequested);
    }

    [Fact]
    public async Task Failsafe_uses_the_primary_monitor_origin()
    {
        var rig = new ToolsTestFakes.Rig { Screen = ToolsTestFakes.Screen.Dual() };
        rig.Input.Cursor = new ScreenPoint(-1920, 0);
        rig.Build();
        Assert.False((await rig.Run("click", "{\"x\":600,\"y\":300}")).IsError);
        rig.Input.Cursor = new ScreenPoint(1, 3);
        Assert.True((await rig.Run("click", "{\"x\":600,\"y\":300}")).IsError);
        Assert.True(rig.Control.IsStopRequested);
    }

    [Fact]
    public async Task Stop_on_user_mouse_move()
    {
        var rig = Rig(r => r.Settings.Safety.StopOnUserMouseMove = true);
        Assert.False((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);
        Assert.Equal(new ScreenPoint(200, 200), rig.Input.Cursor);

        rig.Input.Cursor = new ScreenPoint(208, 205); // small jitter is fine
        Assert.False((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);

        rig.Input.Cursor = new ScreenPoint(260, 200); // the user grabbed the mouse
        var stopped = await rig.Run("type_text", "{\"text\":\"hi\"}");
        Assert.True(stopped.IsError);
        Assert.StartsWith("STOPPED:", stopped.Text);
        Assert.Contains("user moved the mouse", rig.Control.StopReason);
        Assert.DoesNotContain(rig.InputCalls, c => c.StartsWith("type", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Stop_on_user_mouse_move_forgets_the_position_between_turns()
    {
        var rig = Rig(r => r.Settings.Safety.StopOnUserMouseMove = true);
        rig.Control.BeginTurn();
        rig.Control.RegisterStep();
        Assert.False((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);

        rig.Input.Cursor = new ScreenPoint(900, 900); // the user used the mouse between turns
        rig.Control.BeginTurn();
        rig.Control.RegisterStep();
        Assert.False((await rig.Run("click", "{\"x\":300,\"y\":300}")).IsError);

        rig.Input.Cursor = new ScreenPoint(10, 900); // ... and now during the turn
        rig.Control.RegisterStep();
        Assert.True((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);
        Assert.True(rig.Control.IsStopRequested);
    }

    [Fact]
    public async Task Input_failure_releases_keys_and_still_notifies_observer()
    {
        var rig = Rig(r => r.Input.ClickError = new InvalidOperationException("SendInput failed"));
        var result = await rig.Run("click", "{\"x\":10,\"y\":10,\"modifiers\":[\"ctrl\"]}");
        Assert.True(result.IsError);
        Assert.Contains("SendInput failed", result.Text);
        Assert.Equal(new[] { "move 20,20", "keydown ctrl", "keyup ctrl", "releaseall" }, rig.InputCalls);
        Assert.Equal(new[] { "before 20,20", "after" }, rig.Observer.Events);
    }

    [Fact]
    public async Task Overlay_under_the_target_is_moved_aside_before_the_safety_check()
    {
        bool overlayHidden = false;
        var overlay = ToolsTestFakes.Window("DeskPilot overlay", "DeskPilot", pid: 999);
        var rig = Rig(r =>
        {
            r.Windows.At = (_, _) => overlayHidden ? ToolsTestFakes.Notepad : overlay;
            r.Observer.OnBefore = () => overlayHidden = true;
        });
        rig.Host.OwnProcessId = 999;

        var result = await rig.Run("click", "{\"x\":100,\"y\":100}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "before 200,200", "after" }, rig.Observer.Events);
        Assert.Contains("'Untitled - Notepad'", rig.Guard.Actions.Single().Summary);
    }

    [Fact]
    public async Task Overlay_is_restored_when_the_action_is_blocked()
    {
        bool overlayHidden = false;
        var overlay = ToolsTestFakes.Window("DeskPilot overlay", "DeskPilot", pid: 999);
        var rig = Rig(r =>
        {
            r.Windows.At = (_, _) => overlayHidden ? ToolsTestFakes.Notepad : overlay;
            r.Observer.OnBefore = () => overlayHidden = true;
        });
        rig.Host.OwnProcessId = 999;
        rig.Guard.Decide = _ => SafetyVerdict.Denied("no");
        Assert.True((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);
        Assert.Equal(new[] { "before 200,200", "after" }, rig.Observer.Events);
    }

    [Fact]
    public async Task Real_safety_guard_blocks_elevated_window_through_the_host()
    {
        var admin = ToolsTestFakes.Window("Administrator: Command Prompt", "cmd", elevated: true);
        var rig = new ToolsTestFakes.Rig();
        rig.Windows.At = (_, _) => admin;
        rig.Windows.Foreground = admin;
        rig.Build(new SafetyGuard(() => rig.Settings, rig.Windows, rig.Ui));

        var result = await rig.Run("click", "{\"x\":100,\"y\":100}");
        Assert.True(result.IsError);
        Assert.StartsWith("Blocked by DeskPilot safety:", result.Text);
        Assert.Contains("Administrator mode", result.Text);
        Assert.Empty(rig.InputCalls);

        rig.Settings.Safety.AllowAdmin = true;
        Assert.False((await rig.Run("click", "{\"x\":100,\"y\":100}")).IsError);
    }

    [Fact]
    public async Task Screenshot_after_action_respects_settings()
    {
        var rig = Rig(r => r.Settings.Screen.ActionSettleDelayMs = 200);
        var moved = await rig.Run("move_mouse", "{\"x\":10,\"y\":10}");
        Assert.Single(moved.Images);
        Assert.Equal(new[] { 200 }, rig.Delays);

        rig.Delays.Clear();
        var launched = await rig.Run("launch", "{\"target\":\"notepad\"}");
        Assert.Single(launched.Images);
        Assert.Equal(new[] { ComputerToolHost.LaunchMinSettleMs }, rig.Delays);

        rig.Settings.Screen.ScreenshotAfterAction = false;
        rig.Delays.Clear();
        var plain = await rig.Run("move_mouse", "{\"x\":10,\"y\":10}");
        Assert.Empty(plain.Images);
        Assert.Equal("Moved the mouse to (10, 10) on 'Untitled - Notepad' (notepad).", plain.Text);
        Assert.Empty(rig.Delays);
    }

    [Fact]
    public async Task Failed_screenshot_after_a_successful_action_is_not_an_error()
    {
        var rig = Rig();
        rig.Screen.CaptureError = new InvalidOperationException("capture broke");
        var result = await rig.Run("move_mouse", "{\"x\":10,\"y\":10}");
        Assert.False(result.IsError);
        Assert.Contains("capture broke", result.Text);
        Assert.Equal(new[] { "move 20,20" }, rig.InputCalls);
    }

    [Fact]
    public async Task Desktop_exceptions_become_error_results()
    {
        var rig = Rig();
        rig.Screen.CaptureError = new InvalidOperationException("boom");
        var result = await rig.Run("screenshot");
        Assert.True(result.IsError);
        Assert.Equal("screenshot failed: boom", result.Text);

        rig.Guard.Decide = _ => throw new OperationCanceledException("not ours");
        var guarded = await rig.Run("move_mouse", "{\"x\":10,\"y\":10}");
        Assert.True(guarded.IsError);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var rig = Rig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Run("click", "{\"x\":10,\"y\":10}", cts.Token));

        using var during = new CancellationTokenSource();
        rig.Host.Delay = (_, ct) =>
        {
            during.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Run("wait", "{\"seconds\":2}", during.Token));
    }

    // ------------------------------------------------------------ other pointer actions

    [Fact]
    public async Task Move_mouse_is_low_risk()
    {
        var rig = Rig();
        await rig.Run("move_mouse", "{\"x\":640,\"y\":360}");
        Assert.Equal(new[] { "move 1280,720" }, rig.InputCalls);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(ActionRisk.Low, action.Risk);
        Assert.Equal(new ScreenPoint(1280, 720), action.Target);
        Assert.True((await rig.Run("move_mouse", "{}")).IsError);
    }

    [Fact]
    public async Task Drag_presses_moves_smoothly_and_releases()
    {
        var rig = Rig();
        var result = await rig.Run("drag", "{\"from_x\":100,\"from_y\":100,\"to_x\":300,\"to_y\":200}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "move 200,200", "down Left", $"smooth 600,400 {ComputerToolHost.DragDurationMs}", "up Left" }, rig.InputCalls);

        Assert.Equal(2, rig.Guard.Actions.Count);
        var drop = rig.Guard.Actions[0];
        var main = rig.Guard.Actions[1];
        Assert.Equal(ActionRisk.None, drop.Risk);
        Assert.Equal(new ScreenPoint(600, 400), drop.Target);
        Assert.Equal(ActionRisk.Medium, main.Risk);
        Assert.Equal(new ScreenPoint(200, 200), main.Target);
        Assert.Equal("Drag left from (100, 100) on 'Untitled - Notepad' (notepad) to (300, 200) on 'Untitled - Notepad' (notepad)", main.Summary);
    }

    [Fact]
    public async Task Drag_is_blocked_when_the_drop_point_is_denied()
    {
        var rig = Rig();
        rig.Guard.Decide = a => a.Target == new ScreenPoint(600, 400) ? SafetyVerdict.Denied("drop target is off limits.") : SafetyVerdict.Allowed;
        var result = await rig.Run("drag", "{\"from_x\":100,\"from_y\":100,\"to_x\":300,\"to_y\":200,\"button\":\"right\"}");
        Assert.True(result.IsError);
        Assert.Equal("Blocked by DeskPilot safety: drop target is off limits.", result.Text);
        Assert.Empty(rig.InputCalls);

        var missing = await rig.Run("drag", "{\"from_x\":1,\"from_y\":1,\"to_x\":5}");
        Assert.Contains("drag takes from_x, from_y, to_x and to_y", missing.Text);
    }

    [Fact]
    public async Task Scroll_directions_amounts_and_points()
    {
        var rig = Rig();
        Assert.False((await rig.Run("scroll", "{\"direction\":\"down\"}")).IsError);
        Assert.Equal(new ScreenPoint(500, 500), rig.Guard.Actions[^1].Target); // cursor position
        Assert.Equal(ActionRisk.Low, rig.Guard.Actions[^1].Risk);
        Assert.False((await rig.Run("scroll", "{\"direction\":\"UP\",\"amount\":5,\"x\":10,\"y\":20}")).IsError);
        Assert.Equal(new ScreenPoint(20, 40), rig.Guard.Actions[^1].Target);
        Assert.False((await rig.Run("scroll", "{\"direction\":\"left\",\"amount\":1}")).IsError);
        Assert.False((await rig.Run("scroll", "{\"direction\":\"right\",\"amount\":\"30\"}")).IsError);
        Assert.Equal(new[] { "scroll 0,3", "move 20,40", "scroll 0,-5", "scroll -1,0", "scroll 30,0" }, rig.InputCalls);
        Assert.Equal("Scroll left 1 notch at the mouse position on 'Untitled - Notepad' (notepad)", rig.Guard.Actions[2].Summary);
    }

    [Theory]
    [InlineData("{}", "'direction' is missing")]
    [InlineData("{\"direction\":\"sideways\"}", "not 'sideways'")]
    [InlineData("{\"direction\":\"down\",\"amount\":31}", "'amount' must be a whole number from 1 to 30")]
    [InlineData("{\"direction\":\"down\",\"amount\":0}", "'amount' must be a whole number from 1 to 30")]
    [InlineData("{\"direction\":\"down\",\"x\":10}", "Give both x and y")]
    public async Task Scroll_validates_arguments(string json, string expected)
    {
        var rig = Rig();
        var result = await rig.Run("scroll", json);
        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Empty(rig.InputCalls);
    }

    // ------------------------------------------------------------ keyboard

    [Fact]
    public async Task Type_text_types_with_configured_delay()
    {
        var rig = Rig(r => r.Settings.Screen.TypingDelayMs = 7);
        var result = await rig.Run("type_text", "{\"text\":\"hello world\"}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { "type hello world|7" }, rig.InputCalls);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(ActionRisk.Medium, action.Risk);
        Assert.Equal("hello world", action.Text);
        Assert.Null(action.Target);
        Assert.Equal("Type \"hello world\" (11 characters) into 'Untitled - Notepad' (notepad)", action.Summary);
        Assert.Equal(new[] { "before none", "after" }, rig.Observer.Events);
    }

    [Fact]
    public async Task Type_text_with_enter_is_high_risk()
    {
        var rig = Rig();
        await rig.Run("type_text", "{\"text\":\"search me\",\"press_enter\":true}");
        Assert.Equal(new[] { "type search me|4", "keys enter" }, rig.InputCalls);
        Assert.Equal(ActionRisk.High, rig.Guard.Actions[^1].Risk);
        Assert.EndsWith(", then press Enter", rig.Guard.Actions[^1].Summary);

        await rig.Run("type_text", "{\"text\":\"line1\\nline2\"}");
        Assert.Equal(ActionRisk.High, rig.Guard.Actions[^1].Risk);
        Assert.Contains("line1\\nline2", rig.Guard.Actions[^1].Summary);

        rig.Input.Calls.Clear();
        await rig.Run("type_text", "{\"text\":\"\",\"press_enter\":\"yes\"}");
        Assert.Equal(new[] { "keys enter" }, rig.InputCalls);
    }

    [Fact]
    public async Task Type_text_enforces_length_cap()
    {
        var rig = Rig();
        var tooLong = new string('a', ComputerToolHost.MaxTypeTextLength + 1);
        var result = await rig.Run("type_text", JsonSerializer.Serialize(new { text = tooLong }));
        Assert.True(result.IsError);
        Assert.Contains("5001 characters", result.Text);
        Assert.Contains("at most 5000", result.Text);
        Assert.Empty(rig.InputCalls);

        var empty = await rig.Run("type_text", "{\"text\":\"\"}");
        Assert.True(empty.IsError);
        var missing = await rig.Run("type_text", "{}");
        Assert.Contains("'text' is missing", missing.Text);

        var exact = new string('b', ComputerToolHost.MaxTypeTextLength);
        Assert.False((await rig.Run("type_text", JsonSerializer.Serialize(new { text = exact }))).IsError);
        Assert.Equal(exact, string.Concat(rig.InputCalls.Select(c => c[5..c.LastIndexOf('|')])));
    }

    [Fact]
    public async Task Type_text_chunks_without_splitting_surrogates_or_crlf()
    {
        var rig = Rig();
        // Chunks are 200 chars: the emoji straddles the first boundary, the CR LF the second.
        var text = new string('x', 199) + "\U0001F600" + new string('y', 197) + "\r\n" + "tail";
        await rig.Run("type_text", JsonSerializer.Serialize(new { text }));
        var parts = rig.InputCalls.Select(c => c[5..c.LastIndexOf('|')]).ToList();
        Assert.True(parts.Count > 1);
        Assert.Equal(text, string.Concat(parts));
        Assert.All(parts, p =>
        {
            Assert.False(char.IsHighSurrogate(p[^1]));
            Assert.False(p.EndsWith('\r'));
        });
    }

    [Theory]
    [InlineData("ctrl+s", "ctrl+s", ActionRisk.Medium)]
    [InlineData("Control + C", "ctrl+c", ActionRisk.Medium)]
    [InlineData("alt+f4", "alt+f4", ActionRisk.High)]
    [InlineData("enter", "enter", ActionRisk.High)]
    [InlineData("shift+del", "shift+delete", ActionRisk.High)]
    [InlineData("ctrl+w", "ctrl+w", ActionRisk.High)]
    [InlineData("ctrl+q", "ctrl+q", ActionRisk.High)]
    [InlineData("win+l", "win+l", ActionRisk.High)]
    [InlineData("ctrl+alt+delete", "ctrl+alt+delete", ActionRisk.High)]
    [InlineData("alt+tab", "alt+tab", ActionRisk.Medium)]
    [InlineData("w", "w", ActionRisk.Medium)]
    public async Task Press_keys_parses_and_rates_risk(string keys, string normalized, ActionRisk risk)
    {
        var rig = Rig();
        var result = await rig.Run("press_keys", JsonSerializer.Serialize(new { keys }));
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { $"keys {normalized}" }, rig.InputCalls);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(risk, action.Risk);
        Assert.Equal(normalized, action.Keys!.ToString());
    }

    [Fact]
    public async Task Press_keys_repeats_and_accepts_arrays()
    {
        var rig = Rig();
        Assert.False((await rig.Run("press_keys", "{\"keys\":\"down\",\"repeat\":3}")).IsError);
        Assert.Equal(new[] { "keys down", "keys down", "keys down" }, rig.InputCalls);
        Assert.Equal("Press down x3 in 'Untitled - Notepad' (notepad)", rig.Guard.Actions[^1].Summary);

        rig.Input.Calls.Clear();
        Assert.False((await rig.Run("press_keys", "{\"keys\":[\"ctrl\",\"shift\",\"t\"]}")).IsError);
        Assert.Equal(new[] { "keys ctrl+shift+t" }, rig.InputCalls);
    }

    [Theory]
    [InlineData("{\"keys\":\"ctrl+a+b\"}", "more than one non-modifier key")]
    [InlineData("{\"keys\":\"\"}", "'keys' is missing")]
    [InlineData("{}", "'keys' is missing")]
    [InlineData("{\"keys\":\"a\",\"repeat\":51}", "'repeat' must be a whole number from 1 to 50")]
    [InlineData("{\"keys\":\"a\",\"repeat\":0}", "'repeat' must be a whole number from 1 to 50")]
    public async Task Press_keys_reports_bad_combinations(string json, string expected)
    {
        var rig = Rig();
        var result = await rig.Run("press_keys", json);
        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Contains("press_keys takes keys", result.Text);
        Assert.Empty(rig.InputCalls);
    }

    // ------------------------------------------------------------ wait, windows, apps

    [Fact]
    public async Task Wait_sleeps_then_returns_screenshot()
    {
        var rig = Rig();
        var result = await rig.Run("wait", "{\"seconds\":1.5}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[] { 1500 }, rig.Delays);
        Assert.StartsWith("Waited 1.5 s.\nScreenshot 1280x720", result.Text);
        Assert.Single(result.Images);
        Assert.Empty(rig.Guard.Actions);

        Assert.Contains("between 0.1 and 30", (await rig.Run("wait", "{\"seconds\":31}")).Text);
        Assert.Contains("between 0.1 and 30", (await rig.Run("wait", "{\"seconds\":0.01}")).Text);
        Assert.Contains("'seconds' is missing", (await rig.Run("wait", "{}")).Text);
    }

    [Fact]
    public async Task List_windows_formats_model_coordinates_and_flags()
    {
        var rig = Rig(r =>
        {
            r.Windows.List.Add(ToolsTestFakes.Window("Chat", "chat", minimized: true));
            r.Windows.List.Add(ToolsTestFakes.Window("Administrator: PowerShell", "powershell", bounds: new ScreenRect(1280, 720, 1280, 720), elevated: true));
            r.Windows.List.Add(ToolsTestFakes.Window("Left screen app", "app", bounds: new ScreenRect(-1920, 0, 800, 600)));
            r.Windows.List.Add(ToolsTestFakes.Window("Vault", "keepass", bounds: new ScreenRect(0, 0, 200, 200)));
            r.Windows.List.Add(ToolsTestFakes.Window("DeskPilot", "DeskPilot", pid: Environment.ProcessId));
            r.Settings.Safety.BlockedProcesses.Add("KeePass.exe");
        });
        var result = await rig.Run("list_windows");
        Assert.False(result.IsError);
        var lines = result.Text.Split('\n');
        Assert.StartsWith("5 open windows, front to back", lines[0]);
        Assert.Equal("- 'Untitled - Notepad' [notepad] at (0, 0) 640x360 (active)", lines[1]);
        Assert.Equal("- 'Chat' [chat] (minimized)", lines[2]);
        Assert.Equal("- 'Administrator: PowerShell' [powershell] at (640, 360) 640x360 (elevated, off limits while Administrator mode is off)", lines[3]);
        Assert.Equal("- 'Left screen app' [app] at (-960, 0) 400x300 (on another monitor, not in the screenshot)", lines[4]);
        Assert.Equal("- 'Vault' [keepass] at (0, 0) 100x100 (blocked by the user)", lines[5]);
        Assert.DoesNotContain("DeskPilot", result.Text);
        Assert.Empty(rig.Guard.Actions);
    }

    [Fact]
    public async Task Focus_window_prefers_exact_then_prefix_then_contains_then_process()
    {
        var contains = ToolsTestFakes.Window("My Notes - Editor", "editor", pid: 1, handle: 11);
        var prefix = ToolsTestFakes.Window("Notes app", "notesapp", pid: 2, handle: 12);
        var exact = ToolsTestFakes.Window("notes", "other", pid: 3, handle: 13);
        var rig = Rig(r =>
        {
            r.Windows.List.Clear();
            r.Windows.List.AddRange(new[] { contains, prefix, exact, ToolsTestFakes.Notepad });
        });

        await rig.Run("focus_window", "{\"title\":\"Notes\"}");
        await rig.Run("focus_window", "{\"title\":\"notes a\"}");
        await rig.Run("focus_window", "{\"title\":\"- edit\"}");
        await rig.Run("focus_window", "{\"title\":\"notepad.exe\"}");
        Assert.Equal(new nint[] { 13, 12, 11, ToolsTestFakes.Notepad.Handle }, rig.Windows.Focused);

        var action = rig.Guard.Actions[0];
        Assert.Equal("focus_window", action.Tool);
        Assert.Equal(ActionRisk.Medium, action.Risk);
        Assert.Equal("notes", action.Text);
        Assert.Same(exact, ActionTargets.GetWindow(action));
        Assert.Equal("Focus window 'notes' (other)", action.Summary);
    }

    [Fact]
    public async Task Focus_window_reports_missing_and_refused_windows()
    {
        var rig = Rig();
        var missing = await rig.Run("focus_window", "{\"title\":\"Photoshop\"}");
        Assert.True(missing.IsError);
        Assert.Contains("No open window matches 'Photoshop'", missing.Text);
        Assert.Contains("'Untitled - Notepad' (notepad)", missing.Text);
        Assert.Empty(rig.Guard.Actions);

        Assert.Contains("'title' is missing", (await rig.Run("focus_window", "{}")).Text);

        rig.Windows.FocusResult = false;
        var refused = await rig.Run("focus_window", "{\"title\":\"notepad\"}");
        Assert.True(refused.IsError);
        Assert.Contains("did not bring", refused.Text);
        Assert.Empty(refused.Images);
    }

    [Fact]
    public async Task Launch_passes_arguments_and_admin_mode()
    {
        var rig = Rig();
        var result = await rig.Run("launch", "{\"target\":\"notepad\",\"arguments\":\"notes.txt\"}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(("notepad", "notes.txt", false), rig.Launcher.Calls.Single());
        Assert.StartsWith("Opened 'notepad' (process id 1234).", result.Text);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(ActionRisk.High, action.Risk);
        Assert.Equal("notepad", action.LaunchTarget);
        Assert.Equal("notes.txt", action.Command);

        rig.Settings.Safety.AllowAdmin = true;
        await rig.Run("launch", "{\"target\":\"https://example.com\"}");
        Assert.Equal(("https://example.com", (string?)null, true), rig.Launcher.Calls[^1]);

        rig.Launcher.Result = new LaunchResult(false, "The system cannot find the file specified.", null);
        var failed = await rig.Run("launch", "{\"target\":\"nothing-here\"}");
        Assert.True(failed.IsError);
        Assert.Equal("Could not open 'nothing-here': The system cannot find the file specified.", failed.Text);

        Assert.Contains("'target' is missing", (await rig.Run("launch", "{\"target\":\"  \"}")).Text);
    }

    [Fact]
    public async Task Ui_elements_lists_elements_in_model_coordinates()
    {
        var rig = Rig(r =>
        {
            r.Ui.Elements.Add(new UiElementInfo("Save", "Button", new ScreenRect(100, 100, 200, 60), "save", true, true, null));
            r.Ui.Elements.Add(new UiElementInfo("Search", "Edit", new ScreenRect(400, 200, 300, 40), null, false, true, null));
            r.Ui.Elements.Add(new UiElementInfo("Elsewhere", "Button", new ScreenRect(3000, 100, 100, 40), null, true, true, null));
            r.Ui.Elements.Add(new UiElementInfo("", "Edit", new ScreenRect(0, 600, 400, 40), "addressBar", true, true, null));
        });

        var result = await rig.Run("ui_elements");
        Assert.False(result.IsError, result.Text);
        Assert.Equal(new[]
        {
            "UI elements of 'Untitled - Notepad' (notepad), centers and sizes in screenshot pixels:",
            "[1] Button 'Save' at (100, 65) size 100x30",
            "[2] Edit 'Search' at (275, 110) size 150x20 (disabled)",
            "[3] Edit '' (id addressBar) at (100, 310) size 200x20",
        }, result.Text.Split('\n'));
        Assert.Equal((ToolsTestFakes.Notepad.Handle, 80), rig.Ui.Requests[^1]);

        var filtered = await rig.Run("ui_elements", "{\"filter\":\"SAVE\",\"max\":500}");
        Assert.Equal(new[]
        {
            "UI elements of 'Untitled - Notepad' (notepad) matching 'SAVE', centers and sizes in screenshot pixels:",
            "[1] Button 'Save' at (100, 65) size 100x30",
        }, filtered.Text.Split('\n'));
        Assert.Equal(200, rig.Ui.Requests[^1].Max);

        // "e" matches Save, Search and the unnamed Edit; only one is shown.
        var limited = await rig.Run("ui_elements", "{\"filter\":\"e\",\"max\":1}");
        Assert.Contains("[1] Button 'Save'", limited.Text);
        Assert.EndsWith("(2 more not shown; raise max or use filter.)", limited.Text);
        Assert.Equal(200, rig.Ui.Requests[^1].Max);

        await rig.Run("ui_elements", "{\"max\":1}");
        Assert.Equal(1, rig.Ui.Requests[^1].Max);
        Assert.Contains("'max' must be a number", (await rig.Run("ui_elements", "{\"max\":\"lots\"}")).Text);

        var normalized = Rig(r =>
        {
            r.Settings.Screen.Coordinates = CoordinateMode.Normalized1000;
            r.Ui.Elements.Add(new UiElementInfo("Save", "Button", new ScreenRect(1180, 670, 200, 100), null, true, true, null));
        });
        Assert.Contains("[1] Button 'Save' at (500, 500) size 78x69", (await normalized.Run("ui_elements")).Text);
    }

    [Fact]
    public async Task Ui_elements_targets_windows_by_title()
    {
        var calc = ToolsTestFakes.Window("Calculator", "calc", handle: 77);
        var rig = Rig(r => r.Windows.List.Add(calc));
        var result = await rig.Run("ui_elements", "{\"window_title\":\"calcul\"}");
        Assert.Equal((nint)77, rig.Ui.Requests.Single().Window);
        Assert.Contains("(no interactive UI elements found", result.Text);

        var missing = await rig.Run("ui_elements", "{\"window_title\":\"Paint\"}");
        Assert.True(missing.IsError);
        Assert.Contains("No open window matches 'Paint'", missing.Text);

        rig.Windows.Foreground = null;
        Assert.True((await rig.Run("ui_elements")).IsError);

        rig.Windows.Foreground = ToolsTestFakes.Window("DeskPilot", "DeskPilot", pid: Environment.ProcessId);
        Assert.Contains("DeskPilot's own window", (await rig.Run("ui_elements")).Text);
    }

    [Fact]
    public async Task Clipboard_tools_read_and_write()
    {
        var rig = Rig();
        Assert.Equal("The clipboard is empty or does not hold text.", (await rig.Run("get_clipboard")).Text);
        rig.Clipboard.Text = "copied";
        Assert.Equal("Clipboard text (6 characters):\ncopied", (await rig.Run("get_clipboard")).Text);

        var set = await rig.Run("set_clipboard", "{\"text\":\"paste me\"}");
        Assert.False(set.IsError, set.Text);
        Assert.Equal(new[] { "paste me" }, rig.Clipboard.Sets);
        Assert.Empty(set.Images);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(ActionRisk.Medium, action.Risk);
        Assert.Equal("paste me", action.Text);
        Assert.Contains("'text' is missing", (await rig.Run("set_clipboard", "{}")).Text);

        rig.Settings.Safety.AllowClipboard = false;
        Assert.True((await rig.Run("get_clipboard")).IsError);
        Assert.True((await rig.Run("set_clipboard", "{\"text\":\"x\"}")).IsError);
        Assert.Single(rig.Clipboard.Sets);
    }

    [Fact]
    public async Task Run_command_uses_home_folder_timeout_and_formats_output()
    {
        var rig = Rig(r => r.Settings.Safety.AllowShellCommands = true);
        rig.Shell.Result = new ShellResult(0, "line1\r\nline2\r\n", "", false);
        var result = await rig.Run("run_command", "{\"command\":\"Get-ChildItem\"}");
        Assert.False(result.IsError, result.Text);
        Assert.Equal("Exit code: 0\n--- stdout ---\nline1\r\nline2", result.Text);
        Assert.Empty(result.Images);
        var call = rig.Shell.Calls.Single();
        Assert.Equal(("Get-ChildItem", "powershell", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 60000), call);
        var action = rig.Guard.Actions.Single();
        Assert.Equal(ActionRisk.High, action.Risk);
        Assert.Equal("Get-ChildItem", action.Command);

        rig.Shell.Result = new ShellResult(1, new string('o', 20000), "bad thing", true);
        result = await rig.Run("run_command", "{\"command\":\"dir\",\"shell\":\"CMD\",\"timeout_seconds\":5}");
        Assert.Equal(("dir", "cmd", 5000), (rig.Shell.Calls[^1].Command, rig.Shell.Calls[^1].Shell, rig.Shell.Calls[^1].TimeoutMs));
        Assert.StartsWith("Exit code: 1 (timed out after 5 s; the command was stopped)", result.Text);
        Assert.Contains("characters omitted", result.Text);
        Assert.Contains("--- stderr ---\nbad thing", result.Text);
        Assert.True(result.Text.Length < 9000);

        rig.Shell.Result = new ShellResult(0, "", "", false);
        Assert.Equal("Exit code: 0\n(no output)", (await rig.Run("run_command", "{\"command\":\"cls\"}")).Text);
    }

    [Theory]
    [InlineData("{}", "'command' is missing")]
    [InlineData("{\"command\":\"x\",\"shell\":\"bash\"}", "not 'bash'")]
    [InlineData("{\"command\":\"x\",\"timeout_seconds\":601}", "between 1 and 600")]
    [InlineData("{\"command\":\"x\",\"timeout_seconds\":0}", "between 1 and 600")]
    public async Task Run_command_validates_arguments(string json, string expected)
    {
        var rig = Rig(r => r.Settings.Safety.AllowShellCommands = true);
        var result = await rig.Run("run_command", json);
        Assert.True(result.IsError);
        Assert.Contains(expected, result.Text);
        Assert.Empty(rig.Shell.Calls);
    }

    [Fact]
    public void TruncateMiddle_keeps_both_ends()
    {
        var text = new string('a', 7000) + new string('z', 3000);
        var cut = ComputerToolHost.TruncateMiddle(text, 8000);
        Assert.StartsWith(new string('a', 6000), cut);
        Assert.EndsWith(new string('z', 2000), cut);
        Assert.Contains("[... 2000 characters omitted ...]", cut);
        Assert.Equal("short", ComputerToolHost.TruncateMiddle("short", 8000));
    }
}

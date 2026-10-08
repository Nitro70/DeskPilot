using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Desktop;
using Xunit.Abstractions;
using static DeskPilot.Core.Desktop.NativeMethods;

namespace DeskPilot.Tests;

// Nothing here moves the real mouse, presses real keys, writes the clipboard or launches programs.
// Input is tested through a recording fake backend; the real desktop is only read (monitors, windows,
// screen capture into memory, UI Automation reads). The shell tests start hidden powershell/cmd processes.
public class DesktopTests
{
    // ------------------------------------------------------------------ input: pure builder

    public class InputBuilderTests
    {
        [Fact]
        public void Input_struct_has_the_native_size()
        {
            Assert.Equal(IntPtr.Size == 8 ? 40 : 28, Marshal.SizeOf<INPUT>());
            if (Environment.Is64BitProcess) Assert.Equal(40, Marshal.SizeOf<INPUT>());
        }

        [Theory]
        [InlineData(0, 0, 2560, 1440)]
        [InlineData(-1920, 0, 4480, 1440)]
        [InlineData(-1280, -300, 5120, 1740)]
        [InlineData(0, 0, 7680, 4320)]
        public void Normalize_round_trips_every_pixel_under_both_windows_conventions(int vx, int vy, int vw, int vh)
        {
            var vs = new ScreenRect(vx, vy, vw, vh);
            for (int x = vx; x < vx + vw; x += 7)
            {
                var (dx, _) = InputBuilder.Normalize(x, vy, vs);
                Assert.InRange(dx, 0, 65535);
                Assert.Equal(x, vx + (int)((long)dx * vw / 65536));
                Assert.Equal(x, vx + (int)((long)dx * vw / 65535));
            }
            for (int y = vy; y < vy + vh; y += 5)
            {
                var (_, dy) = InputBuilder.Normalize(vx, y, vs);
                Assert.Equal(y, vy + (int)((long)dy * vh / 65536));
                Assert.Equal(y, vy + (int)((long)dy * vh / 65535));
            }
            // Last pixel and clamping outside the desktop.
            Assert.Equal(vx + vw - 1, vx + (int)((long)InputBuilder.Normalize(vx + vw - 1, vy, vs).Dx * vw / 65536));
            Assert.Equal(0, InputBuilder.Normalize(vx - 500, vy - 500, vs).Dx);
            Assert.Equal(65535, InputBuilder.Normalize(vx + vw + 500, vy, vs).Dx);
        }

        [Fact]
        public void Absolute_move_uses_virtual_desktop_flags()
        {
            var input = InputBuilder.MoveAbsolute(100, 200, new ScreenRect(0, 0, 1920, 1080));
            Assert.Equal(INPUT_MOUSE, input.type);
            Assert.Equal(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, input.U.mi.dwFlags);
            Assert.Equal(InputBuilder.InjectedMarker, input.U.mi.dwExtraInfo);
        }

        [Fact]
        public void Wheel_signs_follow_the_interface()
        {
            var down = InputBuilder.VerticalWheel(1);
            Assert.Equal(MOUSEEVENTF_WHEEL, down.U.mi.dwFlags);
            Assert.Equal(-120, down.U.mi.mouseData);
            Assert.Equal(120, InputBuilder.VerticalWheel(-1).U.mi.mouseData);

            var right = InputBuilder.HorizontalWheel(1);
            Assert.Equal(MOUSEEVENTF_HWHEEL, right.U.mi.dwFlags);
            Assert.Equal(120, right.U.mi.mouseData);
            Assert.Equal(-120, InputBuilder.HorizontalWheel(-1).U.mi.mouseData);
        }

        [Fact]
        public void Button_flags_and_swapped_buttons()
        {
            Assert.Equal(MOUSEEVENTF_LEFTDOWN, InputBuilder.ButtonFlag(MouseButton.Left, false));
            Assert.Equal(MOUSEEVENTF_LEFTUP, InputBuilder.ButtonFlag(MouseButton.Left, true));
            Assert.Equal(MOUSEEVENTF_RIGHTDOWN, InputBuilder.ButtonFlag(MouseButton.Right, false));
            Assert.Equal(MOUSEEVENTF_RIGHTUP, InputBuilder.ButtonFlag(MouseButton.Right, true));
            Assert.Equal(MOUSEEVENTF_MIDDLEDOWN, InputBuilder.ButtonFlag(MouseButton.Middle, false));
            Assert.Equal(MOUSEEVENTF_MIDDLEUP, InputBuilder.ButtonFlag(MouseButton.Middle, true));

            Assert.Equal(MouseButton.Left, InputBuilder.ToPhysical(MouseButton.Left, false));
            Assert.Equal(MouseButton.Right, InputBuilder.ToPhysical(MouseButton.Left, true));
            Assert.Equal(MouseButton.Left, InputBuilder.ToPhysical(MouseButton.Right, true));
            Assert.Equal(MouseButton.Middle, InputBuilder.ToPhysical(MouseButton.Middle, true));
        }

        [Fact]
        public void Text_becomes_unicode_units_with_enter_and_tab()
        {
            var chunks = InputBuilder.TextChunks("a\nb\tc\rd\u0007", vk => (ushort)(vk + 1000));
            var described = chunks.Select(c => string.Join(" ", c.Select(Describe))).ToList();
            Assert.Equal(6, chunks.Count); // a, Enter, b, Tab, c, d ('\r' and BEL skipped)
            Assert.Equal("u61 u61^", described[0]);
            Assert.Equal("k0D k0D^", described[1]);
            Assert.Equal("u62 u62^", described[2]);
            Assert.Equal("k09 k09^", described[3]);
            Assert.Equal("u63 u63^", described[4]);
            Assert.Equal("u64 u64^", described[5]);
            Assert.Equal((ushort)(0x0D + 1000), chunks[1][0].U.ki.wScan);
            Assert.Equal(KEYEVENTF_UNICODE, chunks[0][0].U.ki.dwFlags);
            Assert.Equal(KEYEVENTF_UNICODE | KEYEVENTF_KEYUP, chunks[0][1].U.ki.dwFlags);
            Assert.Equal(0, chunks[0][0].U.ki.wVk);
        }

        [Fact]
        public void Surrogate_pairs_are_two_units_sent_together()
        {
            var text = "x\U0001F600y";
            Assert.Equal(4, text.Length);
            var chunks = InputBuilder.TextChunks(text, vk => vk);
            Assert.Equal(3, chunks.Count);
            var emoji = chunks[1];
            Assert.Equal(4, emoji.Length);
            Assert.Equal(new[] { "uD83D", "uDE00", "uD83D^", "uDE00^" }, emoji.Select(Describe));
        }

        [Fact]
        public void Combo_presses_modifiers_in_order_and_releases_in_reverse()
        {
            var mods = new[] { KeyMap.Modifier("ctrl"), KeyMap.Modifier("shift") };
            var key = new KeyStroke(0x54, false);
            var inputs = InputBuilder.Combo(mods, key, vk => 0);
            Assert.Equal(new[] { "kA2", "kA0", "k54", "k54^", "kA0^", "kA2^" }, inputs.Select(Describe));

            var win = InputBuilder.Combo(new[] { KeyMap.Modifier("win") }, null, vk => 0);
            Assert.Equal(new[] { "k5B+", "k5B+^" }, win.Select(Describe));
        }

        [Fact]
        public void Smooth_path_is_eased_and_ends_on_target()
        {
            var path = InputBuilder.SmoothPath(new ScreenPoint(0, 0), new ScreenPoint(1000, 500), 20);
            Assert.Equal(20, path.Count);
            Assert.Equal(new ScreenPoint(1000, 500), path[^1]);
            for (int i = 1; i < path.Count; i++) Assert.True(path[i].X >= path[i - 1].X);
            // Ease in: the first step is much shorter than the middle one.
            int first = path[0].X, middle = path[10].X - path[9].X;
            Assert.True(first < middle, $"first step {first} should be shorter than middle step {middle}");
            Assert.Single(InputBuilder.SmoothPath(new ScreenPoint(5, 5), new ScreenPoint(9, 9), 0));
        }
    }

    // ------------------------------------------------------------------ input: key names

    public class KeyMapTests
    {
        [Theory]
        [InlineData("a", 0x41, false)]
        [InlineData("Z", 0x5A, false)]
        [InlineData("7", 0x37, false)]
        [InlineData("f1", 0x70, false)]
        [InlineData("F24", 0x87, false)]
        [InlineData("enter", 0x0D, false)]
        [InlineData("return", 0x0D, false)]
        [InlineData("esc", 0x1B, false)]
        [InlineData("tab", 0x09, false)]
        [InlineData("space", 0x20, false)]
        [InlineData("backspace", 0x08, false)]
        [InlineData("delete", 0x2E, true)]
        [InlineData("insert", 0x2D, true)]
        [InlineData("home", 0x24, true)]
        [InlineData("end", 0x23, true)]
        [InlineData("pageup", 0x21, true)]
        [InlineData("pgdn", 0x22, true)]
        [InlineData("up", 0x26, true)]
        [InlineData("down", 0x28, true)]
        [InlineData("left", 0x25, true)]
        [InlineData("right", 0x27, true)]
        [InlineData("printscreen", 0x2C, true)]
        [InlineData("contextmenu", 0x5D, true)]
        [InlineData("capslock", 0x14, false)]
        [InlineData("numlock", 0x90, true)]
        [InlineData("scrolllock", 0x91, false)]
        [InlineData("pause", 0x13, false)]
        [InlineData("volumeup", 0xAF, true)]
        [InlineData("volumedown", 0xAE, true)]
        [InlineData("volumemute", 0xAD, true)]
        [InlineData("medianext", 0xB0, true)]
        [InlineData("mediaprev", 0xB1, true)]
        [InlineData("mediaplaypause", 0xB3, true)]
        [InlineData("mediastop", 0xB2, true)]
        [InlineData("browserback", 0xA6, true)]
        [InlineData("browserforward", 0xA7, true)]
        [InlineData("browserrefresh", 0xA8, true)]
        [InlineData("browserhome", 0xAC, true)]
        [InlineData("numpad0", 0x60, false)]
        [InlineData("numpad9", 0x69, false)]
        [InlineData("multiply", 0x6A, false)]
        [InlineData("add", 0x6B, false)]
        [InlineData("subtract", 0x6D, false)]
        [InlineData("decimal", 0x6E, false)]
        [InlineData("divide", 0x6F, true)]
        [InlineData("ctrl", 0xA2, false)]
        [InlineData("alt", 0xA4, false)]
        [InlineData("shift", 0xA0, false)]
        [InlineData("win", 0x5B, true)]
        public void Named_keys_map_to_virtual_keys(string name, int vk, bool extended)
        {
            Assert.True(KeyMap.TryResolve(name, UsLayout, out var stroke, out var error), error);
            Assert.Equal(vk, stroke.Vk);
            Assert.Equal(extended, stroke.Extended);
            Assert.False(stroke.NeedsShift);
        }

        [Fact]
        public void Punctuation_uses_the_layout_and_adds_needed_modifiers()
        {
            var plus = KeyMap.Resolve("+", UsLayout);
            Assert.Equal(0xBB, plus.Vk);
            Assert.True(plus.NeedsShift);

            var equals = KeyMap.Resolve("equals", UsLayout);
            Assert.Equal(0xBB, equals.Vk);
            Assert.False(equals.NeedsShift);

            var minus = KeyMap.Resolve("minus", UsLayout);
            Assert.Equal(0xBD, minus.Vk);

            var euro = KeyMap.Resolve("€", UsLayout);
            Assert.True(euro.NeedsCtrl && euro.NeedsAlt);

            var mods = KeyMap.ModifiersFor(new[] { "ctrl" }, plus);
            Assert.Equal(new ushort[] { 0xA2, 0xA0 }, mods.Select(m => m.Vk));
            var noDup = KeyMap.ModifiersFor(new[] { "ctrl", "shift" }, plus);
            Assert.Equal(2, noDup.Count);
        }

        [Fact]
        public void Unknown_keys_fail_with_a_helpful_message()
        {
            Assert.False(KeyMap.TryResolve("frobnicate", UsLayout, out _, out var error));
            Assert.Contains("Unknown key 'frobnicate'", error);
            Assert.Contains("f1-f24", error);
            Assert.Contains("pageup", error);

            var ex = Assert.Throws<ArgumentException>(() => KeyMap.Resolve("hyperdrive", UsLayout));
            Assert.Contains("hyperdrive", ex.Message);

            Assert.False(KeyMap.TryResolve("\u00df", UsLayout, out _, out var layoutError));
            Assert.Contains("type_text", layoutError);
            Assert.False(KeyMap.TryResolve("", UsLayout, out _, out _));
        }
    }

    // ------------------------------------------------------------------ input: simulator over a fake backend

    public class InputSimulatorTests
    {
        [Fact]
        public void PressCombo_sends_ordered_events_and_leaves_nothing_pressed()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.PressCombo(KeyCombo.Parse("ctrl+shift+t"));
            Assert.Equal(new[] { "kA2", "kA0", "k54", "k54^", "kA0^", "kA2^" }, fake.Events);
            Assert.Empty(sim.PressedKeys);
            Assert.All(fake.SentKeyInputs, k => Assert.Equal((ushort)(k.wVk & 0x7F), k.wScan));
        }

        [Fact]
        public void PressCombo_adds_shift_for_shifted_punctuation()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.PressCombo(KeyCombo.Parse("ctrl++"));
            Assert.Equal(new[] { "kA2", "kA0", "kBB", "kBB^", "kA0^", "kA2^" }, fake.Events);
        }

        [Fact]
        public void PressCombo_extended_keys_and_modifier_only_combo()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.PressCombo(KeyCombo.Parse("alt+left"));
            sim.PressCombo(KeyCombo.Parse("win"));
            Assert.Equal(new[] { "kA4", "k25+", "k25+^", "kA4^", "k5B+", "k5B+^" }, fake.Events);
        }

        [Fact]
        public void PressCombo_with_unknown_key_sends_nothing()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            var ex = Assert.Throws<ArgumentException>(() => sim.PressCombo(KeyCombo.Parse("ctrl+warpdrive")));
            Assert.Contains("warpdrive", ex.Message);
            Assert.Empty(fake.Events);
            Assert.Empty(sim.PressedKeys);
        }

        [Fact]
        public void PressCombo_releases_modifiers_when_the_main_key_fails()
        {
            var fake = new FakeInputBackend();
            fake.SendOverride = inputs => inputs[0].U.ki.wVk == 0x54 && (inputs[0].U.ki.dwFlags & KEYEVENTF_KEYUP) == 0 ? 0 : inputs.Length;
            var sim = new WindowsInputSimulator(fake);
            Assert.Throws<InvalidOperationException>(() => sim.PressCombo(KeyCombo.Parse("ctrl+t")));
            Assert.Equal(new[] { "kA2", "k54", "kA2^" }, fake.Events);
            Assert.Empty(sim.PressedKeys);
        }

        [Fact]
        public void Held_modifier_survives_a_combo_and_ReleaseAll_lifts_it()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.KeyDown("shift");
            sim.PressCombo(KeyCombo.Parse("shift+a"));
            Assert.Equal(new ushort[] { 0xA0 }, sim.PressedKeys);
            sim.MouseDown(MouseButton.Left);
            Assert.Contains(MouseButton.Left, sim.PressedButtons);

            sim.ReleaseAll();
            Assert.Equal(new[] { "kA0", "k41", "k41^", "LEFTDOWN", "kA0^", "LEFTUP" }, fake.Events);
            Assert.Empty(sim.PressedKeys);
            Assert.Empty(sim.PressedButtons);

            fake.Clear();
            sim.ReleaseAll();
            Assert.Empty(fake.Events);
        }

        [Fact]
        public void KeyDown_and_KeyUp_accept_names_and_track_state()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.KeyDown("Control");
            sim.KeyDown("pagedown");
            sim.KeyUp("pagedown");
            sim.KeyUp("ctrl");
            Assert.Equal(new[] { "kA2", "k22+", "k22+^", "kA2^" }, fake.Events);
            Assert.Empty(sim.PressedKeys);
            Assert.Throws<ArgumentException>(() => sim.KeyDown("nope-key"));

            // A character that needs shift on the layout holds shift with it, and lets go of it afterwards.
            fake.Clear();
            sim.KeyDown("+");
            Assert.Equal(new ushort[] { 0xA0, 0xBB }, sim.PressedKeys);
            sim.KeyUp("plus");
            Assert.Equal(new[] { "kA0", "kBB", "kBB^", "kA0^" }, fake.Events);
            Assert.Empty(sim.PressedKeys);
        }

        [Fact]
        public void Factory_builds_every_service_without_side_effects()
        {
            var services = DesktopFactory.CreateDefault();
            Assert.IsType<WindowsScreenCapture>(services.Screen);
            Assert.IsType<WindowsInputSimulator>(services.Input);
            Assert.IsType<WindowsWindowManager>(services.Windows);
            Assert.IsType<UiAutomationInspector>(services.Ui);
            Assert.IsType<WindowsAppLauncher>(services.Launcher);
            Assert.IsType<WindowsClipboard>(services.Clipboard);
            Assert.IsType<PowerShellRunner>(services.Shell);
        }

        [Fact]
        public void Swapped_buttons_flip_left_and_right()
        {
            var fake = new FakeInputBackend { ButtonsSwapped = true };
            var sim = new WindowsInputSimulator(fake);
            sim.Click(MouseButton.Left, 1);
            sim.MouseDown(MouseButton.Right);
            sim.ReleaseAll();
            Assert.Equal(new[] { "RIGHTDOWN", "RIGHTUP", "LEFTDOWN", "LEFTUP" }, fake.Events);
        }

        [Fact]
        public void Multi_click_holds_and_spaces_clicks_inside_the_double_click_time()
        {
            var fake = new FakeInputBackend { DoubleClickTimeMs = 500 };
            var sim = new WindowsInputSimulator(fake);
            sim.Click(MouseButton.Left, 3);
            Assert.Equal(new[] { "LEFTDOWN", "LEFTUP", "LEFTDOWN", "LEFTUP", "LEFTDOWN", "LEFTUP" }, fake.Events);
            int gap = WindowsInputSimulator.ClickGapMs(500);
            Assert.Equal(new[] { 30, gap, 30, gap, 30 }, fake.Sleeps);
            Assert.InRange(WindowsInputSimulator.ClickHoldMs, 25, 40);
            Assert.True(WindowsInputSimulator.ClickHoldMs * 3 + gap * 2 < 500 / 2);
            Assert.InRange(WindowsInputSimulator.ClickGapMs(200), 15, 25);

            fake.Clear();
            sim.Click(MouseButton.Middle, 9);
            Assert.Equal(6, fake.Events.Count); // clamped to 3
        }

        [Fact]
        public void Scroll_sends_one_notch_per_event_with_the_right_signs()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.Scroll(0, 3);
            sim.Scroll(-2, 0);
            sim.Scroll(1, -1);
            Assert.Equal(new[] { "WHEEL-120", "WHEEL-120", "WHEEL-120", "HWHEEL-120", "HWHEEL-120", "WHEEL120", "HWHEEL120" }, fake.Events);
        }

        [Fact]
        public void TypeText_batches_without_delay_and_paces_with_delay()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            var text = new string('x', 40);
            sim.TypeText(text, 0);
            Assert.Equal(3, fake.Calls.Count); // 16 + 16 + 8 characters
            Assert.Equal(80, fake.Calls.Sum(c => c.Length));
            Assert.All(fake.Sleeps, s => Assert.Equal(WindowsInputSimulator.TypingBatchPauseMs, s));

            fake.Clear();
            sim.TypeText("ab\r\nc", 5);
            Assert.Equal(4, fake.Calls.Count); // a, b, Enter, c
            Assert.Equal(new[] { 5, 5, 5, 5 }, fake.Sleeps);
            Assert.Equal(new[] { "u61", "u61^", "u62", "u62^", "k0D", "k0D^", "u63", "u63^" }, fake.Events);

            fake.Clear();
            sim.TypeText("", 0);
            Assert.Empty(fake.Calls);
        }

        [Fact]
        public void MoveMouse_verifies_and_falls_back_to_SetCursorPos()
        {
            var fake = new FakeInputBackend { Virtual = new ScreenRect(-1920, 0, 4480, 1440) };
            var sim = new WindowsInputSimulator(fake);
            sim.MoveMouse(-1000, 700);
            Assert.Equal(new ScreenPoint(-1000, 700), fake.Cursor);
            Assert.Empty(fake.SetCursorCalls);

            fake.CursorFollows = false;
            sim.MoveMouse(2000, 300);
            Assert.Equal(new[] { new ScreenPoint(2000, 300) }, fake.SetCursorCalls);
            Assert.Equal(new ScreenPoint(2000, 300), sim.GetCursorPosition());
        }

        [Fact]
        public void MoveMouseSmooth_steps_towards_the_target()
        {
            var fake = new FakeInputBackend { Cursor = new ScreenPoint(100, 100) };
            var sim = new WindowsInputSimulator(fake);
            sim.MoveMouseSmooth(900, 500, 320);
            var moves = fake.Events.Where(e => e.StartsWith("MOVE")).ToList();
            Assert.InRange(moves.Count, 10, 40);
            Assert.Equal(new ScreenPoint(900, 500), fake.Cursor);
            Assert.True(fake.Sleeps.Sum() >= 250, $"slept {fake.Sleeps.Sum()} ms");

            fake.Clear();
            sim.MoveMouseSmooth(10, 10, 0);
            Assert.Single(fake.Events, e => e.StartsWith("MOVE"));
        }

        [Fact]
        public void Rejected_input_raises_a_readable_error()
        {
            var fake = new FakeInputBackend { SendOverride = _ => 0 };
            var sim = new WindowsInputSimulator(fake);
            var ex = Assert.Throws<InvalidOperationException>(() => sim.Click(MouseButton.Left, 1));
            Assert.Contains("rejected", ex.Message);
            Assert.Throws<InvalidOperationException>(() => sim.TypeText("hi", 0));
        }

        [Fact]
        public void Every_public_action_runs_inside_a_dpi_scope()
        {
            var fake = new FakeInputBackend();
            var sim = new WindowsInputSimulator(fake);
            sim.MoveMouse(1, 1);
            sim.MoveMouseSmooth(5, 5, 50);
            sim.MouseDown(MouseButton.Left);
            sim.MouseUp(MouseButton.Left);
            sim.Click(MouseButton.Left, 1);
            sim.Scroll(0, 1);
            sim.TypeText("a", 0);
            sim.PressCombo(KeyCombo.Parse("a"));
            sim.KeyDown("a");
            sim.KeyUp("a");
            sim.ReleaseAll();
            sim.GetCursorPosition();
            Assert.Equal(12, fake.DpiScopes);
            Assert.Equal(0, fake.OpenDpiScopes);
        }
    }

    // ------------------------------------------------------------------ DPI scope (thread-local, read-only)

    public class DpiTests
    {
        [Fact]
        public void Scope_switches_the_thread_to_per_monitor_v2_and_restores_it()
        {
            bool inside = false, restored = false, setUnaware = false;
            var t = new Thread(() =>
            {
                // Start from an unaware thread so the switch is observable even when the host is PMv2.
                var unaware = SetThreadDpiAwarenessContext(-1);
                setUnaware = unaware != 0;
                var before = DpiScope.CurrentContext();
                using (DpiScope.PerMonitorV2()) inside = DpiScope.IsThreadPerMonitorV2();
                restored = AreDpiAwarenessContextsEqual(before, DpiScope.CurrentContext());
            });
            t.Start();
            t.Join();
            Assert.True(setUnaware);
            Assert.True(inside);
            Assert.True(restored);
        }
    }

    // ------------------------------------------------------------------ screen capture (in memory only)

    public class ScreenCaptureTests
    {
        private readonly ITestOutputHelper _out;
        public ScreenCaptureTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Monitors_have_one_primary_at_the_origin()
        {
            var monitors = new WindowsScreenCapture().GetMonitors();
            Assert.NotEmpty(monitors);
            var primary = Assert.Single(monitors, m => m.IsPrimary);
            Assert.Equal(0, primary.Bounds.X);
            Assert.Equal(0, primary.Bounds.Y);
            for (int i = 0; i < monitors.Count; i++)
            {
                var m = monitors[i];
                Assert.Equal(i, m.Index);
                Assert.StartsWith(@"\\.\", m.DeviceName);
                Assert.False(m.Bounds.IsEmpty);
                Assert.Equal(m.WorkArea, m.Bounds.Intersect(m.WorkArea));
                Assert.InRange(m.Scale, 1.0, 5.0);
                _out.WriteLine($"{m.Index} {m.DeviceName} {m.Bounds} work {m.WorkArea} primary={m.IsPrimary} scale={m.Scale}");
            }
        }

        [Fact]
        public void Virtual_screen_is_the_union_of_monitors_in_physical_pixels()
        {
            var cap = new WindowsScreenCapture();
            var union = WindowsScreenCapture.Union(cap.GetMonitors().Select(m => m.Bounds));
            Assert.Equal(union, cap.GetVirtualScreen());
        }

        [Fact]
        public void Union_of_rects()
        {
            Assert.Equal(new ScreenRect(-1920, -100, 4480, 1540),
                WindowsScreenCapture.Union(new[] { new ScreenRect(0, 0, 2560, 1440), new ScreenRect(-1920, -100, 1920, 1080) }));
            Assert.Equal(default, WindowsScreenCapture.Union(Array.Empty<ScreenRect>()));
        }

        [Fact]
        public void Captures_primary_monitor_scaled_to_jpeg_quickly()
        {
            var cap = new WindowsScreenCapture();
            var primary = cap.GetMonitors().First(m => m.IsPrimary).Bounds;
            double s = Math.Min(1280.0 / primary.Width, 800.0 / primary.Height);
            int w = (int)Math.Round(primary.Width * s), h = (int)Math.Round(primary.Height * s);
            var request = new CaptureRequest(primary, w, h, ImageFormatKind.Jpeg, 75, DrawCursor: true, GridSpacing: 0);

            cap.Capture(request); // warm-up (codec lookup, GDI+ startup)
            var times = new List<double>();
            CapturedFrame frame = null!;
            for (int i = 0; i < 5; i++)
            {
                var sw = Stopwatch.StartNew();
                frame = cap.Capture(request);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            _out.WriteLine($"{primary.Width}x{primary.Height} -> {w}x{h} jpeg: {string.Join(", ", times.Select(t => t.ToString("0")))} ms, {frame.Data.Length / 1024} KB");

            Assert.Equal("image/jpeg", frame.MediaType);
            Assert.Equal(primary, frame.Source);
            Assert.Equal((w, h), (frame.Width, frame.Height));
            Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF }, frame.Data.Take(3));
            Assert.Equal((w, h), DecodedSize(frame.Data));
            Assert.True(times.Min() < 1000, $"capture took {times.Min():0} ms");
        }

        [Fact]
        public void Captures_unscaled_png_region_with_grid_and_cursor()
        {
            var cap = new WindowsScreenCapture();
            var frame = cap.Capture(new CaptureRequest(new ScreenRect(10, 20, 300, 200), 0, 0, ImageFormatKind.Png, 75, DrawCursor: true, GridSpacing: 50));
            Assert.Equal("image/png", frame.MediaType);
            Assert.Equal((300, 200), (frame.Width, frame.Height));
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, frame.Data.Take(4));
            Assert.Equal((300, 200), DecodedSize(frame.Data));
        }

        [Fact]
        public void Captures_the_whole_virtual_desktop_including_negative_coordinates()
        {
            var cap = new WindowsScreenCapture();
            var vs = cap.GetVirtualScreen();
            var frame = cap.Capture(new CaptureRequest(vs, 800, 400, ImageFormatKind.Jpeg, 60, DrawCursor: false, GridSpacing: 100));
            Assert.Equal((800, 400), DecodedSize(frame.Data));

            // A region that starts left of / above the primary monitor still works (black where nothing is).
            var offDesk = cap.Capture(new CaptureRequest(new ScreenRect(vs.X - 50, vs.Y - 50, 120, 90), 60, 45, ImageFormatKind.Png, 75, false, 0));
            Assert.Equal((60, 45), DecodedSize(offDesk.Data));
        }

        [Fact]
        public void Rejects_an_empty_source()
        {
            var cap = new WindowsScreenCapture();
            Assert.Throws<ArgumentException>(() => cap.Capture(new CaptureRequest(new ScreenRect(0, 0, 0, 10), 10, 10, ImageFormatKind.Png, 75, false, 0)));
        }

        [Fact]
        public void Repeated_captures_do_not_leak_gdi_handles()
        {
            var cap = new WindowsScreenCapture();
            var req = new CaptureRequest(new ScreenRect(0, 0, 400, 300), 200, 150, ImageFormatKind.Jpeg, 70, DrawCursor: true, GridSpacing: 40);
            cap.Capture(req);
            var self = Process.GetCurrentProcess().Handle;
            uint before = GetGuiResources(self, GR_GDIOBJECTS);
            for (int i = 0; i < 40; i++) cap.Capture(req);
            uint after = GetGuiResources(self, GR_GDIOBJECTS);
            _out.WriteLine($"GDI objects before {before}, after {after}");
            Assert.True(after <= before + 5, $"GDI objects grew from {before} to {after}");
        }

        [Fact]
        public void Grid_positions_and_cursor_placement()
        {
            Assert.Equal(new[] { 100, 200, 300 }, WindowsScreenCapture.GridPositions(400, 100));
            Assert.Equal(new[] { 100, 200, 300, 400 }, WindowsScreenCapture.GridPositions(401, 100));
            Assert.Empty(WindowsScreenCapture.GridPositions(400, 0));
            Assert.Empty(WindowsScreenCapture.GridPositions(50, 100));

            var src = new ScreenRect(-1920, 0, 1920, 1080);
            Assert.Equal((910, 495), WindowsScreenCapture.CursorDrawPosition(new ScreenPoint(-1000, 500), 10, 5, src));
        }

        [Fact]
        public void Grid_is_drawn_onto_the_image()
        {
            using var bmp = new System.Drawing.Bitmap(400, 300, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bmp)) g.Clear(System.Drawing.Color.FromArgb(128, 128, 128));
            WindowsScreenCapture.DrawGrid(bmp, 100);
            var background = System.Drawing.Color.FromArgb(128, 128, 128).ToArgb();
            Assert.NotEqual(background, bmp.GetPixel(100, 150).ToArgb()); // vertical line
            Assert.NotEqual(background, bmp.GetPixel(250, 200).ToArgb()); // horizontal line
            Assert.Equal(background, bmp.GetPixel(150, 150).ToArgb()); // middle of a cell
            Assert.NotEqual(background, bmp.GetPixel(104, 6).ToArgb()); // label box at the top edge
        }

        private static (int, int) DecodedSize(byte[] data)
        {
            using var ms = new MemoryStream(data);
            using var img = System.Drawing.Image.FromStream(ms);
            return (img.Width, img.Height);
        }
    }

    // ------------------------------------------------------------------ windows (read-only queries)

    public class WindowManagerTests
    {
        private readonly ITestOutputHelper _out;
        public WindowManagerTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void Lists_titled_app_windows()
        {
            var wm = new WindowsWindowManager();
            var sw = Stopwatch.StartNew();
            var windows = wm.ListWindows();
            _out.WriteLine($"{windows.Count} windows in {sw.ElapsedMilliseconds} ms, {windows.Count(w => w.IsElevated)} elevated, {windows.Count(w => w.IsMinimized)} minimized");
            Assert.All(windows, w =>
            {
                Assert.False(string.IsNullOrWhiteSpace(w.Title));
                Assert.True(w.IsVisible);
                Assert.NotEqual("Progman", w.ClassName);
                Assert.True(w.ProcessId > 0);
            });
            // A window can vanish mid-enumeration, so allow a stray unnamed one.
            Assert.True(windows.Count(w => string.IsNullOrEmpty(w.ProcessName)) <= 1);
            Assert.Equal(windows.Count, windows.Select(w => w.Handle).Distinct().Count());
            Assert.True(windows.Count(w => w.IsForeground) <= 1);
        }

        [Fact]
        public void Foreground_and_hit_test_do_not_throw()
        {
            var wm = new WindowsWindowManager();
            var fg = wm.GetForegroundWindow();
            if (fg != null) Assert.True(fg.IsForeground);
            var monitors = new WindowsScreenCapture().GetMonitors();
            var center = monitors.First(m => m.IsPrimary).Bounds.Center;
            var at = wm.GetWindowAt(center.X, center.Y);
            if (at != null) Assert.NotEqual(0, at.Handle);
            _ = wm.IsUacPromptActive();
        }

        [Fact]
        public void Focus_rejects_invalid_handles_without_side_effects()
        {
            var wm = new WindowsWindowManager();
            Assert.False(wm.FocusWindow(0));
            Assert.False(wm.FocusWindow(unchecked((nint)0x7FFF0001)));
        }

        [Fact]
        public void Own_elevation_matches_the_admin_role()
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            Assert.Equal(admin, new WindowsWindowManager().IsCurrentProcessElevated);
        }

        [Theory]
        [InlineData(true, false, "Notepad", 0L, "Notepad", true)]
        [InlineData(false, false, "Notepad", 0L, "Notepad", false)]
        [InlineData(true, true, "Settings", 0L, "ApplicationFrameWindow", false)]
        [InlineData(true, false, "", 0L, "Chrome_WidgetWin_1", false)]
        [InlineData(true, false, "  ", 0L, "X", false)]
        [InlineData(true, false, "Tool", 0x80L, "X", false)]
        [InlineData(true, false, "Tool app", 0x80L | 0x40000L, "X", true)]
        [InlineData(true, false, "Program Manager", 0L, "Progman", false)]
        public void Window_filter(bool visible, bool cloaked, string title, long exStyle, string cls, bool expected) =>
            Assert.Equal(expected, WindowsWindowManager.ShouldList(visible, cloaked, title, exStyle, cls));

        [Fact]
        public void Uac_and_elevation_rules()
        {
            Assert.True(WindowsWindowManager.IsUacPromptWindow("consent", "Whatever"));
            Assert.True(WindowsWindowManager.IsUacPromptWindow("Consent", ""));
            Assert.True(WindowsWindowManager.IsUacPromptWindow("CredentialUIBroker", "Credential Dialog Xaml Host"));
            Assert.False(WindowsWindowManager.IsUacPromptWindow("notepad", "Notepad"));

            Assert.True(WindowsWindowManager.InferElevated(true, false, false));
            Assert.False(WindowsWindowManager.InferElevated(false, false, false));
            Assert.True(WindowsWindowManager.InferElevated(null, accessDenied: true, selfElevated: false));
            Assert.False(WindowsWindowManager.InferElevated(null, accessDenied: true, selfElevated: true));
            Assert.False(WindowsWindowManager.InferElevated(null, accessDenied: false, selfElevated: false));
        }
    }

    // ------------------------------------------------------------------ UI Automation (reads only)

    public class UiAutomationTests
    {
        private readonly ITestOutputHelper _out;
        public UiAutomationTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public async Task Reads_the_foreground_window_within_the_timeout()
        {
            var ui = new UiAutomationInspector();
            var sw = Stopwatch.StartNew();
            var elements = await ui.GetElementsAsync(0, 15, CancellationToken.None);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(7), $"took {sw.Elapsed}");
            _out.WriteLine($"{elements.Count} elements in {sw.ElapsedMilliseconds} ms");
            Assert.True(elements.Count <= 15);
            Assert.All(elements, e =>
            {
                Assert.False(e.Bounds.IsEmpty);
                Assert.True(e.Name.Length <= UiAutomationInspector.MaxTextLength);
                Assert.True((e.Value?.Length ?? 0) <= UiAutomationInspector.MaxTextLength);
                Assert.False(string.IsNullOrEmpty(e.ControlType));
            });
        }

        [Fact]
        public async Task Invalid_window_gives_an_empty_list()
        {
            var elements = await new UiAutomationInspector().GetElementsAsync(unchecked((nint)0x7FFF0003), 10, CancellationToken.None);
            Assert.Empty(elements);
        }

        [Fact]
        public async Task Element_at_point_returns_quickly()
        {
            var center = new WindowsScreenCapture().GetMonitors().First(m => m.IsPrimary).Bounds.Center;
            var sw = Stopwatch.StartNew();
            var el = await new UiAutomationInspector().GetElementAtAsync(center.X, center.Y, CancellationToken.None);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"took {sw.Elapsed}");
            _out.WriteLine($"{(el == null ? "no element" : el.ControlType)} in {sw.ElapsedMilliseconds} ms");
            if (el != null) Assert.True(el.Name.Length <= UiAutomationInspector.MaxTextLength);
        }

        [Fact]
        public async Task Cancelled_token_throws()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var ui = new UiAutomationInspector();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ui.GetElementsAsync(0, 10, cts.Token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ui.GetElementAtAsync(0, 0, cts.Token));
        }

        [Fact]
        public async Task Timeout_returns_the_fallback_without_waiting_for_hung_work()
        {
            bool abandoned = false;
            using var release = new ManualResetEventSlim();
            var sw = Stopwatch.StartNew();
            var result = await UiAutomationInspector.RunWithTimeout(
                () => { release.Wait(TimeSpan.FromSeconds(10)); return "work"; },
                () => "partial",
                () => abandoned = true,
                TimeSpan.FromMilliseconds(150),
                CancellationToken.None);
            release.Set();
            Assert.Equal("partial", result);
            Assert.True(abandoned);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));
        }

        [Fact]
        public async Task Work_runs_per_monitor_aware_and_errors_propagate()
        {
            var aware = await UiAutomationInspector.RunWithTimeout(DpiScope.IsThreadPerMonitorV2, () => false, () => { }, TimeSpan.FromSeconds(5), CancellationToken.None);
            Assert.True(aware);
            await Assert.ThrowsAsync<FormatException>(() => UiAutomationInspector.RunWithTimeout<int>(() => throw new FormatException("x"), () => 0, () => { }, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        [Fact]
        public async Task Cancellation_mid_way_abandons_the_work()
        {
            using var cts = new CancellationTokenSource();
            using var release = new ManualResetEventSlim();
            bool abandoned = false;
            var task = UiAutomationInspector.RunWithTimeout(() => { release.Wait(TimeSpan.FromSeconds(10)); return 1; }, () => 0, () => abandoned = true, TimeSpan.FromSeconds(10), cts.Token);
            cts.CancelAfter(100);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            release.Set();
            Assert.True(abandoned);
        }

        [Fact]
        public void Helpers_trim_classify_and_convert()
        {
            Assert.Equal("", UiAutomationInspector.Trim(null));
            Assert.Equal("Save as", UiAutomationInspector.Trim("  Save \r\n  as "));
            var trimmed = UiAutomationInspector.Trim(new string('n', 300));
            Assert.Equal(UiAutomationInspector.MaxTextLength, trimmed.Length);

            Assert.True(UiAutomationInspector.IsInteractive("Button", false));
            Assert.True(UiAutomationInspector.IsInteractive("Hyperlink", false));
            Assert.True(UiAutomationInspector.IsInteractive("Pane", true));
            Assert.False(UiAutomationInspector.IsInteractive("Pane", false));
            Assert.False(UiAutomationInspector.IsInteractive("Text", false));

            Assert.Equal("Button", UiAutomationInspector.ControlTypeName(System.Windows.Automation.ControlType.Button));
            Assert.Equal("MenuItem", UiAutomationInspector.ControlTypeName(System.Windows.Automation.ControlType.MenuItem));
            Assert.Equal("Unknown", UiAutomationInspector.ControlTypeName(null));

            Assert.Equal(default, UiAutomationInspector.ToScreenRect(System.Windows.Rect.Empty));
            Assert.Equal(new ScreenRect(-10, 20, 101, 50), UiAutomationInspector.ToScreenRect(new System.Windows.Rect(-10.2, 19.6, 100.6, 50.4)));
            Assert.Equal(default, UiAutomationInspector.ToScreenRect(new System.Windows.Rect(5, 5, 0, 10)));
        }

        [Fact]
        public void Collector_caps_and_stops_after_abandon()
        {
            var c = new UiAutomationInspector.Collector(2);
            var e = new UiElementInfo("a", "Button", new ScreenRect(0, 0, 1, 1), null, true, true, null);
            c.Add(e);
            Assert.False(c.IsDone);
            c.Add(e);
            c.Add(e);
            Assert.True(c.IsDone);
            Assert.Equal(2, c.Snapshot().Count);

            var d = new UiAutomationInspector.Collector(5);
            d.Abandon();
            d.Add(e);
            Assert.True(d.IsDone);
            Assert.Empty(d.Snapshot());
        }
    }

    // ------------------------------------------------------------------ app launcher (classification and read-only search only)

    public class AppLauncherTests
    {
        private readonly ITestOutputHelper _out;
        public AppLauncherTests(ITestOutputHelper output) => _out = output;

        private static LaunchTarget Classify(string target, string[]? files = null, string[]? dirs = null, Dictionary<string, string>? commands = null) =>
            AppTargetClassifier.Classify(
                target,
                p => files?.Contains(p, StringComparer.OrdinalIgnoreCase) == true,
                p => dirs?.Contains(p, StringComparer.OrdinalIgnoreCase) == true,
                n => commands != null && commands.TryGetValue(n, out var r) ? r : null);

        [Theory]
        [InlineData("https://example.com/a?b=c", "https://example.com/a?b=c")]
        [InlineData("http://localhost:8080", "http://localhost:8080")]
        [InlineData("mailto:someone@example.com", "mailto:someone@example.com")]
        [InlineData("ms-settings:display", "ms-settings:display")]
        [InlineData("steam://rungameid/570", "steam://rungameid/570")]
        [InlineData("shell:Downloads", "shell:Downloads")]
        [InlineData("file:///C:/temp/x.txt", "file:///C:/temp/x.txt")]
        [InlineData("www.example.com", "https://www.example.com")]
        [InlineData("  \"https://example.com\"  ", "https://example.com")]
        public void Urls_and_protocols(string target, string expected)
        {
            var t = Classify(target);
            Assert.Equal(LaunchKind.Uri, t.Kind);
            Assert.Equal(expected, t.Value);
        }

        [Fact]
        public void Existing_and_missing_paths()
        {
            Assert.Equal(new LaunchTarget(LaunchKind.Path, @"C:\Data\report.docx"), Classify(@"C:\Data\report.docx", files: new[] { @"C:\Data\report.docx" }));
            Assert.Equal(new LaunchTarget(LaunchKind.Path, @"C:\Data"), Classify("\"C:\\Data\"", dirs: new[] { @"C:\Data" }));
            Assert.Equal(new LaunchTarget(LaunchKind.Path, @"D:\"), Classify("D:", dirs: new[] { @"D:\" }));
            Assert.Equal(LaunchKind.MissingPath, Classify(@"C:\Nope\missing.txt").Kind);
            Assert.Equal(LaunchKind.MissingPath, Classify(@"\\server\share\file.txt").Kind);
            Assert.Equal(new LaunchTarget(LaunchKind.Path, @"\\server\share\file.txt"), Classify(@"\\server\share\file.txt", files: new[] { @"\\server\share\file.txt" }));
        }

        [Fact]
        public void Environment_variables_and_home_are_expanded()
        {
            var dir = Path.Combine(Path.GetTempPath(), "deskpilot-launch-" + Guid.NewGuid().ToString("N"));
            Environment.SetEnvironmentVariable("DESKPILOT_TEST_LAUNCH_DIR", dir);
            try
            {
                var t = Classify("%DESKPILOT_TEST_LAUNCH_DIR%\\notes.txt", files: new[] { Path.Combine(dir, "notes.txt") });
                Assert.Equal(new LaunchTarget(LaunchKind.Path, Path.Combine(dir, "notes.txt")), t);
            }
            finally
            {
                Environment.SetEnvironmentVariable("DESKPILOT_TEST_LAUNCH_DIR", null);
            }
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(new LaunchTarget(LaunchKind.Path, Path.Combine(home, "Documents")), Classify(@"~\Documents", dirs: new[] { Path.Combine(home, "Documents") }));
        }

        [Fact]
        public void Commands_and_app_names()
        {
            var commands = new Dictionary<string, string> { ["notepad"] = @"C:\Windows\system32\notepad.exe", ["winword"] = @"C:\Office\WINWORD.EXE" };
            Assert.Equal(new LaunchTarget(LaunchKind.Command, @"C:\Windows\system32\notepad.exe"), Classify("notepad", commands: commands));
            Assert.Equal(new LaunchTarget(LaunchKind.Command, @"C:\Office\WINWORD.EXE"), Classify("winword", commands: commands));
            Assert.Equal(new LaunchTarget(LaunchKind.AppName, "Visual Studio Code"), Classify("Visual Studio Code", commands: commands));
            Assert.Equal(new LaunchTarget(LaunchKind.AppName, "Calculator"), Classify("Calculator"));
            Assert.Throws<ArgumentException>(() => Classify("   "));
        }

        [Fact]
        public void Real_command_resolution_is_read_only_and_finds_system_programs()
        {
            var cmd = AppTargetClassifier.ResolveCommand("cmd");
            Assert.NotNull(cmd);
            Assert.EndsWith("cmd.exe", cmd, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(AppTargetClassifier.ResolveCommand("notepad.exe"));
            Assert.Null(AppTargetClassifier.ResolveCommand("definitely-not-a-program-" + Guid.NewGuid().ToString("N")));
            Assert.Null(AppTargetClassifier.ResolveCommand(@"C:\Windows\notepad.exe"));
            Assert.Equal(LaunchKind.Command, AppTargetClassifier.Classify("cmd").Kind);
        }

        [Fact]
        public void Match_tiers()
        {
            Assert.Equal(StartMenuSearch.MatchTier.Exact, StartMenuSearch.Match("Visual Studio Code", "visual studio code"));
            Assert.Equal(StartMenuSearch.MatchTier.StartsWith, StartMenuSearch.Match("Visual Studio Code", "visual"));
            Assert.Equal(StartMenuSearch.MatchTier.Contains, StartMenuSearch.Match("Visual Studio Code", "studio"));
            Assert.Equal(StartMenuSearch.MatchTier.AllWords, StartMenuSearch.Match("Visual Studio Code", "code visual"));
            Assert.Equal(StartMenuSearch.MatchTier.None, StartMenuSearch.Match("Visual Studio Code", "word"));
            Assert.Equal(StartMenuSearch.MatchTier.None, StartMenuSearch.Match("", "word"));
        }

        [Fact]
        public void Start_menu_search_ranks_exact_then_prefix_then_substring()
        {
            var root = Path.Combine(Path.GetTempPath(), "deskpilot-startmenu-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(root, "Tools", "Deep"));
                foreach (var f in new[] { "Visual Studio Code.lnk", "Code Helper.lnk", "Uninstall Code.lnk", "Notepad++.lnk", "readme.txt", @"Tools\Calculator Plus.url", @"Tools\Deep\Code.lnk" })
                    File.WriteAllText(Path.Combine(root, f), "");

                var entries = StartMenuSearch.EnumerateShortcuts(new[] { root, Path.Combine(root, "does-not-exist") });
                Assert.Equal(6, entries.Count);
                Assert.DoesNotContain(entries, e => e.Name == "readme");

                Assert.Equal("Code", StartMenuSearch.FindBest(entries, "code")!.Value.Entry.Name);
                Assert.Equal(StartMenuSearch.MatchTier.Exact, StartMenuSearch.FindBest(entries, "CODE")!.Value.Tier);
                Assert.Equal("Visual Studio Code", StartMenuSearch.FindBest(entries, "visual")!.Value.Entry.Name);
                Assert.Equal("Visual Studio Code", StartMenuSearch.FindBest(entries, "studio")!.Value.Entry.Name);
                Assert.Equal("Calculator Plus", StartMenuSearch.FindBest(entries, "calculator")!.Value.Entry.Name);
                Assert.EndsWith("Calculator Plus.url", StartMenuSearch.FindBest(entries, "calculator")!.Value.Entry.ShortcutPath);
                Assert.Equal("Notepad++", StartMenuSearch.FindBest(entries, "notepad")!.Value.Entry.Name);
                Assert.Null(StartMenuSearch.FindBest(entries, "photoshop"));

                // Uninstallers only when asked for.
                var noCode = entries.Where(e => e.Name is not ("Code" or "Code Helper" or "Visual Studio Code")).ToList();
                Assert.Null(StartMenuSearch.FindBest(noCode, "code"));
                Assert.Equal("Uninstall Code", StartMenuSearch.FindBest(entries, "uninstall code")!.Value.Entry.Name);

                // Shorter name wins a tie; on equal length the earlier entry (shortcuts before Store apps) wins.
                var tie = new[] { new StartMenuEntry("Paint 3D", "a.lnk", null), new StartMenuEntry("Paint", null, "Microsoft.Paint!App"), new StartMenuEntry("Paint", "b.lnk", null) };
                Assert.Equal("Microsoft.Paint!App", StartMenuSearch.FindBest(tie, "paint")!.Value.Entry.AppUserModelId);
            }
            finally
            {
                try { Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Fact]
        public void Real_start_menu_and_apps_folder_can_be_read()
        {
            foreach (var root in StartMenuSearch.DefaultShortcutRoots()) Assert.True(Directory.Exists(root));
            var shortcuts = StartMenuSearch.EnumerateShortcuts(StartMenuSearch.DefaultShortcutRoots());
            Assert.All(shortcuts, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
            var sw = Stopwatch.StartNew();
            var apps = StartMenuSearch.EnumerateAppsFolder(TimeSpan.FromSeconds(15));
            _out.WriteLine($"{shortcuts.Count} shortcuts, {apps.Count} apps-folder items in {sw.ElapsedMilliseconds} ms");
            Assert.All(apps, a =>
            {
                Assert.False(string.IsNullOrWhiteSpace(a.Name));
                Assert.False(string.IsNullOrWhiteSpace(a.AppUserModelId));
            });
        }

        [Fact]
        public void Launch_reports_problems_without_starting_anything()
        {
            var launcher = new WindowsAppLauncher();
            var empty = launcher.Launch("  ", null, false);
            Assert.False(empty.Success);
            var missing = launcher.Launch(@"C:\deskpilot-missing-" + Guid.NewGuid().ToString("N") + @"\file.txt", null, false);
            Assert.False(missing.Success);
            Assert.Contains("Nothing exists", missing.Message);
            Assert.Null(missing.ProcessId);
        }

        [Fact]
        public void Display_name_prefers_the_file_description()
        {
            var cmd = AppTargetClassifier.ResolveCommand("cmd")!;
            Assert.False(string.IsNullOrWhiteSpace(WindowsAppLauncher.DisplayName(cmd)));
            Assert.Equal("missing-app", WindowsAppLauncher.DisplayName(@"C:\nowhere\missing-app.exe"));
        }
    }

    // ------------------------------------------------------------------ clipboard (never written in tests)

    public class ClipboardTests
    {
        [Fact]
        public void Retry_recovers_from_a_busy_clipboard()
        {
            int calls = 0;
            var sleeps = new List<int>();
            var result = WindowsClipboard.Retry(() =>
            {
                calls++;
                if (calls < 3) throw new ExternalException("busy", unchecked((int)0x800401D0));
                return "ok";
            }, sleep: sleeps.Add);
            Assert.Equal("ok", result);
            Assert.Equal(3, calls);
            Assert.Equal(new[] { 50, 50 }, sleeps);
        }

        [Fact]
        public void Retry_gives_up_after_five_attempts()
        {
            int calls = 0;
            Assert.Throws<COMException>(() => WindowsClipboard.Retry<string>(() => { calls++; throw new COMException("busy"); }, sleep: _ => { }));
            Assert.Equal(5, calls);
        }

        [Fact]
        public void Retry_does_not_retry_unrelated_errors()
        {
            int calls = 0;
            Assert.Throws<InvalidOperationException>(() => WindowsClipboard.Retry<string>(() => { calls++; throw new InvalidOperationException(); }, sleep: _ => { }));
            Assert.Equal(1, calls);
        }

        [Fact]
        public void Sta_thread_runs_work_and_reports_errors_and_timeouts()
        {
            Assert.Equal(ApartmentState.STA, StaThread.Run(() => Thread.CurrentThread.GetApartmentState(), TimeSpan.FromSeconds(5), "test"));
            Assert.Throws<FormatException>(() => StaThread.Run<int>(() => throw new FormatException(), TimeSpan.FromSeconds(5), "test"));
            var ex = Assert.Throws<TimeoutException>(() => StaThread.Run(() => Thread.Sleep(2000), TimeSpan.FromMilliseconds(100), "wait"));
            Assert.Contains("wait", ex.Message);
        }

        [Fact]
        public void Reading_the_clipboard_does_not_throw()
        {
            var text = new WindowsClipboard().GetText();
            Assert.True(text == null || text.Length >= 0);
        }
    }

    // ------------------------------------------------------------------ shell runner (hidden processes)

    public class ShellRunnerTests
    {
        private static readonly string Temp = Path.GetTempPath();

        [Fact]
        public async Task PowerShell_output_and_exit_code()
        {
            var r = await new PowerShellRunner().RunAsync("Write-Output 'hello'; Write-Output 'caf\u00e9 \u2713'", "powershell", Temp, 30_000, CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
            Assert.False(r.TimedOut);
            Assert.Contains("hello", r.StdOut);
            Assert.Contains("caf\u00e9 \u2713", r.StdOut);

            var fail = await new PowerShellRunner().RunAsync("Write-Error 'boom'; exit 3", "powershell", Temp, 30_000, CancellationToken.None);
            Assert.Equal(3, fail.ExitCode);
            Assert.Contains("boom", fail.StdErr);
        }

        [Fact]
        public async Task PowerShell_handles_quotes_and_special_characters()
        {
            var r = await new PowerShellRunner().RunAsync("$a = \"x`\"y\"; Write-Output ($a + ' & | < > %PATH% \"q\"')", "powershell", Temp, 30_000, CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
            Assert.Contains("x\"y & | < > %PATH% \"q\"", r.StdOut);
        }

        [Fact]
        public async Task Cmd_output_exit_code_and_working_directory()
        {
            var runner = new PowerShellRunner();
            var echo = await runner.RunAsync("echo hi there", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal(0, echo.ExitCode);
            Assert.Equal("hi there", echo.StdOut.Trim());

            var exit = await runner.RunAsync("exit /b 4", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal(4, exit.ExitCode);

            var cd = await runner.RunAsync("cd", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal(Path.GetFullPath(Temp).TrimEnd('\\'), cd.StdOut.Trim().TrimEnd('\\'), ignoreCase: true);

            var unicode = await runner.RunAsync("echo café über 中文", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal("café über 中文", unicode.StdOut.Trim());
        }

        [Fact]
        public async Task Cmd_special_characters_and_multiple_lines_behave_like_a_prompt()
        {
            var runner = new PowerShellRunner();
            var r = await runner.RunAsync("echo \"a & b\" & echo x^&y & (echo p) | findstr p & if 1==1 (echo yes) else (echo no)", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal(new[] { "\"a & b\"", "x&y", "p", "yes" }, r.StdOut.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));

            var lines = await runner.RunAsync("echo one\r\necho two\n\nexit /b 7", "cmd", Temp, 30_000, CancellationToken.None);
            Assert.Equal(new[] { "one", "two" }, lines.StdOut.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()));
            Assert.Equal(7, lines.ExitCode);
        }

        [Fact]
        public async Task Pwsh_runs_or_falls_back_to_windows_powershell_with_a_note()
        {
            var r = await new PowerShellRunner().RunAsync("$PSVersionTable.PSVersion.Major", "pwsh", Temp, 30_000, CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
            Assert.True(int.Parse(r.StdOut.Trim()) >= 5, r.StdOut);
            if (r.StdOut.Trim() == "5") Assert.Contains("pwsh", r.StdErr);
        }

        [Fact]
        public async Task Windows_powershell_core_modules_load_with_a_clean_module_path()
        {
            var (psi, _) = PowerShellRunner.BuildStartInfo("x", "powershell", Temp);
            var fresh = PowerShellRunner.FreshPsModulePath();
            Assert.Equal(fresh, psi.Environment.TryGetValue("PSModulePath", out var v) ? v : null);

            var r = await new PowerShellRunner().RunAsync("(Get-Command Get-FileHash).Source", "powershell", Temp, 30_000, CancellationToken.None);
            Assert.Equal(0, r.ExitCode);
            Assert.Equal("Microsoft.PowerShell.Utility", r.StdOut.Trim());
        }

        [Fact]
        public async Task Timeout_kills_the_process_and_reports_it()
        {
            var sw = Stopwatch.StartNew();
            var r = await new PowerShellRunner().RunAsync("Write-Output 'started'; Start-Sleep -Seconds 30; Write-Output 'never'", "powershell", Temp, 2500, CancellationToken.None);
            Assert.True(r.TimedOut);
            Assert.Equal(-1, r.ExitCode);
            Assert.DoesNotContain("never", r.StdOut);
            Assert.Contains("timed out", r.StdErr);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"took {sw.Elapsed}");
        }

        [Fact]
        public async Task Timeout_kills_grandchildren_too()
        {
            static int PingCount()
            {
                var procs = Process.GetProcessesByName("PING");
                foreach (var p in procs) p.Dispose();
                return procs.Length;
            }
            int before = PingCount();
            var sw = Stopwatch.StartNew();
            // outer cmd -> inner cmd -> ping: all three must go.
            var r = await new PowerShellRunner().RunAsync("ping -n 30 127.0.0.1", "cmd", Temp, 2000, CancellationToken.None);
            Assert.True(r.TimedOut);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
            var deadline = Stopwatch.StartNew();
            while (PingCount() > before && deadline.ElapsedMilliseconds < 3000) await Task.Delay(100);
            Assert.True(PingCount() <= before, "ping.exe survived the timeout");
        }

        [Fact]
        public async Task Cancellation_throws()
        {
            using var pre = new CancellationTokenSource();
            pre.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PowerShellRunner().RunAsync("Write-Output 1", "powershell", Temp, 30_000, pre.Token));

            using var later = new CancellationTokenSource();
            later.CancelAfter(1500);
            var sw = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PowerShellRunner().RunAsync("Start-Sleep -Seconds 30", "powershell", Temp, 60_000, later.Token));
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15));
        }

        [Fact]
        public async Task Large_output_is_truncated_with_a_note()
        {
            var r = await new PowerShellRunner().RunAsync("Write-Output ('x' * 100000)", "powershell", Temp, 30_000, CancellationToken.None);
            Assert.True(r.StdOut.Length < PowerShellRunner.MaxStreamChars + 200);
            Assert.Contains("truncated", r.StdOut);
        }

        [Fact]
        public async Task Unknown_shell_is_rejected()
        {
            await Assert.ThrowsAsync<ArgumentException>(() => new PowerShellRunner().RunAsync("echo", "bash", Temp, 1000, CancellationToken.None));
        }

        [Fact]
        public void Arguments_are_built_robustly()
        {
            var ps = PowerShellRunner.PowerShellArguments("Write-Output \"a 'b'\"");
            Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", ps);
            var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(ps.Split(' ').Last()));
            Assert.StartsWith("[Console]::OutputEncoding=[Text.Encoding]::UTF8;", decoded);
            Assert.EndsWith("Write-Output \"a 'b'\"", decoded);

            var cmdArgs = PowerShellRunner.CmdArguments("echo \"x\" y");
            Assert.StartsWith("/d /s /c \"chcp 65001>nul & \"", cmdArgs);
            Assert.EndsWith("cmd.exe\" /d /s /c \"echo \"x\" y\"\"", cmdArgs, StringComparison.OrdinalIgnoreCase);

            // The command starts inside the outer quote, so only text between its own quotes needs carets.
            Assert.Equal("echo a & echo b", PowerShellRunner.EscapeForOuterCmd("echo a & echo b"));
            Assert.Equal("echo \"a ^& b\" & x", PowerShellRunner.EscapeForOuterCmd("echo \"a & b\" & x"));
            Assert.Equal("\"q\"^|<>()", PowerShellRunner.EscapeForOuterCmd("\"q\"^|<>()"));
            Assert.Equal("x\"^^^|^<^>^(^)\"", PowerShellRunner.EscapeForOuterCmd("x\"^|<>()\""));
            Assert.Equal("a & b &  c", PowerShellRunner.JoinLines("a\r\nb\n\n  \n c"));

            var (psi, _) = PowerShellRunner.BuildStartInfo("echo 1", "cmd", Temp);
            Assert.False(psi.UseShellExecute);
            Assert.True(psi.CreateNoWindow);
            Assert.True(psi.RedirectStandardOutput && psi.RedirectStandardError && psi.RedirectStandardInput);
            Assert.Equal("", psi.Verb);
        }

        [Fact]
        public void Working_directory_falls_back_to_the_user_profile()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            Assert.Equal(home, PowerShellRunner.ResolveWorkingDirectory(""));
            Assert.Equal(home, PowerShellRunner.ResolveWorkingDirectory(null));
            Assert.Equal(home, PowerShellRunner.ResolveWorkingDirectory(@"C:\deskpilot-missing-" + Guid.NewGuid().ToString("N")));
            Assert.Equal(home, PowerShellRunner.ResolveWorkingDirectory("relative\\dir"));
            Assert.Equal(Temp, PowerShellRunner.ResolveWorkingDirectory(Temp));
        }

        [Fact]
        public void Capped_text_counts_what_it_drops()
        {
            var c = new PowerShellRunner.CappedText(10);
            c.Append("hello ");
            c.Append("world, more text");
            var s = c.ToString();
            Assert.StartsWith("hello worl", s);
            Assert.Contains("12 more characters", s);

            var small = new PowerShellRunner.CappedText(100);
            small.Append("ok");
            Assert.Equal("ok", small.ToString());
            small.AppendLine("note");
            Assert.Equal("ok\nnote\n", small.ToString());
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>Compact text form of an INPUT: k41 / k41^ (key down/up), +: extended, u61: unicode unit, mouse flag names.</summary>
    internal static string Describe(INPUT input)
    {
        if (input.type == INPUT_KEYBOARD)
        {
            var ki = input.U.ki;
            bool up = (ki.dwFlags & KEYEVENTF_KEYUP) != 0;
            if ((ki.dwFlags & KEYEVENTF_UNICODE) != 0) return $"u{ki.wScan:X2}{(up ? "^" : "")}";
            bool ext = (ki.dwFlags & KEYEVENTF_EXTENDEDKEY) != 0;
            return $"k{ki.wVk:X2}{(ext ? "+" : "")}{(up ? "^" : "")}";
        }
        var mi = input.U.mi;
        if ((mi.dwFlags & MOUSEEVENTF_MOVE) != 0) return "MOVE";
        if ((mi.dwFlags & MOUSEEVENTF_WHEEL) != 0) return "WHEEL" + mi.mouseData;
        if ((mi.dwFlags & MOUSEEVENTF_HWHEEL) != 0) return "HWHEEL" + mi.mouseData;
        return mi.dwFlags switch
        {
            MOUSEEVENTF_LEFTDOWN => "LEFTDOWN",
            MOUSEEVENTF_LEFTUP => "LEFTUP",
            MOUSEEVENTF_RIGHTDOWN => "RIGHTDOWN",
            MOUSEEVENTF_RIGHTUP => "RIGHTUP",
            MOUSEEVENTF_MIDDLEDOWN => "MIDDLEDOWN",
            MOUSEEVENTF_MIDDLEUP => "MIDDLEUP",
            _ => $"MOUSE{mi.dwFlags:X}",
        };
    }

    /// <summary>A US-like layout: '+' is shift+VK_OEM_PLUS, '€' needs AltGr (ctrl+alt), 'ß' is missing.</summary>
    internal static short UsLayout(char c) => c switch
    {
        '+' => 0x01BB,
        '=' => 0x00BB,
        '-' => 0x00BD,
        ',' => 0x00BC,
        '.' => 0x00BE,
        '/' => 0x00BF,
        ';' => 0x00BA,
        '[' => 0x00DB,
        ']' => 0x00DD,
        '@' => 0x0132,
        '€' => 0x0645,
        _ => -1,
    };

    internal sealed class FakeInputBackend : IInputBackend
    {
        public readonly List<INPUT[]> Calls = new();
        public readonly List<string> Events = new();
        public readonly List<int> Sleeps = new();
        public readonly List<ScreenPoint> SetCursorCalls = new();
        public ScreenPoint Cursor;
        public bool CursorFollows = true;
        public ScreenRect Virtual = new(0, 0, 1920, 1080);
        public Func<INPUT[], int>? SendOverride;
        public int DpiScopes;
        public int OpenDpiScopes;

        public int DoubleClickTimeMs { get; set; } = 500;
        public bool ButtonsSwapped { get; set; }

        public IEnumerable<KEYBDINPUT> SentKeyInputs => Calls.SelectMany(c => c).Where(i => i.type == INPUT_KEYBOARD).Select(i => i.U.ki);

        public void Clear()
        {
            Calls.Clear();
            Events.Clear();
            Sleeps.Clear();
            SetCursorCalls.Clear();
        }

        public int Send(INPUT[] inputs)
        {
            Calls.Add(inputs);
            Events.AddRange(inputs.Select(Describe));
            int accepted = SendOverride?.Invoke(inputs) ?? inputs.Length;
            if (accepted == inputs.Length && CursorFollows)
            {
                foreach (var i in inputs.Where(i => i.type == INPUT_MOUSE && (i.U.mi.dwFlags & MOUSEEVENTF_ABSOLUTE) != 0))
                {
                    Cursor = new ScreenPoint(
                        Virtual.X + (int)((long)i.U.mi.dx * Virtual.Width / 65536),
                        Virtual.Y + (int)((long)i.U.mi.dy * Virtual.Height / 65536));
                }
            }
            return accepted;
        }

        public ScreenPoint GetCursorPos() => Cursor;

        public bool SetCursorPos(int x, int y)
        {
            SetCursorCalls.Add(new ScreenPoint(x, y));
            Cursor = new ScreenPoint(x, y);
            return true;
        }

        public ScreenRect GetVirtualScreen() => Virtual;
        public short VkKeyScan(char c) => UsLayout(c);
        public ushort ScanCode(ushort vk) => (ushort)(vk & 0x7F);
        public void Sleep(int ms) => Sleeps.Add(ms);

        public IDisposable EnterDpiScope()
        {
            DpiScopes++;
            OpenDpiScopes++;
            return new Scope(this);
        }

        private sealed class Scope : IDisposable
        {
            private FakeInputBackend? _owner;
            public Scope(FakeInputBackend owner) => _owner = owner;
            public void Dispose()
            {
                if (_owner != null) _owner.OpenDpiScopes--;
                _owner = null;
            }
        }
    }
}

using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Desktop.Linux;
using DeskPilot.Linux.Controls;
using DeskPilot.Linux.Services;
using DeskPilot.Linux.ViewModels;
using DeskPilot.Linux.Views;
using SkiaSharp;

namespace DeskPilot.Linux.Tests;

/// <summary>
/// The Linux main UI: view-model logic (pure, runs anywhere) and the windows built on the headless Avalonia
/// platform with fakes (no live session, nothing shown on a screen).
/// </summary>
public class LinuxUiMainTests
{
    // ================================================================ fakes

    internal sealed class UiFakeSession : IAgentSession
    {
        public event Action<AgentEvent>? EventRaised;
        public event Action<AgentState>? StateChanged;
        public AgentState State { get; private set; } = AgentState.Idle;
        public string ActiveDescription => "Fake - model";
        public Func<string, CancellationToken, Task<TurnResult>>? OnSend { get; set; }
        public int StopCalls;
        public int NewConversationCalls;
        public int ReloadCalls;
        public bool Disposed;

        public void Raise(AgentEvent e) => EventRaised?.Invoke(e);

        public void SetState(AgentState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }

        public async Task<TurnResult> SendAsync(string message, CancellationToken ct = default)
        {
            if (OnSend != null) return await OnSend(message, ct);
            return new TurnResult(TurnOutcome.Completed, "ok", new TurnStats(10, 5, 0, null, TimeSpan.FromSeconds(1), "m"), null);
        }

        public Task StopAsync(string reason = "Stopped by user")
        {
            Interlocked.Increment(ref StopCalls);
            return Task.CompletedTask;
        }

        public Task NewConversationAsync()
        {
            NewConversationCalls++;
            return Task.CompletedTask;
        }

        public Task ReloadSettingsAsync()
        {
            Interlocked.Increment(ref ReloadCalls);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class UiFakeCatalog : IModelCatalog
    {
        public ModelListResult Result { get; set; } = new(new[]
        {
            new ModelInfo("model-a", "Model A", true, true, true),
            new ModelInfo("model-b", "", false, null, null),
        }, null);

        public string? LastKey;

        public Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
        {
            LastKey = apiKey;
            return Task.FromResult(Result);
        }
    }

    internal sealed class UiFakeDetector : IEnvironmentDetector
    {
        public Task<EnvironmentReport> DetectAsync(CancellationToken ct) => Task.FromResult(App.EmptyEnvironmentReport());
    }

    /// <summary>Queues posted work until the test runs it, like a UI thread that is busy for a moment.</summary>
    internal sealed class UiQueueDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public int RunAll()
        {
            int n = 0;
            while (_queue.TryDequeue(out var a))
            {
                a();
                n++;
            }
            return n;
        }
    }

    private static SettingsStore NewStore(Action<AppSettings>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "DeskPilotLinuxUiTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        var s = store.CloneCurrent();
        s.FirstRunCompleted = true;
        configure?.Invoke(s);
        store.Save(s);
        return store;
    }

    private static (MainViewModel Vm, UiFakeSession Session, UiQueueDispatcher Ui, SettingsStore Store) NewViewModel(
        LinuxSessionKind kind = LinuxSessionKind.X11, Action<AppSettings>? configure = null, ConversationLog? log = null)
    {
        var store = NewStore(configure);
        var session = new UiFakeSession();
        var ui = new UiQueueDispatcher();
        var vm = new MainViewModel(store, session, new UiFakeCatalog(), new UiFakeDetector(), ui, log, kind);
        return (vm, session, ui, store);
    }

    private static byte[] MakePng(int width, int height, SKColor color)
    {
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        bmp.Erase(color);
        using var image = SKImage.FromBitmap(bmp);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static ToolImage MakeToolImage(int width = 320, int height = 200) =>
        ToolImage.FromBytes(MakePng(width, height, new SKColor(0x40, 0x60, 0xA0)), "image/png", width, height);

    // ================================================================ conversation log (pure)

    [Fact]
    public void Partial_assistant_chunks_append_to_one_item_and_a_final_block_replaces_them()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("hi");
        log.Apply(new AssistantTextEvent("Hel", IsPartial: true));
        log.Apply(new AssistantTextEvent("lo ", IsPartial: true));
        log.Apply(new AssistantTextEvent("world", IsPartial: true));

        var item = Assert.IsType<AssistantTextItem>(log.Items[^1]);
        Assert.Equal("Hello world", item.Text);
        Assert.True(item.IsStreaming);
        Assert.Equal(2, log.Items.Count);

        // The complete block repeats the streamed text: no duplicate.
        log.Apply(new AssistantTextEvent("Hello world"));
        Assert.Equal(2, log.Items.Count);
        Assert.False(item.IsStreaming);

        // A tool step closes the stream; new text starts a new item.
        log.Apply(new ToolCallEvent("c1", "screenshot", "{}", "Take a screenshot"));
        log.Apply(new AssistantTextEvent("Next", IsPartial: true));
        Assert.Equal(4, log.Items.Count);
        Assert.Equal("Next", Assert.IsType<AssistantTextItem>(log.Items[^1]).Text);
    }

    [Fact]
    public void Thinking_and_text_stream_into_separate_items_and_echoed_user_message_is_ignored()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("do it");
        log.Apply(new UserMessageEvent("do it"));
        Assert.Single(log.Items);

        log.Apply(new ThinkingEvent("plan ", IsPartial: true));
        log.Apply(new ThinkingEvent("more", IsPartial: true));
        log.Apply(new AssistantTextEvent("ok", IsPartial: true));
        Assert.Equal(3, log.Items.Count);
        Assert.Equal("plan more", Assert.IsType<ThinkingItem>(log.Items[1]).Text);
        Assert.Equal("ok", Assert.IsType<AssistantTextItem>(log.Items[2]).Text);
    }

    [Fact]
    public void Show_thinking_toggles_existing_and_new_thinking_items()
    {
        var log = new ConversationLog();
        log.Apply(new ThinkingEvent("a"));
        log.ShowThinking = false;
        Assert.False(Assert.IsType<ThinkingItem>(log.Items[0]).IsShown);
        log.Apply(new StatusEvent("x"));
        log.Apply(new ThinkingEvent("b"));
        Assert.False(Assert.IsType<ThinkingItem>(log.Items[^1]).IsShown);
        log.ShowThinking = true;
        Assert.All(log.Items.OfType<ThinkingItem>(), t => Assert.True(t.IsShown));
    }

    [Fact]
    public void Log_is_capped_and_drops_the_oldest_items()
    {
        var log = new ConversationLog(maxItems: 50);
        for (int i = 0; i < 120; i++) log.AddStatus($"status {i}");
        Assert.Equal(50, log.Items.Count);
        Assert.Equal("status 70", Assert.IsType<StatusLineItem>(log.Items[0]).Message);
        Assert.Equal("status 119", Assert.IsType<StatusLineItem>(log.Items[^1]).Message);

        var defaults = new ConversationLog();
        Assert.Equal(2000, defaults.MaxItems);
    }

    [Fact]
    public void Tool_results_pair_with_calls_and_only_recent_images_are_kept()
    {
        var log = new ConversationLog(maxItems: 100, maxImages: 2);
        var image = MakeToolImage(40, 30);
        for (int i = 0; i < 4; i++)
        {
            log.Apply(new ToolCallEvent($"c{i}", "screenshot", "{}", $"Screenshot {i}"));
            log.Apply(new ToolResultEvent($"c{i}", "screenshot", false, "1280x800", image, TimeSpan.FromMilliseconds(300)));
        }
        var steps = log.Items.OfType<ToolStepItem>().ToList();
        Assert.Equal(4, steps.Count);
        Assert.All(steps, s => Assert.False(s.IsRunning));
        Assert.Equal(new[] { false, false, true, true }, steps.Select(s => s.HasImage));
        Assert.Equal(2, log.ImageCount);
        Assert.Equal("0.3s", steps[0].DurationText);

        // A call whose result never arrives is abandoned when the turn ends.
        log.Apply(new ToolCallEvent("c9", "click", "{}", "Click"));
        log.AddTurnResult(new TurnResult(TurnOutcome.Cancelled, null, new TurnStats(0, 0, 1, null, TimeSpan.FromSeconds(2), null), null));
        Assert.False(log.Items.OfType<ToolStepItem>().Last().IsRunning);
        Assert.IsType<TurnFooterItem>(log.Items[^1]);
        Assert.Null(log.AddTurnResult(new TurnResult(TurnOutcome.Completed, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), null)));

        log.ReleaseImages();
        Assert.All(log.Items.OfType<ToolStepItem>(), s => Assert.False(s.HasImage));
    }

    [Fact]
    public void Copy_log_text_lists_every_item()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("open the editor");
        log.Apply(new ToolCallEvent("c1", "launch", "{}", "Open gedit"));
        log.Apply(new ToolResultEvent("c1", "launch", true, "not found", null, TimeSpan.FromSeconds(1.5)));
        log.Apply(new AssistantTextEvent("Done"));
        log.AddTurnResult(new TurnResult(TurnOutcome.Completed, "Done", new TurnStats(1200, 30, 1, 0.0021, TimeSpan.FromSeconds(4), "haiku"), null));
        var text = log.ToPlainText();
        Assert.Contains("You: open the editor", text);
        Assert.Contains("> Open gedit (1.5s)  [error] not found", text);
        Assert.Contains("DeskPilot: Done", text);
        Assert.Contains("--- Done  ·  1 step  ·  1.2k in / 30 out tokens  ·  $0.0021  ·  4.0s  ·  haiku", text);
    }

    [Fact]
    public void Tool_icons_and_footer_icons_map_to_path_data()
    {
        Assert.Equal(Icons.Camera, ToolIcons.For("screenshot"));
        Assert.Equal(Icons.Keyboard, ToolIcons.For("TYPE_TEXT"));
        Assert.Equal(ToolIcons.Default, ToolIcons.For("something_new"));
        Assert.Equal(ToolIcons.Default, ToolIcons.For(null));
        var stats = new TurnStats(0, 0, 0, null, TimeSpan.Zero, null);
        Assert.Equal(Icons.Error, new TurnFooterItem(new TurnResult(TurnOutcome.Failed, null, stats, "boom")).Icon);
        Assert.Equal(Icons.Stop, new TurnFooterItem(new TurnResult(TurnOutcome.Cancelled, null, stats, null)).Icon);
        Assert.Equal(Icons.Warning, new StatusLineItem("w", StatusLevel.Warning).Icon);
    }

    // ================================================================ view model (pure)

    [Fact]
    public void Status_labels_and_display_state_follow_the_session()
    {
        Assert.Equal("Idle", MainViewModel.StatusLabel(AgentState.Idle));
        Assert.Equal("Working", MainViewModel.StatusLabel(AgentState.Running));
        Assert.Equal("Stopping", MainViewModel.StatusLabel(AgentState.Stopping));
        Assert.Equal("Error", MainViewModel.StatusLabel(AgentState.Error));
        Assert.Equal("Starting", MainViewModel.StatusLabel(AgentState.Starting));

        var (vm, session, ui, _) = NewViewModel();
        Assert.False(vm.IsBusy);
        Assert.Equal("Send", vm.SendButtonText);

        session.SetState(AgentState.Running);
        Assert.Equal(AgentState.Idle, vm.State); // not applied until the UI thread runs it
        ui.RunAll();
        Assert.Equal(AgentState.Running, vm.State);
        Assert.True(vm.IsBusy);
        Assert.True(vm.IsStateActive);
        Assert.Equal("Stop", vm.SendButtonText);
        Assert.Equal(Icons.Stop, vm.SendButtonIcon);

        session.SetState(AgentState.Stopping);
        ui.RunAll();
        Assert.True(vm.IsStateStopping);
        Assert.False(vm.IsStateActive);

        session.SetState(AgentState.Error);
        ui.RunAll();
        Assert.True(vm.IsStateError);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Send_shows_starting_while_pending_and_adds_events_then_a_footer()
    {
        var (vm, session, ui, _) = NewViewModel();
        var release = new TaskCompletionSource();
        session.OnSend = async (_, _) =>
        {
            session.Raise(new ToolCallEvent("c1", "click", "{}", "Click OK"));
            session.Raise(new ToolResultEvent("c1", "click", false, "clicked", null, TimeSpan.FromMilliseconds(120)));
            session.Raise(new AssistantTextEvent("All done", IsPartial: true));
            await release.Task;
            return new TurnResult(TurnOutcome.Completed, "All done", new TurnStats(100, 20, 1, null, TimeSpan.FromSeconds(3), "m"), null);
        };
        int started = 0, ended = 0;
        vm.TurnStarted += () => started++;
        vm.TurnEnded += () => ended++;

        vm.InputText = "  click ok  ";
        Assert.True(vm.SendOrStopCommand.CanExecute(null));
        var send = vm.SendAsync();
        Assert.Equal(1, started);
        Assert.Equal("", vm.InputText);
        Assert.True(vm.IsBusy);
        Assert.Equal(AgentState.Starting, vm.DisplayState);
        Assert.Equal("Starting", vm.StatusText);
        Assert.False(vm.NewConversationCommand.CanExecute(null));

        release.SetResult();
        await send;
        ui.RunAll();

        Assert.Equal(1, ended);
        Assert.False(vm.IsBusy);
        Assert.IsType<UserMessageItem>(vm.Items[0]);
        Assert.Equal("click ok", ((UserMessageItem)vm.Items[0]).Text);
        Assert.IsType<ToolStepItem>(vm.Items[1]);
        Assert.Equal("All done", Assert.IsType<AssistantTextItem>(vm.Items[2]).Text);
        Assert.IsType<TurnFooterItem>(vm.Items[^1]);
    }

    [Fact]
    public async Task Failed_send_adds_a_failure_footer_and_second_stop_cancels_the_request()
    {
        var (vm, session, ui, _) = NewViewModel();
        session.OnSend = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null!;
        };
        vm.InputText = "wait forever";
        var send = vm.SendAsync();
        await vm.StopAsync();
        Assert.Equal(1, session.StopCalls);
        Assert.Equal("Stop now (force)", vm.SendButtonTooltip);
        await vm.StopAsync(); // second press: cancel outright
        await send;
        ui.RunAll();
        var footer = Assert.IsType<TurnFooterItem>(vm.Items[^1]);
        Assert.Equal(TurnOutcome.Cancelled, footer.Outcome);

        session.OnSend = (_, _) => throw new InvalidOperationException("backend exploded");
        vm.InputText = "again";
        await vm.SendAsync();
        ui.RunAll();
        footer = Assert.IsType<TurnFooterItem>(vm.Items[^1]);
        Assert.Equal(TurnOutcome.Failed, footer.Outcome);
        Assert.Equal("backend exploded", footer.Error);
    }

    [Fact]
    public async Task New_conversation_clears_and_copy_log_raises_text()
    {
        var (vm, session, _, _) = NewViewModel();
        Assert.False(vm.CopyLogCommand.CanExecute(null));
        vm.Conversation.AddStatus("hello");
        Assert.True(vm.HasItems);
        Assert.False(vm.IsEmpty);
        string? copied = null;
        vm.CopyRequested += t => copied = t;
        vm.CopyLogCommand.Execute(null);
        Assert.Contains("[info] hello", copied);

        await vm.NewConversationAsync();
        Assert.Equal(1, session.NewConversationCalls);
        Assert.Empty(vm.Items);
        Assert.True(vm.IsEmpty);
    }

    [Fact]
    public void Thinking_effort_and_model_are_saved_to_the_active_profile()
    {
        var (vm, session, _, store) = NewViewModel();
        vm.IsThinkingOff = true;
        Assert.Equal(ThinkingMode.Off, store.Current.ActiveProfile!.Thinking);
        vm.Effort = "high";
        Assert.Equal("high", store.Current.ActiveProfile!.Effort);
        vm.Effort = MainViewModel.DefaultEffort;
        Assert.Equal("", store.Current.ActiveProfile!.Effort);
        vm.Effort = null; // a combo box clearing its items is not a choice
        Assert.Equal(MainViewModel.DefaultEffort, vm.Effort);
        vm.ModelText = " sonnet ";
        Assert.Equal("sonnet", store.Current.ActiveProfile!.Model);
        Assert.Contains("sonnet", vm.ProfileModelText);
        Assert.True(session.ReloadCalls >= 3);
    }

    [Fact]
    public async Task Refresh_models_lists_the_catalog_and_reports_errors()
    {
        var store = NewStore();
        var catalog = new UiFakeCatalog();
        var vm = new MainViewModel(store, new UiFakeSession(), catalog, new UiFakeDetector(), new UiQueueDispatcher());
        await vm.RefreshModelsAsync();
        Assert.Equal(new[] { "model-a", "model-b" }, vm.Models.Select(m => m.Id));
        Assert.Equal("vision · thinking", vm.Models[0].Hints);
        Assert.Equal("text only", vm.Models[1].Hints);
        Assert.Null(vm.ModelListError);

        catalog.Result = new ModelListResult(Array.Empty<ModelInfo>(), "401 unauthorized");
        await vm.RefreshModelsAsync();
        Assert.Equal("401 unauthorized", vm.ModelListError);
        Assert.Contains("401 unauthorized", vm.RefreshModelsTooltip);
        Assert.Contains(vm.Items.OfType<StatusLineItem>(), s => s.Level == StatusLevel.Warning && s.Message.Contains("401 unauthorized"));
    }

    [Fact]
    public void Status_bar_reflects_vault_admin_dry_run_and_profile()
    {
        var vaultDir = Path.Combine(Path.GetTempPath(), "DeskPilotLinuxUiTests", "vault-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(vaultDir);
        var (vm, _, _, store) = NewViewModel(configure: s =>
        {
            s.Vault.Path = vaultDir;
            s.Safety.AllowAdmin = true;
            s.Safety.DryRun = true;
        });
        Assert.True(vm.VaultOn);
        Assert.Equal("Vault on", vm.VaultText);
        Assert.Contains("read-only", vm.VaultTooltip);
        Assert.True(vm.AdminMode);
        Assert.True(vm.DryRun);
        Assert.Contains(store.Current.ActiveProfile!.Name, vm.ProfileModelText);
    }

    // ================================================================ stop hotkey and hints (pure)

    [Theory]
    [InlineData("Ctrl+Alt+X", "Ctrl+Alt+X")]
    [InlineData("ctrl + alt + x", "Ctrl+Alt+X")]
    [InlineData("super+shift+q", "Shift+Super+Q")]
    [InlineData("F9", "F9")]
    [InlineData("shift+f12", "Shift+F12")]
    [InlineData("Pause", "Pause")]
    [InlineData("ctrl+alt+esc", "Ctrl+Alt+Esc")]
    public void Stop_hotkey_parses_to_a_display_form(string text, string display)
    {
        Assert.True(StopHotkey.TryParse(text, out _, out var shown, out var error), error);
        Assert.Equal(display, shown);
    }

    [Theory]
    [InlineData("x")]
    [InlineData("shift+a")]
    [InlineData("ctrl+alt")]
    [InlineData("")]
    [InlineData("ctrl+nosuchkey")]
    public void Stop_hotkey_refuses_combinations_that_would_break_typing(string text)
    {
        Assert.False(StopHotkey.TryParse(text, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Stop_hint_depends_on_the_session_kind()
    {
        Assert.Equal("Stop: Ctrl+Alt+X", StopHint.StatusText(LinuxSessionKind.X11, "ctrl+alt+x"));
        Assert.Equal("No stop hotkey", StopHint.StatusText(LinuxSessionKind.X11, ""));
        Assert.Equal("Stop: overlay button, tray or top-left corner", StopHint.StatusText(LinuxSessionKind.Wayland, "ctrl+alt+x"));
        Assert.Equal("Ctrl+Alt+X to stop", StopHint.OverlayText(LinuxSessionKind.X11, "Ctrl+Alt+X", hotkeyActive: true, failsafeCorner: true));
        Assert.Equal("Top-left corner stops", StopHint.OverlayText(LinuxSessionKind.X11, "Ctrl+Alt+X", hotkeyActive: false, failsafeCorner: true));
        Assert.Equal("Top-left corner stops", StopHint.OverlayText(LinuxSessionKind.Wayland, "Ctrl+Alt+X", hotkeyActive: true, failsafeCorner: true));
        Assert.Equal("", StopHint.OverlayText(LinuxSessionKind.Wayland, "Ctrl+Alt+X", hotkeyActive: false, failsafeCorner: false));
    }

    [Fact]
    public void View_model_shows_the_hotkey_on_x11_and_the_stop_controls_on_wayland()
    {
        var (x11, _, _, _) = NewViewModel(LinuxSessionKind.X11);
        Assert.Equal("Stop: Ctrl+Alt+X", x11.StopHotkeyText);
        x11.SetHotkeyStatus(false, "already grabbed by another app");
        Assert.True(x11.HotkeyProblem);
        Assert.Contains("already grabbed", x11.HotkeyTooltip);
        x11.SetHotkeyStatus(true, null);
        Assert.False(x11.HotkeyProblem);

        var (wayland, _, _, _) = NewViewModel(LinuxSessionKind.Wayland);
        Assert.Equal(StopHint.WaylandStatus, wayland.StopHotkeyText);
        wayland.SetHotkeyStatus(false, "global hotkeys are not available on Wayland");
        Assert.False(wayland.HotkeyProblem);
        Assert.Equal(StopHint.WaylandTooltip, wayland.HotkeyTooltip);
    }

    [Fact]
    public void Stop_hotkey_service_registers_on_x11_only_and_survives_a_failing_backend()
    {
        using var wayland = new StopHotkeyService(LinuxSessionKind.Wayland, (KeyCombo _, Action _, out string e) => { e = ""; return new UiHandle(); });
        Assert.False(wayland.IsSupported);
        Assert.False(wayland.Register("ctrl+alt+x", out var error));
        Assert.Contains("Wayland", error);

        KeyCombo? seen = null;
        Action? fire = null;
        var handle = new UiHandle();
        using var x11 = new StopHotkeyService(LinuxSessionKind.X11, (KeyCombo combo, Action pressed, out string e) =>
        {
            seen = combo;
            fire = pressed;
            e = "";
            return handle;
        });
        int presses = 0;
        x11.Pressed += () => presses++;
        Assert.True(x11.Register("Ctrl+Alt+X", out error), error);
        Assert.Equal("ctrl+alt+x", seen!.ToString());
        fire!();
        Assert.Equal(1, presses);
        Assert.True(x11.Register("ctrl+alt+s", out _));
        Assert.True(handle.Disposed); // the old grab is released before the new one

        Assert.False(x11.Register("q", out error));
        Assert.Contains("typing", error);

        using var broken = new StopHotkeyService(LinuxSessionKind.X11, (KeyCombo _, Action _, out string _) => throw new NotImplementedException("STUB"));
        Assert.False(broken.Register("ctrl+alt+x", out error));
        Assert.False(string.IsNullOrWhiteSpace(error));

        using var refused = new StopHotkeyService(LinuxSessionKind.X11, (KeyCombo _, Action _, out string e) => { e = "BadAccess"; return null; });
        Assert.False(refused.Register("ctrl+alt+x", out error));
        Assert.Equal("BadAccess", error);
    }

    private sealed class UiHandle : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    // ================================================================ markdown-lite (pure)

    [Fact]
    public void Markdown_lite_parses_blocks()
    {
        var blocks = MarkdownLite.Parse("# Title\nSome **bold** and `code`.\nNext line\n\n- one\n  - nested\n2. two\n```bash\nls -la\n```\n[docs](https://example.org)");
        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(MdBlockKind.Paragraph, blocks[1].Kind);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Bold && i.Text == "bold");
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.Code && i.Text == "code");
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.LineBreak);
        Assert.Equal(MdBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal(0, blocks[2].Level);
        Assert.Equal(1, blocks[3].Level);
        Assert.Equal(MdBlockKind.Numbered, blocks[4].Kind);
        Assert.Equal("2.", blocks[4].Marker);
        Assert.Equal(MdBlockKind.Code, blocks[5].Kind);
        Assert.Equal("ls -la", blocks[5].Code);
        Assert.Equal("bash", blocks[5].Marker);
        var link = Assert.Single(blocks[6].Inlines);
        Assert.Equal(MdInlineKind.Link, link.Kind);
        Assert.Equal("https://example.org", link.Url);
    }

    [Fact]
    public void Markdown_lite_keeps_an_unclosed_fence_as_code_and_leaves_odd_markers_literal()
    {
        var blocks = MarkdownLite.Parse("```\nstill streaming");
        Assert.Equal(MdBlockKind.Code, Assert.Single(blocks).Kind);
        Assert.Equal("still streaming", blocks[0].Code);

        var inlines = MarkdownLite.ParseInlines("2 * 3 = 6 and **unclosed");
        Assert.Equal(MdInlineKind.Text, Assert.Single(inlines).Kind);
        Assert.Empty(MarkdownLite.Parse(null));
        Assert.Equal(MdBlockKind.Paragraph, Assert.Single(MarkdownLite.Parse("---")).Kind);
    }

    // ================================================================ overlay geometry (pure)

    [Fact]
    public void Overlay_placement_uses_the_corner_of_the_work_area()
    {
        var wa = new ScreenRect(0, 32, 1920, 1048);
        Assert.Equal(new ScreenPoint(1920 - 16 - 400, 32 + 1048 - 16 - 100), OverlayPlacement.Compute(wa, 400, 100, OverlayCorner.BottomRight, 16));
        Assert.Equal(new ScreenPoint(16, 48), OverlayPlacement.Compute(wa, 400, 100, OverlayCorner.TopLeft, 16));
        Assert.Equal(new ScreenPoint(760, 48), OverlayPlacement.Compute(wa, 400, 100, OverlayCorner.TopCenter, 16));
        Assert.True(OverlayPlacement.ShouldHide(new ScreenRect(100, 100, 200, 50), new ScreenPoint(95, 120)));
        Assert.False(OverlayPlacement.ShouldHide(new ScreenRect(100, 100, 200, 50), new ScreenPoint(50, 120)));
        Assert.False(OverlayPlacement.ShouldHide(new ScreenRect(100, 100, 200, 50), null));
    }

    // ================================================================ single instance (real sockets)

    [Fact]
    public async Task Single_instance_hands_activation_to_the_first_instance_and_replaces_a_stale_socket()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dpui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "deskpilot-test.sock");
        try
        {
            using (var first = new SingleInstanceGuard(path))
            {
                Assert.True(first.TryAcquire());
                var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                first.ActivationRequested += () => activated.TrySetResult();

                using var second = new SingleInstanceGuard(path);
                Assert.False(second.TryAcquire());
                Assert.True(second.SignalFirstInstance());
                var done = await Task.WhenAny(activated.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                Assert.Same(activated.Task, done);
            }
            Assert.False(File.Exists(path)); // removed on dispose

            // A file left behind by a crashed instance: nobody answers, so the next launch takes over.
            File.WriteAllText(path, "stale");
            using var third = new SingleInstanceGuard(path);
            Assert.True(third.TryAcquire());
            Assert.True(third.IsOwner);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void Default_socket_path_is_per_user()
    {
        var path = SingleInstanceGuard.DefaultSocketPath();
        Assert.StartsWith("deskpilot-", Path.GetFileName(path));
        Assert.EndsWith(".sock", path);
        Assert.Contains(SingleInstanceGuard.CurrentUserId(), Path.GetFileName(path));
        if (OperatingSystem.IsLinux()) Assert.Matches("^[0-9]+$", SingleInstanceGuard.CurrentUserId());
    }

    // ================================================================ theme (headless Avalonia)

    public static readonly string[] ContractBrushes =
    {
        "BgBrush", "PanelBrush", "Panel2Brush", "CardBrush", "BorderBrush", "TextBrush", "SubtleTextBrush", "MutedTextBrush",
        "AccentBrush", "AccentHoverBrush", "AccentTextBrush", "DangerBrush", "WarningBrush", "SuccessBrush", "InputBgBrush", "SelectionBrush",
    };

    [AvaloniaFact]
    public void Theme_defines_every_contract_brush()
    {
        var app = Application.Current!;
        Assert.IsType<App>(app);
        foreach (var key in ContractBrushes)
        {
            Assert.True(app.TryFindResource(key, app.ActualThemeVariant, out var value), $"missing {key}");
            Assert.IsAssignableFrom<IBrush>(value);
        }
        Assert.Equal(Avalonia.Styling.ThemeVariant.Dark, app.ActualThemeVariant);
    }

    [AvaloniaFact]
    public void Theme_style_classes_apply()
    {
        var accent = new Button { Content = "Go", Classes = { "accent" } };
        var danger = new Button { Content = "Stop", Classes = { "danger" } };
        var ghost = new Button { Content = "More", Classes = { "ghost" } };
        var icon = new Button { Content = new PathIcon(), Classes = { "icon" } };
        var header = new TextBlock { Text = "Section", Classes = { "section-header" } };
        var caption = new TextBlock { Text = "Small", Classes = { "caption" } };
        var selectableCaption = new SelectableTextBlock { Text = "Small too", Classes = { "caption" } };
        var card = new Border { Classes = { "card" }, Child = new TextBlock { Text = "card" } };
        var seg = new RadioButton { Content = "On", Classes = { "segmented" }, IsChecked = true };
        var segOff = new RadioButton { Content = "Off", Classes = { "segmented" } };
        var mono = new TextBox { Text = "x", Classes = { "mono" } };
        var toggle = new ToggleSwitch();
        var panel = new StackPanel { Children = { accent, danger, ghost, icon, header, caption, selectableCaption, card, seg, segOff, mono, toggle } };
        var window = new Window { Content = panel, Width = 600, Height = 800 };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            IBrush Res(string key) => (IBrush)Application.Current!.FindResource(key)!;

            Assert.Same(Res("AccentBrush"), accent.Background);
            Assert.Same(Res("AccentTextBrush"), accent.Foreground);
            Assert.Same(Res("DangerBrush"), danger.Background);
            Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(ghost.Background).Color);
            Assert.Equal(32, icon.Width);
            Assert.Equal(FontWeight.SemiBold, header.FontWeight);
            Assert.Same(Res("SubtleTextBrush"), caption.Foreground);
            Assert.Same(Res("SubtleTextBrush"), selectableCaption.Foreground);
            Assert.Same(Res("CardBrush"), card.Background);
            Assert.Same(Res("AccentBrush"), seg.Background);
            Assert.NotSame(Res("AccentBrush"), segOff.Background);
            Assert.Contains("Mono", mono.FontFamily.ToString());
            Assert.Same(Res("BgBrush"), window.Background);
            Assert.Equal("", toggle.OnContent);
        }
        finally
        {
            window.Close();
        }
    }

    // ================================================================ windows (headless Avalonia)

    [AvaloniaFact]
    public void Main_window_builds_with_fakes_and_shows_every_item_kind()
    {
        var (vm, _, ui, _) = NewViewModel();
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Assert.True(vm.IsEmpty);
            vm.Conversation.AddLocalUserMessage("Open the files app");
            vm.Conversation.Apply(new ThinkingEvent("I should look first"));
            vm.Conversation.Apply(new ToolCallEvent("c1", "screenshot", "{}", "Take a screenshot"));
            vm.Conversation.Apply(new ToolResultEvent("c1", "screenshot", false, "1280x800", MakeToolImage(), TimeSpan.FromMilliseconds(220)));
            vm.Conversation.Apply(new ToolCallEvent("c2", "click", "{}", "Click Files"));
            vm.Conversation.Apply(new ToolResultEvent("c2", "click", true, "Denied: elevated window", null, TimeSpan.FromMilliseconds(40)));
            vm.Conversation.Apply(new AssistantTextEvent("Opened **Files**.\n\n- step one\n```\ncode\n```"));
            vm.Conversation.AddStatus("Rate limit soon", StatusLevel.Warning);
            vm.Conversation.AddTurnResult(new TurnResult(TurnOutcome.Completed, "ok", new TurnStats(100, 10, 2, 0.001, TimeSpan.FromSeconds(5), "haiku"), null));
            ui.RunAll();
            Dispatcher.UIThread.RunJobs();
            window.CaptureRenderedFrame();

            Assert.False(vm.IsEmpty);
            Assert.False(window.FindControl<StackPanel>("EmptyState")!.IsVisible);
            var log = window.FindControl<ItemsControl>("LogList")!;
            var realized = log.GetRealizedContainers().ToList();
            Assert.Equal(vm.Items.Count, realized.Count);

            var markdown = window.GetVisualDescendants().OfType<MarkdownView>().Single();
            Assert.True(markdown.Children.Count >= 3);

            var step = vm.Items.OfType<ToolStepItem>().First();
            Assert.True(step.HasImage);
            Assert.NotNull(step.Thumbnail);
            Assert.True(step.Thumbnail!.PixelSize.Width <= ToolStepItem.ThumbnailDecodeWidth);

            // Thinking is collapsed until the toggle opens it; the state lives on the item.
            var thinking = vm.Items.OfType<ThinkingItem>().Single();
            var toggle = window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(t => t.Classes.Contains("thinking"));
            Assert.False(thinking.IsExpanded);
            toggle.IsChecked = true;
            Assert.True(thinking.IsExpanded);

            // Thinking hidden by settings: the item stays but is not shown.
            vm.Store.Update(s => s.Ui.ShowThinking = false);
            ui.RunAll();
            Assert.False(vm.Items.OfType<ThinkingItem>().Single().IsShown);

            Assert.Equal("Stop: Ctrl+Alt+X", window.FindControl<TextBlock>("HotkeyText")!.Text);
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Main_window_enter_sends_and_shift_enter_does_not()
    {
        var (vm, session, _, _) = NewViewModel();
        string? sent = null;
        session.OnSend = (text, _) =>
        {
            sent = text;
            return Task.FromResult(new TurnResult(TurnOutcome.Completed, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), null));
        };
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            var input = window.FindControl<TextBox>("InputBox")!;
            input.Focus();
            vm.InputText = "first line";
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.Shift, Avalonia.Input.PhysicalKey.Enter, "\r");
            Assert.Null(sent);
            window.KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, "\r");
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(sent);
            Assert.StartsWith("first line", sent);
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Main_window_close_goes_to_tray_or_asks_to_exit()
    {
        var (vm, _, _, store) = NewViewModel(configure: s => s.Ui.CloseToTray = true);
        var window = new MainWindow(vm);
        int hidden = 0, exit = 0;
        window.HiddenToTray += (_, _) => hidden++;
        window.ExitRequested += (_, _) => exit++;
        window.Show();
        window.Close();
        Assert.Equal(1, hidden);
        Assert.False(window.IsVisible);

        store.Update(s => s.Ui.CloseToTray = false);
        window.ShowAndActivate();
        Assert.True(window.IsVisible);
        window.Close();
        Assert.Equal(1, exit);
        Assert.True(window.IsVisible);

        window.AllowClose = true;
        window.Close();
        Assert.False(window.IsVisible);
        vm.Dispose();
    }

    [AvaloniaFact]
    public void Main_window_on_wayland_shows_the_stop_controls_hint()
    {
        var (vm, _, _, _) = NewViewModel(LinuxSessionKind.Wayland);
        var window = new MainWindow(vm);
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(StopHint.WaylandStatus, window.FindControl<TextBlock>("HotkeyText")!.Text);
        }
        finally
        {
            window.AllowClose = true;
            window.Close();
            vm.Dispose();
        }
    }

    [AvaloniaFact]
    public void Saved_placement_is_applied_only_when_on_screen()
    {
        var screens = new[] { new PixelRect(0, 0, 1920, 1080) };
        Assert.True(MainWindow.IsOnScreen(screens, 100, 100, 900));
        Assert.True(MainWindow.IsOnScreen(screens, 1800, 100, 900)); // partly visible title bar is enough
        Assert.False(MainWindow.IsOnScreen(screens, 4000, 100, 900));
        Assert.False(MainWindow.IsOnScreen(screens, double.NaN, 100, 900));
        Assert.False(MainWindow.IsOnScreen(Array.Empty<PixelRect>(), 100, 100, 900));
    }

    [AvaloniaFact]
    public async Task Overlay_shows_during_a_turn_and_hides_for_captures_and_input_on_it()
    {
        var store = NewStore(s => s.Ui.OverlayPosition = OverlayCorner.TopLeft);
        using var overlay = new OverlayController(store, LinuxSessionKind.X11);
        var session = new UiFakeSession();
        overlay.Attach(session);
        Assert.False(overlay.IsWindowVisible);

        session.SetState(AgentState.Running);
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsTurnActive);
        Assert.True(overlay.IsWindowVisible);
        var w = overlay.Window!;
        Assert.True(w.Topmost);
        Assert.False(w.ShowActivated);
        Assert.False(w.ShowInTaskbar);
        Assert.Equal(WindowDecorations.None, w.WindowDecorations);

        session.Raise(new ToolCallEvent("c1", "click", "{}", "Click Save"));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Click Save", overlay.ViewModel.Detail);
        Assert.Equal(1, overlay.ViewModel.Steps);

        // Around a capture: hidden while the screenshot is taken, back afterwards.
        await overlay.BeforeCaptureAsync(CancellationToken.None);
        Assert.False(overlay.IsWindowVisible);
        Assert.True(overlay.IsHiddenForCapture);
        overlay.AfterCapture();
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsWindowVisible);

        // Input aimed at the overlay hides it; input elsewhere does not.
        var b = w.PhysicalBounds;
        await overlay.BeforeInputAsync(new ScreenPoint(b.X + b.Width + 500, b.Y + b.Height + 500), CancellationToken.None);
        Assert.True(overlay.IsWindowVisible);
        await overlay.BeforeInputAsync(new ScreenPoint(b.X + b.Width / 2, b.Y + b.Height / 2), CancellationToken.None);
        Assert.False(overlay.IsWindowVisible);
        Assert.True(overlay.IsHiddenForInput);
        overlay.AfterInput();
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsWindowVisible);

        // Stop button wiring and turn end.
        int stops = 0;
        overlay.StopRequested += () => stops++;
        overlay.ViewModel.StopCommand.Execute(null);
        Assert.Equal(1, stops);
        session.SetState(AgentState.Idle);
        Dispatcher.UIThread.RunJobs();
        Assert.False(overlay.IsWindowVisible);

        // Turned off in settings: never shown.
        store.Update(s => s.Ui.ShowOverlay = false);
        overlay.ApplySettings();
        overlay.ApplyState(AgentState.Running);
        Assert.False(overlay.IsWindowVisible);
    }

    [AvaloniaFact]
    public async Task Overlay_capture_hide_waits_from_a_background_thread()
    {
        var store = NewStore();
        using var overlay = new OverlayController(store, LinuxSessionKind.Wayland);
        overlay.ApplyState(AgentState.Running);
        Assert.True(overlay.IsWindowVisible);
        Assert.Equal("Top-left corner stops", overlay.ViewModel.StopHint);

        var capture = Task.Run(() => overlay.BeforeCaptureAsync(CancellationToken.None));
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!capture.IsCompleted && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(capture.IsCompletedSuccessfully);
        Assert.False(overlay.IsWindowVisible);
        overlay.AfterCapture();
        Dispatcher.UIThread.RunJobs();
        Assert.True(overlay.IsWindowVisible);
    }

    [AvaloniaFact]
    public void Overlay_footer_shows_the_hotkey_once_registered()
    {
        var store = NewStore();
        using var overlay = new OverlayController(store, LinuxSessionKind.X11);
        Assert.Equal("No actions yet  ·  Top-left corner stops", overlay.ViewModel.FooterText);
        overlay.SetHotkeyActive(true);
        Assert.Equal("No actions yet  ·  Ctrl+Alt+X to stop", overlay.ViewModel.FooterText);
    }

    [AvaloniaFact]
    public void Confirm_window_defaults_to_deny()
    {
        var action = new ProposedAction("run_command", "Run 'rm -rf build'", ActionRisk.High, Command: "rm -rf build");
        var window = new ConfirmWindow(action);
        window.Show();
        try
        {
            Assert.True(window.Topmost);
            var deny = window.FindControl<Button>("DenyButton")!;
            Assert.True(deny.IsDefault);
            Assert.True(deny.IsCancel);
            Assert.False(window.FindControl<Button>("AllowButton")!.IsDefault);
            Assert.Equal(ConfirmationChoice.Deny, window.Choice);
            Assert.True(window.ViewModel.IsHighRisk);
            Assert.Contains(window.ViewModel.Details, d => d.Label == "Command" && d.Value == "rm -rf build" && d.IsMono);
        }
        finally
        {
            window.Close();
        }
        Assert.Equal(ConfirmationChoice.Deny, window.Choice);
    }

    [AvaloniaFact]
    public async Task Confirmation_service_returns_the_choice_and_cancellation_closes_as_deny()
    {
        var service = new AvaloniaUserConfirmation();
        ConfirmWindow? opened = null;
        service.DialogOpened += w => opened = w;
        var action = new ProposedAction("click", "Click Delete", ActionRisk.Medium, new ScreenPoint(10, 20));

        var allow = service.ConfirmAsync(action, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        Assert.NotNull(opened);
        Assert.True(opened!.IsVisible);
        opened.FindControl<Button>("AllowButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(ConfirmationChoice.Allow, await allow);

        opened = null;
        var all = service.ConfirmAsync(action, CancellationToken.None);
        Dispatcher.UIThread.RunJobs();
        opened!.FindControl<Button>("AllowAllButton")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(ConfirmationChoice.AllowAllThisTurn, await all);

        opened = null;
        using var cts = new CancellationTokenSource();
        var cancelled = service.ConfirmAsync(action, cts.Token);
        Dispatcher.UIThread.RunJobs();
        var window = opened!;
        cts.Cancel();
        Assert.Equal(ConfirmationChoice.Deny, await cancelled);
        Dispatcher.UIThread.RunJobs();
        Assert.False(window.IsVisible);

        Assert.Equal(ConfirmationChoice.Deny, await service.ConfirmAsync(action, new CancellationToken(canceled: true)));
    }

    [AvaloniaFact]
    public void Image_viewer_shows_the_screenshot_and_toggles_zoom()
    {
        var bytes = MakePng(640, 400, SKColors.SteelBlue);
        var bitmap = ToolStepItem.DecodeImage(bytes, 0)!;
        Assert.Equal(new PixelSize(640, 400), bitmap.PixelSize);
        var viewer = new ImageViewerWindow(bitmap, "Take a screenshot  ·  12:00:00");
        viewer.Show();
        try
        {
            Assert.Equal("Take a screenshot  ·  12:00:00", viewer.FindControl<TextBlock>("CaptionText")!.Text);
            Assert.Contains("640 × 400 px", viewer.FindControl<TextBlock>("SizeText")!.Text);
            Assert.False(viewer.IsActualSize);
            viewer.ToggleZoom();
            Assert.True(viewer.IsActualSize);
            Assert.Equal(Stretch.None, viewer.FindControl<Image>("Picture")!.Stretch);
            viewer.ToggleZoom();
            Assert.False(viewer.IsActualSize);
        }
        finally
        {
            viewer.Close();
        }
        Assert.Null(ToolStepItem.DecodeImage(new byte[] { 1, 2, 3 }, 0));
    }

    [AvaloniaFact]
    public void Markdown_view_renders_blocks_as_selectable_text()
    {
        var view = new MarkdownView { Markdown = "Hello **world**\n\n- a\n- b\n\n```\ncode here\n```" };
        var window = new Window { Content = view };
        window.Show();
        try
        {
            Assert.Equal(4, view.Children.Count);
            var paragraph = Assert.IsType<SelectableTextBlock>(view.Children[0]);
            Assert.Equal(2, paragraph.Inlines!.Count);
            Assert.IsType<Grid>(view.Children[1]);
            var code = Assert.IsType<Border>(view.Children[3]);
            Assert.Equal("code here", Assert.IsType<SelectableTextBlock>(code.Child).Text);

            view.Markdown = "changed";
            Assert.Single(view.Children);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Message_and_startup_error_windows_build()
    {
        var message = new MessageWindow("Heading", "Body text", MessageKind.Question, "Yes", "No");
        message.Show();
        Assert.True(message.FindControl<Button>("SecondaryButton")!.IsVisible);
        message.Close();
        Assert.False(message.Result);

        var error = new StartupErrorWindow("No X11 display", new[] { "Install xdotool", "Log in to an X11 session" }, "/tmp/log.txt");
        int exits = 0;
        error.ExitRequested += (_, _) => exits++;
        error.Show();
        Assert.True(error.FindControl<Border>("NotesCard")!.IsVisible);
        Assert.Equal(2, error.Notes.Count);
        error.Close();
        Assert.Equal(1, exits);
    }

    [AvaloniaFact]
    public void Tray_service_builds_and_tracks_state()
    {
        using var tray = new TrayIconService();
        Assert.False(tray.IsStopEnabled);
        tray.SetState(AgentState.Running);
        Assert.True(tray.IsStopEnabled);
        Assert.Equal("DeskPilot (working)", tray.ToolTipText);
        tray.SetState(AgentState.Idle);
        Assert.False(tray.IsStopEnabled);
        Assert.NotNull(TrayIconService.LoadAppIcon());
    }

    [AvaloniaFact]
    public void Icon_geometries_parse()
    {
        foreach (var field in typeof(Icons).GetFields())
        {
            var data = (string)field.GetValue(null)!;
            Assert.NotNull(IconGeometryConverter.Parse(data));
        }
        Assert.Null(IconGeometryConverter.Parse(""));
        Assert.NotNull(IconGeometries.Send);
    }

    // ================================================================ the real app in a real session (CI)

    /// <summary>The built app (src/DeskPilot.Linux/bin/&lt;config&gt;/&lt;tfm&gt;/deskpilot.dll) matching this test build.</summary>
    private static string? FindAppDll()
    {
        var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parts = baseDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        int bin = Array.LastIndexOf(parts, "bin");
        if (bin < 0 || bin + 2 >= parts.Length) return null;
        var (config, tfm) = (parts[bin + 1], parts[bin + 2]);
        var dir = new DirectoryInfo(baseDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DeskPilot.sln"))) dir = dir.Parent;
        if (dir == null) return null;
        var candidate = Path.Combine(dir.FullName, "src", "DeskPilot.Linux", "bin", config, tfm, "deskpilot.dll");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>A private home for one app run: config, data and runtime folders, so nothing touches the real ones.</summary>
    private static string NewAppRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "dpapp-" + Guid.NewGuid().ToString("N")[..10]);
        foreach (var sub in new[] { "config", "data", "run" }) Directory.CreateDirectory(Path.Combine(root, sub));
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Path.Combine(root, "run"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return root;
    }

    private static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class AppProcess : IDisposable
    {
        private readonly System.Text.StringBuilder _out = new();
        private readonly System.Text.StringBuilder _err = new();

        public AppProcess(string dll, string root, params string[] args)
        {
            var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
            var psi = new System.Diagnostics.ProcessStartInfo(string.IsNullOrEmpty(dotnet) ? "dotnet" : dotnet)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            psi.ArgumentList.Add(dll);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(root, "config");
            psi.Environment["XDG_DATA_HOME"] = Path.Combine(root, "data");
            psi.Environment["XDG_RUNTIME_DIR"] = Path.Combine(root, "run");
            Process = System.Diagnostics.Process.Start(psi)!;
            Process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (_out) _out.AppendLine(e.Data); };
            Process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (_err) _err.AppendLine(e.Data); };
            Process.BeginOutputReadLine();
            Process.BeginErrorReadLine();
        }

        public System.Diagnostics.Process Process { get; }

        public string Output
        {
            get
            {
                string o, e;
                lock (_out) o = _out.ToString();
                lock (_err) e = _err.ToString();
                return o + e;
            }
        }

        public string StdErr
        {
            get { lock (_err) return _err.ToString(); }
        }

        public bool WaitForExit(TimeSpan timeout)
        {
            if (!Process.WaitForExit((int)timeout.TotalMilliseconds)) return false;
            Process.WaitForExit(); // flush the async readers
            return true;
        }

        public void Dispose()
        {
            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(entireProcessTree: true);
                    Process.WaitForExit(10000);
                }
            }
            catch (Exception) { }
            Process.Dispose();
        }
    }

    private static (int Exit, string Output) RunTool(string file, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000)) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        return (p.HasExited ? p.ExitCode : -1, stdout.Result + stderr.Result);
    }

    [LinuxFact]
    public void Real_app_mcp_bridge_switch_runs_before_any_ui_or_single_instance_work()
    {
        var dll = FindAppDll();
        Assert.True(dll != null, "the app was not built next to this test build");
        var root = NewAppRoot();
        try
        {
            using var bridge = new AppProcess(dll!, root, "--mcp-bridge");
            bridge.Process.StandardInput.Close();
            Assert.True(bridge.WaitForExit(TimeSpan.FromSeconds(30)), bridge.Output);
            Assert.NotEqual(0, bridge.Process.ExitCode);
            Assert.Contains("Usage", bridge.Output);
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "run")));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    [X11Fact]
    public async Task Real_app_opens_a_window_on_x11_and_a_second_launch_hands_over()
    {
        var dll = FindAppDll();
        Assert.True(dll != null, "the app was not built next to this test build");
        var root = NewAppRoot();
        var first = new AppProcess(dll!, root);
        try
        {
            // Whatever this build shows first (main window, welcome or the startup error), its title has DeskPilot in it.
            var ids = Array.Empty<string>();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && !first.Process.HasExited)
            {
                var (exit, output) = RunTool("xdotool", "search", "--onlyvisible", "--name", "DeskPilot");
                ids = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => l.Length > 0 && l.All(char.IsDigit)).ToArray();
                if (exit == 0 && ids.Length > 0) break;
                await Task.Delay(500);
            }
            Assert.False(first.Process.HasExited, "the app exited early: " + first.Output);
            Assert.True(ids.Length > 0, "no DeskPilot window appeared: " + first.Output);
            var titles = ids.Select(id => RunTool("xdotool", "getwindowname", id).Output.Trim()).ToList();
            Assert.Contains(titles, t => t.Contains("DeskPilot", StringComparison.Ordinal));

            await Task.Delay(1500); // let it finish drawing for the snapshot
            SaveX11Snapshot("app-x11.png");

            var socket = Assert.Single(Directory.GetFiles(Path.Combine(root, "run"), "deskpilot-*.sock"));
            Assert.Matches(@"deskpilot-[0-9]+\.sock$", socket);

            using (var second = new AppProcess(dll!, root))
            {
                Assert.True(second.WaitForExit(TimeSpan.FromSeconds(30)), "the second launch did not hand over: " + second.Output);
                Assert.Equal(0, second.Process.ExitCode);
            }
            Assert.False(first.Process.HasExited);

            var logs = Directory.GetFiles(Path.Combine(root, "data", "DeskPilot", "logs"), "*.log");
            var log = string.Join("\n", logs.Select(File.ReadAllText));
            Assert.Contains("starting (Linux)", log);
            Assert.Contains("Session: X11", log);
        }
        finally
        {
            first.Dispose();
            DeleteQuietly(root);
        }
    }

    [WaylandFact]
    public void Real_app_without_xwayland_explains_what_it_needs()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) Assert.Skip("Xwayland is available in this session");
        var dll = FindAppDll();
        Assert.True(dll != null, "the app was not built next to this test build");
        var root = NewAppRoot();
        try
        {
            using var app = new AppProcess(dll!, root);
            Assert.True(app.WaitForExit(TimeSpan.FromSeconds(60)), "the app kept running without a display: " + app.Output);
            Assert.True(app.Process.ExitCode == 1, $"exit code {app.Process.ExitCode}: {app.Output}");
            Assert.Contains("Xwayland", app.StdErr);
            // The single-instance socket is released on the way out.
            Assert.Empty(Directory.GetFiles(Path.Combine(root, "run"), "deskpilot-*.sock"));
        }
        finally
        {
            DeleteQuietly(root);
        }
    }

    /// <summary>Saves the X11 root window as PNG into $CI_LOGS (uploaded by CI) for a look at the real rendering.</summary>
    private static void SaveX11Snapshot(string name)
    {
        var logs = Environment.GetEnvironmentVariable("CI_LOGS");
        if (string.IsNullOrEmpty(logs)) return;
        var xwd = Path.Combine(Path.GetTempPath(), $"dp-{Guid.NewGuid():N}.xwd");
        try
        {
            var (exit, _) = RunTool("xwd", "-root", "-silent", "-out", xwd);
            if (exit != 0 || !File.Exists(xwd)) return;
            var png = XwdToPng(File.ReadAllBytes(xwd));
            if (png != null) File.WriteAllBytes(Path.Combine(logs, name), png);
        }
        catch (Exception)
        {
            // A snapshot is a convenience for reviewing CI runs, never a failure.
        }
        finally
        {
            try { File.Delete(xwd); } catch (IOException) { }
        }
    }

    /// <summary>Converts an XWD dump (ZPixmap, 24 or 32 bits per pixel, what xwd writes for Xvfb) to PNG.</summary>
    internal static byte[]? XwdToPng(byte[] data)
    {
        uint U32(int field) => (uint)(data[field * 4] << 24 | data[field * 4 + 1] << 16 | data[field * 4 + 2] << 8 | data[field * 4 + 3]);
        if (data.Length < 100) return null;
        int headerSize = (int)U32(0), format = (int)U32(2), width = (int)U32(4), height = (int)U32(5);
        int byteOrder = (int)U32(7), bpp = (int)U32(11), bytesPerLine = (int)U32(12), ncolors = (int)U32(19);
        uint redMask = U32(14), greenMask = U32(15), blueMask = U32(16);
        if (format != 2 || (bpp != 32 && bpp != 24) || width <= 0 || height <= 0) return null;
        int offset = headerSize + ncolors * 12;
        if (offset + (long)bytesPerLine * height > data.Length) return null;

        static int Shift(uint mask)
        {
            int s = 0;
            while (mask != 0 && (mask & 1) == 0) { mask >>= 1; s++; }
            return s;
        }
        int rs = Shift(redMask), gs = Shift(greenMask), bs = Shift(blueMask);
        using var bmp = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        int step = bpp / 8;
        for (int y = 0; y < height; y++)
        {
            int row = offset + y * bytesPerLine;
            for (int x = 0; x < width; x++)
            {
                int i = row + x * step;
                uint p = step == 4
                    ? (byteOrder == 0 ? (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16 | data[i + 3] << 24) : (uint)(data[i] << 24 | data[i + 1] << 16 | data[i + 2] << 8 | data[i + 3]))
                    : (byteOrder == 0 ? (uint)(data[i] | data[i + 1] << 8 | data[i + 2] << 16) : (uint)(data[i] << 16 | data[i + 1] << 8 | data[i + 2]));
                bmp.SetPixel(x, y, new SKColor((byte)((p & redMask) >> rs), (byte)((p & greenMask) >> gs), (byte)((p & blueMask) >> bs)));
            }
        }
        using var image = SKImage.FromBitmap(bmp);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    [Fact]
    public void Xwd_dumps_convert_to_png()
    {
        // A 2x1 ZPixmap, 32 bits per pixel, LSB first: one red pixel, one blue pixel.
        var header = new uint[25];
        header[0] = 100 + 4; header[1] = 7; header[2] = 2; header[3] = 24; header[4] = 2; header[5] = 1; header[7] = 0;
        header[11] = 32; header[12] = 8; header[14] = 0xFF0000; header[15] = 0x00FF00; header[16] = 0x0000FF; header[19] = 0;
        var bytes = new List<byte>();
        foreach (var v in header) bytes.AddRange(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
        bytes.AddRange(new byte[] { (byte)'r', (byte)'o', (byte)'o', 0 });
        bytes.AddRange(new byte[] { 0x00, 0x00, 0xFF, 0x00, 0xFF, 0x00, 0x00, 0x00 });
        var png = XwdToPng(bytes.ToArray());
        Assert.NotNull(png);
        using var decoded = SKBitmap.Decode(png);
        Assert.Equal(new SKColor(255, 0, 0), decoded.GetPixel(0, 0));
        Assert.Equal(new SKColor(0, 0, 255), decoded.GetPixel(1, 0));
    }

    /// <summary>
    /// Renders the main windows to PNG files for a visual check when DESKPILOT_UI_SNAPSHOT_DIR is set
    /// (headless rendering, nothing is shown on a screen).
    /// </summary>
    [AvaloniaFact]
    public void Render_snapshots_when_asked()
    {
        var dir = Environment.GetEnvironmentVariable("DESKPILOT_UI_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(dir)) Assert.Skip("Set DESKPILOT_UI_SNAPSHOT_DIR to render UI snapshots");
        Directory.CreateDirectory(dir);

        var (vm, _, ui, _) = NewViewModel(configure: s => s.Safety.DryRun = true);
        var main = new MainWindow(vm) { Width = 1100, Height = 820 };
        main.Show();
        Dispatcher.UIThread.RunJobs();
        main.CaptureRenderedFrame()?.Save(Path.Combine(dir, "main-empty.png"));

        vm.Conversation.AddLocalUserMessage("Open the files app and show me the Downloads folder sorted by date");
        vm.Conversation.Apply(new ThinkingEvent("The user wants the file manager. I will take a screenshot first."));
        vm.Conversation.Apply(new ToolCallEvent("c1", "screenshot", "{}", "Take a screenshot"));
        vm.Conversation.Apply(new ToolResultEvent("c1", "screenshot", false, "1280x800", MakeToolImage(640, 400), TimeSpan.FromMilliseconds(220)));
        vm.Conversation.Apply(new ToolCallEvent("c2", "launch", "{}", "Open 'nautilus'"));
        vm.Conversation.Apply(new ToolResultEvent("c2", "launch", false, "started", null, TimeSpan.FromSeconds(1.2)));
        vm.Conversation.Apply(new ToolCallEvent("c3", "click", "{}", "Click at (210, 340) on 'Downloads'"));
        vm.Conversation.Apply(new ToolResultEvent("c3", "click", true, "Denied: the window belongs to a program running as root", null, TimeSpan.FromMilliseconds(40)));
        vm.Conversation.AddStatus("Rate limit: 80% of the 5-hour window used", StatusLevel.Warning);
        vm.Conversation.Apply(new AssistantTextEvent("The **Downloads** folder is open and sorted by date.\n\n- Newest file: `report.pdf`\n- 42 files in total\n\n```\nls -lt ~/Downloads | head\n```"));
        vm.Conversation.AddTurnResult(new TurnResult(TurnOutcome.Completed, "ok", new TurnStats(14200, 320, 3, 0.0042, TimeSpan.FromSeconds(18), "claude-haiku"), null));
        ui.RunAll();
        Dispatcher.UIThread.RunJobs();
        main.CaptureRenderedFrame()?.Save(Path.Combine(dir, "main-log.png"));

        var store = NewStore();
        using var overlay = new OverlayController(store, LinuxSessionKind.X11);
        overlay.SetHotkeyActive(true);
        overlay.ApplyState(AgentState.Running);
        overlay.ViewModel.OnEvent(new ToolCallEvent("c1", "click", "{}", "Click at (512, 300) on 'Files'"));
        Dispatcher.UIThread.RunJobs();
        overlay.Window!.CaptureRenderedFrame()?.Save(Path.Combine(dir, "overlay.png"));

        var confirm = new ConfirmWindow(new ProposedAction("run_command", "Run a shell command", ActionRisk.High, Command: "rm -rf ~/build/output"));
        confirm.Show();
        Dispatcher.UIThread.RunJobs();
        confirm.CaptureRenderedFrame()?.Save(Path.Combine(dir, "confirm.png"));
        confirm.Close();

        var error = new StartupErrorWindow("No supported screen capture method was found for this Wayland session.",
            new[] { "Install grim (wlroots compositors) or enable the xdg-desktop-portal screenshot interface.", "Install wtype for keyboard input." }, "/home/user/.local/share/DeskPilot/logs/deskpilot.log");
        error.Show();
        Dispatcher.UIThread.RunJobs();
        error.CaptureRenderedFrame()?.Save(Path.Combine(dir, "startup-error.png"));
        error.Close();

        var message = new MessageWindow("DeskPilot is working on a task", "Stop it and exit?", MessageKind.Question, "Stop and exit", "Keep working");
        message.Show();
        Dispatcher.UIThread.RunJobs();
        message.CaptureRenderedFrame()?.Save(Path.Combine(dir, "message.png"));
        message.Close();

        var themeSample = new Window
        {
            Width = 520, Height = 360,
            Content = new StackPanel
            {
                Margin = new Thickness(20), Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Section header", Classes = { "section-header" } },
                    new TextBlock { Text = "Caption text explains a setting in a sentence or two.", Classes = { "caption" } },
                    new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children =
                    {
                        new Button { Content = "Accent", Classes = { "accent" } },
                        new Button { Content = "Default" },
                        new Button { Content = "Danger", Classes = { "danger" } },
                        new Button { Content = "Ghost", Classes = { "ghost" } },
                        new Button { Content = new PathIcon { Data = IconGeometries.Settings }, Classes = { "icon" } },
                        new Button { Content = "Disabled", Classes = { "accent" }, IsEnabled = false },
                    } },
                    new Border { Classes = { "segmented" }, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, Child = new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        Children =
                        {
                            new RadioButton { Content = "Auto", Classes = { "segmented" }, GroupName = "g", IsChecked = true },
                            new RadioButton { Content = "On", Classes = { "segmented" }, GroupName = "g" },
                            new RadioButton { Content = "Off", Classes = { "segmented" }, GroupName = "g" },
                        },
                    } },
                    new Border { Classes = { "card" }, Child = new StackPanel { Spacing = 8, Children =
                    {
                        new ToggleSwitch { IsChecked = true, Content = "Minimize while working" },
                        new TextBox { Text = "monospace text", Classes = { "mono" } },
                        new TextBox { PlaceholderText = "Placeholder" },
                    } } },
                },
            },
        };
        themeSample.Show();
        Dispatcher.UIThread.RunJobs();
        themeSample.CaptureRenderedFrame()?.Save(Path.Combine(dir, "theme.png"));
        themeSample.Close();

        main.AllowClose = true;
        main.Close();
        vm.Dispose();
    }
}

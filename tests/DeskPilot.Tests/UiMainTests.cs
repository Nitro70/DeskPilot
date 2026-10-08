using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;
using DeskPilot.Services;
using DeskPilot.ViewModels;
using DeskPilot.Views;

namespace DeskPilot.Tests;

/// <summary>
/// Tests for the main UI module. View-model logic runs without WPF; window tests construct windows on a
/// dedicated STA thread and never call Show/ShowDialog, so nothing appears on screen and no real input,
/// clipboard, hotkey or tray icon is touched.
/// </summary>
public sealed class UiMainTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskpilot-ui-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    // =====================================================================================
    // ConversationLog: event aggregation, pairing, capping
    // =====================================================================================

    [Fact]
    public void Partial_assistant_chunks_merge_into_one_item()
    {
        var log = new ConversationLog();
        log.Apply(new AssistantTextEvent("Hel", IsPartial: true));
        log.Apply(new AssistantTextEvent("lo ", IsPartial: true));
        log.Apply(new AssistantTextEvent("world", IsPartial: true));

        var item = Assert.IsType<AssistantTextItem>(Assert.Single(log.Items));
        Assert.Equal("Hello world", item.Text);
        Assert.True(item.IsStreaming);
    }

    [Fact]
    public void Complete_block_after_chunks_replaces_instead_of_duplicating()
    {
        var log = new ConversationLog();
        log.Apply(new AssistantTextEvent("Opening ", true));
        log.Apply(new AssistantTextEvent("Notepad", true));
        log.Apply(new AssistantTextEvent("Opening Notepad now."));

        var item = Assert.IsType<AssistantTextItem>(Assert.Single(log.Items));
        Assert.Equal("Opening Notepad now.", item.Text);
        Assert.False(item.IsStreaming);
    }

    [Fact]
    public void Complete_blocks_without_chunks_are_separate_items()
    {
        var log = new ConversationLog();
        log.Apply(new AssistantTextEvent("First."));
        log.Apply(new AssistantTextEvent("Second."));
        Assert.Equal(2, log.Items.OfType<AssistantTextItem>().Count());
    }

    [Fact]
    public void Thinking_and_text_chunks_interleave_into_their_own_items()
    {
        var log = new ConversationLog();
        log.Apply(new ThinkingEvent("Let me ", true));
        log.Apply(new AssistantTextEvent("Sure", true));
        log.Apply(new ThinkingEvent("look.", true));
        log.Apply(new AssistantTextEvent("!", true));

        Assert.Equal(2, log.Items.Count);
        Assert.Equal("Let me look.", Assert.IsType<ThinkingItem>(log.Items[0]).Text);
        Assert.Equal("Sure!", Assert.IsType<AssistantTextItem>(log.Items[1]).Text);
    }

    [Fact]
    public void A_tool_call_closes_streaming_so_later_chunks_start_a_new_item()
    {
        var log = new ConversationLog();
        log.Apply(new AssistantTextEvent("Clicking.", true));
        log.Apply(new ToolCallEvent("c1", "click", "{}", "left click at (1, 2)"));
        log.Apply(new AssistantTextEvent("Done", true));

        Assert.Equal(3, log.Items.Count);
        Assert.False(((AssistantTextItem)log.Items[0]).IsStreaming);
        Assert.Equal("Done", ((AssistantTextItem)log.Items[2]).Text);
    }

    [Fact]
    public void Empty_thinking_blocks_are_skipped()
    {
        var log = new ConversationLog();
        log.Apply(new ThinkingEvent(""));
        log.Apply(new ThinkingEvent("   "));
        log.Apply(new ThinkingEvent("", IsPartial: true));
        Assert.Empty(log.Items);
    }

    [Fact]
    public void Tool_results_complete_their_call()
    {
        var log = new ConversationLog();
        log.Apply(new ToolCallEvent("c1", "screenshot", "{}", "Take a screenshot"));
        var step = Assert.IsType<ToolStepItem>(Assert.Single(log.Items));
        Assert.True(step.IsRunning);
        Assert.Equal(ToolIcons.For("screenshot"), step.Icon);

        log.Apply(new ToolResultEvent("c1", "screenshot", false, "1280x800", FakeImage(), TimeSpan.FromMilliseconds(420)));

        Assert.Single(log.Items);
        Assert.False(step.IsRunning);
        Assert.False(step.IsError);
        Assert.Equal("0.4s", step.DurationText);
        Assert.True(step.HasImage);
        Assert.Equal(1280, step.ImageWidth);
    }

    [Fact]
    public void Tool_errors_are_flagged_with_a_preview()
    {
        var log = new ConversationLog();
        log.Apply(new ToolCallEvent("c1", "click", "{}", "left click at (5, 5)"));
        log.Apply(new ToolResultEvent("c1", "click", true, "Denied: the target window is elevated.\nAsk the user.", null, TimeSpan.FromSeconds(1)));

        var step = (ToolStepItem)log.Items[0];
        Assert.True(step.IsError);
        Assert.Contains("elevated", step.ResultPreview);
        Assert.NotNull(step.ResultTooltip);
        Assert.Contains("[error]", step.ToPlainText());
    }

    [Fact]
    public void A_result_without_a_call_still_shows_a_step()
    {
        var log = new ConversationLog();
        log.Apply(new ToolResultEvent("orphan", "wait", false, "ok", null, TimeSpan.FromSeconds(2)));
        var step = Assert.IsType<ToolStepItem>(Assert.Single(log.Items));
        Assert.Equal("wait", step.Summary);
        Assert.False(step.IsRunning);
    }

    [Fact]
    public void Log_is_capped_and_drops_the_oldest_items()
    {
        var log = new ConversationLog(maxItems: 50);
        for (int i = 0; i < 120; i++) log.Apply(new StatusEvent($"line {i}"));
        Assert.Equal(50, log.Items.Count);
        Assert.Equal("line 70", ((StatusLineItem)log.Items[0]).Message);
        Assert.Equal("line 119", ((StatusLineItem)log.Items[^1]).Message);
    }

    [Fact]
    public void Default_cap_is_about_two_thousand()
    {
        var log = new ConversationLog();
        for (int i = 0; i < 2100; i++) log.Apply(new StatusEvent("x"));
        Assert.Equal(2000, log.Items.Count);
    }

    [Fact]
    public void Only_the_most_recent_images_are_kept()
    {
        var log = new ConversationLog(maxImages: 3);
        for (int i = 0; i < 5; i++)
        {
            log.Apply(new ToolCallEvent($"c{i}", "screenshot", "{}", "shot"));
            log.Apply(new ToolResultEvent($"c{i}", "screenshot", false, "ok", FakeImage(), TimeSpan.Zero));
        }
        var steps = log.Items.OfType<ToolStepItem>().ToList();
        Assert.Equal(new[] { false, false, true, true, true }, steps.Select(s => s.HasImage).ToArray());
        Assert.Equal(3, log.ImageCount);
    }

    [Fact]
    public void Capped_steps_release_their_images()
    {
        var log = new ConversationLog(maxItems: 10, maxImages: 10);
        log.Apply(new ToolCallEvent("c0", "screenshot", "{}", "shot"));
        log.Apply(new ToolResultEvent("c0", "screenshot", false, "ok", FakeImage(), TimeSpan.Zero));
        var first = (ToolStepItem)log.Items[0];
        for (int i = 0; i < 15; i++) log.Apply(new StatusEvent("filler"));
        Assert.DoesNotContain(first, log.Items);
        Assert.False(first.HasImage);
        Assert.Equal(0, log.ImageCount);
    }

    [Fact]
    public void Images_are_not_kept_when_screenshots_are_hidden()
    {
        var log = new ConversationLog { KeepImages = false };
        log.Apply(new ToolCallEvent("c1", "screenshot", "{}", "shot"));
        log.Apply(new ToolResultEvent("c1", "screenshot", false, "ok", FakeImage(), TimeSpan.Zero));
        Assert.False(((ToolStepItem)log.Items[0]).HasImage);
    }

    [Fact]
    public void Local_user_message_is_not_duplicated_by_the_session_echo()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("open notepad");
        log.Apply(new UserMessageEvent("open notepad"));
        Assert.Single(log.Items.OfType<UserMessageItem>());

        log.Apply(new UserMessageEvent("something else"));
        Assert.Equal(2, log.Items.OfType<UserMessageItem>().Count());
    }

    [Fact]
    public void Only_one_footer_per_turn()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("hi");
        var result = Result(TurnOutcome.Completed);
        log.Apply(new TurnCompletedEvent(result));
        Assert.Null(log.AddTurnResult(result));
        Assert.Single(log.Items.OfType<TurnFooterItem>());

        log.AddLocalUserMessage("again");
        Assert.NotNull(log.AddTurnResult(result));
        Assert.Equal(2, log.Items.OfType<TurnFooterItem>().Count());
    }

    [Fact]
    public void Turn_end_marks_unfinished_steps_as_not_running()
    {
        var log = new ConversationLog();
        log.Apply(new ToolCallEvent("c1", "click", "{}", "click"));
        log.Apply(new TurnCompletedEvent(Result(TurnOutcome.Cancelled)));
        Assert.False(((ToolStepItem)log.Items[0]).IsRunning);
    }

    [Fact]
    public void Plain_text_export_lists_every_item()
    {
        var log = new ConversationLog();
        log.AddLocalUserMessage("hello");
        log.Apply(new AssistantTextEvent("Hi there"));
        log.Apply(new ToolCallEvent("c1", "type_text", "{}", "Type \"x\""));
        log.Apply(new StatusEvent("careful", StatusLevel.Warning));
        log.Apply(new TurnCompletedEvent(Result(TurnOutcome.Completed)));

        var text = log.ToPlainText();
        Assert.Contains("You: hello", text);
        Assert.Contains("DeskPilot: Hi there", text);
        Assert.Contains("> Type \"x\"", text);
        Assert.Contains("[warning] careful", text);
        Assert.Contains("--- Done", text);
    }

    [Fact]
    public void Clear_empties_everything()
    {
        var log = new ConversationLog();
        log.Apply(new ToolCallEvent("c1", "screenshot", "{}", "shot"));
        log.Apply(new ToolResultEvent("c1", "screenshot", false, "ok", FakeImage(), TimeSpan.Zero));
        log.Clear();
        Assert.Empty(log.Items);
        Assert.Equal(0, log.ImageCount);
        Assert.False(log.TurnFooterAdded);
    }

    // =====================================================================================
    // Formatting, icons, model hints
    // =====================================================================================

    [Theory]
    [InlineData(0.004, "<0.1s")]
    [InlineData(-1, "<0.1s")]
    [InlineData(0.42, "0.4s")]
    [InlineData(9.94, "9.9s")]
    [InlineData(12.4, "12s")]
    [InlineData(65, "1m 05s")]
    [InlineData(3725, "1h 02m")]
    public void Durations_are_short(double seconds, string expected) =>
        Assert.Equal(expected, Formatting.Duration(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(820, "820")]
    [InlineData(1234, "1.2k")]
    [InlineData(45678, "46k")]
    [InlineData(2_500_000, "2.5M")]
    public void Token_counts_are_short(int n, string expected) => Assert.Equal(expected, Formatting.Tokens(n));

    [Fact]
    public void Turn_summary_lists_the_stats()
    {
        var r = new TurnResult(TurnOutcome.Completed, "ok", new TurnStats(12_300, 820, 5, 0.0123, TimeSpan.FromSeconds(42), "haiku"), null);
        var s = Formatting.TurnSummary(r);
        Assert.Equal("Done  ·  5 steps  ·  12k in / 820 out tokens  ·  $0.012  ·  42s  ·  haiku", s);

        var noCost = new TurnResult(TurnOutcome.StepLimit, null, new TurnStats(0, 0, 1, null, TimeSpan.FromSeconds(1.5), null), null);
        Assert.Equal("Step limit reached  ·  1 step  ·  1.5s", Formatting.TurnSummary(noCost));
    }

    [Fact]
    public void Footer_exposes_outcome_and_error()
    {
        var footer = new TurnFooterItem(new TurnResult(TurnOutcome.Failed, null, new TurnStats(0, 0, 0, null, TimeSpan.Zero, null), "HTTP 401"));
        Assert.True(footer.IsProblem);
        Assert.Equal("HTTP 401", footer.Error);
        Assert.StartsWith("Failed", footer.Text);
    }

    [Fact]
    public void Preview_trims_lines_and_length()
    {
        Assert.Equal("a\nb…", Formatting.Preview("a\n\nb\nc", 2, 100));
        Assert.Equal("abc…", Formatting.Preview("abcdef", 3, 3));
        Assert.Equal("", Formatting.Preview("  ", 3, 3));
    }

    [Fact]
    public void Every_known_tool_has_its_own_icon()
    {
        foreach (var tool in new[] { "screenshot", "zoom", "click", "move_mouse", "drag", "scroll", "type_text", "press_keys", "wait",
                     "list_windows", "focus_window", "launch", "ui_elements", "get_clipboard", "set_clipboard", "run_command",
                     "vault_search", "vault_read", "vault_list", "vault_append" })
        {
            Assert.NotEqual(ToolIcons.Default, ToolIcons.For(tool));
        }
        Assert.Equal(ToolIcons.Default, ToolIcons.For("something_new"));
        Assert.Equal(ToolIcons.Default, ToolIcons.For(null));
    }

    [Fact]
    public void Model_hints_describe_capabilities()
    {
        Assert.Equal("vision · thinking", ModelOption.BuildHints(new ModelInfo("a", "A", true, true, true)));
        Assert.Equal("text only · no tools", ModelOption.BuildHints(new ModelInfo("b", "B", false, false, null)));
        Assert.Equal("", ModelOption.BuildHints(new ModelInfo("c", "c", null, null, null)));
        var opt = ModelOption.From(new ModelInfo("claude-haiku-5-5", "Claude Haiku 5.5", true, true, true));
        Assert.Equal("claude-haiku-5-5", opt.ToString());
        Assert.True(opt.HasDisplayName);
        Assert.False(ModelOption.FromId("x").HasHints);
    }

    // =====================================================================================
    // MainViewModel
    // =====================================================================================

    [Theory]
    [InlineData(AgentState.Idle, "Idle")]
    [InlineData(AgentState.Starting, "Starting")]
    [InlineData(AgentState.Running, "Working")]
    [InlineData(AgentState.Stopping, "Stopping")]
    [InlineData(AgentState.Error, "Error")]
    public void Status_labels(AgentState state, string expected) => Assert.Equal(expected, MainViewModel.StatusLabel(state));

    [Fact]
    public async Task Session_events_are_marshalled_to_the_ui_dispatcher()
    {
        var (vm, session, ui) = CreateViewModel();
        await Task.Run(() => session.Raise(new StatusEvent("from a worker thread")));
        Assert.Empty(vm.Items);
        ui.RunAll();
        Assert.Equal("from a worker thread", Assert.IsType<StatusLineItem>(Assert.Single(vm.Items)).Message);
    }

    [Fact]
    public void Session_state_drives_the_status_pill_and_busy_flag()
    {
        var (vm, session, ui) = CreateViewModel();
        Assert.Equal("Idle", vm.StatusText);
        session.SetState(AgentState.Running);
        Assert.False(vm.IsBusy);
        ui.RunAll();
        Assert.Equal("Working", vm.StatusText);
        Assert.True(vm.IsBusy);
        Assert.Equal("Stop", vm.SendButtonText);
        session.SetState(AgentState.Error);
        ui.RunAll();
        Assert.Equal("Error", vm.StatusText);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Send_adds_the_message_and_a_footer_when_the_session_reports_none()
    {
        var (vm, session, ui) = CreateViewModel();
        int started = 0, ended = 0;
        vm.TurnStarted += () => started++;
        vm.TurnEnded += () => ended++;
        Assert.False(vm.SendOrStopCommand.CanExecute(null));

        vm.InputText = "  open notepad  ";
        Assert.True(vm.SendOrStopCommand.CanExecute(null));
        await vm.SendAsync();
        ui.RunAll();

        Assert.Equal(new[] { "open notepad" }, session.Sent);
        Assert.Equal("", vm.InputText);
        Assert.IsType<UserMessageItem>(vm.Items[0]);
        Assert.IsType<TurnFooterItem>(vm.Items[^1]);
        Assert.Equal(1, started);
        Assert.Equal(1, ended);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Send_does_not_duplicate_what_the_session_reports()
    {
        var (vm, session, ui) = CreateViewModel();
        session.OnSend = (text, _) =>
        {
            var r = Result(TurnOutcome.Completed);
            session.Raise(new UserMessageEvent(text));
            session.Raise(new AssistantTextEvent("All done."));
            session.Raise(new TurnCompletedEvent(r));
            return Task.FromResult(r);
        };
        vm.InputText = "do it";
        await vm.SendAsync();
        ui.RunAll();

        Assert.Single(vm.Items.OfType<UserMessageItem>());
        Assert.Single(vm.Items.OfType<AssistantTextItem>());
        Assert.Single(vm.Items.OfType<TurnFooterItem>());
    }

    [Fact]
    public async Task Busy_while_the_turn_runs_and_stop_twice_forces_cancellation()
    {
        var (vm, session, ui) = CreateViewModel();
        var pending = new TaskCompletionSource<TurnResult>();
        session.OnSend = (_, ct) =>
        {
            ct.Register(() => pending.TrySetCanceled(ct));
            return pending.Task;
        };
        vm.InputText = "long task";
        var turn = vm.SendAsync();

        Assert.True(vm.IsBusy);
        Assert.Equal("Starting", vm.StatusText);
        Assert.Equal("Stop", vm.SendButtonText);
        Assert.True(vm.SendOrStopCommand.CanExecute(null));
        Assert.False(vm.NewConversationCommand.CanExecute(null));

        await vm.StopAsync();
        Assert.Equal(1, session.StopCalls);
        Assert.False(session.LastToken.IsCancellationRequested);

        await vm.StopAsync();
        Assert.True(session.LastToken.IsCancellationRequested);

        await turn;
        ui.RunAll();
        Assert.False(vm.IsBusy);
        Assert.Equal(TurnOutcome.Cancelled, vm.Items.OfType<TurnFooterItem>().Single().Outcome);
    }

    [Fact]
    public async Task A_failing_send_ends_with_a_failed_footer()
    {
        var (vm, session, ui) = CreateViewModel();
        session.OnSend = (_, _) => Task.FromException<TurnResult>(new InvalidOperationException("backend exploded"));
        vm.InputText = "x";
        await vm.SendAsync();
        ui.RunAll();
        var footer = vm.Items.OfType<TurnFooterItem>().Single();
        Assert.Equal(TurnOutcome.Failed, footer.Outcome);
        Assert.Equal("backend exploded", footer.Error);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task Send_is_ignored_while_busy_or_empty()
    {
        var (vm, session, ui) = CreateViewModel();
        vm.InputText = "   ";
        await vm.SendAsync();
        Assert.Empty(session.Sent);

        session.SetState(AgentState.Running);
        ui.RunAll();
        vm.InputText = "hello";
        await vm.SendAsync();
        Assert.Empty(session.Sent);
        Assert.Equal("hello", vm.InputText);
    }

    [Fact]
    public void Switching_profile_saves_and_reloads()
    {
        var store = NewStore(s =>
        {
            var ollama = ProviderPresets.CreateProfile(ProviderPresets.OllamaId);
            s.Profiles.Add(ollama);
        });
        var (vm, session, ui) = CreateViewModel(store);
        Assert.Equal(2, vm.Profiles.Count);
        Assert.Equal("haiku", vm.ModelText);

        vm.SelectedProfile = vm.Profiles[1];
        ui.RunAll();

        Assert.Equal(store.Current.Profiles[1].Id, store.Current.ActiveProfileId);
        Assert.True(session.ReloadCalls >= 1);
        Assert.Equal("qwen2.5vl:7b", vm.ModelText);
        Assert.Contains(vm.Models, m => m.Id == "gemma3:12b");
        Assert.Contains("Ollama", vm.ProfileModelText);
    }

    [Fact]
    public void Model_thinking_and_effort_are_saved_to_the_active_profile()
    {
        var store = NewStore();
        var (vm, session, ui) = CreateViewModel(store);

        vm.ModelText = " sonnet ";
        ui.RunAll();
        Assert.Equal("sonnet", store.Current.ActiveProfile!.Model);

        vm.IsThinkingOff = true;
        ui.RunAll();
        Assert.Equal(ThinkingMode.Off, store.Current.ActiveProfile!.Thinking);
        Assert.True(vm.IsThinkingOff);
        Assert.False(vm.IsThinkingAuto);

        vm.Effort = "high";
        ui.RunAll();
        Assert.Equal("high", store.Current.ActiveProfile!.Effort);

        vm.Effort = null!;
        Assert.Equal("high", vm.Effort);

        vm.Effort = MainViewModel.DefaultEffort;
        ui.RunAll();
        Assert.Equal("", store.Current.ActiveProfile!.Effort);
        Assert.True(session.ReloadCalls >= 4);
    }

    [Fact]
    public void Picking_a_model_from_the_list_sets_the_model_text()
    {
        var store = NewStore();
        var (vm, _, ui) = CreateViewModel(store);
        var sonnet = vm.Models.First(m => m.Id == "sonnet");
        vm.SelectedModel = sonnet;
        ui.RunAll();
        Assert.Equal("sonnet", vm.ModelText);
        Assert.Equal("sonnet", store.Current.ActiveProfile!.Model);
        vm.SelectedModel = null;
        Assert.Equal("sonnet", vm.ModelText);
    }

    [Fact]
    public void External_settings_changes_refresh_the_status_bar()
    {
        var store = NewStore();
        var (vm, _, ui) = CreateViewModel(store);
        Assert.False(vm.DryRun);
        Assert.False(vm.VaultOn);
        Assert.Equal("Stop: Ctrl+Alt+X", vm.StopHotkeyText);

        Directory.CreateDirectory(Path.Combine(_dir, "vault"));
        store.Update(s =>
        {
            s.Safety.DryRun = true;
            s.Safety.AllowAdmin = true;
            s.Ui.StopHotkey = "ctrl+shift+f12";
            s.Ui.ShowThinking = false;
            s.Vault.Path = Path.Combine(_dir, "vault");
        });
        ui.RunAll();

        Assert.True(vm.DryRun);
        Assert.True(vm.AdminMode);
        Assert.True(vm.VaultOn);
        Assert.Equal("Vault on", vm.VaultText);
        Assert.False(vm.ShowThinking);
        Assert.Equal("Stop: Ctrl+Shift+F12", vm.StopHotkeyText);
    }

    [Fact]
    public void Missing_vault_folder_counts_as_off()
    {
        var store = NewStore(s => s.Vault.Path = Path.Combine(_dir, "does-not-exist"));
        var (vm, _, _) = CreateViewModel(store);
        Assert.False(vm.VaultOn);
        Assert.Contains("not found", vm.VaultTooltip);
    }

    [Fact]
    public async Task Refreshing_models_lists_them_with_hints()
    {
        var catalog = new FakeCatalog
        {
            Result = new ModelListResult(new[]
            {
                new ModelInfo("claude-haiku-5-5", "Claude Haiku 5.5", true, true, true),
                new ModelInfo("text-model", "text-model", false, null, null),
            }, null),
        };
        var (vm, _, _) = CreateViewModel(catalog: catalog);
        await vm.RefreshModelsAsync();

        Assert.Equal(new[] { "claude-haiku-5-5", "text-model" }, vm.Models.Select(m => m.Id).ToArray());
        Assert.Equal("vision · thinking", vm.Models[0].Hints);
        Assert.Equal("text only", vm.Models[1].Hints);
        Assert.Null(vm.ModelListError);
        Assert.Equal("", catalog.LastApiKey);
        Assert.False(vm.IsRefreshingModels);
        Assert.Equal("haiku", vm.ModelText);
    }

    [Fact]
    public async Task Model_list_errors_are_reported_and_keep_the_suggestions()
    {
        var catalog = new FakeCatalog { Result = new ModelListResult(Array.Empty<ModelInfo>(), "401 unauthorized") };
        var (vm, _, _) = CreateViewModel(catalog: catalog);
        int before = vm.Models.Count;
        await vm.RefreshModelsAsync();

        Assert.Equal("401 unauthorized", vm.ModelListError);
        Assert.Contains("401 unauthorized", vm.RefreshModelsTooltip);
        Assert.Equal(before, vm.Models.Count);
        Assert.Contains(vm.Items.OfType<StatusLineItem>(), s => s.Level == StatusLevel.Warning && s.Message.Contains("401"));
    }

    [Fact]
    public async Task New_conversation_clears_the_log_only_when_the_session_reset_worked()
    {
        var (vm, session, ui) = CreateViewModel();
        session.Raise(new AssistantTextEvent("hello"));
        ui.RunAll();
        await vm.NewConversationAsync();
        Assert.Empty(vm.Items);
        Assert.Equal(1, session.NewConversationCalls);

        session.Raise(new AssistantTextEvent("again"));
        ui.RunAll();
        session.NewConversationError = new IOException("pipe broken");
        await vm.NewConversationAsync();
        Assert.Contains(vm.Items, i => i is AssistantTextItem);
        Assert.Contains(vm.Items.OfType<StatusLineItem>(), s => s.Level == StatusLevel.Error);
    }

    [Fact]
    public void Copy_log_hands_the_text_to_the_window()
    {
        var (vm, session, ui) = CreateViewModel();
        Assert.False(vm.CopyLogCommand.CanExecute(null));
        string? copied = null;
        vm.CopyRequested += t => copied = t;
        session.Raise(new AssistantTextEvent("visible text"));
        ui.RunAll();
        Assert.True(vm.CopyLogCommand.CanExecute(null));
        vm.CopyLogCommand.Execute(null);
        Assert.Contains("DeskPilot: visible text", copied);
    }

    [Fact]
    public void Example_prompts_fill_the_input()
    {
        var (vm, _, _) = CreateViewModel();
        bool focused = false;
        vm.FocusInputRequested += () => focused = true;
        vm.UseExampleCommand.Execute(MainViewModel.ExamplePrompts[0]);
        Assert.Equal(MainViewModel.ExamplePrompts[0], vm.InputText);
        Assert.True(focused);
    }

    [Fact]
    public void Open_image_only_for_steps_with_an_image()
    {
        var (vm, _, _) = CreateViewModel();
        ToolStepItem? opened = null;
        vm.ImageRequested += s => opened = s;
        var noImage = new ToolStepItem("1", "click", "click");
        vm.OpenImageCommand.Execute(noImage);
        Assert.Null(opened);
        var withImage = new ToolStepItem("2", "screenshot", "shot");
        withImage.SetImage(FakeImage());
        vm.OpenImageCommand.Execute(withImage);
        Assert.Same(withImage, opened);
    }

    [Fact]
    public void Hotkey_status_is_shown()
    {
        var (vm, _, _) = CreateViewModel();
        vm.SetHotkeyStatus(false, "Ctrl+Alt+X is already used by another program");
        Assert.False(vm.HotkeyOk);
        Assert.Contains("already used", vm.HotkeyTooltip);
        vm.SetHotkeyStatus(true, null);
        Assert.True(vm.HotkeyOk);
    }

    [Fact]
    public void Disposed_view_model_ignores_late_events()
    {
        var (vm, session, ui) = CreateViewModel();
        session.Raise(new StatusEvent("before"));
        vm.Dispose();
        session.Raise(new StatusEvent("after"));
        ui.RunAll();
        Assert.Empty(vm.Items);
    }

    // =====================================================================================
    // Hotkey parsing
    // =====================================================================================

    [Theory]
    [InlineData("Ctrl+Alt+X", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x58u, "Ctrl+Alt+X")]
    [InlineData("ctrl + alt + x", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x58u, "Ctrl+Alt+X")]
    [InlineData("Ctrl+Shift+F12", HotkeyGesture.ModControl | HotkeyGesture.ModShift, 0x7Bu, "Ctrl+Shift+F12")]
    [InlineData("Pause", 0u, 0x13u, "Pause")]
    [InlineData("Break", 0u, 0x13u, "Pause")]
    [InlineData("F9", 0u, 0x78u, "F9")]
    [InlineData("Shift+F1", HotkeyGesture.ModShift, 0x70u, "Shift+F1")]
    [InlineData("Win+Esc", HotkeyGesture.ModWin, 0x1Bu, "Win+Esc")]
    [InlineData("Alt+Shift+1", HotkeyGesture.ModAlt | HotkeyGesture.ModShift, 0x31u, "Alt+Shift+1")]
    [InlineData("Ctrl+Alt+Del", HotkeyGesture.ModControl | HotkeyGesture.ModAlt, 0x2Eu, "Ctrl+Alt+Delete")]
    [InlineData("Control+ScrollLock", HotkeyGesture.ModControl, 0x91u, "Ctrl+ScrollLock")]
    [InlineData("Ctrl+Numpad5", HotkeyGesture.ModControl, 0x65u, "Ctrl+Num5")]
    [InlineData("ScrollLock", 0u, 0x91u, "ScrollLock")]
    public void Hotkeys_parse(string text, uint mods, uint vk, string display)
    {
        Assert.True(HotkeyGesture.TryParse(text, out var g, out var error), error);
        Assert.Equal(mods, g!.Modifiers);
        Assert.Equal(vk, g.VirtualKey);
        Assert.Equal(display, g.Display);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("Ctrl+Alt")]
    [InlineData("X")]
    [InlineData("Shift+A")]
    [InlineData("Space")]
    [InlineData("Enter")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Alt+Bogus")]
    [InlineData("F25")]
    public void Bad_hotkeys_are_rejected_with_a_reason(string? text)
    {
        Assert.False(HotkeyGesture.TryParse(text, out var g, out var error));
        Assert.Null(g);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    // =====================================================================================
    // Markdown-lite
    // =====================================================================================

    [Fact]
    public void Markdown_inlines()
    {
        var inl = MarkdownLite.ParseInlines("Run **this** with `dir /b` and see [the docs](https://example.com/x).");
        Assert.Equal(new[] { MdInlineKind.Text, MdInlineKind.Bold, MdInlineKind.Text, MdInlineKind.Code, MdInlineKind.Text, MdInlineKind.Link, MdInlineKind.Text },
            inl.Select(i => i.Kind).ToArray());
        Assert.Equal("this", inl[1].Text);
        Assert.Equal("dir /b", inl[3].Text);
        Assert.Equal("the docs", inl[5].Text);
        Assert.Equal("https://example.com/x", inl[5].Url);
    }

    [Fact]
    public void Markdown_unclosed_markers_stay_literal()
    {
        var inl = MarkdownLite.ParseInlines("2 ** 3 and a `tick and [x](");
        Assert.Single(inl);
        Assert.Equal("2 ** 3 and a `tick and [x](", inl[0].Text);
        Assert.Equal(MdInlineKind.Bold, MarkdownLite.ParseInlines("__under__")[0].Kind);
    }

    [Fact]
    public void Markdown_blocks()
    {
        var text = "# Plan\nFirst line\nsecond line\n\n- one\n  - nested **bold**\n* two\n1. alpha\n2) beta\n\n```powershell\nGet-Process\n  | Sort CPU\n```\nAfter";
        var blocks = MarkdownLite.Parse(text);
        Assert.Equal(new[]
        {
            MdBlockKind.Heading, MdBlockKind.Paragraph, MdBlockKind.Bullet, MdBlockKind.Bullet, MdBlockKind.Bullet,
            MdBlockKind.Numbered, MdBlockKind.Numbered, MdBlockKind.Code, MdBlockKind.Paragraph,
        }, blocks.Select(b => b.Kind).ToArray());

        Assert.Equal(1, blocks[0].Level);
        Assert.Contains(blocks[1].Inlines, i => i.Kind == MdInlineKind.LineBreak);
        Assert.Equal(0, blocks[2].Level);
        Assert.Equal(1, blocks[3].Level);
        Assert.Contains(blocks[3].Inlines, i => i.Kind == MdInlineKind.Bold);
        Assert.Equal("1.", blocks[5].Marker);
        Assert.Equal("2.", blocks[6].Marker);
        Assert.Equal("Get-Process\n  | Sort CPU", blocks[7].Code);
        Assert.Equal("powershell", blocks[7].Marker);
    }

    [Fact]
    public void Markdown_unclosed_fence_while_streaming_is_code()
    {
        var blocks = MarkdownLite.Parse("Here:\n```\nline 1\nline 2");
        Assert.Equal(MdBlockKind.Code, blocks[^1].Kind);
        Assert.Equal("line 1\nline 2", blocks[^1].Code);
        Assert.Empty(MarkdownLite.Parse(""));
        Assert.Empty(MarkdownLite.Parse(null));
    }

    // =====================================================================================
    // Overlay, confirmation view models, placement
    // =====================================================================================

    [Theory]
    [InlineData(OverlayCorner.TopLeft, 16, 16)]
    [InlineData(OverlayCorner.TopCenter, 760, 16)]
    [InlineData(OverlayCorner.TopRight, 1504, 16)]
    [InlineData(OverlayCorner.BottomLeft, 16, 944)]
    [InlineData(OverlayCorner.BottomCenter, 760, 944)]
    [InlineData(OverlayCorner.BottomRight, 1504, 944)]
    public void Overlay_is_placed_in_the_chosen_corner(OverlayCorner corner, int x, int y)
    {
        var work = new ScreenRect(0, 0, 1920, 1040);
        Assert.Equal(new ScreenPoint(x, y), OverlayPlacement.Compute(work, 400, 80, corner, 16));
    }

    [Fact]
    public void Overlay_placement_handles_offset_monitors_and_tiny_work_areas()
    {
        Assert.Equal(new ScreenPoint(-1904, 116), OverlayPlacement.Compute(new ScreenRect(-1920, 100, 1920, 1080), 400, 80, OverlayCorner.TopLeft, 16));
        var tiny = OverlayPlacement.Compute(new ScreenRect(0, 0, 300, 50), 400, 80, OverlayCorner.BottomRight, 16);
        Assert.Equal(new ScreenPoint(16, 16), tiny);
    }

    [Fact]
    public void Overlay_hides_only_for_targets_on_or_next_to_it()
    {
        var bounds = new ScreenRect(1500, 940, 400, 80);
        Assert.True(OverlayPlacement.ShouldHide(bounds, new ScreenPoint(1600, 980)));
        Assert.True(OverlayPlacement.ShouldHide(bounds, new ScreenPoint(1495, 935)));
        Assert.False(OverlayPlacement.ShouldHide(bounds, new ScreenPoint(1400, 980)));
        Assert.False(OverlayPlacement.ShouldHide(bounds, null));
        Assert.False(OverlayPlacement.ShouldHide(default, new ScreenPoint(0, 0)));
    }

    [Fact]
    public void Overlay_view_model_tracks_steps_and_latest_action()
    {
        bool stopped = false;
        var vm = new OverlayViewModel(() => stopped = true) { HotkeyHint = "Ctrl+Alt+X" };
        vm.BeginTurn();
        vm.OnState(AgentState.Running);
        Assert.Equal("No actions yet  ·  Ctrl+Alt+X to stop", vm.FooterText);

        vm.OnEvent(new ToolCallEvent("1", "click", "{}", "left click at (3, 4)"));
        vm.OnEvent(new ToolCallEvent("2", "type_text", "{}", "Type \"hi\""));
        Assert.Equal(2, vm.Steps);
        Assert.Equal("Type \"hi\"", vm.Detail);
        Assert.StartsWith("2 actions", vm.FooterText);

        vm.OnState(AgentState.Stopping);
        Assert.True(vm.IsStopping);
        Assert.Equal("Stopping", vm.Title);

        vm.StopCommand.Execute(null);
        Assert.True(stopped);

        vm.HotkeyHint = "";
        vm.BeginTurn();
        Assert.Equal("No actions yet", vm.FooterText);
    }

    [Theory]
    [InlineData(ActionRisk.None, "No risk")]
    [InlineData(ActionRisk.Low, "Low risk")]
    [InlineData(ActionRisk.Medium, "Medium risk")]
    [InlineData(ActionRisk.High, "High risk")]
    public void Risk_labels(ActionRisk risk, string label) => Assert.Equal(label, ConfirmViewModel.RiskLabel(risk));

    [Fact]
    public void Confirmation_details_show_what_will_happen()
    {
        var action = new ProposedAction("run_command", "Run: Remove-Item temp", ActionRisk.High,
            Target: new ScreenPoint(10, 20), Text: new string('a', 700), Keys: KeyCombo.Parse("ctrl+enter"),
            LaunchTarget: "notepad.exe", Command: "Remove-Item temp");
        var vm = new ConfirmViewModel(action);
        Assert.Equal("Run: Remove-Item temp", vm.Summary);
        Assert.Equal("High risk", vm.RiskText);
        var labels = vm.Details.Select(d => d.Label).ToArray();
        Assert.Equal(new[] { "Tool", "Screen point", "Text", "Keys", "Opens", "Command" }, labels);
        Assert.Equal("(10, 20)", vm.Details[1].Value);
        Assert.Contains("700 characters", vm.Details[2].Value);
        Assert.Equal("ctrl+enter", vm.Details[3].Value);
        Assert.True(vm.Details[5].IsMono);

        var minimal = new ConfirmViewModel(new ProposedAction("click", "", ActionRisk.Medium));
        Assert.Equal("click", minimal.Summary);
        Assert.Single(minimal.Details);
    }

    // =====================================================================================
    // Single instance (kernel objects only, unique names)
    // =====================================================================================

    [Fact]
    public async Task Second_instance_is_refused_and_can_signal_the_first()
    {
        var name = "DeskPilotTest." + Guid.NewGuid().ToString("N");
        using var first = new SingleInstanceGuard(name);
        Assert.True(first.TryAcquire());
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ActivationRequested += () => activated.TrySetResult();

        // A mutex is re-entrant on its owning thread, so the "second launch" runs elsewhere.
        var (acquired, signalled) = await Task.Run(() =>
        {
            using var second = new SingleInstanceGuard(name);
            return (second.TryAcquire(), second.SignalFirstInstance());
        });

        Assert.False(acquired);
        Assert.True(signalled);
        await activated.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Single_instance_names_are_per_user_and_stable()
    {
        var a = SingleInstanceGuard.DefaultBaseName();
        Assert.Equal(a, SingleInstanceGuard.DefaultBaseName());
        Assert.Matches("^DeskPilot\\.[0-9A-F]{16}$", a);
        Assert.False(new SingleInstanceGuard("DeskPilotTest." + Guid.NewGuid().ToString("N")).SignalFirstInstance());
    }

    // =====================================================================================
    // WPF: resources, templates and window construction (STA, never shown)
    // =====================================================================================

    private static readonly string[] ContractBrushes =
    {
        "BgBrush", "PanelBrush", "Panel2Brush", "CardBrush", "BorderBrush", "TextBrush", "SubtleTextBrush", "MutedTextBrush",
        "AccentBrush", "AccentHoverBrush", "AccentTextBrush", "DangerBrush", "WarningBrush", "SuccessBrush", "InputBgBrush", "SelectionBrush",
    };

    [Fact]
    public void Theme_defines_every_contract_key()
    {
        WpfTestHost.Run(() =>
        {
            var app = Application.Current!;
            foreach (var key in ContractBrushes)
            {
                var brush = Assert.IsAssignableFrom<Brush>(app.TryFindResource(key));
                Assert.True(brush.IsFrozen, key + " should be frozen");
            }

            var named = new (string Key, Type Target)[]
            {
                ("AccentButton", typeof(Button)), ("DangerButton", typeof(Button)), ("GhostButton", typeof(Button)), ("IconButton", typeof(Button)),
                ("ToggleSwitch", typeof(CheckBox)), ("SectionHeader", typeof(TextBlock)), ("CaptionText", typeof(TextBlock)),
                ("CardBorder", typeof(Border)), ("SegmentedRadio", typeof(RadioButton)), ("MonoTextBox", typeof(TextBox)),
            };
            foreach (var (key, target) in named)
            {
                var style = Assert.IsType<Style>(app.TryFindResource(key));
                Assert.Equal(target, style.TargetType);
            }

            foreach (var type in new[]
                     {
                         typeof(Button), typeof(TextBox), typeof(PasswordBox), typeof(ComboBox), typeof(ComboBoxItem), typeof(CheckBox),
                         typeof(RadioButton), typeof(TabControl), typeof(TabItem), typeof(ListBox), typeof(ListBoxItem), typeof(ScrollBar),
                         typeof(ScrollViewer), typeof(ToolTip), typeof(Expander), typeof(Slider), typeof(GroupBox), typeof(Label),
                     })
            {
                // The application dictionaries only: TryFindResource would fall back to the system theme.
                Assert.True(app.Resources[type] is Style s && s.TargetType == type, $"implicit style for {type.Name}");
            }

            Assert.False(app.Resources.Contains(typeof(TextBlock)), "TextBlock must not be restyled implicitly");
        });
    }

    [Fact]
    public void Theme_templates_apply_to_every_styled_control()
    {
        WpfTestHost.Run(() =>
        {
            var app = Application.Current!;
            Style S(string key) => (Style)app.FindResource(key);

            var editable = new ComboBox { IsEditable = true, ItemsSource = new[] { "a", "b" }, Text = "a" };
            var tabsTop = new TabControl { Height = 120 };
            tabsTop.Items.Add(new TabItem { Header = "One", Content = new TextBlock { Text = "1" } });
            tabsTop.Items.Add(new TabItem { Header = "Two" });
            var tabsLeft = new TabControl { TabStripPlacement = Dock.Left, Height = 120 };
            tabsLeft.Items.Add(new TabItem { Header = "Left", Content = new TextBlock { Text = "L" }, IsSelected = true });
            var list = new ListBox { Height = 80 };
            list.Items.Add(new ListBoxItem { Content = "row", IsSelected = true });
            var combo = new ComboBox { Width = 120 };
            combo.Items.Add(new ComboBoxItem { Content = "item", IsSelected = true });

            var controls = new List<Control>
            {
                new Button { Content = "Default" },
                new Button { Content = "Accent", Style = S("AccentButton") },
                new Button { Content = "Danger", Style = S("DangerButton") },
                new Button { Content = "Ghost", Style = S("GhostButton") },
                new Button { Content = ToolIcons.Glyph(0xE713), Style = S("IconButton") },
                new TextBox { Text = "text" },
                new TextBox { Text = "mono", Style = S("MonoTextBox") },
                new PasswordBox(),
                combo, editable,
                new CheckBox { Content = "check", IsChecked = true },
                new CheckBox { Content = "three", IsThreeState = true, IsChecked = null },
                new CheckBox { Content = "switch", Style = S("ToggleSwitch"), IsChecked = true },
                new RadioButton { Content = "radio", IsChecked = true },
                new RadioButton { Content = "segment", Style = S("SegmentedRadio"), IsChecked = true },
                tabsTop, tabsLeft, list,
                new Expander { Header = "More", Content = new TextBlock { Text = "inside" }, IsExpanded = true },
                new Slider { Minimum = 0, Maximum = 10, Value = 4, Width = 150 },
                new GroupBox { Header = "Group", Content = new TextBlock { Text = "body" } },
                new Label { Content = "_Label" },
                new ScrollViewer { Height = 40, Content = new TextBlock { Text = "scroll", Height = 200 } },
            };

            var panel = new StackPanel();
            foreach (var c in controls) panel.Children.Add(c);
            panel.Children.Add(new TextBlock { Text = "Header", Style = S("SectionHeader") });
            panel.Children.Add(new TextBlock { Text = "Caption", Style = S("CaptionText") });
            panel.Children.Add(new Border { Style = S("CardBorder"), Child = new TextBlock { Text = "card" } });

            var window = new Window { Content = panel };
            try
            {
                MeasureAndArrange(panel, 600, 2000);
                foreach (var c in controls)
                {
                    Assert.NotNull(c.Template);
                    Assert.True(VisualTreeHelper.GetChildrenCount(c) > 0, $"{c.GetType().Name} has no visual tree");
                }
                Assert.Same(app.FindResource("DpButtonTemplate"), controls[0].Template);

                var tip = new ToolTip { Content = "A tooltip with quite a lot of text so that it wraps" };
                tip.ApplyTemplate();
                Assert.True(VisualTreeHelper.GetChildrenCount(tip) > 0);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public void Main_window_builds_with_every_kind_of_log_item()
    {
        WpfTestHost.Run(() =>
        {
            var store = NewStore();
            var session = new FakeSession();
            var vm = new MainViewModel(store, session, new FakeCatalog(), new FakeDetector(), new QueueDispatcher());
            vm.Conversation.AddLocalUserMessage("Open Notepad and type hello");
            vm.Conversation.Apply(new ThinkingEvent("I should open the Start menu.", true));
            vm.Conversation.Apply(new AssistantTextEvent("Opening **Notepad**:\n\n- step `one`\n\n```\ncode\n```", true));
            vm.Conversation.Apply(new ToolCallEvent("c1", "screenshot", "{}", "Take a screenshot"));
            vm.Conversation.Apply(new ToolResultEvent("c1", "screenshot", false, "1280x800", PngImage(64, 40), TimeSpan.FromMilliseconds(300)));
            vm.Conversation.Apply(new ToolCallEvent("c2", "click", "{}", "left click at (1, 2)"));
            vm.Conversation.Apply(new ToolResultEvent("c2", "click", true, "Denied", null, TimeSpan.FromMilliseconds(5)));
            vm.Conversation.Apply(new StatusEvent("info"));
            vm.Conversation.Apply(new StatusEvent("warn", StatusLevel.Warning));
            vm.Conversation.Apply(new StatusEvent("err", StatusLevel.Error));
            vm.Conversation.Apply(new TurnCompletedEvent(new TurnResult(TurnOutcome.Failed, null, new TurnStats(10, 5, 2, 0.001, TimeSpan.FromSeconds(3), "haiku"), "boom")));

            var window = new MainWindow(vm);
            try
            {
                Assert.Same(vm, window.DataContext);
                Assert.Same(Application.Current!.FindResource("BgBrush"), window.Background);
                Assert.NotNull(window.Icon);

                var root = (FrameworkElement)window.Content;
                MeasureAndArrange(root, 1000, 2400);

                var logList = (ItemsControl)window.FindName("LogList");
                int realized = Enumerable.Range(0, vm.Items.Count).Count(i => logList.ItemContainerGenerator.ContainerFromIndex(i) != null);
                Assert.True(realized >= 5, $"only {realized} log items were realized");

                var reply = FindDescendants<RichTextBox>(logList).First();
                Assert.Contains(reply.Document.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines), i => i is Bold);

                // A small screenshot is decoded as is, never scaled up.
                var step = vm.Items.OfType<ToolStepItem>().First();
                Assert.NotNull(step.Thumbnail);
                Assert.Equal(64, step.Thumbnail!.PixelWidth);
            }
            finally
            {
                window.AllowClose = true;
                window.Close();
                vm.Dispose();
            }

            var bare = new MainWindow();
            bare.Close();
        });
    }

    [Fact]
    public void Other_windows_build_without_being_shown()
    {
        WpfTestHost.Run(() =>
        {
            var overlay = new OverlayWindow(new OverlayViewModel(() => { }));
            Assert.False(overlay.ShowActivated);
            Assert.True(overlay.Topmost);
            Assert.False(overlay.ShowInTaskbar);
            MeasureAndArrange((FrameworkElement)overlay.Content, 400, 200);
            overlay.Close();

            var confirm = new ConfirmWindow(new ProposedAction("launch", "Open notepad.exe", ActionRisk.High, LaunchTarget: "notepad.exe"));
            Assert.Equal(ConfirmationChoice.Deny, confirm.Choice);
            Assert.True(confirm.Topmost);
            var deny = (Button)confirm.FindName("DenyButton");
            var allow = (Button)confirm.FindName("AllowButton");
            Assert.True(deny.IsDefault);
            Assert.True(deny.IsCancel);
            Assert.False(allow.IsDefault);
            MeasureAndArrange((FrameworkElement)confirm.Content, 500, 600);
            confirm.CloseWith(ConfirmationChoice.Allow);
            Assert.Equal(ConfirmationChoice.Allow, confirm.Choice);

            var image = BitmapSource.Create(8, 6, 96, 96, PixelFormats.Bgra32, null, new byte[8 * 6 * 4], 8 * 4);
            var viewer = new ImageViewerWindow(image, "caption");
            MeasureAndArrange((FrameworkElement)viewer.Content, 800, 600);
            Assert.False(viewer.IsActualSize);
            viewer.ToggleZoom();
            Assert.True(viewer.IsActualSize);
            viewer.Close();
        });
    }

    [Fact]
    public void Markdown_renders_into_a_flow_document()
    {
        WpfTestHost.Run(() =>
        {
            var doc = MarkdownView.Render(MarkdownLite.Parse("**b** and `c` [l](http://x)\n\n- item\n\n```\ncode\n```"), null);
            Assert.Equal(3, doc.Blocks.Count);
            var first = (Paragraph)doc.Blocks.FirstBlock;
            Assert.Contains(first.Inlines, i => i is Bold);
            Assert.Contains(first.Inlines.OfType<Run>(), r => r.Text == "l" && (string?)r.ToolTip == "http://x");
            Assert.Contains("•", new TextRange(doc.Blocks.ElementAt(1).ContentStart, doc.Blocks.ElementAt(1).ContentEnd).Text);

            var box = new RichTextBox();
            MarkdownView.SetText(box, "hello");
            Assert.Equal("hello", new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Text.Trim());
        });
    }

    [Fact]
    public void App_icon_resource_has_all_sizes_and_loads()
    {
        WpfTestHost.Run(() =>
        {
            var info = Application.GetResourceStream(new Uri(TrayIconService.IconResourceUri, UriKind.Absolute));
            Assert.NotNull(info);
            byte[] bytes;
            using (var ms = new MemoryStream())
            {
                info!.Stream.CopyTo(ms);
                bytes = ms.ToArray();
            }
            Assert.Equal(0, BitConverter.ToUInt16(bytes, 0));
            Assert.Equal(1, BitConverter.ToUInt16(bytes, 2));
            int count = BitConverter.ToUInt16(bytes, 4);
            var sizes = Enumerable.Range(0, count).Select(i => bytes[6 + 16 * i] == 0 ? 256 : bytes[6 + 16 * i]).ToArray();
            Assert.Equal(new[] { 16, 24, 32, 48, 64, 128, 256 }, sizes);

            using var icon16 = TrayIconService.LoadAppIcon(new System.Drawing.Size(16, 16));
            using var icon32 = TrayIconService.LoadAppIcon(new System.Drawing.Size(32, 32));
            Assert.NotNull(icon16);
            Assert.Equal(16, icon16!.Width);
            Assert.Equal(32, icon32!.Width);

            var png = Application.GetResourceStream(new Uri("pack://application:,,,/DeskPilot;component/Assets/DeskPilot.png"));
            Assert.NotNull(png);
            var frame = BitmapFrame.Create(png!.Stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            Assert.Equal(256, frame.PixelWidth);
        });
    }

    [Fact]
    public void Thumbnails_decode_small_and_garbage_is_ignored()
    {
        WpfTestHost.Run(() =>
        {
            var step = new ToolStepItem("1", "screenshot", "shot");
            step.SetImage(PngImage(800, 500));
            Assert.Equal(ToolStepItem.ThumbnailDecodeWidth, step.Thumbnail!.PixelWidth);
            Assert.True(step.Thumbnail.IsFrozen);
            var full = ToolStepItem.DecodeImage(step.ImageBytes!, 0);
            Assert.Equal(800, full!.PixelWidth);
            step.ReleaseImage();
            Assert.Null(step.Thumbnail);

            Assert.Null(ToolStepItem.DecodeImage(new byte[] { 1, 2, 3, 4 }, 100));
            var bad = new ToolStepItem("2", "screenshot", "shot");
            bad.SetImage(new ToolImage("image/png", "not base64!", 1, 1));
            Assert.False(bad.HasImage);
        });
    }

    [Fact]
    public async Task Confirmation_with_a_cancelled_token_denies_without_any_window()
    {
        var dispatcher = WpfTestHost.UiDispatcher;
        var confirmation = new WpfUserConfirmation(dispatcher);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var choice = await confirmation.ConfirmAsync(new ProposedAction("click", "click", ActionRisk.Medium), cts.Token);
        Assert.Equal(ConfirmationChoice.Deny, choice);
    }

    [Fact]
    public async Task Overlay_controller_tracks_turns_without_showing_when_disabled()
    {
        var store = NewStore(s => s.Ui.ShowOverlay = false);
        var dispatcher = WpfTestHost.UiDispatcher;
        var controller = WpfTestHost.Run(() => new OverlayController(dispatcher, store));
        try
        {
            WpfTestHost.Run(() =>
            {
                controller.ApplyState(AgentState.Running);
                Assert.True(controller.IsTurnActive);
                Assert.False(controller.ShouldBeVisible);
                Assert.Equal("Ctrl+Alt+X", controller.ViewModel.HotkeyHint);
            });

            // Keyboard actions have no target; pointer actions with no visible overlay pass straight through.
            await controller.BeforeInputAsync(null, CancellationToken.None);
            await controller.BeforeInputAsync(new ScreenPoint(10, 10), CancellationToken.None);
            controller.AfterInput();
            Assert.False(controller.IsHiddenForInput);

            WpfTestHost.Run(() =>
            {
                controller.ApplyState(AgentState.Idle);
                Assert.False(controller.IsTurnActive);
            });
        }
        finally
        {
            WpfTestHost.Run(controller.Dispose);
        }
    }

    [Fact]
    public void Empty_environment_report_assumes_nothing()
    {
        var r = App.EmptyEnvironmentReport();
        Assert.Null(r.ClaudeCli.Path);
        Assert.False(r.Ollama.Running);
        Assert.Empty(r.ApiKeyEnvVarsFound);
        Assert.Empty(r.ObsidianVaults);
        Assert.False(r.IsElevated);
    }

    [Fact]
    public void Saved_window_positions_off_screen_are_ignored()
    {
        Assert.False(MainWindow.IsOnScreen(-100000, -100000, 900, 700));
        Assert.False(MainWindow.IsOnScreen(double.NaN, 0, 900, 700));
        Assert.True(MainWindow.IsOnScreen(SystemParameters.VirtualScreenLeft + 10, SystemParameters.VirtualScreenTop + 10, 900, 700));
    }

    // =====================================================================================
    // helpers and fakes
    // =====================================================================================

    private SettingsStore NewStore(Action<AppSettings>? configure = null)
    {
        Directory.CreateDirectory(_dir);
        var store = new SettingsStore(Path.Combine(_dir, $"settings-{Guid.NewGuid():N}.json"));
        if (configure != null) store.Update(configure);
        return store;
    }

    private (MainViewModel Vm, FakeSession Session, QueueDispatcher Ui) CreateViewModel(SettingsStore? store = null, FakeCatalog? catalog = null)
    {
        var session = new FakeSession();
        var ui = new QueueDispatcher();
        var vm = new MainViewModel(store ?? NewStore(), session, catalog ?? new FakeCatalog(), new FakeDetector(), ui);
        return (vm, session, ui);
    }

    private static TurnResult Result(TurnOutcome outcome) =>
        new(outcome, "done", new TurnStats(100, 20, 1, null, TimeSpan.FromSeconds(2), "haiku"), null);

    private static ToolImage FakeImage() => ToolImage.FromBytes(new byte[] { 1, 2, 3, 4 }, "image/png", 1280, 800);

    private static ToolImage PngImage(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = 200; pixels[i + 1] = 120; pixels[i + 2] = 60; pixels[i + 3] = 255; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ToolImage.FromBytes(ms.ToArray(), "image/png", width, height);
    }

    private static void MeasureAndArrange(FrameworkElement element, double width, double height)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private sealed class QueueDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public void Post(Action action) => _queue.Enqueue(action);

        public int RunAll()
        {
            int n = 0;
            while (_queue.TryDequeue(out var action))
            {
                action();
                n++;
            }
            return n;
        }
    }

    private sealed class FakeSession : IAgentSession
    {
        public event Action<AgentEvent>? EventRaised;
        public event Action<AgentState>? StateChanged;
        public AgentState State { get; private set; } = AgentState.Idle;
        public string ActiveDescription => "fake";
        public List<string> Sent { get; } = new();
        public int StopCalls { get; private set; }
        public int NewConversationCalls { get; private set; }
        public int ReloadCalls { get; private set; }
        public CancellationToken LastToken { get; private set; }
        public Exception? NewConversationError { get; set; }
        public Func<string, CancellationToken, Task<TurnResult>>? OnSend { get; set; }

        public Task<TurnResult> SendAsync(string message, CancellationToken ct = default)
        {
            Sent.Add(message);
            LastToken = ct;
            return OnSend?.Invoke(message, ct) ?? Task.FromResult(Result(TurnOutcome.Completed));
        }

        public Task StopAsync(string reason = "Stopped by user")
        {
            StopCalls++;
            return Task.CompletedTask;
        }

        public Task NewConversationAsync()
        {
            NewConversationCalls++;
            return NewConversationError != null ? Task.FromException(NewConversationError) : Task.CompletedTask;
        }

        public Task ReloadSettingsAsync()
        {
            ReloadCalls++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Raise(AgentEvent e) => EventRaised?.Invoke(e);

        public void SetState(AgentState state)
        {
            State = state;
            StateChanged?.Invoke(state);
        }
    }

    private sealed class FakeCatalog : IModelCatalog
    {
        public ModelListResult Result { get; set; } = new(Array.Empty<ModelInfo>(), null);
        public string? LastApiKey { get; private set; }

        public Task<ModelListResult> ListModelsAsync(ProviderProfile profile, string apiKey, CancellationToken ct)
        {
            LastApiKey = apiKey;
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeDetector : IEnvironmentDetector
    {
        public Task<EnvironmentReport> DetectAsync(CancellationToken ct) => Task.FromResult(App.EmptyEnvironmentReport());
    }
}

/// <summary>
/// Runs WPF code on one STA thread with a live dispatcher and the DeskPilot App (and its theme) loaded.
/// If another test already created the Application, its dispatcher is reused, since only one can exist.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();

    public static Dispatcher UiDispatcher => EnsureApplication();

    public static void Run(Action action) => Run<object?>(() =>
    {
        action();
        return null;
    });

    public static T Run<T>(Func<T> func)
    {
        var dispatcher = EnsureApplication();
        if (dispatcher.CheckAccess()) return func();

        T result = default!;
        Exception? error = null;
        bool ran = false;
        dispatcher.Invoke(() =>
        {
            ran = true;
            try { result = func(); }
            catch (Exception ex) { error = ex; }
        }, DispatcherPriority.Normal, CancellationToken.None, TimeSpan.FromSeconds(60));
        if (!ran) throw new TimeoutException("The WPF test dispatcher did not run the test code.");
        if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        return result;
    }

    private static Dispatcher EnsureApplication()
    {
        Application? existing;
        lock (Gate)
        {
            existing = Application.Current;
            if (existing == null) return StartApplication();
        }
        // InitializeComponent is idempotent; it makes sure the theme is loaded even if another test created the App.
        existing.Dispatcher.Invoke(() =>
        {
            if (existing is App app) app.InitializeComponent();
        });
        return existing.Dispatcher;
    }

    private static Dispatcher StartApplication()
    {
        {
            Dispatcher? dispatcher = null;
            Exception? startError = null;
            using var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    var app = new App();
                    app.InitializeComponent();
                    dispatcher = Dispatcher.CurrentDispatcher;
                }
                catch (Exception ex)
                {
                    startError = ex;
                }
                finally
                {
                    ready.Set();
                }
                if (startError == null) Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "DeskPilot WPF test thread",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            if (startError != null) ExceptionDispatchInfo.Capture(startError).Throw();
            return dispatcher!;
        }
    }
}

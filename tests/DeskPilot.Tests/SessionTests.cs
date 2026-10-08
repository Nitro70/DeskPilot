using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Agent;
using DeskPilot.Core.Backends.Acp;
using DeskPilot.Core.Backends.Cli;
using DeskPilot.Core.Backends.Http;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;

namespace DeskPilot.Tests;

public sealed class SessionTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);
    private const string ScreenText = "screenshots are 1280x720 pixels and show the primary monitor (2560x1440 physical pixels)";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskpilot-session-" + Guid.NewGuid().ToString("N"));

    public SessionTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // =====================================================================================
    // AgentSession: turns and events
    // =====================================================================================

    [Fact]
    public async Task Turn_raises_events_in_order_and_hands_the_backend_a_full_context()
    {
        await using var h = new Harness(NewStore(), _dir, b => b.OnRun = async (be, turn, ct) =>
        {
            be.Context!.Emit(new AssistantTextEvent("Looking at the screen"));
            var r = await be.Context.Tools.ExecuteAsync("fake_tool", JsonArgs.EmptyObject(), ct);
            Assert.False(r.IsError);
            be.Context.Emit(new AssistantTextEvent("Finished"));
            return Result(TurnOutcome.Completed, "Finished");
        });

        var result = await h.Session.SendAsync("open notepad").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var events = h.Events;
        var user = Assert.IsType<UserMessageEvent>(events[0]);
        Assert.Equal("open notepad", user.Text);
        var done = Assert.IsType<TurnCompletedEvent>(events[^1]);
        Assert.Equal(result, done.Result);

        int starting = events.FindIndex(e => e is StatusEvent s && s.Message.StartsWith("Starting", StringComparison.Ordinal));
        int looking = events.FindIndex(e => e is AssistantTextEvent { Text: "Looking at the screen" });
        int call = events.FindIndex(e => e is ToolCallEvent { ToolName: "fake_tool" });
        int toolResult = events.FindIndex(e => e is ToolResultEvent { ToolName: "fake_tool" });
        int finished = events.FindIndex(e => e is AssistantTextEvent { Text: "Finished" });
        Assert.True(0 < starting && starting < looking && looking < call && call < toolResult && toolResult < finished && finished < events.Count - 1,
            string.Join(" | ", events.Select(e => e.GetType().Name)));
        Assert.Single(events.OfType<UserMessageEvent>());
        Assert.Single(events.OfType<TurnCompletedEvent>());

        Assert.Equal(new[] { AgentState.Starting, AgentState.Running, AgentState.Idle }, h.States);
        Assert.Equal(AgentState.Idle, h.Session.State);

        var backend = Assert.Single(h.Backends);
        var ctx = backend.Context!;
        Assert.Equal(1, backend.StartCount);
        Assert.Contains(ScreenText, ctx.SystemPrompt);
        Assert.Same(h.Session.Tools, ctx.Tools);
        Assert.Same(h.Session.Control, ctx.Control);
        Assert.Equal(_dir, ctx.WorkingDirectory);
        Assert.Equal(ProviderKind.ClaudeCli, ctx.Profile.Kind);
        Assert.Equal("", ctx.ApiKey);
        Assert.NotNull(ctx.Mcp);
        Assert.Equal("DeskPilot-test.exe", ctx.Mcp!.Command);
        Assert.Equal("open notepad", backend.LastTurn!.Text);
    }

    [Fact]
    public async Task Custom_prompt_gets_tools_screen_and_settings_filled_in()
    {
        var store = NewStore(s =>
        {
            s.Prompt.CustomSystemPrompt = "Tools: {{TOOLS}}. Screen: {{SCREEN}}. Steps: {{MAX_STEPS}}.";
            s.Safety.MaxStepsPerTurn = 12;
        });
        await using var h = new Harness(store, _dir);

        await h.Session.SendAsync("hi").WaitAsync(Wait);

        Assert.Equal($"Tools: fake_tool. Screen: {ScreenText}. Steps: 12.\n", h.Backends[0].Context!.SystemPrompt);
    }

    [Fact]
    public async Task Second_turn_reuses_the_running_backend()
    {
        await using var h = new Harness(NewStore(), _dir);

        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("one").WaitAsync(Wait)).Outcome);
        h.ClearEvents();
        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("two").WaitAsync(Wait)).Outcome);

        var backend = Assert.Single(h.Backends);
        Assert.Equal(1, backend.StartCount);
        Assert.Equal(2, backend.RunCount);
        Assert.Equal(0, backend.DisposeCount);
        Assert.Equal(new[] { AgentState.Running, AgentState.Idle }, h.States);
        Assert.DoesNotContain(h.Events, e => e is StatusEvent s && s.Message.StartsWith("Starting", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Busy_session_rejects_a_second_message_without_throwing()
    {
        var started = NewSignal();
        var release = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var h = new Harness(NewStore(), _dir, b => b.OnRun = (_, _, _) => { started.TrySetResult(); return release.Task; });

        var first = h.Session.SendAsync("first");
        await started.Task.WaitAsync(Wait);

        var second = await h.Session.SendAsync("second").WaitAsync(Wait);
        Assert.Equal(TurnOutcome.Failed, second.Outcome);
        Assert.Equal("DeskPilot is already working on a request", second.Error);
        Assert.Single(h.Events.OfType<UserMessageEvent>());
        Assert.Empty(h.Events.OfType<TurnCompletedEvent>());

        release.SetResult(Result(TurnOutcome.Completed, "ok"));
        Assert.Equal(TurnOutcome.Completed, (await first.WaitAsync(Wait)).Outcome);
        Assert.Equal(1, h.Backends[0].RunCount);
    }

    [Fact]
    public async Task Empty_message_is_rejected_without_events()
    {
        await using var h = new Harness(NewStore(), _dir);
        var result = await h.Session.SendAsync("   ").WaitAsync(Wait);
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Empty(h.Events);
        Assert.Empty(h.Backends);
    }

    [Fact]
    public async Task StopAsync_interrupts_the_backend_and_the_turn_ends_cancelled()
    {
        var started = NewSignal();
        await using var h = new Harness(NewStore(), _dir, b => b.OnRun = (_, _, ct) => WaitForCancel(started, ct));

        var send = h.Session.SendAsync("long task");
        await started.Task.WaitAsync(Wait);
        await h.Session.StopAsync("Stopped by user").WaitAsync(Wait);
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        // StopAsync interrupts once itself; the StopRequested handler must not send a second interrupt.
        Assert.Equal(1, h.Backends[0].InterruptCount);
        Assert.Contains(AgentState.Stopping, h.States);
        Assert.Equal(AgentState.Idle, h.States[^1]);
        Assert.Contains(h.Events, e => e is StatusEvent s && s.Message == "Stopping: Stopped by user");
        Assert.Equal("Stopped by user", result.Error);
        Assert.Equal(new[] { AgentState.Starting, AgentState.Running, AgentState.Stopping, AgentState.Idle }, h.States);
        Assert.IsType<TurnCompletedEvent>(h.Events[^1]);
    }

    [Fact]
    public async Task StopAsync_when_idle_does_nothing()
    {
        await using var h = new Harness(NewStore(), _dir);
        await h.Session.SendAsync("hi").WaitAsync(Wait);
        h.ClearEvents();

        await h.Session.StopAsync().WaitAsync(Wait);

        Assert.Equal(0, h.Backends[0].InterruptCount);
        Assert.Empty(h.Events);
        Assert.Empty(h.States);
    }

    [Fact]
    public async Task Failsafe_stop_from_the_tool_layer_interrupts_the_backend()
    {
        var started = NewSignal();
        await using var h = new Harness(NewStore(), _dir, b => b.OnRun = (_, _, ct) => WaitForCancel(started, ct));

        var send = h.Session.SendAsync("task");
        await started.Task.WaitAsync(Wait);
        h.Session.Control.RequestStop("Failsafe: mouse moved to the top-left corner");
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal("Failsafe: mouse moved to the top-left corner", result.Error);
        await WaitUntil(() => h.Backends[0].InterruptCount == 1);
        var events = h.Events;
        int stopping = events.FindIndex(e => e is StatusEvent s && s.Message.Contains("Failsafe", StringComparison.Ordinal));
        Assert.InRange(stopping, 1, events.Count - 2);
        Assert.IsType<TurnCompletedEvent>(events[^1]);
        Assert.Equal(new[] { AgentState.Starting, AgentState.Running, AgentState.Stopping, AgentState.Idle }, h.States);
    }

    [Fact]
    public async Task Step_limit_stops_the_turn_and_reports_StepLimit()
    {
        var store = NewStore(s => s.Safety.MaxStepsPerTurn = 1);
        await using var h = new Harness(store, _dir, b => b.OnRun = async (be, _, ct) =>
        {
            for (int i = 0; i < 20; i++)
            {
                var r = await be.Context!.Tools.ExecuteAsync("fake_tool", JsonArgs.EmptyObject(), CancellationToken.None);
                if (r.Text.StartsWith("STOPPED", StringComparison.Ordinal)) break;
            }
            return Result(TurnOutcome.Cancelled);
        });

        var result = await h.Session.SendAsync("click forever").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.StepLimit, result.Outcome);
        Assert.Contains("Step limit", result.Error);
        await WaitUntil(() => h.Backends[0].InterruptCount >= 1);
        Assert.Equal(1, h.Tools.Calls);
    }

    [Fact]
    public async Task Cancelling_the_callers_token_stops_the_turn()
    {
        var started = NewSignal();
        await using var h = new Harness(NewStore(), _dir, b => b.OnRun = (_, _, ct) => WaitForCancel(started, ct));
        using var cts = new CancellationTokenSource();

        var send = h.Session.SendAsync("task", cts.Token);
        await started.Task.WaitAsync(Wait);
        cts.Cancel();
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        await WaitUntil(() => h.Backends[0].InterruptCount == 1);
        Assert.True(h.Session.Control.IsStopRequested);
    }

    [Fact]
    public async Task Stop_during_a_slow_start_cancels_the_turn_and_drops_the_backend()
    {
        var starting = NewSignal();
        int created = 0;
        await using var h = new Harness(NewStore(), _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1)
                b.OnStart = async (_, ct) => { starting.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        });

        var send = h.Session.SendAsync("task");
        await starting.Task.WaitAsync(Wait);
        await h.Session.StopAsync().WaitAsync(Wait);
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, h.Backends[0].DisposeCount);
        Assert.Equal(0, h.Backends[0].RunCount);

        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("again").WaitAsync(Wait)).Outcome);
        Assert.Equal(2, h.Backends.Count);
    }

    [Fact]
    public async Task Backend_exception_becomes_a_failed_turn_and_the_backend_is_replaced()
    {
        int created = 0;
        await using var h = new Harness(NewStore(), _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1)
                b.OnRun = (_, _, _) => throw new InvalidOperationException("boom");
        });

        var result = await h.Session.SendAsync("task").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("boom", result.Error);
        Assert.Contains(h.Events, e => e is StatusEvent { Level: StatusLevel.Error } s && s.Message.Contains("boom", StringComparison.Ordinal));
        Assert.IsType<TurnCompletedEvent>(h.Events[^1]);
        Assert.Equal(AgentState.Idle, h.Session.State);

        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("again").WaitAsync(Wait)).Outcome);
        Assert.Equal(2, h.Backends.Count);
        Assert.Equal(1, h.Backends[0].DisposeCount);
    }

    [Fact]
    public async Task Subscriber_exceptions_do_not_break_the_turn()
    {
        await using var h = new Harness(NewStore(), _dir);
        h.Session.EventRaised += _ => throw new InvalidOperationException("bad subscriber");
        h.Session.StateChanged += _ => throw new InvalidOperationException("bad subscriber");

        var result = await h.Session.SendAsync("hello").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.IsType<UserMessageEvent>(h.Events[0]);
        Assert.IsType<TurnCompletedEvent>(h.Events[^1]);
        Assert.Equal(AgentState.Idle, h.States[^1]);
    }

    // =====================================================================================
    // AgentSession: configuration changes and restarts
    // =====================================================================================

    [Fact]
    public async Task Changing_the_model_restarts_the_backend_on_the_next_turn()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir);

        await h.Session.SendAsync("one").WaitAsync(Wait);
        store.Update(s => s.ActiveProfile!.Model = "sonnet");
        await h.Session.SendAsync("two").WaitAsync(Wait);

        Assert.Equal(2, h.Backends.Count);
        Assert.Equal(1, h.Backends[0].DisposeCount);
        Assert.Equal("sonnet", h.Backends[1].Context!.Profile.Model);
        Assert.Equal(0, h.Backends[1].DisposeCount);
    }

    [Fact]
    public async Task Prompt_settings_restart_the_backend_but_ui_settings_do_not()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir);

        await h.Session.SendAsync("one").WaitAsync(Wait);
        store.Update(s => { s.Ui.ShowThinking = false; s.Ui.WindowWidth = 1200; s.Safety.Confirm = ConfirmMode.Always; });
        await h.Session.SendAsync("two").WaitAsync(Wait);
        Assert.Single(h.Backends);

        store.Update(s => s.Prompt.UserInstructions = "Always answer in one sentence.");
        await h.Session.SendAsync("three").WaitAsync(Wait);
        Assert.Equal(2, h.Backends.Count);
        Assert.Contains("Always answer in one sentence.", h.Backends[1].Context!.SystemPrompt);
    }

    [Fact]
    public async Task Renaming_the_profile_keeps_the_backend_but_updates_the_description()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir);
        await h.Session.SendAsync("one").WaitAsync(Wait);

        store.Update(s => s.ActiveProfile!.Name = "My Claude");
        await h.Session.ReloadSettingsAsync().WaitAsync(Wait);
        await h.Session.SendAsync("two").WaitAsync(Wait);

        Assert.Single(h.Backends);
        Assert.Equal("My Claude (haiku)", h.Session.ActiveDescription);
    }

    [Fact]
    public async Task ReloadSettings_disposes_an_idle_backend_and_names_the_new_profile()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir);
        await h.Session.SendAsync("one").WaitAsync(Wait);
        h.ClearEvents();

        await h.Session.ReloadSettingsAsync().WaitAsync(Wait);
        Assert.Empty(h.Events);
        Assert.Equal(0, h.Backends[0].DisposeCount);

        store.Update(s =>
        {
            var p = ProviderPresets.CreateProfile(ProviderPresets.OllamaId);
            p.Model = "qwen2.5vl:7b";
            s.Profiles.Add(p);
            s.ActiveProfileId = p.Id;
        });
        await h.Session.ReloadSettingsAsync().WaitAsync(Wait);

        Assert.Equal(1, h.Backends[0].DisposeCount);
        var status = Assert.Single(h.Events.OfType<StatusEvent>());
        Assert.Equal(StatusLevel.Info, status.Level);
        Assert.Contains("Ollama (local) (qwen2.5vl:7b)", status.Message);

        // Restarts lazily with the new profile.
        Assert.Single(h.Backends);
        await h.Session.SendAsync("two").WaitAsync(Wait);
        Assert.Equal(2, h.Backends.Count);
        Assert.Equal(ProviderKind.Ollama, h.Backends[1].Profile.Kind);
    }

    [Fact]
    public async Task ReloadSettings_while_busy_leaves_the_running_backend_alone()
    {
        var store = NewStore();
        var started = NewSignal();
        var release = new TaskCompletionSource<TurnResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        int created = 0;
        await using var h = new Harness(store, _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1)
                b.OnRun = (_, _, _) => { started.TrySetResult(); return release.Task; };
        });

        var first = h.Session.SendAsync("one");
        await started.Task.WaitAsync(Wait);
        store.Update(s => s.ActiveProfile!.Model = "opus");
        await h.Session.ReloadSettingsAsync().WaitAsync(Wait);
        Assert.Equal(0, h.Backends[0].DisposeCount);

        release.SetResult(Result(TurnOutcome.Completed, "ok"));
        await first.WaitAsync(Wait);
        await h.Session.SendAsync("two").WaitAsync(Wait);

        Assert.Equal(2, h.Backends.Count);
        Assert.Equal("opus", h.Backends[1].Context!.Profile.Model);
    }

    [Fact]
    public async Task Missing_api_key_fails_the_turn_with_a_readable_message()
    {
        var envVar = "DESKPILOT_TEST_NO_KEY_" + Guid.NewGuid().ToString("N");
        var store = NewStore(s =>
        {
            var p = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
            p.ApiKeyEnvVar = envVar;
            s.Profiles.Add(p);
            s.ActiveProfileId = p.Id;
        });
        await using var h = new Harness(store, _dir);

        var result = await h.Session.SendAsync("hello").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("Add an API key for OpenAI API in Settings", result.Error);
        Assert.Contains(envVar, result.Error);
        Assert.Empty(h.Backends);

        var events = h.Events;
        Assert.Equal(3, events.Count);
        Assert.IsType<UserMessageEvent>(events[0]);
        var status = Assert.IsType<StatusEvent>(events[1]);
        Assert.Equal(StatusLevel.Error, status.Level);
        Assert.Equal(result.Error, status.Message);
        Assert.Equal(TurnOutcome.Failed, Assert.IsType<TurnCompletedEvent>(events[2]).Result.Outcome);
        Assert.Equal(AgentState.Idle, h.Session.State);
        Assert.DoesNotContain(AgentState.Running, h.States);
    }

    [Fact]
    public async Task Stored_api_key_reaches_an_http_backend_that_gets_no_mcp_server()
    {
        const string key = "sk-test-session-key-0001";
        var store = NewStore(s =>
        {
            var p = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
            p.ApiKeyEnvVar = "DESKPILOT_TEST_NO_KEY_" + Guid.NewGuid().ToString("N");
            p.ApiKeyProtected = SecretProtector.Protect(key);
            s.Profiles.Add(p);
            s.ActiveProfileId = p.Id;
        });
        await using var h = new Harness(store, _dir, usesMcp: false);

        var result = await h.Session.SendAsync("hello").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var ctx = h.Backends[0].Context!;
        Assert.Equal(key, ctx.ApiKey);
        Assert.Null(ctx.Mcp);
        Assert.Equal(0, h.McpCreated);
        Assert.DoesNotContain(h.Events, e => e is StatusEvent s && s.Message.Contains(key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Start_failure_reports_an_error_and_the_next_turn_tries_again()
    {
        int created = 0;
        await using var h = new Harness(NewStore(), _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1)
                b.OnStart = (_, _) => throw new InvalidOperationException("claude was not found");
        });

        var result = await h.Session.SendAsync("hello").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("claude was not found", result.Error);
        Assert.Contains(h.Events, e => e is StatusEvent { Level: StatusLevel.Error } s && s.Message.Contains("claude was not found", StringComparison.Ordinal));
        Assert.Equal(TurnOutcome.Failed, Assert.IsType<TurnCompletedEvent>(h.Events[^1]).Result.Outcome);
        Assert.Equal(1, h.Backends[0].DisposeCount);
        Assert.Equal(0, h.Backends[0].RunCount);
        Assert.Equal(AgentState.Idle, h.Session.State);

        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("again").WaitAsync(Wait)).Outcome);
        Assert.Equal(2, h.Backends.Count);
    }

    [Fact]
    public async Task Backend_factory_failure_is_a_failed_turn()
    {
        var store = NewStore();
        await using var session = new AgentSession(store, FakeDesktop.Create(), null, null, "DeskPilot-test.exe",
            _ => throw new InvalidOperationException("no backend for you"), Harness.Hooks(_dir, new FakeToolHost(), null));

        var result = await session.SendAsync("hello").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("no backend for you", result.Error);
        Assert.Equal(AgentState.Idle, session.State);
    }

    [Fact]
    public async Task Mcp_server_is_created_once_per_session_and_only_for_mcp_backends()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir, usesMcp: true);

        await h.Session.SendAsync("one").WaitAsync(Wait);
        store.Update(s => s.ActiveProfile!.Model = "sonnet");
        await h.Session.SendAsync("two").WaitAsync(Wait);

        Assert.Equal(2, h.Backends.Count);
        Assert.Equal(1, h.McpCreated);
        Assert.Equal(2, h.Mcp!.StartCount);
        Assert.Same(h.Session.Tools, h.Mcp.Tools);
        Assert.Equal("DeskPilot-test.exe", h.Mcp.LastBridgePath);
        Assert.All(h.Backends, b => Assert.Equal(new[] { "--mcp-bridge", "pipe-test", "token-test" }, b.Context!.Mcp!.Args));

        await using var http = new Harness(NewStore(), _dir, usesMcp: false);
        await http.Session.SendAsync("one").WaitAsync(Wait);
        Assert.Equal(0, http.McpCreated);
        Assert.Null(http.Backends[0].Context!.Mcp);
    }

    [Fact]
    public async Task Mcp_start_failure_fails_the_turn_and_a_fresh_server_is_built_next_time()
    {
        await using var h = new Harness(NewStore(), _dir, usesMcp: true) { McpStartThrows = true };

        var result = await h.Session.SendAsync("one").WaitAsync(Wait);
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("pipe busy", result.Error);
        Assert.Equal(1, h.Backends[0].DisposeCount);
        await WaitUntil(() => h.Mcp!.DisposeCount == 1);

        h.McpStartThrows = false;
        Assert.Equal(TurnOutcome.Completed, (await h.Session.SendAsync("two").WaitAsync(Wait)).Outcome);
        Assert.Equal(2, h.McpCreated);
    }

    // =====================================================================================
    // AgentSession: new conversation, dispose, construction
    // =====================================================================================

    [Fact]
    public async Task NewConversation_resets_the_backend_and_announces_it()
    {
        await using var h = new Harness(NewStore(), _dir);
        await h.Session.NewConversationAsync().WaitAsync(Wait);
        Assert.Contains(h.Events, e => e is StatusEvent { Message: "Started a new conversation", Level: StatusLevel.Info });

        await h.Session.SendAsync("one").WaitAsync(Wait);
        h.ClearEvents();
        await h.Session.NewConversationAsync().WaitAsync(Wait);

        Assert.Equal(1, h.Backends[0].ResetCount);
        Assert.Equal(0, h.Backends[0].DisposeCount);
        var status = Assert.Single(h.Events.OfType<StatusEvent>());
        Assert.Equal("Started a new conversation", status.Message);
    }

    [Fact]
    public async Task NewConversation_while_running_stops_the_turn_first()
    {
        var started = NewSignal();
        int created = 0;
        await using var h = new Harness(NewStore(), _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1) b.OnRun = (_, _, ct) => WaitForCancel(started, ct);
        });

        var send = h.Session.SendAsync("long");
        await started.Task.WaitAsync(Wait);
        await h.Session.NewConversationAsync().WaitAsync(Wait);
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, h.Backends[0].InterruptCount);
        Assert.Equal(1, h.Backends[0].ResetCount);
        var events = h.Events;
        int completed = events.FindIndex(e => e is TurnCompletedEvent);
        int announced = events.FindIndex(e => e is StatusEvent { Message: "Started a new conversation" });
        Assert.True(completed >= 0 && announced > completed);
    }

    [Fact]
    public async Task NewConversation_restarts_the_backend_when_reset_fails()
    {
        int created = 0;
        await using var h = new Harness(NewStore(), _dir, b =>
        {
            if (Interlocked.Increment(ref created) == 1) b.ResetThrows = new IOException("process gone");
        });
        await h.Session.SendAsync("one").WaitAsync(Wait);

        await h.Session.NewConversationAsync().WaitAsync(Wait);
        Assert.Equal(1, h.Backends[0].DisposeCount);
        Assert.Contains(h.Events, e => e is StatusEvent { Message: "Started a new conversation" });

        await h.Session.SendAsync("two").WaitAsync(Wait);
        Assert.Equal(2, h.Backends.Count);
    }

    [Fact]
    public async Task Dispose_is_idempotent_and_releases_backend_and_mcp_server()
    {
        var h = new Harness(NewStore(), _dir, usesMcp: true);
        await h.Session.SendAsync("one").WaitAsync(Wait);

        await h.Session.DisposeAsync().AsTask().WaitAsync(Wait);
        await h.Session.DisposeAsync().AsTask().WaitAsync(Wait);

        Assert.Equal(1, h.Backends[0].DisposeCount);
        Assert.Equal(1, h.Mcp!.DisposeCount);
        var after = await h.Session.SendAsync("two").WaitAsync(Wait);
        Assert.Equal(TurnOutcome.Failed, after.Outcome);
        Assert.Single(h.Backends);
    }

    [Fact]
    public async Task Dispose_while_running_stops_the_turn()
    {
        var started = NewSignal();
        var h = new Harness(NewStore(), _dir, b => b.OnRun = (_, _, ct) => WaitForCancel(started, ct));

        var send = h.Session.SendAsync("long");
        await started.Task.WaitAsync(Wait);
        await h.Session.DisposeAsync().AsTask().WaitAsync(Wait);
        var result = await send.WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal(1, h.Backends[0].InterruptCount);
        Assert.Equal(1, h.Backends[0].DisposeCount);
    }

    [Fact]
    public async Task ActiveDescription_names_profile_and_model()
    {
        var store = NewStore();
        await using var h = new Harness(store, _dir);
        Assert.Equal("Claude (your subscription via Claude Code) (haiku)", h.Session.ActiveDescription);

        store.Update(s => { s.ActiveProfile!.Name = "Local"; s.ActiveProfile!.Model = " "; });
        Assert.Equal("Local (default model)", h.Session.ActiveDescription);
    }

    [Fact]
    public async Task Real_tool_hosts_that_fail_do_not_break_construction_or_a_turn()
    {
        // No Tools/ScreenDescription/VaultAvailable hooks: the session builds the real SafetyGuard,
        // ComputerToolHost and VaultToolHost over fake desktop services and must cope whatever they do.
        var store = NewStore();
        FakeBackend? backend = null;
        var hooks = new AgentSessionHooks { WorkingDirectory = _dir, McpHostFactory = t => new FakeMcpHost(t) };
        await using var session = new AgentSession(store, FakeDesktop.Create(), null, null, "DeskPilot-test.exe",
            p => backend = new FakeBackend(p, usesMcp: false), hooks);

        Assert.Equal(AgentState.Idle, session.State);
        var result = await session.SendAsync("what time is it?").WaitAsync(Wait);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(backend!.Context!.SystemPrompt));
        Assert.Contains("screenshots", backend.Context.SystemPrompt);
    }

    [Fact]
    public async Task Public_constructor_builds_without_touching_the_desktop()
    {
        var desktop = FakeDesktop.Create();
        await using var session = new AgentSession(NewStore(), desktop, null, null, "DeskPilot-test.exe", p => new FakeBackend(p, false));
        Assert.Equal(AgentState.Idle, session.State);
        Assert.Contains("haiku", session.ActiveDescription);
        Assert.Equal(0, ((FakeInput)desktop.Input).Calls);
    }

    [Fact]
    public async Task Default_backend_factory_maps_provider_kinds()
    {
        static ProviderProfile P(ProviderKind kind) => new() { Kind = kind };
        var made = new List<IAgentBackend>
        {
            AgentSession.CreateDefaultBackend(P(ProviderKind.ClaudeCli)),
            AgentSession.CreateDefaultBackend(P(ProviderKind.AcpAgent)),
            AgentSession.CreateDefaultBackend(P(ProviderKind.AnthropicApi)),
            AgentSession.CreateDefaultBackend(P(ProviderKind.OpenAiCompatible)),
            AgentSession.CreateDefaultBackend(P(ProviderKind.Ollama)),
        };
        try
        {
            Assert.IsType<ClaudeCliBackend>(made[0]);
            Assert.IsType<AcpBackend>(made[1]);
            Assert.IsType<HttpAgentBackend>(made[2]);
            Assert.IsType<HttpAgentBackend>(made[3]);
            Assert.IsType<HttpAgentBackend>(made[4]);
            Assert.True(made[0].UsesMcp);
            Assert.True(made[1].UsesMcp);
            Assert.False(made[2].UsesMcp);
        }
        finally
        {
            foreach (var b in made)
            {
                try { await b.DisposeAsync(); }
                catch (NotImplementedException) { } // module stubs in isolated builds
            }
        }
    }

    // =====================================================================================
    // SessionConfig: fingerprint, API key rules, descriptions
    // =====================================================================================

    [Fact]
    public void Fingerprint_tracks_backend_and_prompt_settings_only()
    {
        var baseline = StoreSettings();
        string Fp(Action<AppSettings>? change = null, bool vault = false)
        {
            var s = SettingsStore.Clone(baseline);
            change?.Invoke(s);
            return SessionConfig.ComputeFingerprint(s, s.ActiveProfile, vault);
        }

        var original = Fp();
        Assert.Equal(original, Fp());
        Assert.Equal(original, Fp(s => s.ActiveProfile!.Name = "Renamed"));
        Assert.Equal(original, Fp(s => { s.Ui.ShowOverlay = false; s.Ui.StopHotkey = "Ctrl+Alt+Q"; }));
        Assert.Equal(original, Fp(s => { s.Safety.Confirm = ConfirmMode.Always; s.Safety.BlockedProcesses.Add("keepass"); }));
        Assert.Equal(original, Fp(s => s.Screen.JpegQuality = 50));

        var changes = new Action<AppSettings>[]
        {
            s => s.ActiveProfile!.Model = "sonnet",
            s => s.ActiveProfile!.Thinking = ThinkingMode.Off,
            s => s.ActiveProfile!.Effort = "high",
            s => s.ActiveProfile!.CliPath = "claude2",
            s => s.ActiveProfile!.ExtraEnv["X"] = "1",
            s => s.ActiveProfile!.ApiKeyProtected = "abc",
            s => s.ActiveProfile!.SupportsVision = false,
            s => s.Vault.Path = "notes",
            s => s.Vault.AllowWrites = true,
            s => s.Safety.AllowAdmin = true,
            s => s.Safety.DryRun = true,
            s => s.Safety.MaxStepsPerTurn = 5,
            s => s.Safety.AllowShellCommands = true,
            s => s.Safety.AllowAppLaunch = false,
            s => s.Safety.AllowClipboard = false,
            s => s.Screen.Coordinates = CoordinateMode.Normalized1000,
            s => s.Screen.MaxImageWidth = 1024,
            s => s.Screen.MaxImageHeight = 600,
            s => s.Screen.Monitor = MonitorSelection.AllMonitors,
            s => s.Prompt.CustomSystemPrompt = "custom",
            s => s.Prompt.UserInstructions = "be brief",
            s =>
            {
                var p = ProviderPresets.CreateProfile(ProviderPresets.OllamaId);
                s.Profiles.Add(p);
                s.ActiveProfileId = p.Id;
            },
        };
        foreach (var change in changes) Assert.NotEqual(original, Fp(change));
        Assert.NotEqual(original, Fp(vault: true));
    }

    [Theory]
    [InlineData(ProviderPresets.ClaudeSubscriptionId, false)]
    [InlineData(ProviderPresets.GeminiCliId, false)]
    [InlineData(ProviderPresets.OllamaId, false)]
    [InlineData(ProviderPresets.LmStudioId, false)]
    [InlineData(ProviderPresets.LocalOpenAiId, false)]
    [InlineData(ProviderPresets.AnthropicApiId, true)]
    [InlineData(ProviderPresets.OpenAiId, true)]
    [InlineData(ProviderPresets.OpenRouterId, true)]
    [InlineData(ProviderPresets.AzureOpenAiId, true)]
    public void RequiresApiKey_follows_the_preset(string presetId, bool expected)
    {
        Assert.Equal(expected, SessionConfig.RequiresApiKey(ProviderPresets.CreateProfile(presetId)));
    }

    [Fact]
    public void RequiresApiKey_handles_custom_profiles_and_auth_headers()
    {
        var custom = new ProviderProfile { Kind = ProviderKind.OpenAiCompatible, PresetId = "", BaseUrl = "http://127.0.0.1:9000/v1" };
        Assert.False(SessionConfig.RequiresApiKey(custom));
        custom.ApiKeyEnvVar = "MY_KEY";
        Assert.True(SessionConfig.RequiresApiKey(custom));

        var azure = ProviderPresets.CreateProfile(ProviderPresets.AzureOpenAiId);
        azure.ExtraHeaders["api-key"] = "from-header";
        Assert.False(SessionConfig.RequiresApiKey(azure));

        Assert.Equal("No API key found. Add an API key for X in Settings.",
            SessionConfig.MissingKeyMessage(new ProviderProfile { Name = "X", ApiKeyEnvVar = "" }));
    }

    [Fact]
    public void DescribeScreen_names_the_captured_monitor()
    {
        var primary = new MonitorInfo(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 2560, 1440), new ScreenRect(0, 0, 2560, 1400), true, 1.5);
        var second = new MonitorInfo(1, @"\\.\DISPLAY2", new ScreenRect(2560, 0, 1920, 1080), new ScreenRect(2560, 0, 1920, 1040), false, 1.0);

        var onlyPrimary = new CoordinateMapper(primary.Bounds, 1280, 720, CoordinateMode.ScreenshotPixels);
        Assert.Equal(ScreenText, SessionConfig.DescribeScreen(onlyPrimary, new[] { primary }, new ScreenSettings()));

        var secondMapper = new CoordinateMapper(second.Bounds, 1280, 720, CoordinateMode.ScreenshotPixels);
        var text = SessionConfig.DescribeScreen(secondMapper, new[] { primary, second }, new ScreenSettings { Monitor = MonitorSelection.Specific, MonitorIndex = 1 });
        Assert.Contains("show monitor 2 (1920x1080 physical pixels)", text);
        Assert.Contains("2 monitors", text);

        var all = new CoordinateMapper(new ScreenRect(0, 0, 4480, 1440), 1280, 411, CoordinateMode.ScreenshotPixels);
        Assert.Contains("all 2 monitors", SessionConfig.DescribeScreen(all, new[] { primary, second }, new ScreenSettings { Monitor = MonitorSelection.AllMonitors }));

        Assert.Contains("1280x800", SessionConfig.FallbackScreenDescription(new ScreenSettings()));
    }

    // =====================================================================================
    // ProfileAutoConfig
    // =====================================================================================

    [Fact]
    public void Ollama_profile_prefers_a_vision_model()
    {
        var s = StoreSettings();
        var report = Report(ollama: Server("Ollama", "http://127.0.0.1:11434", "llama3.1:8b", "nomic-embed-text:latest", "qwen2.5vl:7b"));

        Assert.True(ProfileAutoConfig.ApplyDetected(s, report));

        var p = Assert.Single(s.Profiles, x => x.PresetId == ProviderPresets.OllamaId);
        Assert.Equal(ProviderKind.Ollama, p.Kind);
        Assert.Equal("qwen2.5vl:7b", p.Model);
        Assert.True(p.SupportsVision);
    }

    [Fact]
    public void Ollama_profile_falls_back_to_the_first_usable_model()
    {
        var s = StoreSettings();
        ProfileAutoConfig.ApplyDetected(s, Report(ollama: Server("Ollama", "http://127.0.0.1:11434", "nomic-embed-text:latest", "llama3.1:8b", "mistral:7b")));
        Assert.Equal("llama3.1:8b", s.Profiles.Single(x => x.PresetId == ProviderPresets.OllamaId).Model);

        var s2 = StoreSettings();
        ProfileAutoConfig.ApplyDetected(s2, Report(ollama: Server("Ollama", "http://127.0.0.1:11434")));
        Assert.Equal(ProviderPresets.Find(ProviderPresets.OllamaId)!.DefaultModel, s2.Profiles.Single(x => x.PresetId == ProviderPresets.OllamaId).Model);
    }

    [Theory]
    [InlineData("qwen2.5vl:7b", true)]
    [InlineData("qwen3-vl:8b", true)]
    [InlineData("Qwen/Qwen2.5-VL-72B-Instruct", true)]
    [InlineData("qwen2-vl:7b", true)]
    [InlineData("llava:13b", true)]
    [InlineData("gemma3:12b", true)]
    [InlineData("llama3.2-vision:11b", true)]
    [InlineData("llama4:scout", true)]
    [InlineData("minicpm-v:8b", true)]
    [InlineData("mistral-small3.2:24b", true)]
    [InlineData("granite3.2-vision:2b", true)]
    [InlineData("moondream:latest", true)]
    [InlineData("pixtral-12b", true)]
    [InlineData("internvl3:8b", true)]
    [InlineData("llama3.1:8b", false)]
    [InlineData("deepseek-r1:14b", false)]
    [InlineData("qwen2.5:14b", false)]
    [InlineData("nomic-embed-text", false)]
    [InlineData("", false)]
    public void Vision_model_names_are_recognized(string name, bool expected)
    {
        Assert.Equal(expected, ProfileAutoConfig.LooksLikeVisionModel(name));
    }

    [Fact]
    public void Lm_studio_profile_uses_its_models()
    {
        var s = StoreSettings();
        ProfileAutoConfig.ApplyDetected(s, Report(lmStudio: Server("LM Studio", "http://127.0.0.1:1234/v1", "text-embedding-nomic-embed-text-v1.5", "google/gemma-3-12b")));
        var p = s.Profiles.Single(x => x.PresetId == ProviderPresets.LmStudioId);
        Assert.Equal("google/gemma-3-12b", p.Model);
        Assert.Equal(ProviderKind.OpenAiCompatible, p.Kind);
    }

    [Fact]
    public void Api_key_variables_add_matching_presets_but_not_azure()
    {
        var s = StoreSettings();
        var report = Report(keys: new[] { "ANTHROPIC_API_KEY", "OPENAI_API_KEY", "GOOGLE_API_KEY", "AZURE_OPENAI_API_KEY", "DEEPSEEK_API_KEY" });

        Assert.True(ProfileAutoConfig.ApplyDetected(s, report));

        Assert.Equal("ANTHROPIC_API_KEY", s.Profiles.Single(p => p.PresetId == ProviderPresets.AnthropicApiId).ApiKeyEnvVar);
        Assert.Equal("OPENAI_API_KEY", s.Profiles.Single(p => p.PresetId == ProviderPresets.OpenAiId).ApiKeyEnvVar);
        Assert.Equal("GOOGLE_API_KEY", s.Profiles.Single(p => p.PresetId == ProviderPresets.GeminiApiId).ApiKeyEnvVar);
        Assert.False(s.Profiles.Single(p => p.PresetId == ProviderPresets.DeepSeekId).SupportsVision);
        Assert.DoesNotContain(s.Profiles, p => p.PresetId == ProviderPresets.AzureOpenAiId);
        Assert.Equal(5, s.Profiles.Count);
    }

    [Fact]
    public void Gemini_key_variable_wins_over_google_key_variable()
    {
        var s = StoreSettings();
        ProfileAutoConfig.ApplyDetected(s, Report(keys: new[] { "GOOGLE_API_KEY", "GEMINI_API_KEY" }));
        var p = Assert.Single(s.Profiles, x => x.PresetId == ProviderPresets.GeminiApiId);
        Assert.Equal("GEMINI_API_KEY", p.ApiKeyEnvVar);
    }

    [Fact]
    public void Applying_twice_never_adds_duplicates()
    {
        var s = StoreSettings();
        var report = Report(
            ollama: Server("Ollama", "http://127.0.0.1:11434", "llava:7b"),
            lmStudio: Server("LM Studio", "http://127.0.0.1:1234/v1", "some-model"),
            keys: new[] { "OPENAI_API_KEY", "GROQ_API_KEY" },
            geminiCli: true);

        Assert.True(ProfileAutoConfig.ApplyDetected(s, report));
        var count = s.Profiles.Count;
        Assert.False(ProfileAutoConfig.ApplyDetected(s, report));
        Assert.Equal(count, s.Profiles.Count);
        Assert.Equal(s.Profiles.Count, s.Profiles.Select(p => p.PresetId).Distinct().Count());
    }

    [Fact]
    public void Existing_profiles_for_the_same_endpoint_count_as_configured()
    {
        var s = StoreSettings();
        s.Profiles.Add(new ProviderProfile { Name = "My Ollama", Kind = ProviderKind.Ollama, PresetId = "", BaseUrl = "http://localhost:11434/" });
        s.Profiles.Add(ProviderPresets.CreateProfile(ProviderPresets.OpenAiId));

        var changed = ProfileAutoConfig.ApplyDetected(s, Report(ollama: Server("Ollama", "http://127.0.0.1:11434", "llava:7b"), keys: new[] { "OPENAI_API_KEY" }));

        Assert.False(changed);
        Assert.Equal(3, s.Profiles.Count);
    }

    [Fact]
    public void Claude_subscription_stays_active_when_its_cli_is_found()
    {
        var s = StoreSettings();
        var claudeId = s.ActiveProfileId;
        Assert.True(ProfileAutoConfig.ApplyDetected(s, Report(claude: true, ollama: Server("Ollama", "http://127.0.0.1:11434", "llava:7b"), keys: new[] { "OPENAI_API_KEY" })));
        Assert.Equal(claudeId, s.ActiveProfileId);
    }

    [Fact]
    public void Active_profile_switches_to_the_first_added_one_when_claude_is_missing()
    {
        var s = StoreSettings();
        Assert.True(ProfileAutoConfig.ApplyDetected(s, Report(claude: false, ollama: Server("Ollama", "http://127.0.0.1:11434", "llava:7b"), keys: new[] { "OPENAI_API_KEY" })));
        Assert.Equal(ProviderPresets.OllamaId, s.ActiveProfile!.PresetId);

        // Nothing new to add: no switch and no change.
        var s2 = StoreSettings();
        var claudeId = s2.ActiveProfileId;
        Assert.False(ProfileAutoConfig.ApplyDetected(s2, Report(claude: false)));
        Assert.Equal(claudeId, s2.ActiveProfileId);
    }

    [Fact]
    public void Active_profile_is_kept_when_it_is_not_the_claude_cli()
    {
        var s = StoreSettings();
        var openai = ProviderPresets.CreateProfile(ProviderPresets.OpenAiId);
        s.Profiles.Add(openai);
        s.ActiveProfileId = openai.Id;

        Assert.True(ProfileAutoConfig.ApplyDetected(s, Report(claude: false, keys: new[] { "ANTHROPIC_API_KEY" })));
        Assert.Equal(openai.Id, s.ActiveProfileId);
    }

    [Fact]
    public void Gemini_cli_adds_an_acp_profile_and_nothing_detected_changes_nothing()
    {
        var s = StoreSettings();
        Assert.True(ProfileAutoConfig.ApplyDetected(s, Report(geminiCli: true)));
        var p = Assert.Single(s.Profiles, x => x.PresetId == ProviderPresets.GeminiCliId);
        Assert.Equal(ProviderKind.AcpAgent, p.Kind);
        Assert.Equal("--acp", p.ExtraCliArgs);

        var empty = StoreSettings();
        Assert.False(ProfileAutoConfig.ApplyDetected(empty, Report()));
        Assert.Single(empty.Profiles);
    }

    // =====================================================================================
    // EnvironmentDetector
    // =====================================================================================

    [Fact]
    public async Task Detector_reports_every_probe_with_fakes()
    {
        var handler = new FakeHttpHandler((req, _) =>
        {
            var url = req.RequestUri!.ToString();
            if (url == "http://127.0.0.1:11434/api/tags")
                return Json("""{"models":[{"name":"llama3.1:8b","details":{"families":["llama"]}},{"name":"custom-mm:latest","details":{"families":["llama","clip"]}}]}""");
            if (url == "http://127.0.0.1:1234/v1/models")
                return Json("""{"object":"list","data":[{"id":"google/gemma-3-12b","object":"model"}]}""");
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var screen = new FakeScreen();
        var windows = new FakeWindows { Elevated = true };
        var ran = new ConcurrentBag<string>();
        var detector = new EnvironmentDetector(screen, windows, new HttpClient(handler))
        {
            ClaudeProbe = (_, _) => throw new NotImplementedException("stub"),
            FindExecutable = (cmd, _) => cmd switch { "claude" => "X:/tools/claude.exe", "gemini" => "X:/npm/gemini.cmd", _ => null },
            RunTool = (path, args, _, _) =>
            {
                ran.Add(path + " " + args);
                return Task.FromResult<string?>(path.Contains("claude") ? "2.1.293 (Claude Code)\n" : "\u001b[32m0.9.1\u001b[0m\r\n");
            },
            IsEnvVarSet = name => name is "OPENAI_API_KEY" or "GOOGLE_API_KEY",
            FindVaults = () => new[] { "X:/notes" },
        };

        var report = await detector.DetectAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("X:/tools/claude.exe", report.ClaudeCli.Path);
        Assert.Equal("2.1.293", report.ClaudeCli.Version);
        Assert.Equal("X:/npm/gemini.cmd", report.GeminiCli.Path);
        Assert.Equal("0.9.1", report.GeminiCli.Version);
        Assert.Null(report.CodexCli.Path);
        Assert.Equal("Not found", report.CodexCli.Detail);
        Assert.Contains("X:/tools/claude.exe --version", ran);

        Assert.True(report.Ollama.Running);
        Assert.Equal("http://127.0.0.1:11434", report.Ollama.BaseUrl);
        Assert.Equal(new[] { "llama3.1:8b", "custom-mm:latest" }, report.Ollama.Models.Select(m => m.Id));
        Assert.Null(report.Ollama.Models[0].SupportsVision);
        Assert.True(report.Ollama.Models[1].SupportsVision);
        Assert.True(report.LmStudio.Running);
        Assert.Equal("google/gemma-3-12b", Assert.Single(report.LmStudio.Models).Id);

        Assert.Equal(new[] { "OPENAI_API_KEY", "GOOGLE_API_KEY" }, report.ApiKeyEnvVarsFound);
        Assert.Equal(new[] { "X:/notes" }, report.ObsidianVaults);
        Assert.Single(report.Monitors);
        Assert.True(report.IsElevated);
    }

    [Fact]
    public async Task Detector_reports_stopped_servers_and_never_throws()
    {
        var detector = new EnvironmentDetector(null, null, new HttpClient(Refusing()))
        {
            ClaudeProbe = (_, _) => Task.FromResult(new CliToolStatus("Claude Code", "X:/claude.exe", "2.1.293", true, null)),
            FindExecutable = (_, _) => throw new UnauthorizedAccessException("no access"),
            RunTool = (_, _, _, _) => throw new InvalidOperationException("should not run"),
            IsEnvVarSet = name => name == "GROQ_API_KEY" ? true : throw new InvalidOperationException("unreadable"),
            FindVaults = () => throw new NotImplementedException("stub"),
            IsProcessElevated = () => throw new InvalidOperationException("no token"),
        };

        var report = await detector.DetectAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.True(report.ClaudeCli.LoggedIn);
        Assert.Equal("2.1.293", report.ClaudeCli.Version);
        Assert.Null(report.GeminiCli.Path);
        Assert.Null(report.CodexCli.Path);
        Assert.False(report.Ollama.Running);
        Assert.Empty(report.Ollama.Models);
        Assert.False(report.LmStudio.Running);
        Assert.Equal(new[] { "GROQ_API_KEY" }, report.ApiKeyEnvVarsFound);
        Assert.Empty(report.ObsidianVaults);
        Assert.Empty(report.Monitors);
        Assert.False(report.IsElevated);
    }

    [Fact]
    public async Task Detector_gives_up_on_slow_servers_and_silent_tools_quickly()
    {
        var handler = new FakeHttpHandler(async (_, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var detector = new EnvironmentDetector(null, null, new HttpClient(handler))
        {
            HttpTimeout = TimeSpan.FromMilliseconds(200),
            ClaudeProbeTimeout = TimeSpan.FromMilliseconds(200),
            ClaudeProbe = async (_, ct) => { await Task.Delay(TimeSpan.FromSeconds(30), ct); return null!; },
            FindExecutable = (cmd, _) => "X:/bin/" + cmd + ".exe",
            RunTool = (_, _, _, _) => Task.FromResult<string?>(null),
            IsEnvVarSet = _ => false,
            FindVaults = Array.Empty<string>,
            IsProcessElevated = () => false,
        };

        var sw = Stopwatch.StartNew();
        var report = await detector.DetectAsync(CancellationToken.None).WaitAsync(Wait);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"took {sw.Elapsed}");
        Assert.False(report.Ollama.Running);
        Assert.False(report.LmStudio.Running);
        Assert.Equal("X:/bin/claude.exe", report.ClaudeCli.Path);
        Assert.Null(report.ClaudeCli.Version);
        Assert.Contains("did not answer", report.GeminiCli.Detail);
    }

    [Fact]
    public async Task Detector_passes_configured_cli_paths_from_settings()
    {
        var settings = StoreSettings();
        settings.ActiveProfile!.CliPath = "X:/custom/claude.exe";
        var gemini = ProviderPresets.CreateProfile(ProviderPresets.GeminiCliId);
        gemini.CliPath = "X:/custom/gemini.exe";
        settings.Profiles.Add(gemini);

        string? claudeArg = null;
        var lookups = new ConcurrentDictionary<string, string?>();
        var detector = new EnvironmentDetector(null, null, new HttpClient(Refusing()))
        {
            Settings = () => settings,
            ClaudeProbe = (path, _) => { claudeArg = path; return Task.FromResult(new CliToolStatus("Claude Code", path, "1.0.0", true, null)); },
            FindExecutable = (cmd, configured) => { lookups[cmd] = configured; return null; },
            IsEnvVarSet = _ => false,
            FindVaults = Array.Empty<string>,
            IsProcessElevated = () => false,
        };

        await detector.DetectAsync(CancellationToken.None).WaitAsync(Wait);

        Assert.Equal("X:/custom/claude.exe", claudeArg);
        Assert.Equal("X:/custom/gemini.exe", lookups["gemini"]);
        Assert.Null(lookups["codex"]);
    }

    [Fact]
    public async Task Detector_with_a_cancelled_token_still_returns_a_report()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var detector = new EnvironmentDetector(new FakeScreen(), new FakeWindows(), new HttpClient(new FakeHttpHandler((_, ct) => Task.FromCanceled<HttpResponseMessage>(ct))))
        {
            ClaudeProbe = (_, ct) => Task.FromCanceled<CliToolStatus>(ct),
            FindExecutable = (_, _) => null,
            IsEnvVarSet = _ => false,
            FindVaults = Array.Empty<string>,
        };

        var report = await detector.DetectAsync(cts.Token).WaitAsync(Wait);

        Assert.NotNull(report);
        Assert.False(report.Ollama.Running);
        Assert.Null(report.ClaudeCli.Path);
    }

    [Fact]
    public async Task Real_detector_finishes_in_time_and_never_throws()
    {
        // Read-only probes against this machine: PATH lookups, "<cli> --version", loopback HTTP, env var names.
        var detector = new EnvironmentDetector(new FakeScreen(), new FakeWindows());
        var sw = Stopwatch.StartNew();
        var report = await detector.DetectAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(45));
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(40), $"took {sw.Elapsed}");
        Assert.NotNull(report.ClaudeCli);
        Assert.NotNull(report.GeminiCli);
        Assert.NotNull(report.CodexCli);
        Assert.Equal("http://127.0.0.1:11434", report.Ollama.BaseUrl);
        Assert.Equal("http://127.0.0.1:1234/v1", report.LmStudio.BaseUrl);
        Assert.All(report.ApiKeyEnvVarsFound, name => Assert.Contains(name, EnvironmentDetector.ApiKeyEnvVars));
        Assert.NotNull(report.ObsidianVaults);
        Assert.Single(report.Monitors);
    }

    [Theory]
    [InlineData("2.1.293 (Claude Code)", "2.1.293", "2.1.293 (Claude Code)")]
    [InlineData("codex-cli 0.46.0\n", "0.46.0", "codex-cli 0.46.0")]
    [InlineData("\u001b[32m0.9.1\u001b[0m\r\n", "0.9.1", "0.9.1")]
    [InlineData("\n\n  v1.2.3-beta.1  \n", "1.2.3-beta.1", "v1.2.3-beta.1")]
    [InlineData("no version here", null, "no version here")]
    [InlineData("   ", null, null)]
    public void Version_output_is_parsed(string output, string? version, string? line)
    {
        var parsed = ProcessProbe.ParseVersion(output);
        Assert.Equal(version, parsed.Version);
        Assert.Equal(line, parsed.FirstLine);
    }

    [Fact]
    public void Cmd_shims_run_through_cmd_and_executables_run_directly()
    {
        var shim = ProcessProbe.BuildStartInfo(@"X:\Program Files\npm\gemini.cmd", "--version");
        Assert.EndsWith("cmd.exe", shim.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/d /s /c \"\"X:\\Program Files\\npm\\gemini.cmd\" --version\"", shim.Arguments);
        Assert.True(shim.CreateNoWindow);
        Assert.False(shim.UseShellExecute);

        var exe = ProcessProbe.BuildStartInfo(@"X:\tools\claude.exe", "--version");
        Assert.Equal(@"X:\tools\claude.exe", exe.FileName);
        Assert.Equal("--version", exe.Arguments);
        Assert.True(exe.CreateNoWindow);
    }

    // =====================================================================================
    // Helpers and fakes
    // =====================================================================================

    private SettingsStore NewStore(Action<AppSettings>? configure = null)
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings-" + Guid.NewGuid().ToString("N") + ".json"));
        if (configure != null) store.Update(configure);
        return store;
    }

    private static AppSettings StoreSettings()
    {
        var s = SettingsStore.CreateDefault();
        SettingsStore.Normalize(s);
        return s;
    }

    private static TurnResult Result(TurnOutcome outcome, string? text = null, string? error = null) =>
        new(outcome, text, new TurnStats(10, 5, 0, null, TimeSpan.FromMilliseconds(5), "fake-model"), error);

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task<TurnResult> WaitForCancel(TaskCompletionSource started, CancellationToken ct)
    {
        started.TrySetResult();
        try { await Task.Delay(Timeout.Infinite, ct); }
        catch (OperationCanceledException) { }
        return Result(TurnOutcome.Cancelled, error: "interrupted");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > Wait) throw new TimeoutException("condition was not met in time");
            await Task.Delay(10);
        }
    }

    private static EnvironmentReport Report(bool claude = true, bool geminiCli = false, LocalServerStatus? ollama = null,
        LocalServerStatus? lmStudio = null, string[]? keys = null) =>
        new(
            new CliToolStatus("Claude Code", claude ? "X:/claude.exe" : null, claude ? "2.1.293" : null, claude ? true : null, null),
            new CliToolStatus("Gemini CLI", geminiCli ? "X:/gemini.cmd" : null, null, null, null),
            new CliToolStatus("Codex CLI", null, null, null, "Not found"),
            ollama ?? new LocalServerStatus("Ollama", "http://127.0.0.1:11434", false, Array.Empty<ModelInfo>()),
            lmStudio ?? new LocalServerStatus("LM Studio", "http://127.0.0.1:1234/v1", false, Array.Empty<ModelInfo>()),
            keys ?? Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<MonitorInfo>(),
            false);

    private static LocalServerStatus Server(string name, string baseUrl, params string[] models) =>
        new(name, baseUrl, true, models.Select(m => new ModelInfo(m, m, null, null, null)).ToList());

    private static FakeHttpHandler Refusing() =>
        new((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ConcurrentQueue<AgentEvent> _events = new();
        private readonly ConcurrentQueue<AgentState> _states = new();
        private readonly Action<FakeBackend>? _configure;
        private readonly bool _usesMcp;
        private readonly List<FakeBackend> _backends = new();
        private int _mcpCreated;

        public Harness(SettingsStore store, string workDir, Action<FakeBackend>? configure = null, bool usesMcp = true)
        {
            _configure = configure;
            _usesMcp = usesMcp;
            Tools = new FakeToolHost();
            Session = new AgentSession(store, FakeDesktop.Create(), null, null, "DeskPilot-test.exe", CreateBackend, Hooks(workDir, Tools, t =>
            {
                Interlocked.Increment(ref _mcpCreated);
                Mcp = new FakeMcpHost(t) { StartThrows = McpStartThrows };
                return Mcp;
            }));
            Session.EventRaised += e => _events.Enqueue(e);
            Session.StateChanged += s => _states.Enqueue(s);
        }

        public AgentSession Session { get; }
        public FakeToolHost Tools { get; }
        public FakeMcpHost? Mcp { get; private set; }
        public bool McpStartThrows { get; set; }
        public int McpCreated => Volatile.Read(ref _mcpCreated);
        public List<AgentEvent> Events => _events.ToList();
        public List<AgentState> States => _states.ToList();

        public List<FakeBackend> Backends
        {
            get { lock (_backends) return _backends.ToList(); }
        }

        public static AgentSessionHooks Hooks(string workDir, IToolHost tools, Func<IToolHost, IMcpHost>? mcp) => new()
        {
            Tools = tools,
            ScreenDescription = () => ScreenText,
            VaultAvailable = () => false,
            McpHostFactory = mcp ?? (t => new FakeMcpHost(t)),
            WorkingDirectory = workDir,
        };

        public void ClearEvents()
        {
            while (_events.TryDequeue(out _)) { }
            while (_states.TryDequeue(out _)) { }
        }

        private IAgentBackend CreateBackend(ProviderProfile profile)
        {
            var backend = new FakeBackend(profile, _usesMcp);
            _configure?.Invoke(backend);
            lock (_backends) _backends.Add(backend);
            return backend;
        }

        public ValueTask DisposeAsync() => Session.DisposeAsync();
    }

    private sealed class FakeBackend : IAgentBackend
    {
        private int _start, _run, _interrupt, _reset, _dispose;

        public FakeBackend(ProviderProfile profile, bool usesMcp)
        {
            Profile = profile;
            UsesMcp = usesMcp;
        }

        public ProviderProfile Profile { get; }
        public bool UsesMcp { get; }
        public AgentBackendContext? Context { get; private set; }
        public UserTurn? LastTurn { get; private set; }
        public Func<AgentBackendContext, CancellationToken, Task>? OnStart { get; set; }
        public Func<FakeBackend, UserTurn, CancellationToken, Task<TurnResult>>? OnRun { get; set; }
        public Exception? ResetThrows { get; set; }
        public int StartCount => Volatile.Read(ref _start);
        public int RunCount => Volatile.Read(ref _run);
        public int InterruptCount => Volatile.Read(ref _interrupt);
        public int ResetCount => Volatile.Read(ref _reset);
        public int DisposeCount => Volatile.Read(ref _dispose);

        public Task StartAsync(AgentBackendContext context, CancellationToken ct)
        {
            Interlocked.Increment(ref _start);
            Context = context;
            return OnStart?.Invoke(context, ct) ?? Task.CompletedTask;
        }

        public Task<TurnResult> RunTurnAsync(UserTurn turn, CancellationToken ct)
        {
            Interlocked.Increment(ref _run);
            LastTurn = turn;
            if (OnRun != null) return OnRun(this, turn, ct);
            Context!.Emit(new AssistantTextEvent("Done: " + turn.Text));
            return Task.FromResult(Result(TurnOutcome.Completed, "Done"));
        }

        public Task InterruptAsync()
        {
            Interlocked.Increment(ref _interrupt);
            return Task.CompletedTask;
        }

        public Task ResetConversationAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref _reset);
            return ResetThrows != null ? Task.FromException(ResetThrows) : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _dispose);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeToolHost : IToolHost
    {
        private static readonly IReadOnlyList<ToolSpec> Specs = new[]
        {
            ToolSpec.Create("fake_tool", "A tool for tests.", """{"type":"object","properties":{}}"""),
        };

        private int _calls;
        public int Calls => Volatile.Read(ref _calls);

        public IReadOnlyList<ToolSpec> GetTools() => Specs;

        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(name == "fake_tool" ? ToolResult.Ok("ok") : ToolResult.Error("unknown"));
        }
    }

    private sealed class FakeMcpHost : IMcpHost
    {
        private int _start, _dispose;

        public FakeMcpHost(IToolHost tools) => Tools = tools;

        public IToolHost Tools { get; }
        public bool StartThrows { get; set; }
        public string? LastBridgePath { get; private set; }
        public int StartCount => Volatile.Read(ref _start);
        public int DisposeCount => Volatile.Read(ref _dispose);

        public void Start()
        {
            Interlocked.Increment(ref _start);
            if (StartThrows) throw new IOException("pipe busy");
        }

        public McpEndpointInfo GetEndpoint(string bridgeExePath)
        {
            LastBridgePath = bridgeExePath;
            return new McpEndpointInfo("deskpilot", bridgeExePath, new[] { "--mcp-bridge", "pipe-test", "token-test" });
        }

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _dispose);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _respond;

        public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) => _respond = respond;

        public FakeHttpHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) =>
            _respond = (r, ct) => Task.FromResult(respond(r, ct));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            _respond(request, cancellationToken);
    }

    // Desktop fakes: nothing here touches the real screen, input, clipboard or processes.

    private static class FakeDesktop
    {
        public static DesktopServices Create() => new(
            new FakeScreen(), new FakeInput(), new FakeWindows(), new FakeUi(), new FakeLauncher(), new FakeClipboard(), new FakeShell());
    }

    private sealed class FakeScreen : IScreenCapture
    {
        private static readonly MonitorInfo Primary =
            new(0, @"\\.\DISPLAY1", new ScreenRect(0, 0, 1920, 1080), new ScreenRect(0, 0, 1920, 1040), true, 1.0);

        public IReadOnlyList<MonitorInfo> GetMonitors() => new[] { Primary };
        public ScreenRect GetVirtualScreen() => Primary.Bounds;

        public CapturedFrame Capture(CaptureRequest request) =>
            new(new byte[] { 0 }, "image/png", request.TargetWidth, request.TargetHeight, request.Source);
    }

    private sealed class FakeInput : IInputSimulator
    {
        public int Calls;
        public void MoveMouse(int x, int y) => Calls++;
        public void MoveMouseSmooth(int x, int y, int durationMs) => Calls++;
        public void MouseDown(MouseButton button) => Calls++;
        public void MouseUp(MouseButton button) => Calls++;
        public void Click(MouseButton button, int clicks) => Calls++;
        public void Scroll(int dx, int dy) => Calls++;
        public void TypeText(string text, int delayMsPerChar) => Calls++;
        public void PressCombo(KeyCombo combo) => Calls++;
        public void KeyDown(string key) => Calls++;
        public void KeyUp(string key) => Calls++;
        public void ReleaseAll() { }
        public ScreenPoint GetCursorPosition() => new(500, 500);
    }

    private sealed class FakeWindows : IWindowManager
    {
        public bool Elevated { get; set; }
        public IReadOnlyList<WindowInfo> ListWindows() => Array.Empty<WindowInfo>();
        public WindowInfo? GetForegroundWindow() => null;
        public WindowInfo? GetWindowAt(int x, int y) => null;
        public bool FocusWindow(nint handle) => false;
        public bool IsCurrentProcessElevated => Elevated;
        public bool IsUacPromptActive() => false;
    }

    private sealed class FakeUi : IUiInspector
    {
        public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UiElementInfo>>(Array.Empty<UiElementInfo>());

        public Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct) => Task.FromResult<UiElementInfo?>(null);
    }

    private sealed class FakeLauncher : IAppLauncher
    {
        public LaunchResult Launch(string target, string? arguments, bool allowElevation) => new(false, "fake launcher", null);
    }

    private sealed class FakeClipboard : IClipboardService
    {
        private string? _text;
        public string? GetText() => _text;
        public void SetText(string text) => _text = text;
    }

    private sealed class FakeShell : IShellRunner
    {
        public Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct) =>
            Task.FromResult(new ShellResult(1, "", "fake shell", false));
    }
}

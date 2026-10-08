using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Acp;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using Xunit.Abstractions;

namespace DeskPilot.Tests;

public class AcpTests
{
    private const string SystemPromptText = "SYSTEM PROMPT TEXT for the computer-use agent.";
    private static readonly string[] DeskPilotToolNames = { "screenshot", "click", "run_command", "vault_search" };

    private readonly ITestOutputHelper _output;

    public AcpTests(ITestOutputHelper output) => _output = output;

    // ------------------------------------------------------------------ handshake

    [Fact]
    public async Task Start_sends_initialize_then_session_new_with_the_mcp_server()
    {
        await using var h = new Harness();
        await h.StartAsync();
        var agent = h.Agent;

        var methods = agent.Received.Select(m => Method(m)).Where(m => m != null).ToList();
        Assert.Equal(new[] { "initialize", "session/new" }, methods);

        var init = agent.ReceivedMethod("initialize").Single().GetProperty("params");
        Assert.Equal(1, init.GetProperty("protocolVersion").GetInt32());
        var caps = init.GetProperty("clientCapabilities");
        Assert.False(caps.GetProperty("fs").GetProperty("readTextFile").GetBoolean());
        Assert.False(caps.GetProperty("fs").GetProperty("writeTextFile").GetBoolean());
        Assert.False(caps.GetProperty("terminal").GetBoolean());
        Assert.Equal("deskpilot", init.GetProperty("clientInfo").GetProperty("name").GetString());

        var session = agent.ReceivedMethod("session/new").Single().GetProperty("params");
        Assert.Equal(h.WorkDir, session.GetProperty("cwd").GetString());
        var server = Assert.Single(session.GetProperty("mcpServers").EnumerateArray());
        Assert.Equal("deskpilot", server.GetProperty("name").GetString());
        Assert.Equal(@"C:\Apps\DeskPilot\DeskPilot.exe", server.GetProperty("command").GetString());
        Assert.Equal(new[] { "--mcp-bridge", "pipe-1", "token-1" }, server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal(0, server.GetProperty("env").GetArrayLength());
        Assert.Equal("sess-1", h.Backend.SessionId);
        Assert.True(h.Backend.UsesMcp);
    }

    [Fact]
    public async Task An_agent_that_exits_during_startup_reports_its_stderr()
    {
        await using var h = new Harness(a => a.OnRequest = (agent, method, id, p) =>
        {
            if (method != "initialize") return Task.FromResult(false);
            agent.Crash(1, "Unknown argument: acp");
            return Task.FromResult(true);
        });

        var ex = await Assert.ThrowsAsync<AcpAgentException>(h.StartAsync);
        Assert.Contains("stopped while starting (exit code 1)", ex.Message);
        Assert.Contains("Unknown argument: acp", ex.Message);
    }

    [Fact]
    public async Task An_unsupported_protocol_version_fails_start()
    {
        await using var h = new Harness(a => a.ProtocolVersion = 2);
        var ex = await Assert.ThrowsAsync<AcpAgentException>(h.StartAsync);
        Assert.Contains("protocol version 2", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(h.Agent.Killed);
    }

    [Fact]
    public async Task A_program_that_cannot_be_started_gives_a_readable_error()
    {
        var tmp = NewTempDir();
        try
        {
            await using var backend = new AcpBackend(
                _ => throw new System.ComponentModel.Win32Exception(2, "The system cannot find the file specified"),
                name => @"C:\fake-bin\" + name + ".exe", Path.Combine(tmp, "temp")) { Logger = _ => { } };
            var ctx = ContextFor(CustomProfile(), tmp);
            var ex = await Assert.ThrowsAsync<AcpAgentException>(() => backend.StartAsync(ctx, CancellationToken.None));
            Assert.Contains("Could not start", ex.Message);
            Assert.Contains("cannot find the file", ex.Message);
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    // ------------------------------------------------------------------ turns

    [Fact]
    public async Task Turn_streams_chunks_as_partial_events_and_returns_the_text()
    {
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            await agent.UpdateAsync(sid, Chunk("agent_thought_chunk", "Let me look."));
            await agent.UpdateAsync(sid, Chunk("agent_message_chunk", "Hel"));
            await agent.UpdateAsync(sid, Chunk("agent_message_chunk", "lo"));
            await agent.UpdateAsync("some-other-session", Chunk("agent_message_chunk", "IGNORED"));
            await agent.UpdateAsync(sid, Chunk("user_message_chunk", "echo of the prompt"));
            await agent.UpdateAsync(sid, new JsonObject
            {
                ["sessionUpdate"] = "plan",
                ["entries"] = new JsonArray(
                    new JsonObject { ["content"] = "Open Notepad", ["priority"] = "high", ["status"] = "completed" },
                    new JsonObject { ["content"] = "Type the note", ["priority"] = "medium", ["status"] = "in_progress" },
                    new JsonObject { ["content"] = "Save", ["priority"] = "low", ["status"] = "pending" }),
            });
            await agent.UpdateAsync(sid, ToolCall("t1", "Read file notes.txt", "read"));
            await agent.UpdateAsync(sid, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t1", ["status"] = "failed" });
            await agent.UpdateAsync(sid, ToolCall("t2", "mcp__deskpilot__screenshot", "other"));
            await agent.UpdateAsync(sid, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "t2", ["status"] = "completed" });
            await agent.UpdateAsync(sid, new JsonObject { ["sessionUpdate"] = "available_commands_update", ["availableCommands"] = new JsonArray() });
            await agent.RespondAsync(id, new JsonObject
            {
                ["stopReason"] = "end_turn",
                ["usage"] = new JsonObject { ["inputTokens"] = 120, ["outputTokens"] = 15, ["totalTokens"] = 135 },
            });
            return true;
        });
        await h.StartAsync();

        var result = await h.TurnAsync("open notepad");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal("Hello", result.FinalText);
        Assert.Null(result.Error);
        Assert.Equal(120, result.Stats.InputTokens);
        Assert.Equal(15, result.Stats.OutputTokens);
        Assert.Equal(0, result.Stats.Steps);

        var texts = h.EventsOf<AssistantTextEvent>();
        Assert.Equal(new[] { "Hel", "lo" }, texts.Select(e => e.Text));
        Assert.All(texts, e => Assert.True(e.IsPartial));
        var thinking = Assert.Single(h.EventsOf<ThinkingEvent>());
        Assert.Equal("Let me look.", thinking.Text);
        Assert.True(thinking.IsPartial);

        var statuses = h.StatusMessages();
        Assert.Contains("Plan:\n[x] Open Notepad\n[>] Type the note\n[ ] Save", statuses);
        Assert.Contains("Agent tool: Read file notes.txt", statuses);
        Assert.Contains("Agent tool failed: Read file notes.txt", statuses);
        Assert.DoesNotContain(statuses, s => s.Contains("screenshot"));
        Assert.DoesNotContain(texts, t => t.Text.Contains("IGNORED"));

        var prompt = h.Agent.ReceivedMethod("session/prompt").Single().GetProperty("params");
        Assert.Equal("sess-1", prompt.GetProperty("sessionId").GetString());
        var block = Assert.Single(prompt.GetProperty("prompt").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        var text = block.GetProperty("text").GetString()!;
        Assert.StartsWith("<deskpilot_instructions>", text);
        Assert.Contains(SystemPromptText, text);
        Assert.EndsWith("</deskpilot_instructions>\n\nopen notepad", text);
    }

    [Fact]
    public async Task System_prompt_is_prepended_once_per_session_and_reset_reuses_the_process()
    {
        await using var h = new Harness();
        await h.StartAsync();

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("first")).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("second")).Outcome);
        await h.Backend.ResetConversationAsync(CancellationToken.None);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("third")).Outcome);

        var prompts = h.Agent.ReceivedMethod("session/prompt").ToList();
        Assert.Contains(SystemPromptText, PromptText(prompts[0]));
        Assert.Equal("second", PromptText(prompts[1]));
        Assert.Contains(SystemPromptText, PromptText(prompts[2]));
        Assert.EndsWith("third", PromptText(prompts[2]));

        Assert.Single(h.Agents);
        Assert.Equal(2, h.Agent.ReceivedMethod("session/new").Count());
        Assert.Equal("sess-2", prompts[2].GetProperty("params").GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Images_are_sent_only_when_the_agent_accepts_them(bool supported)
    {
        await using var h = new Harness(a => a.ImageSupport = supported);
        await h.StartAsync();
        h.Control.BeginTurn();
        var image = new ToolImage("image/png", "iVBORw0KGgo=", 1, 1);

        var result = await h.Backend.RunTurnAsync(new UserTurn("what is this", new[] { image }), CancellationToken.None);

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var blocks = h.Agent.ReceivedMethod("session/prompt").Single().GetProperty("params").GetProperty("prompt").EnumerateArray().ToList();
        if (supported)
        {
            Assert.Equal(2, blocks.Count);
            Assert.Equal("image", blocks[1].GetProperty("type").GetString());
            Assert.Equal("iVBORw0KGgo=", blocks[1].GetProperty("data").GetString());
            Assert.Equal("image/png", blocks[1].GetProperty("mimeType").GetString());
        }
        else
        {
            Assert.Single(blocks);
            Assert.Contains(h.StatusMessages(), s => s.Contains("does not accept images"));
        }
    }

    [Theory]
    [InlineData("end_turn", TurnOutcome.Completed, null)]
    [InlineData("max_tokens", TurnOutcome.Completed, "output limit")]
    [InlineData("max_turn_requests", TurnOutcome.StepLimit, "limit of model requests")]
    [InlineData("refusal", TurnOutcome.Completed, "refused")]
    [InlineData("cancelled", TurnOutcome.Cancelled, null)]
    public async Task Stop_reasons_map_to_turn_outcomes(string stopReason, TurnOutcome expected, string? warning)
    {
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = stopReason });
            return true;
        });
        await h.StartAsync();

        var result = await h.TurnAsync("go");

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Error);
        if (warning != null)
            Assert.Contains(h.EventsOf<StatusEvent>(), s => s.Level == StatusLevel.Warning && s.Message.Contains(warning));
        else
            Assert.DoesNotContain(h.EventsOf<StatusEvent>(), s => s.Level == StatusLevel.Warning);
    }

    [Fact]
    public async Task Gemini_style_usage_in_meta_and_the_model_are_reported()
    {
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            await agent.RespondAsync(id, new JsonObject
            {
                ["stopReason"] = "end_turn",
                ["_meta"] = new JsonObject
                {
                    ["quota"] = new JsonObject
                    {
                        ["token_count"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 42 },
                        ["model_usage"] = new JsonArray(new JsonObject
                        {
                            ["model"] = "gemini-2.5-flash",
                            ["token_count"] = new JsonObject { ["input_tokens"] = 900, ["output_tokens"] = 42 },
                        }),
                    },
                },
            });
            return true;
        });
        await h.StartAsync();

        var result = await h.TurnAsync("go");

        Assert.Equal(900, result.Stats.InputTokens);
        Assert.Equal(42, result.Stats.OutputTokens);
        Assert.Equal("gemini-2.5-flash", result.Stats.Model);
        Assert.Null(result.Stats.CostUsd);
    }

    [Fact]
    public async Task Session_cost_updates_become_a_per_turn_cost_and_steps_come_from_the_run_control()
    {
        var turnNumber = 0;
        await using var h = new Harness();
        h.OnAgent = a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var amount = Interlocked.Increment(ref turnNumber) == 1 ? 0.5 : 0.75;
            h.Control.RegisterStep();
            h.Control.RegisterStep();
            await agent.UpdateAsync(SessionOf(p), new JsonObject
            {
                ["sessionUpdate"] = "usage_update",
                ["used"] = 1000,
                ["size"] = 100000,
                ["cost"] = new JsonObject { ["amount"] = amount, ["currency"] = "USD" },
            });
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
            return true;
        };
        await h.StartAsync();

        var first = await h.TurnAsync("one");
        var second = await h.TurnAsync("two");

        Assert.Equal(0.5, first.Stats.CostUsd!.Value, 6);
        Assert.Equal(0.25, second.Stats.CostUsd!.Value, 6);
        Assert.Equal(2, second.Stats.Steps);
    }

    [Fact]
    public async Task A_prompt_error_becomes_a_failed_turn_and_the_agent_keeps_serving()
    {
        var calls = 0;
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt" || Interlocked.Increment(ref calls) > 1) return false;
            await agent.ErrorAsync(id, 429, "Rate limit exceeded. Try again later.");
            return true;
        });
        await h.StartAsync();

        var failed = await h.TurnAsync("go");
        Assert.Equal(TurnOutcome.Failed, failed.Outcome);
        Assert.Equal("Fake Agent reported an error: Rate limit exceeded. Try again later.", failed.Error);

        var next = await h.TurnAsync("again");
        Assert.Equal(TurnOutcome.Completed, next.Outcome);
        Assert.Single(h.Agents);
    }

    [Fact]
    public async Task An_auth_error_during_a_prompt_tells_the_user_to_log_in()
    {
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            await agent.ErrorAsync(id, -32000, "Authentication required");
            return true;
        });
        await h.StartAsync();

        var result = await h.TurnAsync("go");

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("Fake Agent is not logged in. Run it in a terminal once and sign in, then try again.", result.Error);
    }

    [Fact]
    public async Task RunTurn_before_start_fails_without_throwing_and_idle_calls_are_safe()
    {
        await using var backend = new AcpBackend(_ => throw new InvalidOperationException("must not start")) { Logger = _ => { } };

        var result = await backend.RunTurnAsync(new UserTurn("hi"), CancellationToken.None);

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("not been started", result.Error);
        await backend.InterruptAsync();
        await backend.ResetConversationAsync(CancellationToken.None);
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Permission_is_granted_for_deskpilot_tools()
    {
        var answers = new ConcurrentQueue<JsonElement>();
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            answers.Enqueue(await agent.RequestAsync("session/request_permission",
                PermissionRequest(sid, "c1", "click (deskpilot MCP Server)", "other", GeminiMcpOptions())));
            answers.Enqueue(await agent.RequestAsync("session/request_permission",
                PermissionRequest(sid, "c2", "mcp__deskpilot__screenshot", "other", new JsonArray(Option("ok", "Allow", "allow_once"), Option("no", "Reject", "reject_once")))));
            var withMeta = PermissionRequest(sid, "c3", "Click", "other", new JsonArray(Option("always", "Always allow", "allow_always"), Option("no", "Reject", "reject_once")));
            withMeta["toolCall"]!["_meta"] = new JsonObject { ["claudeCode"] = new JsonObject { ["toolName"] = "mcp__deskpilot__click" } };
            answers.Enqueue(await agent.RequestAsync("session/request_permission", withMeta));
            var structured = PermissionRequest(sid, "c4", "Tool: deskpilot/run_command", "execute", new JsonArray(Option("once", "Allow", "allow_once"), Option("no", "Reject", "reject_once")));
            structured["toolCall"]!["rawInput"] = new JsonObject { ["server"] = "deskpilot", ["tool"] = "run_command", ["arguments"] = new JsonObject { ["command"] = "dir" } };
            answers.Enqueue(await agent.RequestAsync("session/request_permission", structured));
            await agent.UpdateAsync(sid, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "c3", ["title"] = "Click", ["status"] = "completed" });
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
            return true;
        });
        await h.StartAsync();

        var result = await h.TurnAsync("click it");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "proceed_always_server", "ok", "always", "once" }, answers.Select(SelectedOption));
        Assert.DoesNotContain(h.StatusMessages(), s => s.Contains("Blocked") || s.StartsWith("Agent tool"));
    }

    [Fact]
    public async Task Permission_is_refused_for_the_agents_own_tools()
    {
        var answers = new ConcurrentQueue<JsonElement>();
        var execOptions = new JsonArray(
            Option("proceed_always", "Allow for this session", "allow_always"),
            Option("proceed_once", "Allow", "allow_once"),
            Option("cancel", "Reject", "reject_once"));
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            foreach (var (callId, title, kind) in new[]
                     {
                         ("s1", @"rm -rf C:\Users\deskpilot", "execute"),
                         ("s2", "mcp__deskpilot__click && del /q *", "execute"),
                         ("s3", "format_disk (deskpilot MCP Server)", "other"),
                         ("s4", "click (other MCP Server)", "other"),
                     })
            {
                answers.Enqueue(await agent.RequestAsync("session/request_permission",
                    PermissionRequest(sid, callId, title, kind, (JsonArray)execOptions.DeepClone())));
            }
            // No reject option offered: the answer is "cancelled", never an allow.
            answers.Enqueue(await agent.RequestAsync("session/request_permission",
                PermissionRequest(sid, "s5", "WriteFile notes.txt", "edit", new JsonArray(Option("yes", "Allow", "allow_once")))));
            await agent.UpdateAsync(sid, new JsonObject { ["sessionUpdate"] = "tool_call_update", ["toolCallId"] = "s1", ["status"] = "failed" });
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
            return true;
        });
        await h.StartAsync();

        await h.TurnAsync("do it");

        Assert.Equal(new[] { "cancel", "cancel", "cancel", "cancel", "cancelled" }, answers.Select(SelectedOption));
        var blocked = h.EventsOf<StatusEvent>().Where(s => s.Message.StartsWith("Blocked")).ToList();
        Assert.Equal(5, blocked.Count);
        Assert.All(blocked, s => Assert.Equal(StatusLevel.Warning, s.Level));
        Assert.Contains(blocked, s => s.Message.Contains(@"rm -rf C:\Users\deskpilot"));
        Assert.DoesNotContain(h.StatusMessages(), s => s.StartsWith("Agent tool"));
    }

    [Fact]
    public async Task Gemini_mcp_confirmations_count_as_deskpilot_only_while_mcp_is_restricted_to_deskpilot()
    {
        var answers = new ConcurrentQueue<JsonElement>();
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            // Gemini titles DeskPilot's run_command with the command itself, but offers MCP-only options.
            answers.Enqueue(await agent.RequestAsync("session/request_permission", PermissionRequest(sid, "g1", "dir C:\\", "other", GeminiMcpOptions())));
            // Its own shell tool: same title, exec options.
            answers.Enqueue(await agent.RequestAsync("session/request_permission", PermissionRequest(sid, "g2", "dir C:\\", "execute", new JsonArray(
                Option("proceed_always", "Allow for this session", "allow_always"),
                Option("proceed_once", "Allow", "allow_once"),
                Option("cancel", "Reject", "reject_once")))));
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
            return true;
        }, GeminiProfile());
        await h.StartAsync();
        Assert.True(h.Agent.Spec.McpRestrictedToDeskPilot);

        await h.TurnAsync("list files");

        Assert.Equal(new[] { "proceed_always_server", "cancel" }, answers.Select(SelectedOption));
    }

    // ------------------------------------------------------------------ cancellation

    [Fact]
    public async Task Cancelling_sends_session_cancel_and_ends_the_turn_as_cancelled()
    {
        JsonElement permissionDuringCancel = default;
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            if (PromptText(p).Contains("again"))
            {
                await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
                return true;
            }
            await agent.UpdateAsync(sid, Chunk("agent_message_chunk", "Working"));
            await agent.WaitForAsync("session/cancel");
            permissionDuringCancel = await agent.RequestAsync("session/request_permission",
                PermissionRequest(sid, "late", "click (deskpilot MCP Server)", "other", GeminiMcpOptions()));
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "cancelled" });
            return true;
        });
        await h.StartAsync();
        using var cts = new CancellationTokenSource();

        var turn = h.TurnAsync("do a long thing", cts.Token);
        await WaitUntil(() => h.EventsOf<AssistantTextEvent>().Count > 0);
        cts.Cancel();
        var result = await turn;

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Equal("Working", result.FinalText);
        var cancel = h.Agent.ReceivedMethod("session/cancel").Single();
        Assert.False(cancel.TryGetProperty("id", out _));
        Assert.Equal("sess-1", cancel.GetProperty("params").GetProperty("sessionId").GetString());
        Assert.Equal("cancelled", permissionDuringCancel.GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        Assert.False(h.Agent.Killed);

        var next = await h.TurnAsync("again");
        Assert.Equal(TurnOutcome.Completed, next.Outcome);
        Assert.Single(h.Agents);
    }

    [Fact]
    public async Task An_agent_that_ignores_cancel_is_killed_and_replaced_on_the_next_turn()
    {
        var created = 0;
        await using var h = new Harness(a =>
        {
            if (Interlocked.Increment(ref created) == 1)
                a.OnRequest = (agent, method, id, p) => Task.FromResult(method == "session/prompt"); // never answers
        });
        h.Backend.CancelGracePeriod = TimeSpan.FromMilliseconds(200);
        await h.StartAsync();

        var turn = h.TurnAsync("hang");
        await h.Agent.WaitForAsync("session/prompt");
        h.Control.RequestStop("Stopped by user");
        var result = await turn;

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.True(h.Agents[0].Killed);
        Assert.Single(h.StatusMessages(), s => s.Contains("did not stop in time"));

        var next = await h.TurnAsync("hello again");
        Assert.Equal(TurnOutcome.Completed, next.Outcome);
        Assert.Equal(2, h.Agents.Count);
        Assert.DoesNotContain(h.StatusMessages(), s => s.Contains("had stopped"));
    }

    [Fact]
    public async Task A_step_limit_stop_reports_StepLimit_and_InterruptAsync_cancels()
    {
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var cancels = agent.ReceivedMethod("session/cancel").Count();
            await agent.WaitForAsync("session/cancel", cancels + 1);
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "cancelled" });
            return true;
        });
        await h.StartAsync();
        await h.Backend.InterruptAsync(); // idle: nothing to do

        var turn = h.TurnAsync("many clicks");
        await h.Agent.WaitForAsync("session/prompt");
        h.Control.RequestStop("Step limit (80) reached");
        Assert.Equal(TurnOutcome.StepLimit, (await turn).Outcome);

        var second = h.TurnAsync("another");
        await h.Agent.WaitForAsync("session/prompt", 2);
        await h.Backend.InterruptAsync();
        Assert.Equal(TurnOutcome.Cancelled, (await second).Outcome);
        Assert.False(h.Agent.Killed);
    }

    [Fact]
    public async Task A_turn_cancelled_before_it_starts_sends_nothing()
    {
        await using var h = new Harness();
        await h.StartAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await h.TurnAsync("never sent", cts.Token);

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Empty(h.Agent.ReceivedMethod("session/prompt"));
    }

    // ------------------------------------------------------------------ authentication

    [Fact]
    public async Task Missing_login_fails_start_with_a_readable_message()
    {
        var profile = GeminiProfile();
        profile.ExtraEnv["GEMINI_API_KEY"] = "";
        profile.ExtraEnv["GOOGLE_API_KEY"] = "";
        await using var h = new Harness(a =>
        {
            a.AuthMethods = GeminiAuthMethods();
            a.OnRequest = async (agent, method, id, p) =>
            {
                if (method != "session/new") return false;
                await agent.ErrorAsync(id, -32000, "Gemini API key is missing or not configured.");
                return true;
            };
        }, profile);

        var ex = await Assert.ThrowsAsync<AcpAgentException>(h.StartAsync);

        Assert.StartsWith("Gemini CLI is not logged in. Run 'gemini' in a terminal once and sign in (or set GEMINI_API_KEY).", ex.Message);
        Assert.Contains("Gemini API key is missing or not configured.", ex.Message);
        Assert.Empty(h.Agent.ReceivedMethod("authenticate"));
        Assert.True(h.Agent.Killed);

        // A turn after the failed start tries again and returns the same readable error.
        var result = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.StartsWith("Gemini CLI is not logged in", result.Error);
        Assert.DoesNotContain(h.StatusMessages(), s => s.Contains("had stopped"));
    }

    [Fact]
    public async Task Generic_agents_get_a_generic_login_message()
    {
        await using var h = new Harness(a =>
        {
            a.AuthMethods = new JsonArray(new JsonObject { ["id"] = "agent-login", ["name"] = "Log in" });
            a.OnRequest = async (agent, method, id, p) =>
            {
                if (method != "session/new") return false;
                await agent.ErrorAsync(id, -32000, "Authentication required");
                return true;
            };
        });

        var ex = await Assert.ThrowsAsync<AcpAgentException>(h.StartAsync);
        Assert.Equal("Fake Agent is not logged in. Run it in a terminal once and sign in, then try again.", ex.Message);
    }

    [Fact]
    public async Task An_api_key_in_the_environment_is_used_to_authenticate_and_retry_once()
    {
        var profile = GeminiProfile();
        profile.ExtraEnv["GEMINI_API_KEY"] = "test-key-not-real-123";
        var attempts = 0;
        await using var h = new Harness(a =>
        {
            a.AuthMethods = GeminiAuthMethods();
            a.OnRequest = async (agent, method, id, p) =>
            {
                if (method != "session/new" || Interlocked.Increment(ref attempts) > 1) return false;
                await agent.ErrorAsync(id, -32000, "Authentication required.");
                return true;
            };
        }, profile);

        await h.StartAsync();

        var methods = h.Agent.Received.Select(m => Method(m)).Where(m => m != null).ToList();
        Assert.Equal(new[] { "initialize", "session/new", "authenticate", "session/new" }, methods);
        Assert.Equal("gemini-api-key", h.Agent.ReceivedMethod("authenticate").Single().GetProperty("params").GetProperty("methodId").GetString());
        Assert.Equal("test-key-not-real-123", h.Agent.Spec.Environment["GEMINI_API_KEY"]);
        Assert.Equal("sess-1", h.Backend.SessionId);
    }

    [Fact]
    public async Task Env_var_auth_methods_are_used_when_their_variables_are_set()
    {
        var profile = CustomProfile();
        profile.ExtraEnv["FAKE_AGENT_TOKEN"] = "token-value-12345";
        var attempts = 0;
        await using var h = new Harness(a =>
        {
            a.AuthMethods = new JsonArray(
                new JsonObject { ["id"] = "browser", ["name"] = "Browser login" },
                new JsonObject
                {
                    ["id"] = "token", ["name"] = "Token", ["type"] = "env_var",
                    ["vars"] = new JsonArray(new JsonObject { ["name"] = "FAKE_AGENT_TOKEN" }, new JsonObject { ["name"] = "FAKE_AGENT_REGION", ["optional"] = true }),
                });
            a.OnRequest = async (agent, method, id, p) =>
            {
                if (method != "session/new" || Interlocked.Increment(ref attempts) > 1) return false;
                await agent.ErrorAsync(id, -32000, "Authentication required");
                return true;
            };
        }, profile);

        await h.StartAsync();

        Assert.Equal("token", h.Agent.ReceivedMethod("authenticate").Single().GetProperty("params").GetProperty("methodId").GetString());
    }

    [Fact]
    public async Task Api_keys_are_redacted_from_agent_messages()
    {
        const string key = "sk-secret-value-0123456789";
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            await agent.ErrorAsync(id, 500, $"Request failed for key {key}");
            return true;
        }, apiKey: key);
        await h.StartAsync();

        var result = await h.TurnAsync("go");

        Assert.DoesNotContain(key, result.Error);
        Assert.Contains("***", result.Error);
    }

    // ------------------------------------------------------------------ process death

    [Fact]
    public async Task Agent_crash_mid_turn_fails_with_the_stderr_tail_and_the_next_turn_restarts_it()
    {
        var created = 0;
        await using var h = new Harness(a =>
        {
            if (Interlocked.Increment(ref created) != 1) return;
            a.OnRequest = async (agent, method, id, p) =>
            {
                if (method != "session/prompt") return false;
                await agent.UpdateAsync(SessionOf(p), Chunk("agent_message_chunk", "Starting"));
                agent.Crash(3, "Error: something exploded\n    at main (gemini.js:1:1)");
                return true;
            };
        });
        await h.StartAsync();

        var result = await h.TurnAsync("go");

        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("stopped unexpectedly (exit code 3)", result.Error);
        Assert.Contains("something exploded", result.Error);
        Assert.Equal("Starting", result.FinalText);

        var next = await h.TurnAsync("again");
        Assert.Equal(TurnOutcome.Completed, next.Outcome);
        Assert.Equal(2, h.Agents.Count);
        Assert.Contains(h.EventsOf<StatusEvent>(), s => s.Level == StatusLevel.Warning && s.Message.Contains("had stopped"));
        // The new process gets a new session, so the system prompt is sent again.
        Assert.Contains(SystemPromptText, PromptText(h.Agent.ReceivedMethod("session/prompt").Single()));
    }

    [Fact]
    public async Task Reset_restarts_an_agent_that_died_while_idle()
    {
        await using var h = new Harness();
        await h.StartAsync();
        h.Agent.Crash(9, "gone");

        await h.Backend.ResetConversationAsync(CancellationToken.None);

        Assert.Equal(2, h.Agents.Count);
        var result = await h.TurnAsync("hi");
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.DoesNotContain(h.StatusMessages(), s => s.Contains("had stopped"));
    }

    [Fact]
    public async Task Unsupported_client_methods_get_method_not_found()
    {
        var answers = new ConcurrentQueue<JsonElement>();
        await using var h = new Harness(a => a.OnRequest = async (agent, method, id, p) =>
        {
            if (method != "session/prompt") return false;
            var sid = SessionOf(p);
            answers.Enqueue(await agent.RequestAsync("fs/read_text_file", new JsonObject { ["sessionId"] = sid, ["path"] = @"C:\secret.txt" }));
            answers.Enqueue(await agent.RequestAsync("terminal/create", new JsonObject { ["sessionId"] = sid, ["command"] = "cmd" }));
            answers.Enqueue(await agent.RequestAsync("_vendor/extension", new JsonObject()));
            await agent.RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
            return true;
        });
        await h.StartAsync();

        await h.TurnAsync("go");

        Assert.Equal(3, answers.Count);
        Assert.All(answers, r => Assert.Equal(-32601, r.GetProperty("error").GetProperty("code").GetInt32()));
    }

    [Fact]
    public async Task Dispose_kills_the_agent_deletes_temp_files_and_is_idempotent()
    {
        var h = new Harness(profile: GeminiProfile());
        try
        {
            await h.StartAsync();
            var spec = h.Agent.Spec;
            Assert.Equal(2, spec.TempFiles.Count);
            Assert.All(spec.TempFiles, f => Assert.True(File.Exists(f)));

            await h.Backend.DisposeAsync();
            await h.Backend.DisposeAsync();

            Assert.True(h.Agent.Killed);
            Assert.All(spec.TempFiles, f => Assert.False(File.Exists(f)));
            var result = await h.TurnAsync("after dispose");
            Assert.Equal(TurnOutcome.Failed, result.Outcome);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Backend.StartAsync(h.Context, CancellationToken.None));
        }
        finally
        {
            await h.DisposeAsync();
        }
    }

    // ------------------------------------------------------------------ launch spec

    [Fact]
    public void Gemini_launch_spec_has_acp_flags_system_prompt_and_policy()
    {
        var tmp = NewTempDir();
        try
        {
            var profile = GeminiProfile();
            profile.Model = "gemini-2.5-flash";
            profile.ExtraEnv["DESKPILOT_TEST_VAR"] = "1";
            profile.ExtraEnv["DESKPILOT_REMOVED_VAR"] = "";
            var spec = AcpLaunchBuilder.Build(ContextFor(profile, tmp), name => name == "gemini" ? @"C:\Program Files\nodejs\gemini.cmd" : null, tmp);

            Assert.True(spec.IsGemini);
            Assert.True(spec.McpRestrictedToDeskPilot);
            Assert.Equal("Gemini CLI", spec.DisplayName);
            Assert.Equal(@"C:\Program Files\nodejs\gemini.cmd", spec.ExecutablePath);
            Assert.EndsWith("cmd.exe", spec.FileName, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("/d /s /c \"C:\\Program^ Files\\nodejs\\gemini.cmd ^^^\"--acp^^^\"", spec.RawArguments);
            Assert.Empty(spec.ArgumentList);

            var args = spec.AgentArguments.ToList();
            Assert.Equal("--acp", args[0]);
            AssertPair(args, "-m", "gemini-2.5-flash");
            AssertPair(args, "--allowed-mcp-server-names", "deskpilot");
            AssertPair(args, "--approval-mode", "default");
            AssertPair(args, "--policy", spec.PolicyFile!);
            Assert.Contains("--skip-trust", args);
            Assert.Equal(tmp, spec.WorkingDirectory);

            Assert.Equal(spec.SystemPromptFile, spec.Environment["GEMINI_SYSTEM_MD"]);
            var promptFile = File.ReadAllText(spec.SystemPromptFile!);
            Assert.StartsWith(SystemPromptText, promptFile);
            Assert.Contains("mcp_deskpilot_screenshot", promptFile);
            var policy = File.ReadAllText(spec.PolicyFile!);
            Assert.Contains("mcpName = \"deskpilot\"", policy);
            Assert.Contains("decision = \"allow\"\npriority = 999", policy.Replace("\r\n", "\n"));
            Assert.Contains("decision = \"deny\"\npriority = 990", policy.Replace("\r\n", "\n"));
            Assert.Equal(new[] { spec.SystemPromptFile!, spec.PolicyFile! }, spec.TempFiles);

            Assert.Equal("1", spec.Environment["DESKPILOT_TEST_VAR"]);
            Assert.True(spec.Environment.ContainsKey("DESKPILOT_REMOVED_VAR"));
            Assert.Null(spec.Environment["DESKPILOT_REMOVED_VAR"]);
            Assert.DoesNotContain(spec.Environment.Keys, k => k == "GEMINI_API_KEY");
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Fact]
    public void Gemini_user_arguments_are_kept_and_not_duplicated()
    {
        var tmp = NewTempDir();
        try
        {
            var profile = GeminiProfile();
            profile.Model = "gemini-2.5-flash";
            profile.ExtraCliArgs = "--experimental-acp --model=gemini-2.5-pro --approval-mode auto_edit --allowed-mcp-server-names deskpilot --skip-trust";
            var spec = AcpLaunchBuilder.Build(ContextFor(profile, tmp), _ => @"C:\bin\gemini.exe", tmp);

            var args = spec.AgentArguments.ToList();
            Assert.DoesNotContain("--acp", args);
            Assert.DoesNotContain("-m", args);
            Assert.Single(args, a => a == "--approval-mode");
            Assert.Single(args, a => a == "--allowed-mcp-server-names");
            Assert.Single(args, a => a == "--skip-trust");
            Assert.Contains("--policy", args);
            Assert.False(spec.McpRestrictedToDeskPilot);
            Assert.Equal(@"C:\bin\gemini.exe", spec.FileName);
            Assert.Null(spec.RawArguments);
            Assert.Equal(args, spec.ArgumentList);
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Fact]
    public void Gemini_gets_the_stored_api_key_and_custom_agents_get_their_key_variable()
    {
        var tmp = NewTempDir();
        try
        {
            var gemini = AcpLaunchBuilder.Build(ContextFor(GeminiProfile(), tmp, apiKey: "stored-gemini-key-123"), _ => @"C:\bin\gemini.exe", tmp);
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY")))
                Assert.Equal("stored-gemini-key-123", gemini.Environment["GEMINI_API_KEY"]);

            var custom = CustomProfile();
            custom.ApiKeyEnvVar = "MY_AGENT_KEY";
            var spec = AcpLaunchBuilder.Build(ContextFor(custom, tmp, apiKey: "stored-agent-key-123"), _ => @"C:\bin\my-agent.exe", tmp);
            Assert.Equal("stored-agent-key-123", spec.Environment["MY_AGENT_KEY"]);
            Assert.False(spec.Environment.ContainsKey("GEMINI_API_KEY"));
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Fact]
    public void Custom_agent_runs_the_configured_command_with_its_arguments()
    {
        var tmp = NewTempDir();
        try
        {
            var profile = CustomProfile();
            profile.CliPath = "my-agent.cmd";
            profile.ExtraCliArgs = "--stdio \"--name=My Agent\"";
            string? asked = null;
            var spec = AcpLaunchBuilder.Build(ContextFor(profile, tmp), name => { asked = name; return @"C:\Tools\my-agent.exe"; }, tmp);

            Assert.Equal("my-agent", asked);
            Assert.False(spec.IsGemini);
            Assert.False(spec.McpRestrictedToDeskPilot);
            Assert.Equal(@"C:\Tools\my-agent.exe", spec.FileName);
            Assert.Null(spec.RawArguments);
            Assert.Equal(new[] { "--stdio", "--name=My Agent" }, spec.ArgumentList);
            Assert.Empty(spec.TempFiles);
            Assert.False(spec.Environment.ContainsKey("GEMINI_SYSTEM_MD"));
            Assert.Equal("my-agent", spec.DisplayName);
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Fact]
    public void Launch_errors_are_readable()
    {
        var tmp = NewTempDir();
        try
        {
            var noCommand = Assert.Throws<AcpAgentException>(() =>
                AcpLaunchBuilder.Build(ContextFor(ProviderPresets.CreateProfile(ProviderPresets.CustomAcpId), tmp), _ => @"C:\x.exe", tmp));
            Assert.Contains("no command", noCommand.Message);

            var noGemini = Assert.Throws<AcpAgentException>(() => AcpLaunchBuilder.Build(ContextFor(GeminiProfile(), tmp), _ => null, tmp));
            Assert.Contains("npm install -g @google/gemini-cli", noGemini.Message);

            var missingPath = CustomProfile();
            missingPath.CliPath = Path.Combine(tmp, "does-not-exist.exe");
            var missing = Assert.Throws<AcpAgentException>(() => AcpLaunchBuilder.Build(ContextFor(missingPath, tmp), _ => null, tmp));
            Assert.Contains("does not exist", missing.Message);

            var unknownName = CustomProfile();
            unknownName.CliPath = "no-such-agent";
            var unknown = Assert.Throws<AcpAgentException>(() => AcpLaunchBuilder.Build(ContextFor(unknownName, tmp), _ => null, tmp));
            Assert.Contains("Could not find the command \"no-such-agent\"", unknown.Message);
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Fact]
    public void An_extensionless_npm_script_path_uses_its_cmd_shim()
    {
        var tmp = NewTempDir();
        try
        {
            var script = Path.Combine(tmp, "agent");
            File.WriteAllText(script, "#!/bin/sh");
            File.WriteAllText(script + ".cmd", "@echo off");
            var profile = CustomProfile();
            profile.CliPath = script;

            var spec = AcpLaunchBuilder.Build(ContextFor(profile, tmp), _ => null, tmp);

            Assert.Equal(script + ".cmd", spec.ExecutablePath);
            Assert.EndsWith("cmd.exe", spec.FileName, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDir(tmp);
        }
    }

    [Theory]
    [InlineData("--acp", "^^^\"--acp^^^\"")]
    [InlineData("a b", "^^^\"a^^^ b^^^\"")]
    [InlineData("a&b|c", "^^^\"a^^^&b^^^|c^^^\"")]
    [InlineData("100%", "^^^\"100^^^%^^^\"")]
    [InlineData("", "^^^\"^^^\"")]
    public void Cmd_shim_arguments_are_double_escaped(string arg, string expected) =>
        Assert.Equal(expected, WindowsCommandLine.EscapeArgument(arg, doubleEscape: true));

    [Fact]
    public void Cmd_argument_quotes_and_backslashes_follow_the_C_runtime_rules()
    {
        Assert.Equal("^\"a\\\\\\^\"b^\"", WindowsCommandLine.EscapeArgument("a\\\"b", doubleEscape: false));
        Assert.Equal("^\"C:\\dir\\\\^\"", WindowsCommandLine.EscapeArgument("C:\\dir\\", doubleEscape: false));
        Assert.Equal("/d /s /c \"C:\\x^ y\\gemini.cmd ^^^\"--acp^^^\"\"", WindowsCommandLine.BuildCmdArguments(@"C:\x y\gemini.cmd", new[] { "--acp" }));
    }

    [Fact]
    public void System_prompt_block_wraps_the_prompt_before_the_user_text()
    {
        var text = AcpBackend.WithSystemPrompt("  Be careful.  ", "Open Paint", "deskpilot");
        Assert.StartsWith("<deskpilot_instructions>\n", text);
        Assert.Contains("Be careful.\n", text);
        Assert.Contains("mcp__deskpilot__screenshot", text);
        Assert.EndsWith("</deskpilot_instructions>\n\nOpen Paint", text);
    }

    // ------------------------------------------------------------------ permission rules

    [Theory]
    [InlineData("click (deskpilot MCP Server)", true)]
    [InlineData("mcp__deskpilot__click", true)]
    [InlineData("mcp_deskpilot_vault_search", true)]
    [InlineData("deskpilot/run_command", true)]
    [InlineData("  mcp__deskpilot__screenshot  ", true)]
    [InlineData("MCP__DESKPILOT__CLICK", true)]
    [InlineData("click (other MCP Server)", false)]
    [InlineData("format_disk (deskpilot MCP Server)", false)]
    [InlineData("mcp__deskpilot__format_disk", false)]
    [InlineData("mcp__deskpilot__click && del /q *", false)]
    [InlineData("echo deskpilot", false)]
    [InlineData("rm -rf C:\\deskpilot", false)]
    [InlineData("Run deskpilot click", false)]
    [InlineData("mcp__other__click", false)]
    [InlineData("", false)]
    public void Tool_calls_are_matched_on_exact_identities(string title, bool expected)
    {
        var call = Parse(new JsonObject { ["toolCallId"] = "1", ["title"] = title });
        Assert.Equal(expected, AcpPermissions.IsDeskPilotToolCall(call, default, "deskpilot", DeskPilotToolNames, mcpRestrictedToDeskPilot: false));
    }

    [Fact]
    public void Meta_and_raw_input_are_read_as_structure_only()
    {
        bool Check(JsonObject toolCall) => AcpPermissions.IsDeskPilotToolCall(Parse(toolCall), default, "deskpilot", DeskPilotToolNames, false);

        Assert.True(Check(new JsonObject { ["title"] = "Click", ["_meta"] = new JsonObject { ["claudeCode"] = new JsonObject { ["toolName"] = "mcp__deskpilot__click" } } }));
        Assert.True(Check(new JsonObject { ["title"] = "x", ["_meta"] = new JsonObject { ["mcp"] = new JsonObject { ["serverName"] = "deskpilot", ["toolName"] = "zoom_unknown", ["tool"] = "click" } } }));
        Assert.True(Check(new JsonObject { ["title"] = "x", ["rawInput"] = new JsonObject { ["server"] = "deskpilot", ["tool"] = "screenshot" } }));
        Assert.False(Check(new JsonObject { ["title"] = "x", ["rawInput"] = new JsonObject { ["server"] = "other", ["tool"] = "screenshot" } }));
        Assert.False(Check(new JsonObject { ["title"] = "x", ["rawInput"] = new JsonObject { ["command"] = "deskpilot click", ["arguments"] = new JsonObject { ["server"] = "deskpilot", ["tool"] = "click" } } }));
        Assert.False(Check(new JsonObject { ["title"] = "x", ["rawInput"] = new JsonObject { ["command"] = "mcp__deskpilot__click" } }));
        Assert.False(Check(new JsonObject { ["title"] = "x", ["_meta"] = new JsonObject { ["note"] = "mcp__deskpilot__click" } }));
    }

    [Fact]
    public void Mcp_confirmation_options_only_count_when_mcp_is_restricted_to_deskpilot()
    {
        var call = Parse(new JsonObject { ["title"] = "dir C:\\", ["kind"] = "other" });
        var options = Parse(GeminiMcpOptions());
        Assert.True(AcpPermissions.IsDeskPilotToolCall(call, options, "deskpilot", DeskPilotToolNames, mcpRestrictedToDeskPilot: true));
        Assert.False(AcpPermissions.IsDeskPilotToolCall(call, options, "deskpilot", DeskPilotToolNames, mcpRestrictedToDeskPilot: false));
        var execOptions = Parse(new JsonArray(Option("proceed_always", "Allow for this session", "allow_always"), Option("cancel", "Reject", "reject_once")));
        Assert.False(AcpPermissions.IsDeskPilotToolCall(call, execOptions, "deskpilot", DeskPilotToolNames, mcpRestrictedToDeskPilot: true));
    }

    [Fact]
    public void Option_choice_prefers_session_wide_allow_and_never_persists_when_avoidable()
    {
        Assert.Equal("proceed_always_server", AcpPermissions.PickAllowOption(Parse(GeminiMcpOptions())));
        Assert.Equal("once", AcpPermissions.PickAllowOption(Parse(new JsonArray(
            Option("save", "Allow tool for all future sessions", "allow_always"),
            Option("once", "Allow", "allow_once")))));
        Assert.Equal("save", AcpPermissions.PickAllowOption(Parse(new JsonArray(Option("save", "Allow for all future sessions", "allow_always")))));
        Assert.Null(AcpPermissions.PickAllowOption(Parse(new JsonArray(Option("no", "Reject", "reject_once")))));
        Assert.Null(AcpPermissions.PickAllowOption(default));

        Assert.Equal("no", AcpPermissions.PickRejectOption(Parse(new JsonArray(Option("yes", "Allow", "allow_once"), Option("never", "Never", "reject_always"), Option("no", "Reject", "reject_once")))));
        Assert.Null(AcpPermissions.PickRejectOption(Parse(new JsonArray(Option("never", "Never", "reject_always")))));
    }

    // ------------------------------------------------------------------ JSON-RPC connection

    [Fact]
    public async Task JsonRpc_correlates_out_of_order_responses()
    {
        await using var pair = new ConnectionPair();
        var first = pair.Connection.SendRequestAsync("first", new JsonObject { ["a"] = 1 });
        var second = pair.Connection.SendRequestAsync("second", null);
        var m1 = await pair.ReadAsync();
        var m2 = await pair.ReadAsync();

        Assert.Equal("2.0", m1.GetProperty("jsonrpc").GetString());
        Assert.Equal("first", Method(m1));
        Assert.Equal(1, m1.GetProperty("params").GetProperty("a").GetInt32());
        Assert.False(m2.TryGetProperty("params", out _));

        await pair.WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{m2.GetProperty("id").GetInt64()},\"result\":{{\"v\":2}}}}");
        await pair.WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":\"{m1.GetProperty("id").GetInt64()}\",\"result\":{{\"v\":1}}}}");

        Assert.Equal(1, (await first).GetProperty("v").GetInt32());
        Assert.Equal(2, (await second).GetProperty("v").GetInt32());
    }

    [Fact]
    public async Task JsonRpc_error_responses_become_exceptions()
    {
        await using var pair = new ConnectionPair();
        var request = pair.Connection.SendRequestAsync("x", null);
        var m = await pair.ReadAsync();
        await pair.WriteAsync($"{{\"jsonrpc\":\"2.0\",\"id\":{m.GetProperty("id").GetInt64()},\"error\":{{\"code\":-32000,\"message\":\"Authentication required\",\"data\":{{\"why\":\"login\"}}}}}}");

        var ex = await Assert.ThrowsAsync<JsonRpcException>(() => request);
        Assert.Equal(-32000, ex.Code);
        Assert.Equal("Authentication required", ex.Message);
        Assert.Contains("login", ex.DataJson);
    }

    [Fact]
    public async Task JsonRpc_delivers_notifications_in_order_and_skips_noise()
    {
        await using var pair = new ConnectionPair();
        await pair.WriteAsync("this is a log line, not JSON");
        await pair.WriteAsync("");
        await pair.WriteAsync("\uFEFF{\"jsonrpc\":\"2.0\",\"method\":\"n\",\"params\":{\"i\":1}}");
        await pair.WriteAsync("[1,2,3]");
        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"n\",\"params\":{\"i\":2}}");
        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":999,\"result\":{}}");
        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"method\":\"n\",\"params\":{\"i\":3}}");

        await WaitUntil(() => pair.Notifications.Count == 3);
        Assert.Equal(new[] { 1, 2, 3 }, pair.Notifications.Select(n => n.GetProperty("i").GetInt32()));
    }

    [Fact]
    public async Task JsonRpc_answers_incoming_requests_with_the_same_id()
    {
        await using var pair = new ConnectionPair((method, p, ct) => method switch
        {
            "echo" => Task.FromResult<JsonNode?>(new JsonObject { ["got"] = p.GetProperty("v").GetString() }),
            "boom" => throw new InvalidOperationException("kaput"),
            _ => Task.FromException<JsonNode?>(new JsonRpcException(JsonRpcConnection.MethodNotFound, "Method not found: " + method)),
        });

        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":\"abc\",\"method\":\"echo\",\"params\":{\"v\":\"hi\"}}");
        var echo = await pair.ReadAsync();
        Assert.Equal("abc", echo.GetProperty("id").GetString());
        Assert.Equal("hi", echo.GetProperty("result").GetProperty("got").GetString());

        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"nope\"}");
        var nope = await pair.ReadAsync();
        Assert.Equal(7, nope.GetProperty("id").GetInt32());
        Assert.Equal(-32601, nope.GetProperty("error").GetProperty("code").GetInt32());

        await pair.WriteAsync("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"boom\"}");
        var boom = await pair.ReadAsync();
        Assert.Equal(-32603, boom.GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task JsonRpc_closing_the_input_fails_pending_and_later_requests()
    {
        await using var pair = new ConnectionPair();
        var pending = pair.Connection.SendRequestAsync("x", null);
        await pair.ReadAsync();

        pair.CloseInput();

        await Assert.ThrowsAsync<JsonRpcConnectionClosedException>(() => pending);
        await pair.Connection.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pair.Connection.IsClosed);
        await Assert.ThrowsAsync<JsonRpcConnectionClosedException>(() => pair.Connection.SendRequestAsync("y", null));
        await Assert.ThrowsAsync<JsonRpcConnectionClosedException>(() => pair.Connection.SendNotificationAsync("z", null));
    }

    // ------------------------------------------------------------------ live (opt-in)

    [Fact]
    public async Task Live_gemini_cli_answers_initialize_and_session_new()
    {
        if (Environment.GetEnvironmentVariable("DESKPILOT_LIVE_TESTS") != "1") return;
        if (ExecutableLocator.Find("gemini") == null)
        {
            _output.WriteLine("gemini not installed; skipped.");
            return;
        }

        var tmp = NewTempDir();
        var backend = new AcpBackend(AcpProcessTransport.Start, null, Path.Combine(tmp, "temp")) { Logger = m => _output.WriteLine(m) };
        var ctx = new AgentBackendContext
        {
            Settings = new AppSettings(),
            Profile = ProviderPresets.CreateProfile(ProviderPresets.GeminiCliId),
            SystemPrompt = "You are a test. Reply with the single word OK.",
            Tools = new FakeTools(),
            Emit = e => _output.WriteLine("event: " + e),
            Control = new AgentRunControl(),
            WorkingDirectory = tmp,
            Mcp = null,
        };
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            try
            {
                // Only initialize and session/new: no prompt is sent, so nothing is billed.
                await backend.StartAsync(ctx, cts.Token);
                _output.WriteLine("Session opened: " + backend.SessionId);
                Assert.False(string.IsNullOrEmpty(backend.SessionId));
            }
            catch (AcpAgentException ex)
            {
                _output.WriteLine("Start failed: " + ex.Message);
                Assert.Contains("not logged in", ex.Message);
            }
            _output.WriteLine("Agent stderr tail:\n" + backend.StderrTail);
        }
        finally
        {
            await backend.DisposeAsync();
            DeleteDir(tmp);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met in time.");
            await Task.Delay(5);
        }
    }

    private static string? Method(JsonElement m) =>
        m.TryGetProperty("method", out var x) && x.ValueKind == JsonValueKind.String ? x.GetString() : null;

    private static string SessionOf(JsonElement p) => p.GetProperty("sessionId").GetString()!;

    private static string PromptText(JsonElement messageOrParams)
    {
        var p = messageOrParams.TryGetProperty("params", out var inner) ? inner : messageOrParams;
        return p.GetProperty("prompt")[0].GetProperty("text").GetString()!;
    }

    private static JsonElement Parse(JsonNode node)
    {
        using var doc = JsonDocument.Parse(node.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static string SelectedOption(JsonElement response)
    {
        var outcome = response.GetProperty("result").GetProperty("outcome");
        return outcome.GetProperty("outcome").GetString() == "selected" ? outcome.GetProperty("optionId").GetString()! : "cancelled";
    }

    private static void AssertPair(List<string> args, string flag, string value)
    {
        var i = args.IndexOf(flag);
        Assert.True(i >= 0 && i + 1 < args.Count, $"{flag} missing");
        Assert.Equal(value, args[i + 1]);
    }

    private static JsonObject Chunk(string kind, string text) => new()
    {
        ["sessionUpdate"] = kind,
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    };

    private static JsonObject ToolCall(string id, string title, string kind) => new()
    {
        ["sessionUpdate"] = "tool_call",
        ["toolCallId"] = id,
        ["title"] = title,
        ["kind"] = kind,
        ["status"] = "pending",
    };

    private static JsonObject Option(string id, string name, string kind) => new() { ["optionId"] = id, ["name"] = name, ["kind"] = kind };

    private static JsonArray GeminiMcpOptions() => new(
        Option("proceed_always_server", "Allow all server tools for this session", "allow_always"),
        Option("proceed_always_tool", "Allow tool for this session", "allow_always"),
        Option("proceed_always_and_save", "Allow tool for all future sessions", "allow_always"),
        Option("proceed_once", "Allow", "allow_once"),
        Option("cancel", "Reject", "reject_once"));

    private static JsonObject PermissionRequest(string sessionId, string callId, string title, string kind, JsonArray options) => new()
    {
        ["sessionId"] = sessionId,
        ["toolCall"] = new JsonObject { ["toolCallId"] = callId, ["title"] = title, ["kind"] = kind, ["status"] = "pending" },
        ["options"] = options,
    };

    private static JsonArray GeminiAuthMethods() => new(
        new JsonObject { ["id"] = "oauth-personal", ["name"] = "Log in with Google" },
        new JsonObject { ["id"] = "gemini-api-key", ["name"] = "Gemini API key", ["_meta"] = new JsonObject { ["api-key"] = new JsonObject { ["provider"] = "google" } } },
        new JsonObject { ["id"] = "vertex-ai", ["name"] = "Vertex AI" });

    private static ProviderProfile CustomProfile() => new()
    {
        Name = "Fake agent",
        Kind = ProviderKind.AcpAgent,
        PresetId = ProviderPresets.CustomAcpId,
        CliPath = "fake-agent",
        ExtraCliArgs = "--stdio",
    };

    private static ProviderProfile GeminiProfile() => ProviderPresets.CreateProfile(ProviderPresets.GeminiCliId);

    private static AgentBackendContext ContextFor(ProviderProfile profile, string workDir, string apiKey = "") => new()
    {
        Settings = new AppSettings(),
        Profile = profile,
        SystemPrompt = SystemPromptText,
        Tools = new FakeTools(),
        Emit = _ => { },
        Control = new AgentRunControl(),
        WorkingDirectory = workDir,
        Mcp = new McpEndpointInfo("deskpilot", @"C:\Apps\DeskPilot\DeskPilot.exe", new[] { "--mcp-bridge", "pipe-1", "token-1" }),
        ApiKey = apiKey,
    };

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-acp-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void DeleteDir(string dir)
    {
        try { Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class FakeTools : IToolHost
    {
        public IReadOnlyList<ToolSpec> GetTools() =>
            DeskPilotToolNames.Select(n => ToolSpec.Create(n, n, "{\"type\":\"object\"}")).ToList();

        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Ok("ok"));
    }

    /// <summary>A backend wired to in-memory fake agents; each (re)start creates a new FakeAgent.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly List<FakeAgent> _agents = new();

        public Harness(Action<FakeAgent>? onAgent = null, ProviderProfile? profile = null, string apiKey = "")
        {
            OnAgent = onAgent ?? (_ => { });
            TempDir = NewTempDir();
            WorkDir = Path.Combine(TempDir, "work");
            Directory.CreateDirectory(WorkDir);
            Profile = profile ?? CustomProfile();
            Context = new AgentBackendContext
            {
                Settings = new AppSettings(),
                Profile = Profile,
                SystemPrompt = SystemPromptText,
                Tools = new FakeTools(),
                Emit = e => Events.Enqueue(e),
                Control = Control,
                WorkingDirectory = WorkDir,
                Mcp = new McpEndpointInfo("deskpilot", @"C:\Apps\DeskPilot\DeskPilot.exe", new[] { "--mcp-bridge", "pipe-1", "token-1" }),
                ApiKey = apiKey,
            };
            Backend = new AcpBackend(CreateAgent, name => @"C:\fake-bin\" + name + ".exe", Path.Combine(TempDir, "temp")) { Logger = _ => { } };
        }

        public Action<FakeAgent> OnAgent { get; set; }
        public ConcurrentQueue<AgentEvent> Events { get; } = new();
        public AgentRunControl Control { get; } = new();
        public string TempDir { get; }
        public string WorkDir { get; }
        public ProviderProfile Profile { get; }
        public AgentBackendContext Context { get; }
        public AcpBackend Backend { get; }

        public List<FakeAgent> Agents
        {
            get { lock (_agents) return _agents.ToList(); }
        }

        public FakeAgent Agent
        {
            get { lock (_agents) return _agents[^1]; }
        }

        public Task StartAsync() => Backend.StartAsync(Context, CancellationToken.None);

        public Task<TurnResult> TurnAsync(string text, CancellationToken ct = default)
        {
            Control.BeginTurn();
            return Backend.RunTurnAsync(new UserTurn(text), ct);
        }

        public List<T> EventsOf<T>() => Events.OfType<T>().ToList();

        public List<string> StatusMessages() => EventsOf<StatusEvent>().Select(s => s.Message).ToList();

        private IAcpTransport CreateAgent(AcpLaunchSpec spec)
        {
            var agent = new FakeAgent(spec);
            OnAgent(agent);
            lock (_agents) _agents.Add(agent);
            agent.Start();
            return agent;
        }

        public async ValueTask DisposeAsync()
        {
            await Backend.DisposeAsync();
            DeleteDir(TempDir);
        }
    }

    /// <summary>An ACP agent that lives in memory: scripted answers over newline-delimited JSON-RPC.</summary>
    private sealed class FakeAgent : IAcpTransport
    {
        private readonly MemoryPipeStream _toClient = new();
        private readonly MemoryPipeStream _toAgent = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private long _nextId = 100;
        private int _sessions;
        private int? _exitCode;

        public FakeAgent(AcpLaunchSpec spec) => Spec = spec;

        public AcpLaunchSpec Spec { get; }
        public ConcurrentQueue<JsonElement> Received { get; } = new();
        public bool ImageSupport { get; set; }
        public int ProtocolVersion { get; set; } = 1;
        public JsonArray? AuthMethods { get; set; }
        /// <summary>Custom handling of the client's requests; return true when handled.</summary>
        public Func<FakeAgent, string, JsonElement, JsonElement, Task<bool>>? OnRequest { get; set; }
        public volatile bool Killed;
        public string Stderr = "";

        public Stream FromAgent => _toClient;
        public Stream ToAgent => _toAgent;
        public Task Exited => _exited.Task;
        public int? ExitCode => _exitCode;
        public string StderrTail => Stderr;

        public void Kill()
        {
            Killed = true;
            Terminate(1);
        }

        public void Crash(int exitCode, string stderr)
        {
            Stderr = stderr;
            Terminate(exitCode);
        }

        private void Terminate(int code)
        {
            _exitCode ??= code;
            _toClient.Complete();
            _toAgent.Complete();
            _exited.TrySetResult();
        }

        public ValueTask DisposeAsync()
        {
            Terminate(0);
            return ValueTask.CompletedTask;
        }

        public void Start() => _ = Task.Run(LoopAsync);

        public IEnumerable<JsonElement> ReceivedMethod(string method) => Received.Where(m => Method(m) == method).ToList();

        public Task WaitForAsync(string method, int count = 1) => WaitUntil(() => ReceivedMethod(method).Count() >= count);

        private async Task LoopAsync()
        {
            using var reader = new StreamReader(_toAgent, new UTF8Encoding(false));
            while (true)
            {
                string? line;
                try { line = await reader.ReadLineAsync(); }
                catch (Exception) { return; }
                if (line == null) return;

                JsonElement message;
                using (var doc = JsonDocument.Parse(line)) message = doc.RootElement.Clone();
                Received.Enqueue(message);

                var method = Method(message);
                var hasId = message.TryGetProperty("id", out var id);
                var p = message.TryGetProperty("params", out var pp) ? pp : default;
                if (method != null && hasId) _ = Task.Run(() => HandleRequestAsync(method, id, p));
                else if (method == null && hasId && id.ValueKind == JsonValueKind.Number && _pending.TryRemove(id.GetInt64(), out var tcs)) tcs.TrySetResult(message);
            }
        }

        private async Task HandleRequestAsync(string method, JsonElement id, JsonElement p)
        {
            try
            {
                if (OnRequest != null && await OnRequest(this, method, id, p)) return;
                switch (method)
                {
                    case "initialize":
                        await RespondAsync(id, new JsonObject
                        {
                            ["protocolVersion"] = ProtocolVersion,
                            ["agentCapabilities"] = new JsonObject
                            {
                                ["loadSession"] = false,
                                ["promptCapabilities"] = new JsonObject { ["image"] = ImageSupport, ["audio"] = false, ["embeddedContext"] = false },
                            },
                            ["agentInfo"] = new JsonObject { ["name"] = "fake-agent", ["title"] = "Fake Agent", ["version"] = "1.0" },
                            ["authMethods"] = AuthMethods?.DeepClone() ?? new JsonArray(),
                        });
                        break;
                    case "session/new":
                        await RespondAsync(id, new JsonObject { ["sessionId"] = "sess-" + Interlocked.Increment(ref _sessions) });
                        break;
                    case "authenticate":
                        await RespondAsync(id, new JsonObject());
                        break;
                    case "session/prompt":
                        await RespondAsync(id, new JsonObject { ["stopReason"] = "end_turn" });
                        break;
                    default:
                        await ErrorAsync(id, -32601, "Method not found: " + method);
                        break;
                }
            }
            catch (Exception)
            {
                // The client went away mid-script (killed or disposed); the test asserts on outcomes instead.
            }
        }

        private async Task SendAsync(JsonObject message)
        {
            var bytes = Encoding.UTF8.GetBytes(message.ToJsonString() + "\n");
            await _writeLock.WaitAsync();
            try { await _toClient.WriteAsync(bytes); }
            finally { _writeLock.Release(); }
        }

        public Task RespondAsync(JsonElement id, JsonNode? result) =>
            SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result });

        public Task ErrorAsync(JsonElement id, int code, string message) =>
            SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = JsonNode.Parse(id.GetRawText()),
                ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
            });

        public Task UpdateAsync(string sessionId, JsonObject update) =>
            SendAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "session/update",
                ["params"] = new JsonObject { ["sessionId"] = sessionId, ["update"] = update },
            });

        /// <summary>Sends a request to the client and returns its whole response message.</summary>
        public async Task<JsonElement> RequestAsync(string method, JsonObject parameters)
        {
            var id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            await SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters });
            return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A JsonRpcConnection talking to the test through two in-memory pipes.</summary>
    private sealed class ConnectionPair : IAsyncDisposable
    {
        private readonly MemoryPipeStream _toConnection = new();
        private readonly MemoryPipeStream _fromConnection = new();
        private readonly StreamReader _reader;

        public ConnectionPair(JsonRpcConnection.RequestHandler? onRequest = null)
        {
            Connection = new JsonRpcConnection(_toConnection, _fromConnection, onRequest, (_, p) => Notifications.Enqueue(p));
            _reader = new StreamReader(_fromConnection, new UTF8Encoding(false));
            Connection.Start();
        }

        public JsonRpcConnection Connection { get; }
        public ConcurrentQueue<JsonElement> Notifications { get; } = new();

        public Task WriteAsync(string line) => _toConnection.WriteAsync(Encoding.UTF8.GetBytes(line + "\n")).AsTask();

        public async Task<JsonElement> ReadAsync()
        {
            var line = await _reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.Clone();
        }

        public void CloseInput() => _toConnection.Complete();

        public async ValueTask DisposeAsync()
        {
            _toConnection.Complete();
            _fromConnection.Complete();
            await Connection.DisposeAsync();
        }
    }

    /// <summary>A one-way in-memory byte pipe: writes become readable in order; Complete() means end of stream.</summary>
    private sealed class MemoryPipeStream : Stream
    {
        private readonly Channel<byte[]> _channel = Channel.CreateUnbounded<byte[]>();
        private ReadOnlyMemory<byte> _current;

        public void Complete() => _channel.Writer.TryComplete();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_current.IsEmpty)
            {
                if (!await _channel.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (_channel.Reader.TryRead(out var next)) _current = next;
            }
            var n = Math.Min(buffer.Length, _current.Length);
            _current[..n].CopyTo(buffer);
            _current = _current[n..];
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (!_channel.Writer.TryWrite(buffer.AsSpan(offset, count).ToArray())) throw new IOException("The pipe is closed.");
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Write(buffer, offset, count);
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_channel.Writer.TryWrite(buffer.ToArray())) throw new IOException("The pipe is closed.");
            return ValueTask.CompletedTask;
        }
    }
}

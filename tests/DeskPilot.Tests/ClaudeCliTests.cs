using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Cli;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using Xunit.Abstractions;

namespace DeskPilot.Tests;

public sealed class ClaudeCliTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "deskpilot-cli-test-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;

    public ClaudeCliTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ================================================================== recorded stream-json lines

    private const string InitConnected =
        """{"type":"system","subtype":"init","cwd":"C:\\Temp\\agent-workdir","session_id":"8f3c2a1e-0d4b-4c55-9a6e-1b2c3d4e5f60","tools":["mcp__deskpilot__screenshot","mcp__deskpilot__click"],"mcp_servers":[{"name":"deskpilot","status":"connected"}],"model":"claude-haiku-5-5","permissionMode":"default","slash_commands":[],"apiKeySource":"none","claude_code_version":"2.1.293","output_style":"default","agents":[],"skills":[],"plugins":[],"uuid":"0c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e4f"}""";

    private const string InitFailed =
        """{"type":"system","subtype":"init","cwd":"C:\\Temp\\agent-workdir","session_id":"8f3c2a1e-0d4b-4c55-9a6e-1b2c3d4e5f60","tools":[],"mcp_servers":[{"name":"deskpilot","status":"failed"}],"model":"claude-haiku-5-5","permissionMode":"default","apiKeySource":"none","claude_code_version":"2.1.293","uuid":"0c1d2e3f-4a5b-4c6d-8e7f-9a0b1c2d3e40"}""";

    private const string InitNoServers =
        """{"type":"system","subtype":"init","cwd":"C:\\Temp\\agent-workdir","session_id":"s","tools":[],"mcp_servers":[],"model":"claude-haiku-5-5","uuid":"u"}""";

    private const string AssistantEmptyThinking =
        """{"type":"assistant","message":{"id":"msg_01AbCdEf","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"thinking","thinking":"","signature":"EqQBCkYIBxgCKkBzdW1tYXJ5LW9mZg=="}],"stop_reason":null,"stop_sequence":null,"usage":{"input_tokens":3,"cache_creation_input_tokens":2048,"cache_read_input_tokens":0,"output_tokens":8,"service_tier":"standard"}},"parent_tool_use_id":null,"session_id":"s","uuid":"a1"}""";

    private const string AssistantThinking =
        """{"type":"assistant","message":{"id":"msg_01AbCdEg","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"thinking","thinking":"The user wants a screenshot first.","signature":"EqQBCkYI"}],"stop_reason":null,"usage":{"input_tokens":3,"output_tokens":12}},"parent_tool_use_id":null,"session_id":"s","uuid":"a2"}""";

    private const string AssistantText =
        """{"type":"assistant","message":{"id":"msg_01AbCdEh","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"Taking a screenshot first."}],"stop_reason":null,"usage":{"input_tokens":3,"output_tokens":7}},"parent_tool_use_id":null,"session_id":"s","uuid":"a3"}""";

    private const string AssistantToolUse =
        """{"type":"assistant","message":{"id":"msg_01AbCdEi","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"tool_use","id":"toolu_01XyZ","name":"mcp__deskpilot__screenshot","input":{}}],"stop_reason":"tool_use","usage":{"input_tokens":3,"output_tokens":20}},"parent_tool_use_id":null,"session_id":"s","uuid":"a4"}""";

    private const string UserToolResultEcho =
        """{"type":"user","message":{"role":"user","content":[{"tool_use_id":"toolu_01XyZ","type":"tool_result","content":[{"type":"text","text":"Screenshot 1280x720, foreground: Notepad"},{"type":"image","source":{"type":"base64","media_type":"image/jpeg","data":"/9j/4AAQSkZJRgABAQ"}}]}]},"parent_tool_use_id":null,"session_id":"s","uuid":"u1"}""";

    private const string AssistantFinal =
        """{"type":"assistant","message":{"id":"msg_01AbCdEj","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"OK"}],"stop_reason":"end_turn","usage":{"input_tokens":3,"output_tokens":2}},"parent_tool_use_id":null,"session_id":"s","uuid":"a5"}""";

    private const string RateAllowed =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":1791460800,"rateLimitType":"five_hour","overageStatus":"rejected","isUsingOverage":false},"uuid":"r1","session_id":"s"}""";

    private const string RateWarning =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"allowed_warning","resetsAt":1791460800,"rateLimitType":"five_hour","utilization":0.92,"isUsingOverage":false},"uuid":"r2","session_id":"s"}""";

    private const string RateRejected =
        """{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1791460800,"rateLimitType":"seven_day","isUsingOverage":false},"uuid":"r3","session_id":"s"}""";

    private const string ControlResponseTemplate =
        """{"type":"control_response","response":{"subtype":"success","request_id":"REQ","response":{}}}""";

    private const string ResultSuccess =
        """{"type":"result","subtype":"success","is_error":false,"duration_ms":2310,"duration_api_ms":2101,"num_turns":3,"result":"OK","stop_reason":"end_turn","session_id":"s","total_cost_usd":0.0004235,"usage":{"input_tokens":3,"cache_creation_input_tokens":2048,"cache_read_input_tokens":512,"output_tokens":5,"server_tool_use":{"web_search_requests":0,"web_fetch_requests":0},"service_tier":"standard","cache_creation":{"ephemeral_1h_input_tokens":0,"ephemeral_5m_input_tokens":2048}},"modelUsage":{"claude-haiku-5-5":{"inputTokens":3,"outputTokens":5,"cacheReadInputTokens":512,"cacheCreationInputTokens":2048,"webSearchRequests":0,"costUSD":0.0004235,"contextWindow":200000,"maxOutputTokens":32000}},"permission_denials":[],"terminal_reason":"completed","uuid":"res1"}""";

    private const string ResultAborted =
        """{"type":"result","subtype":"error_during_execution","duration_ms":1500,"duration_api_ms":900,"is_error":true,"num_turns":1,"stop_reason":null,"session_id":"s","total_cost_usd":0.0002,"usage":{"input_tokens":3,"cache_creation_input_tokens":0,"cache_read_input_tokens":100,"output_tokens":4},"modelUsage":{},"permission_denials":[],"errors":["Request was aborted."],"terminal_reason":"aborted_streaming","uuid":"res2"}""";

    private const string AssistantAuthSynthetic =
        """{"type":"assistant","message":{"id":"7a9c0b1d","container":null,"model":"<synthetic>","role":"assistant","stop_reason":"stop_sequence","stop_sequence":"","type":"message","usage":{"input_tokens":0,"output_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0},"content":[{"type":"text","text":"Not logged in · Please run /login"}]},"parent_tool_use_id":null,"session_id":"s","uuid":"a9","error":"authentication_failed"}""";

    private const string ResultAuthError =
        """{"type":"result","subtype":"success","is_error":true,"duration_ms":312,"duration_api_ms":0,"num_turns":1,"result":"Not logged in · Please run /login","stop_reason":"stop_sequence","session_id":"s","total_cost_usd":0,"usage":{"input_tokens":0,"cache_creation_input_tokens":0,"cache_read_input_tokens":0,"output_tokens":0},"modelUsage":{},"permission_denials":[],"terminal_reason":"completed","uuid":"res3"}""";

    private const string ResultApiError =
        """{"type":"result","subtype":"success","is_error":true,"duration_ms":900,"num_turns":1,"result":"API Error: 500 {\"type\":\"error\",\"error\":{\"type\":\"api_error\",\"message\":\"Internal server error\"}}","session_id":"s","total_cost_usd":0,"usage":{"input_tokens":0,"output_tokens":0},"terminal_reason":"completed","uuid":"res4"}""";

    private const string ThinkingTokensSystem =
        """{"type":"system","subtype":"thinking_tokens","tokens":0,"session_id":"s","uuid":"t1"}""";

    // ================================================================== arguments

    private static ProviderProfile Profile(Action<ProviderProfile>? configure = null)
    {
        var p = ProviderPresets.CreateProfile(ProviderPresets.ClaudeSubscriptionId);
        configure?.Invoke(p);
        return p;
    }

    [Fact]
    public void Arguments_have_every_flag_in_stable_order()
    {
        var profile = Profile(p =>
        {
            p.Model = "sonnet";
            p.Effort = "high";
            p.ExtraCliArgs = "--fallback-model \"claude opus\" --debug";
        });
        var args = ClaudeCliCommand.BuildArguments(profile, @"C:\t\prompt.md", @"C:\t\mcp.json", "deskpilot");
        Assert.Equal(new[]
        {
            "-p",
            "--input-format", "stream-json",
            "--output-format", "stream-json",
            "--verbose",
            "--model", "sonnet",
            "--system-prompt-file", @"C:\t\prompt.md",
            "--tools", "",
            "--strict-mcp-config",
            "--mcp-config", @"C:\t\mcp.json",
            "--allowedTools", "mcp__deskpilot",
            "--setting-sources", "",
            "--disable-slash-commands",
            "--no-session-persistence",
            "--effort", "high",
            "--fallback-model", "claude opus",
            "--debug",
        }, args);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Arguments_default_model_is_haiku(string model)
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(p => p.Model = model), "p.md", "m.json", "deskpilot");
        Assert.Equal("haiku", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public void Arguments_trim_the_model()
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(p => p.Model = "  claude-sonnet-5-5 "), "p.md", null, null);
        Assert.Equal("claude-sonnet-5-5", args[args.IndexOf("--model") + 1]);
    }

    [Fact]
    public void Arguments_omit_effort_when_not_set()
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(p => p.Effort = ""), "p.md", "m.json", "deskpilot");
        Assert.DoesNotContain("--effort", args);
        Assert.Equal("--no-session-persistence", args[^1]);
    }

    [Theory]
    [InlineData("low", "low")]
    [InlineData(" MAX ", "max")]
    [InlineData("xhigh", "xhigh")]
    [InlineData("none", "low")]
    [InlineData("minimal", "low")]
    public void Arguments_normalize_effort(string effort, string expected)
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(p => p.Effort = effort), "p.md", null, null);
        Assert.Equal(expected, args[args.IndexOf("--effort") + 1]);
    }

    [Fact]
    public void Arguments_without_mcp_keep_strict_config_only()
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(), "p.md", null, null);
        Assert.Contains("--strict-mcp-config", args);
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--allowedTools", args);
        Assert.Equal("", args[args.IndexOf("--tools") + 1]);
        Assert.Equal("", args[args.IndexOf("--setting-sources") + 1]);
    }

    [Fact]
    public void Arguments_allow_the_named_mcp_server()
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(), "p.md", "m.json", "desk2");
        Assert.Equal("mcp__desk2", args[args.IndexOf("--allowedTools") + 1]);
    }

    [Fact]
    public void Extra_cli_args_are_split_like_a_shell()
    {
        var args = ClaudeCliCommand.BuildArguments(Profile(p => p.ExtraCliArgs = "--a \"b c\" d\\\"e"), "p.md", null, null);
        Assert.Equal(new[] { "--a", "b c", "d\"e" }, args.TakeLast(3));
    }

    // ================================================================== process start info

    [Fact]
    public void Start_info_for_native_exe_passes_arguments_verbatim()
    {
        var args = new List<string> { "-p", "--tools", "", "a b" };
        var psi = ClaudeCliCommand.BuildStartInfo(@"C:\Tools\claude.exe", args, _dir, Profile());
        Assert.Equal(@"C:\Tools\claude.exe", psi.FileName);
        Assert.Equal(args, psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.True(psi.CreateNoWindow);
        Assert.Equal(_dir, psi.WorkingDirectory);
        Assert.Empty(psi.StandardInputEncoding!.GetPreamble());
        Assert.Empty(psi.StandardOutputEncoding!.GetPreamble());
        Assert.Equal(Encoding.UTF8.WebName, psi.StandardInputEncoding.WebName);
    }

    [Fact]
    public void Start_info_wraps_cmd_shims_in_cmd_exe()
    {
        var psi = ClaudeCliCommand.BuildStartInfo(@"C:\npm\claude.cmd", new[] { "-p", "", "a b" }, _dir, Profile());
        Assert.EndsWith("cmd.exe", psi.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(psi.ArgumentList);
        Assert.Equal("/d /s /c \"C:\\npm\\claude.cmd ^^^\"-p^^^\" ^^^\"^^^\" ^^^\"a^^^ b^^^\"\"", psi.Arguments);
    }

    [Fact]
    public void Cmd_wrapping_escapes_the_script_path_and_tricky_arguments()
    {
        var line = ClaudeCliCommand.BuildCmdArguments(@"C:\My Tools (x86)\claude.bat", new[] { "100%", "trail\\", "he said \"hi\"", "a&b" });
        Assert.Equal(
            "/d /s /c \"C:\\My^ Tools^ ^(x86^)\\claude.bat ^^^\"100^^^%^^^\" ^^^\"trail\\\\^^^\" ^^^\"he^^^ said^^^ \\^^^\"hi\\^^^\"^^^\" ^^^\"a^^^&b^^^\"\"",
            line);
    }

    [Theory]
    [InlineData("", "^^^\"^^^\"")]
    [InlineData("plain", "^^^\"plain^^^\"")]
    [InlineData("%PATH%", "^^^\"^^^%PATH^^^%^^^\"")]
    [InlineData("x\\\\\"y", "^^^\"x\\\\\\\\\\^^^\"y^^^\"")]
    public void Cmd_argument_escaping_matches_the_verified_scheme(string arg, string expected)
    {
        Assert.Equal(expected, ClaudeCliCommand.EscapeCmdArgument(arg, doubleEscape: true));
    }

    [Theory]
    [InlineData(@"C:\x\claude.cmd", true)]
    [InlineData(@"C:\x\CLAUDE.BAT", true)]
    [InlineData(@"C:\x\claude.exe", false)]
    [InlineData(@"C:\x\claude", false)]
    public void Shell_scripts_are_detected(string path, bool expected) => Assert.Equal(expected, ClaudeCliCommand.IsShellScript(path));

    [Fact]
    public void Native_exe_is_preferred_over_a_shim_in_the_same_folder()
    {
        var npm = Directory.CreateDirectory(Path.Combine(_dir, "npm")).FullName;
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "");
        Assert.Equal(Path.Combine(npm, "claude.cmd"), ClaudeCliCommand.PreferNative(Path.Combine(npm, "claude.cmd"), Array.Empty<string>()));

        File.WriteAllText(Path.Combine(npm, "claude.exe"), "");
        Assert.Equal(Path.Combine(npm, "claude.exe"), ClaudeCliCommand.PreferNative(Path.Combine(npm, "claude.cmd"), Array.Empty<string>()));
    }

    [Fact]
    public void Native_exe_is_found_in_other_search_folders()
    {
        var npm = Directory.CreateDirectory(Path.Combine(_dir, "npm")).FullName;
        var native = Directory.CreateDirectory(Path.Combine(_dir, "native")).FullName;
        File.WriteAllText(Path.Combine(npm, "claude.cmd"), "");
        File.WriteAllText(Path.Combine(native, "claude.exe"), "");
        var empty = Directory.CreateDirectory(Path.Combine(_dir, "empty")).FullName;
        Assert.Equal(Path.Combine(native, "claude.exe"), ClaudeCliCommand.PreferNative(Path.Combine(npm, "claude.cmd"), new[] { empty, native }));
        // A native path is returned untouched.
        Assert.Equal(@"C:\elsewhere\claude.exe", ClaudeCliCommand.PreferNative(@"C:\elsewhere\claude.exe", new[] { native }));
    }

    // ================================================================== environment

    private static Dictionary<string, string?> Env(params (string, string)[] vars)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in vars) d[k] = v;
        return d;
    }

    [Fact]
    public void Env_disables_thinking_only_when_off()
    {
        var off = Env();
        ClaudeCliCommand.ApplyEnvironment(off, Profile(p => p.Thinking = ThinkingMode.Off));
        Assert.Equal("0", off["MAX_THINKING_TOKENS"]);

        foreach (var mode in new[] { ThinkingMode.Auto, ThinkingMode.On })
        {
            var env = Env();
            ClaudeCliCommand.ApplyEnvironment(env, Profile(p => p.Thinking = mode));
            Assert.False(env.ContainsKey("MAX_THINKING_TOKENS"));
        }
    }

    [Fact]
    public void Env_removes_api_keys_when_forcing_the_subscription_login()
    {
        var env = Env(("ANTHROPIC_API_KEY", "sk-x"), ("anthropic_auth_token", "tok"), ("PATH", @"C:\bin"));
        ClaudeCliCommand.ApplyEnvironment(env, Profile(p => p.ForceSubscriptionLogin = true));
        Assert.False(env.ContainsKey("ANTHROPIC_API_KEY"));
        Assert.False(env.ContainsKey("ANTHROPIC_AUTH_TOKEN"));
        Assert.Equal(@"C:\bin", env["PATH"]);

        var keep = Env(("ANTHROPIC_API_KEY", "sk-x"));
        ClaudeCliCommand.ApplyEnvironment(keep, Profile(p => p.ForceSubscriptionLogin = false));
        Assert.Equal("sk-x", keep["ANTHROPIC_API_KEY"]);
    }

    [Fact]
    public void Env_applies_extra_variables_last()
    {
        var env = Env(("FOO", "old"), ("REMOVE_ME", "x"));
        ClaudeCliCommand.ApplyEnvironment(env, Profile(p =>
        {
            p.Thinking = ThinkingMode.Off;
            p.ExtraEnv = new Dictionary<string, string> { ["foo"] = "new", ["MAX_THINKING_TOKENS"] = "1024", ["REMOVE_ME"] = "", ["  "] = "ignored" };
        }));
        Assert.Equal("new", env["FOO"]);
        Assert.Equal("1024", env["MAX_THINKING_TOKENS"]);
        Assert.False(env.ContainsKey("REMOVE_ME"));
        Assert.Single(env, kv => kv.Key.Equals("foo", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Env_drops_variables_of_a_parent_claude_code_session()
    {
        var env = Env(("CLAUDECODE", "1"), ("CLAUDE_CODE_ENTRYPOINT", "cli"), ("CLAUDE_AGENT_SDK_MCP_NO_PREFIX", "1"), ("CLAUDE_CODE_GIT_BASH_PATH", @"C:\git\bash.exe"));
        ClaudeCliCommand.ApplyEnvironment(env, Profile());
        Assert.False(env.ContainsKey("CLAUDECODE"));
        Assert.False(env.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
        Assert.False(env.ContainsKey("CLAUDE_AGENT_SDK_MCP_NO_PREFIX"));
        // User configuration stays.
        Assert.Equal(@"C:\git\bash.exe", env["CLAUDE_CODE_GIT_BASH_PATH"]);
    }

    [Fact]
    public void Start_info_environment_follows_the_profile()
    {
        var psi = ClaudeCliCommand.BuildStartInfo(@"C:\Tools\claude.exe", Array.Empty<string>(), _dir, Profile(p =>
        {
            p.Thinking = ThinkingMode.Off;
            p.ExtraEnv = new Dictionary<string, string> { ["DESKPILOT_TEST_VAR"] = "42" };
        }));
        Assert.Equal("0", psi.Environment["MAX_THINKING_TOKENS"]);
        Assert.Equal("42", psi.Environment["DESKPILOT_TEST_VAR"]);
        Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
    }

    // ================================================================== json written by DeskPilot

    [Fact]
    public void Mcp_config_has_the_documented_shape()
    {
        var mcp = new McpEndpointInfo("deskpilot", @"C:\Program Files\DeskPilot\DeskPilot.exe", new[] { "--mcp-bridge", "DeskPilot-pipe-1", "tok\"en" });
        using var doc = JsonDocument.Parse(ClaudeCliCommand.BuildMcpConfigJson(mcp));
        var servers = doc.RootElement.GetProperty("mcpServers");
        Assert.Single(servers.EnumerateObject());
        var server = servers.GetProperty("deskpilot");
        Assert.Equal("stdio", server.GetProperty("type").GetString());
        Assert.Equal(@"C:\Program Files\DeskPilot\DeskPilot.exe", server.GetProperty("command").GetString());
        Assert.Equal(new[] { "--mcp-bridge", "DeskPilot-pipe-1", "tok\"en" }, server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void User_message_is_a_single_stream_json_line()
    {
        var line = ClaudeCliCommand.BuildUserMessage(new UserTurn("Open \"Notepad\"\nplease"));
        Assert.DoesNotContain('\n', line);
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("user", doc.RootElement.GetProperty("type").GetString());
        var message = doc.RootElement.GetProperty("message");
        Assert.Equal("user", message.GetProperty("role").GetString());
        var block = Assert.Single(message.GetProperty("content").EnumerateArray());
        Assert.Equal("text", block.GetProperty("type").GetString());
        Assert.Equal("Open \"Notepad\"\nplease", block.GetProperty("text").GetString());
    }

    [Fact]
    public void User_message_carries_images_as_base64_blocks()
    {
        var turn = new UserTurn("What is this?", new[] { new ToolImage("image/jpeg", "AAEC", 2, 2), new ToolImage("", "AwQF", 1, 1) });
        using var doc = JsonDocument.Parse(ClaudeCliCommand.BuildUserMessage(turn));
        var content = doc.RootElement.GetProperty("message").GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(3, content.Count);
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        var source = content[0].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal("AAEC", source.GetProperty("data").GetString());
        Assert.Equal("image/png", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("What is this?", content[2].GetProperty("text").GetString());
    }

    [Fact]
    public void Image_only_turn_has_no_empty_text_block()
    {
        using var doc = JsonDocument.Parse(ClaudeCliCommand.BuildUserMessage(new UserTurn("  ", new[] { new ToolImage("image/png", "AA", 1, 1) })));
        var content = doc.RootElement.GetProperty("message").GetProperty("content");
        Assert.Equal("image", Assert.Single(content.EnumerateArray()).GetProperty("type").GetString());
    }

    [Fact]
    public void Interrupt_request_has_the_documented_shape()
    {
        using var doc = JsonDocument.Parse(ClaudeCliCommand.BuildInterruptRequest("req-7"));
        Assert.Equal("control_request", doc.RootElement.GetProperty("type").GetString());
        Assert.Equal("req-7", doc.RootElement.GetProperty("request_id").GetString());
        Assert.Equal("interrupt", doc.RootElement.GetProperty("request").GetProperty("subtype").GetString());
    }

    // ================================================================== parser

    [Fact]
    public void Parser_reads_init_with_connected_mcp()
    {
        var line = ClaudeStreamParser.Parse(InitConnected, "deskpilot");
        Assert.Equal(ClaudeLineKind.Init, line.Kind);
        Assert.Equal("claude-haiku-5-5", line.Model);
        Assert.Equal("connected", line.McpStatus);
        Assert.Empty(line.Events);
    }

    [Fact]
    public void Parser_reports_failed_mcp_as_an_error()
    {
        var line = ClaudeStreamParser.Parse(InitFailed, "deskpilot");
        var status = Assert.IsType<StatusEvent>(Assert.Single(line.Events));
        Assert.Equal(StatusLevel.Error, status.Level);
        Assert.Equal("DeskPilot's tools did not connect to Claude Code: failed", status.Message);
    }

    [Fact]
    public void Parser_reports_a_missing_mcp_server()
    {
        var line = ClaudeStreamParser.Parse(InitNoServers, "deskpilot");
        var status = Assert.IsType<StatusEvent>(Assert.Single(line.Events));
        Assert.Equal(StatusLevel.Error, status.Level);
        Assert.Contains("not listed", status.Message);
    }

    [Fact]
    public void Parser_skips_the_mcp_check_without_an_expected_server()
    {
        var line = ClaudeStreamParser.Parse(InitNoServers, null);
        Assert.Equal(ClaudeLineKind.Init, line.Kind);
        Assert.Null(line.McpStatus);
        Assert.Empty(line.Events);
    }

    [Fact]
    public void Parser_warns_while_mcp_is_pending()
    {
        var line = ClaudeStreamParser.Parse(InitConnected.Replace("\"connected\"", "\"pending\""), "deskpilot");
        Assert.Equal(StatusLevel.Warning, Assert.IsType<StatusEvent>(Assert.Single(line.Events)).Level);
    }

    [Fact]
    public void Parser_skips_empty_thinking_with_only_a_signature()
    {
        var line = ClaudeStreamParser.Parse(AssistantEmptyThinking, "deskpilot");
        Assert.Equal(ClaudeLineKind.Assistant, line.Kind);
        Assert.Empty(line.Events);
        Assert.Null(line.AssistantError);
    }

    [Fact]
    public void Parser_emits_thinking_text()
    {
        var e = Assert.IsType<ThinkingEvent>(Assert.Single(ClaudeStreamParser.Parse(AssistantThinking, "deskpilot").Events));
        Assert.Equal("The user wants a screenshot first.", e.Text);
        Assert.False(e.IsPartial);
    }

    [Fact]
    public void Parser_emits_complete_assistant_text()
    {
        var line = ClaudeStreamParser.Parse(AssistantText, "deskpilot");
        var e = Assert.IsType<AssistantTextEvent>(Assert.Single(line.Events));
        Assert.Equal("Taking a screenshot first.", e.Text);
        Assert.False(e.IsPartial);
        Assert.Equal("Taking a screenshot first.", line.AssistantText);
        Assert.Equal("claude-haiku-5-5", line.Model);
    }

    [Fact]
    public void Parser_keeps_block_order_in_mixed_messages()
    {
        const string mixed = """{"type":"assistant","message":{"model":"m","content":[{"type":"thinking","thinking":"plan"},{"type":"text","text":"one"},{"type":"tool_use","id":"t","name":"mcp__deskpilot__click","input":{"x":1}},{"type":"text","text":"   "},{"type":"text","text":"two"}]}}""";
        var events = ClaudeStreamParser.Parse(mixed, "deskpilot").Events;
        Assert.Collection(events,
            e => Assert.Equal("plan", Assert.IsType<ThinkingEvent>(e).Text),
            e => Assert.Equal("one", Assert.IsType<AssistantTextEvent>(e).Text),
            e => Assert.Equal("two", Assert.IsType<AssistantTextEvent>(e).Text));
    }

    [Fact]
    public void Parser_does_not_turn_tool_use_into_events()
    {
        var line = ClaudeStreamParser.Parse(AssistantToolUse, "deskpilot");
        Assert.Equal(ClaudeLineKind.Assistant, line.Kind);
        Assert.Empty(line.Events);
    }

    [Theory]
    [InlineData(UserToolResultEcho)]
    [InlineData("""{ "type" : "user", "message": {"content": [{"type":"tool_result","content":"ok"}]}}""")]
    public void Parser_ignores_tool_result_echoes(string json)
    {
        var line = ClaudeStreamParser.Parse(json, "deskpilot");
        Assert.Equal(ClaudeLineKind.UserEcho, line.Kind);
        Assert.Empty(line.Events);
    }

    [Fact]
    public void Parser_ignores_allowed_rate_limit_events()
    {
        var line = ClaudeStreamParser.Parse(RateAllowed, "deskpilot");
        Assert.Equal(ClaudeLineKind.RateLimit, line.Kind);
        Assert.Empty(line.Events);
    }

    [Fact]
    public void Parser_warns_about_rate_limits()
    {
        var warning = Assert.IsType<StatusEvent>(Assert.Single(ClaudeStreamParser.Parse(RateWarning, "deskpilot").Events));
        Assert.Equal(StatusLevel.Warning, warning.Level);
        Assert.Contains("Approaching", warning.Message);
        Assert.Contains("5-hour", warning.Message);
        Assert.Contains("resets", warning.Message);

        var rejected = Assert.IsType<StatusEvent>(Assert.Single(ClaudeStreamParser.Parse(RateRejected, "deskpilot").Events));
        Assert.Equal(StatusLevel.Warning, rejected.Level);
        Assert.Contains("limit reached", rejected.Message);
        Assert.Contains("weekly", rejected.Message);
    }

    [Fact]
    public void Parser_reads_control_responses()
    {
        var line = ClaudeStreamParser.Parse(ControlResponseTemplate.Replace("REQ", "deskpilot-interrupt-1-abcd"), "deskpilot");
        Assert.Equal(ClaudeLineKind.ControlResponse, line.Kind);
        Assert.Equal("deskpilot-interrupt-1-abcd", line.RequestId);
        Assert.Equal("success", line.Subtype);
        Assert.Empty(line.Events);
    }

    [Fact]
    public void Parser_reads_a_successful_result()
    {
        var line = ClaudeStreamParser.Parse(ResultSuccess, "deskpilot");
        Assert.Equal(ClaudeLineKind.Result, line.Kind);
        var r = line.Result!;
        Assert.True(r.IsSuccess);
        Assert.False(r.IsAborted);
        Assert.Equal("OK", r.Text);
        Assert.Equal(3 + 2048 + 512, r.InputTokens);
        Assert.Equal(5, r.OutputTokens);
        Assert.Equal(0.0004235, r.CostUsd!.Value, 9);
        Assert.Equal(3, r.NumTurns);
        Assert.Equal("claude-haiku-5-5", r.Model);
        Assert.Equal("completed", r.TerminalReason);
    }

    [Fact]
    public void Parser_reads_an_aborted_result()
    {
        var r = ClaudeStreamParser.Parse(ResultAborted, "deskpilot").Result!;
        Assert.Equal("error_during_execution", r.Subtype);
        Assert.True(r.IsAborted);
        Assert.False(r.IsSuccess);
        Assert.Equal("Request was aborted.", Assert.Single(r.Errors));
        Assert.Null(r.Model);
    }

    [Fact]
    public void Parser_maps_auth_failures_to_the_login_hint()
    {
        var assistant = ClaudeStreamParser.Parse(AssistantAuthSynthetic, "deskpilot");
        Assert.Empty(assistant.Events);
        Assert.Equal("authentication_failed", assistant.AssistantError);
        Assert.Null(assistant.Model);

        var r = ClaudeStreamParser.Parse(ResultAuthError, "deskpilot").Result!;
        Assert.True(r.IsError);
        Assert.False(r.IsSuccess);
        Assert.Equal(ClaudeCliCommand.NotLoggedInMessage, ClaudeStreamParser.DescribeFailure(r, null));
        Assert.Equal(ClaudeCliCommand.NotLoggedInMessage, ClaudeStreamParser.DescribeFailure(r with { Text = "boom" }, "authentication_failed"));
    }

    [Theory]
    [InlineData("Not logged in · Please run /login")]
    [InlineData("Invalid API key · Fix external API key")]
    [InlineData("OAuth token has expired. Please obtain a new token or refresh your existing token.")]
    [InlineData("API Error: 401 {\"type\":\"error\",\"error\":{\"type\":\"authentication_error\"}}")]
    public void Auth_failure_texts_are_detected(string text)
    {
        Assert.True(ClaudeStreamParser.IsAuthFailure(text));
    }

    [Fact]
    public void Failures_get_readable_messages()
    {
        var api = ClaudeStreamParser.Parse(ResultApiError, "deskpilot").Result!;
        Assert.StartsWith("Claude Code error: API Error: 500", ClaudeStreamParser.DescribeFailure(api, null));

        var maxTurns = api with { Subtype = "error_max_turns", Text = null };
        Assert.Contains("maximum number of turns", ClaudeStreamParser.DescribeFailure(maxTurns, null));

        var withErrors = api with { Subtype = "error_during_execution", Text = null, Errors = new[] { "first", "second" } };
        Assert.Equal("Claude Code error: first; second", ClaudeStreamParser.DescribeFailure(withErrors, null));

        var limit = api with { Text = "Claude AI usage limit reached|1791460800" };
        Assert.StartsWith("Claude usage limit reached", ClaudeStreamParser.DescribeFailure(limit, null));

        var empty = api with { Subtype = "error_during_execution", Text = null };
        Assert.Contains("error_during_execution", ClaudeStreamParser.DescribeFailure(empty, null));

        var huge = api with { Text = new string('x', 5000) };
        Assert.True(ClaudeStreamParser.DescribeFailure(huge, null).Length < 700);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{broken")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"no_type\":1}")]
    [InlineData("Warning: something printed to stdout")]
    public void Parser_flags_garbage(string line)
    {
        var parsed = ClaudeStreamParser.Parse(line, "deskpilot");
        Assert.Equal(ClaudeLineKind.Garbage, parsed.Kind);
        Assert.Empty(parsed.Events);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("""{"type":"stream_event","event":{"type":"content_block_delta"}}""")]
    [InlineData("""{"type":"keep_alive"}""")]
    public void Parser_ignores_blank_and_unknown_lines(string? line)
    {
        var parsed = ClaudeStreamParser.Parse(line, "deskpilot");
        Assert.Equal(ClaudeLineKind.Ignored, parsed.Kind);
        Assert.Empty(parsed.Events);
    }

    [Fact]
    public void Parser_treats_other_system_messages_as_informational()
    {
        var thinkingTokens = ClaudeStreamParser.Parse(ThinkingTokensSystem, "deskpilot");
        Assert.Equal(ClaudeLineKind.System, thinkingTokens.Kind);
        Assert.Empty(thinkingTokens.Events);

        var compact = ClaudeStreamParser.Parse("""{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":150000}}""", "deskpilot");
        Assert.Equal(StatusLevel.Info, Assert.IsType<StatusEvent>(Assert.Single(compact.Events)).Level);

        var retry = ClaudeStreamParser.Parse("""{"type":"system","subtype":"api_retry","attempt":2,"max_retries":10,"retry_delay_ms":1200,"error_status":529,"error":"overloaded"}""", "deskpilot");
        var status = Assert.IsType<StatusEvent>(Assert.Single(retry.Events));
        Assert.Equal("Claude is busy, retrying the request (attempt 2 of 10): overloaded", status.Message);
    }

    [Fact]
    public void Parser_reads_control_requests_from_the_cli()
    {
        var line = ClaudeStreamParser.Parse("""{"type":"control_request","request_id":"cli-1","request":{"subtype":"can_use_tool","tool_name":"mcp__deskpilot__click","input":{"x":10,"y":20}}}""", "deskpilot");
        Assert.Equal(ClaudeLineKind.ControlRequest, line.Kind);
        Assert.Equal("cli-1", line.RequestId);
        Assert.Equal("can_use_tool", line.Subtype);
        Assert.Equal("mcp__deskpilot__click", line.ToolName);
        Assert.Equal("""{"x":10,"y":20}""", line.ToolInputJson);
    }

    // ================================================================== probe parsing

    [Theory]
    [InlineData("2.1.293 (Claude Code)", "2.1.293")]
    [InlineData("2.2.0-beta.3 (Claude Code)\n", "2.2.0-beta.3")]
    [InlineData("claude version 1.0", "1.0")]
    [InlineData("", null)]
    [InlineData("no digits here", null)]
    public void Probe_parses_the_version(string output, string? expected)
    {
        Assert.Equal(expected, ClaudeCliProbe.ParseVersion(output));
    }

    [Fact]
    public void Probe_describes_a_subscription_login_without_personal_data()
    {
        const string json = """{"loggedIn":true,"authMethod":"claude.ai","apiProvider":"firstParty","email":"someone@example.com","orgId":"org-123","orgName":"Example Org","subscriptionType":"max"}""";
        var (loggedIn, detail) = ClaudeCliProbe.DescribeAuthStatus(json);
        Assert.True(loggedIn);
        Assert.Equal("Logged in with a Claude subscription (max)", detail);
        Assert.DoesNotContain("someone", detail);
        Assert.DoesNotContain("org", detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Example", detail);
    }

    [Fact]
    public void Probe_describes_other_login_states()
    {
        var (notLoggedIn, notLoggedInDetail) = ClaudeCliProbe.DescribeAuthStatus("""{"loggedIn":false,"authMethod":"none","apiProvider":"firstParty"}""");
        Assert.False(notLoggedIn);
        Assert.Equal("Not logged in: run 'claude auth login'", notLoggedInDetail);
        Assert.Equal("Logged in with an API key (usage is billed to that key)", ClaudeCliProbe.DescribeAuthStatus("""{"loggedIn":true,"authMethod":"api_key"}""").Detail);
        Assert.Equal("Logged in through bedrock", ClaudeCliProbe.DescribeAuthStatus("""{"loggedIn":true,"authMethod":"third_party","apiProvider":"bedrock"}""").Detail);
        Assert.Equal("Logged in with a Claude subscription", ClaudeCliProbe.DescribeAuthStatus("""{"loggedIn":true,"authMethod":"claude.ai"}""").Detail);
        // Warnings printed before the JSON do not break parsing.
        Assert.True(ClaudeCliProbe.DescribeAuthStatus("warning: something\n{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"subscriptionType\":\"pro\"}").LoggedIn);

        var (unknown, detail) = ClaudeCliProbe.DescribeAuthStatus("error: unknown command 'auth'");
        Assert.Null(unknown);
        Assert.Contains("claude auth login", detail);
        Assert.Null(ClaudeCliProbe.DescribeAuthStatus("{\"something\":1}").LoggedIn);
        Assert.Null(ClaudeCliProbe.DescribeAuthStatus("{not json}").LoggedIn);
    }

    // ================================================================== backend (fake process)

    private sealed class FakeProcess : IClaudeProcess
    {
        private readonly Action<string> _onLine;
        private readonly object _emitGate = new();
        private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _written = new();
        private readonly List<string> _stderr = new();

        public FakeProcess(ClaudeLaunchSpec spec, Action<string> onLine)
        {
            Spec = spec;
            _onLine = onLine;
        }

        public ClaudeLaunchSpec Spec { get; }
        public Func<FakeProcess, string, Task>? OnInput { get; set; }
        public bool ExitOnCloseInput { get; set; } = true;
        public bool Killed { get; private set; }
        public bool InputClosed { get; private set; }
        public bool Disposed { get; private set; }
        public bool HasExited => _exited.Task.IsCompleted;
        public int? ExitCode { get; private set; }
        public Task Exited => _exited.Task;
        public IReadOnlyList<string> StderrTail { get { lock (_stderr) return _stderr.ToArray(); } }

        public IReadOnlyList<string> Written { get { lock (_written) return _written.ToArray(); } }

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            if (HasExited || InputClosed) throw new IOException("The pipe has been ended.");
            lock (_written) _written.Add(line);
            var handler = OnInput;
            if (handler != null) _ = Task.Run(() => handler(this, line));
            return Task.CompletedTask;
        }

        public void Emit(params string[] lines)
        {
            lock (_emitGate)
            {
                foreach (var l in lines)
                    if (!HasExited) _onLine(l);
            }
        }

        public void Exit(int code, params string[] stderr)
        {
            lock (_emitGate)
            {
                lock (_stderr) _stderr.AddRange(stderr);
                if (!HasExited) ExitCode = code;
                _exited.TrySetResult();
            }
        }

        public void CloseInput()
        {
            InputClosed = true;
            if (ExitOnCloseInput) Exit(0);
        }

        public void Kill()
        {
            Killed = true;
            Exit(-1);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            Exit(-1);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly List<FakeProcess> Processes = new();
        public readonly ConcurrentQueue<AgentEvent> Events = new();
        public Func<FakeProcess, string, Task> Script = DefaultScript;
        public Func<string?, string?> Locate = _ => @"C:\fake\claude.exe";
        public AgentRunControl Control = new();
        public ClaudeCliBackend Backend = null!;
        public AgentBackendContext Context = null!;

        public static ClaudeLineKind TypeOf(string line) => ClaudeStreamParser.Parse(line, null).Kind;

        public static string RequestIdOf(string line)
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("request_id").GetString()!;
        }

        public static bool IsUserMessage(string line) => line.StartsWith("{\"type\":\"user\"", StringComparison.Ordinal);
        public static bool IsInterrupt(string line) => line.Contains("\"subtype\":\"interrupt\"", StringComparison.Ordinal);

        /// <summary>A normal turn: init, thinking, tool use with echo, text, result.</summary>
        public static Task DefaultScript(FakeProcess p, string line)
        {
            if (IsUserMessage(line))
                p.Emit(InitConnected, AssistantEmptyThinking, AssistantText, AssistantToolUse, UserToolResultEcho, RateAllowed, "garbage line", AssistantFinal, ResultSuccess);
            else if (IsInterrupt(line))
                p.Emit(ControlResponseTemplate.Replace("REQ", RequestIdOf(line)), ResultAborted);
            return Task.CompletedTask;
        }

        public FakeProcess Current => Processes[^1];

        public IClaudeProcess Start(ClaudeLaunchSpec spec, Action<string> onLine)
        {
            var p = new FakeProcess(spec, onLine) { OnInput = (proc, l) => Script(proc, l) };
            lock (Processes) Processes.Add(p);
            return p;
        }

        public async Task StartAsync(string tempDir, ProviderProfile? profile = null, McpEndpointInfo? mcp = null, bool withMcp = true)
        {
            Backend = new ClaudeCliBackend(path => Locate(path), Start, tempDir)
            {
                InterruptGrace = TimeSpan.FromMilliseconds(300),
                ShutdownGrace = TimeSpan.FromMilliseconds(300),
            };
            var settings = SettingsStore.CreateDefault();
            Context = new AgentBackendContext
            {
                Settings = settings,
                Profile = profile ?? settings.ActiveProfile!,
                SystemPrompt = "You are DeskPilot (test prompt).",
                Tools = new NoTools(),
                Emit = e => Events.Enqueue(e),
                Control = Control,
                WorkingDirectory = tempDir,
                Mcp = withMcp ? mcp ?? new McpEndpointInfo("deskpilot", @"C:\fake\DeskPilot.exe", new[] { "--mcp-bridge", "pipe", "token" }) : null,
            };
            await Backend.StartAsync(Context, CancellationToken.None);
        }

        public async Task<TurnResult> TurnAsync(string text = "Open Notepad", CancellationToken ct = default)
        {
            Control.BeginTurn();
            return await Backend.RunTurnAsync(new UserTurn(text), ct).WaitAsync(TimeSpan.FromSeconds(15));
        }

        public List<StatusEvent> Statuses => Events.OfType<StatusEvent>().ToList();

        public ValueTask DisposeAsync() => Backend?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    private sealed class NoTools : IToolHost
    {
        public IReadOnlyList<ToolSpec> GetTools() => Array.Empty<ToolSpec>();
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) => Task.FromResult(ToolResult.Error("no tools"));
    }

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("condition not met");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Start_throws_a_readable_error_when_claude_is_missing()
    {
        await using var h = new Harness { Locate = _ => null };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => h.StartAsync(_dir));
        Assert.Equal("Claude Code CLI not found. Install Claude Code, run 'claude' once to log in, or set its path in Settings.", ex.Message);
        Assert.Empty(h.Processes);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Start_writes_prompt_and_mcp_files_and_dispose_deletes_them()
    {
        var h = new Harness();
        await h.StartAsync(_dir, profile: Profile(p => p.CliPath = @"D:\custom\claude.exe"));
        var promptFile = h.Backend.SystemPromptFile!;
        var mcpFile = h.Backend.McpConfigFile!;
        Assert.StartsWith(_dir, promptFile);
        Assert.StartsWith(_dir, mcpFile);
        Assert.Equal("You are DeskPilot (test prompt).", File.ReadAllText(promptFile));
        Assert.NotEqual(0xEF, File.ReadAllBytes(promptFile)[0]);
        using (var doc = JsonDocument.Parse(File.ReadAllText(mcpFile)))
            Assert.Equal("stdio", doc.RootElement.GetProperty("mcpServers").GetProperty("deskpilot").GetProperty("type").GetString());

        var p = Assert.Single(h.Processes);
        Assert.Equal(@"C:\fake\claude.exe", p.Spec.ExePath);
        Assert.Equal(_dir, p.Spec.WorkingDirectory);
        Assert.Equal(promptFile, p.Spec.Args[p.Spec.Args.ToList().IndexOf("--system-prompt-file") + 1]);
        Assert.Equal(mcpFile, p.Spec.Args[p.Spec.Args.ToList().IndexOf("--mcp-config") + 1]);
        Assert.Contains("--allowedTools", p.Spec.Args);

        await h.Backend.DisposeAsync();
        Assert.False(File.Exists(promptFile));
        Assert.False(File.Exists(mcpFile));
        Assert.True(p.InputClosed);
        Assert.True(p.Disposed);
        await h.Backend.DisposeAsync(); // idempotent
    }

    [Fact]
    public async Task Start_passes_the_configured_cli_path_to_the_locator()
    {
        string? asked = "unset";
        await using var h = new Harness { Locate = path => { asked = path; return @"C:\fake\claude.exe"; } };
        await h.StartAsync(_dir, profile: Profile(p => p.CliPath = @"D:\custom\claude.exe"));
        Assert.Equal(@"D:\custom\claude.exe", asked);
    }

    [Fact]
    public async Task Start_without_mcp_omits_the_mcp_config()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir, withMcp: false);
        Assert.Null(h.Backend.McpConfigFile);
        var args = h.Current.Spec.Args;
        Assert.DoesNotContain("--mcp-config", args);
        Assert.DoesNotContain("--allowedTools", args);
        Assert.Contains("--strict-mcp-config", args);
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Turn_completes_with_text_events_and_stats()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line))
            {
                h.Control.RegisterStep();
                h.Control.RegisterStep();
            }
            return Harness.DefaultScript(p, line);
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync("Open \"Notepad\"");

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal("OK", result.FinalText);
        Assert.Equal(3 + 2048 + 512, result.Stats.InputTokens);
        Assert.Equal(5, result.Stats.OutputTokens);
        Assert.Equal(2, result.Stats.Steps);
        Assert.Equal(0.0004235, result.Stats.CostUsd!.Value, 9);
        Assert.Equal("claude-haiku-5-5", result.Stats.Model);
        Assert.True(result.Stats.Duration > TimeSpan.Zero);

        // Only text: no thinking (empty), no tool events, nothing for the garbage line or allowed rate limit.
        Assert.Collection(h.Events,
            e => Assert.Equal("Taking a screenshot first.", Assert.IsType<AssistantTextEvent>(e).Text),
            e => Assert.Equal("OK", Assert.IsType<AssistantTextEvent>(e).Text));

        var written = Assert.Single(h.Current.Written);
        Assert.Equal(ClaudeCliCommand.BuildUserMessage(new UserTurn("Open \"Notepad\"")), written);
    }

    [Fact]
    public async Task Conversation_continues_on_the_same_process()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("one")).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync("two")).Outcome);
        Assert.Single(h.Processes);
        Assert.Equal(2, h.Current.Written.Count);
        Assert.Empty(h.Statuses);
    }

    [Fact]
    public async Task Failed_mcp_connection_is_reported()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) p.Emit(InitFailed, AssistantFinal, ResultSuccess);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        var status = Assert.Single(h.Statuses);
        Assert.Equal(StatusLevel.Error, status.Level);
        Assert.Equal("DeskPilot's tools did not connect to Claude Code: failed", status.Message);
    }

    [Fact]
    public async Task Rate_limit_warnings_become_status_events()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) p.Emit(InitConnected, RateWarning, AssistantFinal, ResultSuccess);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        await h.TurnAsync();
        Assert.Equal(StatusLevel.Warning, Assert.Single(h.Statuses).Level);
    }

    [Fact]
    public async Task Auth_failure_returns_the_login_hint()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) p.Emit(InitConnected, AssistantAuthSynthetic, ResultAuthError);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal("Claude Code is not logged in. Open a terminal and run: claude auth login", result.Error);
        Assert.Null(result.FinalText);
        Assert.DoesNotContain(h.Events, e => e is AssistantTextEvent);
    }

    [Fact]
    public async Task Api_errors_fail_the_turn_with_the_message()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) p.Emit(InitConnected, ResultApiError);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.StartsWith("Claude Code error: API Error: 500", result.Error);
    }

    [Fact]
    public async Task Cancelling_sends_an_interrupt_and_returns_cancelled()
    {
        await using var h = new Harness();
        var sawUser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) { p.Emit(InitConnected, AssistantText); sawUser.TrySetResult(); }
            else if (Harness.IsInterrupt(line)) p.Emit(ControlResponseTemplate.Replace("REQ", Harness.RequestIdOf(line)), ResultAborted);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        using var cts = new CancellationTokenSource();
        var turn = h.TurnAsync(ct: cts.Token);
        await sawUser.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await turn;

        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Null(result.Error);
        Assert.Equal(103, result.Stats.InputTokens);
        var interrupt = h.Current.Written.Single(Harness.IsInterrupt);
        using (var doc = JsonDocument.Parse(interrupt))
        {
            Assert.Equal("control_request", doc.RootElement.GetProperty("type").GetString());
            Assert.StartsWith("deskpilot-interrupt-", doc.RootElement.GetProperty("request_id").GetString());
        }
        Assert.False(h.Current.Killed);

        // The process survives an interrupt and serves the next turn without a restart.
        h.Script = Harness.DefaultScript;
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync()).Outcome);
        Assert.Single(h.Processes);
    }

    [Fact]
    public async Task Interrupt_request_ids_are_unique()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        for (int i = 0; i < 2; i++)
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Script = (p, line) =>
            {
                if (Harness.IsUserMessage(line)) started.TrySetResult();
                else if (Harness.IsInterrupt(line)) p.Emit(ResultAborted);
                return Task.CompletedTask;
            };
            using var cts = new CancellationTokenSource();
            var turn = h.TurnAsync(ct: cts.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            Assert.Equal(TurnOutcome.Cancelled, (await turn).Outcome);
        }
        var ids = h.Current.Written.Where(Harness.IsInterrupt).Select(Harness.RequestIdOf).ToList();
        Assert.Equal(2, ids.Count);
        Assert.NotEqual(ids[0], ids[1]);
    }

    [Fact]
    public async Task Stop_signal_cancels_and_step_limit_is_reported()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) h.Control.RequestStop("Stopped by user");
            else if (Harness.IsInterrupt(line)) p.Emit(ResultAborted);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        Assert.Equal(TurnOutcome.Cancelled, (await h.TurnAsync()).Outcome);

        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) h.Control.RequestStop("Step limit (80) reached");
            else if (Harness.IsInterrupt(line)) p.Emit(ResultAborted);
            return Task.CompletedTask;
        };
        Assert.Equal(TurnOutcome.StepLimit, (await h.TurnAsync()).Outcome);
    }

    [Fact]
    public async Task InterruptAsync_stops_the_active_turn()
    {
        await using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) started.TrySetResult();
            else if (Harness.IsInterrupt(line)) p.Emit(ControlResponseTemplate.Replace("REQ", Harness.RequestIdOf(line)), ResultAborted);
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var turn = h.TurnAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Backend.InterruptAsync();
        var result = await turn;
        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Single(h.Current.Written, Harness.IsInterrupt);
    }

    [Fact]
    public async Task InterruptAsync_when_idle_does_nothing()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        await h.Backend.InterruptAsync();
        Assert.Empty(h.Current.Written);
    }

    [Fact]
    public async Task Unanswered_interrupt_kills_the_process_and_the_next_turn_restarts()
    {
        await using var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) started.TrySetResult();
            return Task.CompletedTask; // never answers the interrupt
        };
        await h.StartAsync(_dir);
        using var cts = new CancellationTokenSource();
        var turn = h.TurnAsync(ct: cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cts.Cancel();
        var result = await turn;
        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        var first = h.Processes[0];
        Assert.True(first.Killed);

        h.Script = Harness.DefaultScript;
        var next = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Completed, next.Outcome);
        Assert.Equal(2, h.Processes.Count);
        Assert.True(first.Disposed);
        var status = Assert.Single(h.Statuses);
        Assert.Contains("conversation context was reset", status.Message);
    }

    [Fact]
    public async Task Process_exit_mid_turn_fails_with_the_stderr_tail()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line))
            {
                p.Emit(InitConnected);
                p.Exit(3, "something went wrong", "fatal: crashed");
            }
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Contains("exit code 3", result.Error);
        Assert.Contains("fatal: crashed", result.Error);

        h.Script = Harness.DefaultScript;
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync()).Outcome);
        Assert.Equal(2, h.Processes.Count);
        Assert.Contains(h.Statuses, s => s.Message.Contains("conversation context was reset"));
    }

    [Fact]
    public async Task Process_exit_with_a_login_error_returns_the_login_hint()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) p.Exit(1, "Invalid API key · Please run /login");
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Failed, result.Outcome);
        Assert.Equal(ClaudeCliCommand.NotLoggedInMessage, result.Error);
    }

    [Fact]
    public async Task Process_that_died_while_idle_is_restarted_with_a_notice()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        h.Current.Exit(0);
        var result = await h.TurnAsync();
        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Equal(2, h.Processes.Count);
        Assert.Contains("conversation context was reset", Assert.Single(h.Statuses).Message);
    }

    [Fact]
    public async Task Reset_stops_the_process_and_the_next_turn_starts_fresh_silently()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync()).Outcome);
        var first = h.Current;

        await h.Backend.ResetConversationAsync(CancellationToken.None);
        Assert.True(first.InputClosed);
        Assert.True(first.Disposed);
        Assert.Single(h.Processes);

        Assert.Equal(TurnOutcome.Completed, (await h.TurnAsync()).Outcome);
        Assert.Equal(2, h.Processes.Count);
        Assert.Empty(h.Statuses);
        Assert.Single(h.Current.Written);
    }

    [Fact]
    public async Task Reset_kills_a_process_that_does_not_exit_on_eof()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        h.Current.ExitOnCloseInput = false;
        await h.Backend.ResetConversationAsync(CancellationToken.None);
        Assert.True(h.Current.Killed);
    }

    [Fact]
    public async Task Dispose_during_a_turn_cancels_it()
    {
        var h = new Harness();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line)) started.TrySetResult();
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        var turn = h.TurnAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await h.Backend.DisposeAsync();
        Assert.Equal(TurnOutcome.Cancelled, (await turn).Outcome);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => h.Backend.RunTurnAsync(new UserTurn("x"), CancellationToken.None));
    }

    [Fact]
    public async Task Turns_run_one_at_a_time()
    {
        await using var h = new Harness();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var userMessages = 0;
        h.Script = async (p, line) =>
        {
            if (!Harness.IsUserMessage(line)) return;
            if (Interlocked.Increment(ref userMessages) == 1) await release.Task;
            p.Emit(InitConnected, AssistantFinal, ResultSuccess);
        };
        await h.StartAsync(_dir);
        h.Control.BeginTurn();
        var first = h.Backend.RunTurnAsync(new UserTurn("first"), CancellationToken.None);
        await WaitUntil(() => h.Current.Written.Count == 1);
        var second = h.Backend.RunTurnAsync(new UserTurn("second"), CancellationToken.None);
        await Task.Delay(150);
        Assert.Single(h.Current.Written);
        Assert.False(second.IsCompleted);

        release.SetResult();
        Assert.Equal(TurnOutcome.Completed, (await first.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Equal(TurnOutcome.Completed, (await second.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
        Assert.Equal(2, h.Current.Written.Count);
    }

    [Fact]
    public async Task Already_cancelled_turn_returns_without_writing()
    {
        await using var h = new Harness();
        await h.StartAsync(_dir);
        h.Control.BeginTurn();
        var result = await h.Backend.RunTurnAsync(new UserTurn("x"), new CancellationToken(canceled: true));
        Assert.Equal(TurnOutcome.Cancelled, result.Outcome);
        Assert.Empty(h.Current.Written);
    }

    [Fact]
    public async Task Permission_requests_from_the_cli_allow_only_deskpilot_tools()
    {
        await using var h = new Harness();
        h.Script = (p, line) =>
        {
            if (Harness.IsUserMessage(line))
            {
                p.Emit(
                    """{"type":"control_request","request_id":"cli-1","request":{"subtype":"can_use_tool","tool_name":"mcp__deskpilot__click","input":{"x":10,"y":20}}}""",
                    """{"type":"control_request","request_id":"cli-2","request":{"subtype":"can_use_tool","tool_name":"Bash","input":{"command":"dir"}}}""",
                    """{"type":"control_request","request_id":"cli-3","request":{"subtype":"something_new"}}""");
            }
            return Task.CompletedTask;
        };
        await h.StartAsync(_dir);
        h.Control.BeginTurn();
        var turn = h.Backend.RunTurnAsync(new UserTurn("x"), CancellationToken.None);
        await WaitUntil(() => h.Current.Written.Count(l => l.Contains("control_response")) == 3);

        JsonElement Response(string id)
        {
            var line = h.Current.Written.Single(l => l.Contains($"\"request_id\":\"{id}\"") && l.Contains("control_response"));
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.GetProperty("response").Clone();
        }

        var allow = Response("cli-1");
        Assert.Equal("success", allow.GetProperty("subtype").GetString());
        Assert.Equal("allow", allow.GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal(10, allow.GetProperty("response").GetProperty("updatedInput").GetProperty("x").GetInt32());
        Assert.Equal("deny", Response("cli-2").GetProperty("response").GetProperty("behavior").GetString());
        Assert.Equal("error", Response("cli-3").GetProperty("subtype").GetString());

        h.Current.Emit(ResultSuccess);
        Assert.Equal(TurnOutcome.Completed, (await turn.WaitAsync(TimeSpan.FromSeconds(10))).Outcome);
    }

    [Fact]
    public async Task Run_before_start_is_a_programming_error()
    {
        await using var backend = new ClaudeCliBackend(_ => @"C:\fake\claude.exe", (_, _) => throw new InvalidOperationException("must not start"), _dir);
        await Assert.ThrowsAsync<InvalidOperationException>(() => backend.RunTurnAsync(new UserTurn("x"), CancellationToken.None));
        Assert.True(backend.UsesMcp);
    }

    // ================================================================== live (opt-in)

    private static bool LiveTestsEnabled => Environment.GetEnvironmentVariable("DESKPILOT_LIVE_TESTS") == "1";

    /// <summary>
    /// Variables a host process (a Claude Code session, the desktop app) put into this test process that a
    /// normally started DeskPilot would not have: anything Claude/Anthropic-related that is not set at user
    /// or machine level. Cleared through ExtraEnv so the live CLI runs like it would for a real user.
    /// </summary>
    private static Dictionary<string, string> HostOnlyClaudeVariables()
    {
        var persistent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var target in new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            foreach (var key in Environment.GetEnvironmentVariables(target).Keys) persistent.Add((string)key);

        var clear = new Dictionary<string, string>();
        foreach (var key in Environment.GetEnvironmentVariables().Keys.Cast<string>())
        {
            var isClaude = key.StartsWith("CLAUDE", StringComparison.OrdinalIgnoreCase) || key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase);
            if (isClaude && !persistent.Contains(key)) clear[key] = "";
        }
        return clear;
    }

    [Fact]
    public async Task Live_claude_cli_completes_a_turn()
    {
        if (!LiveTestsEnabled)
        {
            _output.WriteLine("Skipped: set DESKPILOT_LIVE_TESTS=1 to run against the real Claude Code CLI.");
            return;
        }

        var settings = SettingsStore.CreateDefault();
        var profile = settings.ActiveProfile!;
        profile.Model = "haiku";
        profile.Thinking = ThinkingMode.Off;
        profile.ExtraEnv = HostOnlyClaudeVariables();

        var events = new ConcurrentQueue<AgentEvent>();
        var control = new AgentRunControl();
        await using var backend = new ClaudeCliBackend(ClaudeCliCommand.ResolveExecutable, (spec, onLine) => ClaudeCliProcess.Start(spec, onLine), _dir);
        await backend.StartAsync(new AgentBackendContext
        {
            Settings = settings,
            Profile = profile,
            SystemPrompt = "You are a test harness. Follow the user's instruction exactly and briefly.",
            Tools = new NoTools(),
            Emit = e => events.Enqueue(e),
            Control = control,
            WorkingDirectory = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName,
            Mcp = null,
        }, CancellationToken.None);

        control.BeginTurn();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var result = await backend.RunTurnAsync(new UserTurn("Reply with just the word OK"), timeout.Token);

        _output.WriteLine($"Outcome={result.Outcome} Text={result.FinalText} Error={result.Error}");
        _output.WriteLine($"Model={result.Stats.Model} In={result.Stats.InputTokens} Out={result.Stats.OutputTokens} Cost={result.Stats.CostUsd} Time={result.Stats.Duration}");
        foreach (var e in events) _output.WriteLine(e.ToString());

        Assert.Equal(TurnOutcome.Completed, result.Outcome);
        Assert.Contains("OK", result.FinalText, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Stats.OutputTokens > 0);

        var probe = await ClaudeCliProbe.ProbeAsync(null, CancellationToken.None);
        _output.WriteLine($"Probe: {probe}");
        Assert.NotNull(probe.Path);
        Assert.NotNull(probe.Version);
        Assert.True(probe.LoggedIn);
    }
}

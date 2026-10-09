using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Backends.Cli;
using DeskPilot.Core.Mcp;
using DeskPilot.Core.Prompts;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;
using DeskPilot.Core.Vault;
using MemoryPipe = System.IO.Pipelines.Pipe;

namespace DeskPilot.Linux.Tests;

// The platform-neutral core (src/DeskPilot.Core) on a real Linux system: the parts that behave differently
// from Windows (command lookup, shells, prompt text, named pipes, process start) are checked here. The same
// logic is covered in depth by tests/DeskPilot.Tests on Windows.

/// <summary>Core checks that touch no process-wide state, so they run in parallel with everything else.</summary>
public class CoreOnLinuxTests
{
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private const UnixFileMode AnyExecute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static void DeleteQuietly(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static async Task WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < Patience, "timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    // ---------------------------------------------------------------- command lookup and shells

    [LinuxTheory]
    [InlineData("bash")]
    [InlineData("ls")]
    public void ExecutableLocator_finds_commands_on_PATH(string command)
    {
        var path = ExecutableLocator.Find(command);
        Assert.NotNull(path);
        Assert.True(Path.IsPathRooted(path), path);
        // No Windows extensions are tried or added on Linux.
        Assert.Equal(command, Path.GetFileName(path));
        Assert.True(File.Exists(path), path);
        Assert.NotEqual((UnixFileMode)0, File.GetUnixFileMode(path!) & AnyExecute);
    }

    [LinuxFact]
    public void ExecutableLocator_needs_the_execute_bit_and_honours_a_configured_path()
    {
        Assert.Null(ExecutableLocator.Find("deskpilot-no-such-command-" + Guid.NewGuid().ToString("N")));

        var dir = TempDir();
        try
        {
            var tool = Path.Combine(dir, "dp-fake-tool");
            File.WriteAllText(tool, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Null(ExecutableLocator.Find("dp-fake-tool", extraCandidates: new[] { dir }));

            File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Assert.Equal(tool, ExecutableLocator.Find("dp-fake-tool", extraCandidates: new[] { dir }));

            // A configured path that exists wins over PATH.
            Assert.Equal(tool, ExecutableLocator.Find("bash", tool));
            // A configured path that does not exist falls back to the normal search.
            Assert.Equal(ExecutableLocator.Find("bash"), ExecutableLocator.Find("bash", Path.Combine(dir, "missing")));
        }
        finally
        {
            DeleteQuietly(dir);
        }
    }

    [LinuxFact]
    public void Run_command_offers_bash_and_sh()
    {
        Assert.Equal(new[] { "bash", "sh" }, ComputerToolHost.ShellNames);
        Assert.Equal("bash", ComputerToolHost.NormalizeShell(null));
        Assert.Equal("bash", ComputerToolHost.NormalizeShell(""));
        Assert.Equal("bash", ComputerToolHost.NormalizeShell(" Bash "));
        Assert.Equal("bash", ComputerToolHost.NormalizeShell("zsh"));
        Assert.Equal("bash", ComputerToolHost.NormalizeShell("fish"));
        Assert.Equal("bash", ComputerToolHost.NormalizeShell("/bin/bash"));
        Assert.Equal("sh", ComputerToolHost.NormalizeShell("sh"));
        Assert.Equal("sh", ComputerToolHost.NormalizeShell("/bin/sh"));
        Assert.Equal("sh", ComputerToolHost.NormalizeShell("dash"));
        Assert.Null(ComputerToolHost.NormalizeShell("powershell"));
        Assert.Null(ComputerToolHost.NormalizeShell("cmd"));

        // Both shells exist on the system the tool will run them on.
        Assert.NotNull(ExecutableLocator.Find("bash"));
        Assert.NotNull(ExecutableLocator.Find("sh"));

        // The tool the model sees says so too.
        var settings = new AppSettings();
        settings.Safety.AllowShellCommands = true;
        var host = new ComputerToolHost(
            new DesktopServices(null!, null!, null!, null!, null!, null!, null!),
            new AllowAllGuard(), () => settings, new AgentRunControl());
        var spec = host.GetTools().Single(t => t.Name == "run_command");
        Assert.Contains("bash or sh", spec.Description);
        Assert.DoesNotContain("PowerShell", spec.Description);
        var shells = spec.InputSchema.GetProperty("properties").GetProperty("shell").GetProperty("enum")
            .EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "bash", "sh" }, shells);
    }

    private sealed class AllowAllGuard : ISafetyGuard
    {
        public Task<SafetyVerdict> CheckAsync(ProposedAction action, CancellationToken ct) => Task.FromResult(SafetyVerdict.Allowed);
    }

    // ---------------------------------------------------------------- system prompt

    private static string BuildPrompt(bool admin)
    {
        var settings = new AppSettings();
        settings.Safety.AllowAdmin = admin;
        var tools = new[] { ToolSpec.Create("screenshot", "Takes a screenshot.", """{"type":"object","properties":{}}""") };
        var ctx = new PromptContext(settings, new ProviderProfile(), tools,
            "screenshots are 1280x720 and show the primary monitor (1920x1080 physical pixels)", false,
            new DateTime(2026, 10, 8, 9, 30, 0));
        return PromptBuilder.Build(ctx);
    }

    [LinuxFact]
    public void Prompt_describes_the_linux_session_and_linux_safety_rules()
    {
        var os = PromptBuilder.DescribeLinux();
        var prompt = BuildPrompt(admin: false);
        Assert.Contains("Operating system: " + os, prompt);

        // The distribution's name from os-release, or plain "Linux".
        string? pretty = null;
        if (File.Exists("/etc/os-release"))
        {
            pretty = File.ReadLines("/etc/os-release").Where(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
                .Select(l => l["PRETTY_NAME=".Length..].Trim().Trim('"')).FirstOrDefault(v => v.Length > 0);
        }
        Assert.StartsWith(pretty ?? "Linux", os);

        var session = Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION");
        if (session == "x11") Assert.Contains("X11 session", os);
        else if (session == "wayland") Assert.Contains("Wayland session", os);
        else Assert.True(os.Contains("X11 session") || os.Contains("Wayland session") || os.Contains("no graphical session detected"), os);

        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP");
        if (!string.IsNullOrWhiteSpace(desktop)) Assert.Contains(desktop.Replace(':', '/') + " desktop", os);

        // Linux app-finding tips and the sudo/pkexec rule instead of the Windows ones.
        Assert.Contains(DefaultPrompts.LinuxTips, prompt);
        Assert.DoesNotContain(DefaultPrompts.WindowsTips, prompt);
        Assert.Contains(DefaultPrompts.SafetyAdminOffLinux.Trim(), prompt);
        Assert.Contains("sudo", prompt);
        Assert.Contains("pkexec", prompt);
        Assert.Contains("polkit", prompt);
        Assert.DoesNotContain("UAC", prompt);
        Assert.DoesNotContain("Run as administrator", prompt);
        Assert.DoesNotContain("{{", prompt);

        var adminPrompt = BuildPrompt(admin: true);
        Assert.Contains(DefaultPrompts.SafetyAdminOnLinux.Trim(), adminPrompt);
        Assert.DoesNotContain("UAC", adminPrompt);
    }

    // ---------------------------------------------------------------- MCP over Unix domain sockets

    private sealed class FakeTools : IToolHost
    {
        public static readonly ToolImage Image = ToolImage.FromBytes(new byte[] { 9, 8, 7, 6 }, "image/png", 2, 2);
        public readonly ConcurrentQueue<string> Calls = new();

        public IReadOnlyList<ToolSpec> GetTools() => new[]
        {
            ToolSpec.Create("echo", "Echoes the text argument.", """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
            ToolSpec.Create("screenshot", "Takes a screenshot.", """{"type":"object","properties":{}}"""),
        };

        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
        {
            Calls.Enqueue(name);
            return Task.FromResult(name switch
            {
                "echo" => ToolResult.Ok("echo: " + (arguments.TryGetProperty("text", out var t) ? t.GetString() : "?")),
                "screenshot" => ToolResult.Ok("1920x1080, foreground: gedit", Image),
                _ => ToolResult.Error($"Unknown tool '{name}'"),
            });
        }
    }

    /// <summary>The CLI agent's end of the bridge's stdio, made of two in-memory pipes.</summary>
    private sealed class StdioPair
    {
        private readonly MemoryPipe _toBridge = new();
        private readonly MemoryPipe _fromBridge = new();
        private readonly Stream _write;
        private readonly StreamReader _read;

        public StdioPair()
        {
            BridgeInput = _toBridge.Reader.AsStream();
            BridgeOutput = _fromBridge.Writer.AsStream();
            _write = _toBridge.Writer.AsStream();
            _read = new StreamReader(_fromBridge.Reader.AsStream(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
        }

        public Stream BridgeInput { get; }
        public Stream BridgeOutput { get; }

        public async Task SendAsync(string line)
        {
            await _write.WriteAsync(Encoding.UTF8.GetBytes(line + "\n"));
            await _write.FlushAsync();
        }

        public async Task<JsonElement> ReadMessageAsync()
        {
            using var cts = new CancellationTokenSource(Patience);
            var line = await _read.ReadLineAsync(cts.Token);
            Assert.NotNull(line);
            using var doc = JsonDocument.Parse(line!);
            return doc.RootElement.Clone();
        }

        public void CloseInput() => _toBridge.Writer.Complete();
    }

    private static string Request(int id, string method, string paramsJson = "{}") =>
        $$"""{"jsonrpc":"2.0","id":{{id}},"method":"{{method}}","params":{{paramsJson}}}""";

    private static string Initialize(int id) =>
        Request(id, "initialize", """{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"linux-test","version":"1.0"}}""");

    private static string SocketPath(McpPipeServer server) => Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + server.PipeName);

    [LinuxFact]
    public async Task Mcp_bridge_and_server_work_end_to_end_over_a_unix_socket()
    {
        var tools = new FakeTools();
        var server = new McpPipeServer(tools) { Diagnostics = _ => { } };
        var socket = SocketPath(server);
        try
        {
            server.Start();

            // .NET serves a named pipe on Linux as a Unix domain socket in the temp folder. Its path must fit
            // sockaddr_un.sun_path (108 bytes including the terminator).
            Assert.True(Encoding.UTF8.GetByteCount(socket) < 108, socket);
            await WaitUntil(() => File.Exists(socket), "the pipe socket");
            var (statExit, type) = CiEnvironmentTests.Run("stat", "-c", "%F", socket);
            Assert.True(statExit == 0, type);
            Assert.Equal("socket", type.Trim());

            var io = new StdioPair();
            var stderr = new MemoryStream();
            var bridge = McpBridge.RunAsync(io.BridgeInput, io.BridgeOutput, stderr, server.PipeName, server.Token, CancellationToken.None);

            await io.SendAsync(Initialize(1));
            var init = await io.ReadMessageAsync();
            Assert.Equal(1, init.GetProperty("id").GetInt32());
            Assert.Equal("2025-06-18", init.GetProperty("result").GetProperty("protocolVersion").GetString());
            Assert.Equal("deskpilot", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            await io.SendAsync("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");

            await io.SendAsync(Request(2, "tools/list"));
            var list = await io.ReadMessageAsync();
            Assert.Equal(2, list.GetProperty("id").GetInt32());
            var names = list.GetProperty("result").GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
            Assert.Equal(new[] { "echo", "screenshot" }, names);

            await io.SendAsync(Request(3, "tools/call", """{"name":"echo","arguments":{"text":"hello from linux"}}"""));
            var echo = await io.ReadMessageAsync();
            Assert.Equal(3, echo.GetProperty("id").GetInt32());
            Assert.Equal("echo: hello from linux", echo.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());

            await io.SendAsync(Request(4, "tools/call", """{"name":"screenshot","arguments":{}}"""));
            var shot = await io.ReadMessageAsync();
            var content = shot.GetProperty("result").GetProperty("content");
            Assert.Equal("image", content[1].GetProperty("type").GetString());
            Assert.Equal(FakeTools.Image.Base64Data, content[1].GetProperty("data").GetString());
            Assert.Equal(1, server.ActiveConnections);
            Assert.Equal(new[] { "echo", "screenshot" }, tools.Calls.ToArray());

            // The agent closing the bridge's stdin ends the bridge cleanly, and the server notices.
            io.CloseInput();
            Assert.Equal(McpBridge.ExitOk, await bridge.WaitAsync(Patience));
            Assert.Equal("", Encoding.UTF8.GetString(stderr.ToArray()));
            await WaitUntil(() => server.ActiveConnections == 0, "the server to drop the connection");
        }
        finally
        {
            await server.DisposeAsync();
        }

        // Shutting the server down removes the socket file, so /tmp does not fill up with stale sockets.
        await WaitUntil(() => !File.Exists(socket), "the socket file to be removed");
    }

    [LinuxFact]
    public async Task Mcp_server_rejects_a_bridge_with_the_wrong_token()
    {
        var tools = new FakeTools();
        var diagnostics = new ConcurrentQueue<string>();
        await using var server = new McpPipeServer(tools) { Diagnostics = diagnostics.Enqueue };
        server.Start();

        var io = new StdioPair();
        var stderr = new MemoryStream();
        var wrongToken = new string(server.Token.Select(c => c == '0' ? '1' : '0').ToArray());
        var bridge = McpBridge.RunAsync(io.BridgeInput, io.BridgeOutput, stderr, server.PipeName, wrongToken, CancellationToken.None);
        await io.SendAsync(Initialize(1));

        Assert.Equal(McpBridge.ExitOk, await bridge.WaitAsync(Patience));
        Assert.Contains("DeskPilot closed the connection.", Encoding.UTF8.GetString(stderr.ToArray()));
        Assert.Empty(tools.Calls);
        await WaitUntil(() => diagnostics.Any(d => d.Contains("did not present the session token")), "the rejection to be logged");
    }

    [LinuxFact]
    public async Task Mcp_bridge_reports_when_deskpilot_is_not_running()
    {
        var stdout = new MemoryStream();
        var stderr = new MemoryStream();
        var missing = McpPipeServer.PipeNamePrefix + Guid.NewGuid().ToString("N");
        var code = await McpBridge.RunAsync(new MemoryStream(), stdout, stderr, missing, "abc", TimeSpan.FromMilliseconds(500), CancellationToken.None)
            .WaitAsync(Patience);
        Assert.Equal(McpBridge.ExitCannotConnect, code);
        Assert.Equal(0, stdout.Length);
        Assert.Contains(McpBridge.NotRunningMessage, Encoding.UTF8.GetString(stderr.ToArray()));
    }

    // ---------------------------------------------------------------- Claude Code command line

    [LinuxFact]
    public void Claude_cli_runs_the_executable_directly_with_the_same_flags()
    {
        var exe = Path.Combine(Path.GetTempPath(), "dp-bin", "claude");
        var profile = new ProviderProfile { Effort = "minimal", ExtraCliArgs = "--debug-to-stderr" };
        var args = ClaudeCliCommand.BuildArguments(profile, "/tmp/dp/prompt.md", "/tmp/dp/mcp.json", "deskpilot");
        Assert.Equal(new[]
        {
            "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
            "--model", "haiku", "--system-prompt-file", "/tmp/dp/prompt.md", "--tools", "", "--strict-mcp-config",
            "--mcp-config", "/tmp/dp/mcp.json", "--allowedTools", "mcp__deskpilot",
            "--setting-sources", "", "--disable-slash-commands", "--no-session-persistence",
            "--effort", "low", "--debug-to-stderr",
        }, args);

        Assert.False(ClaudeCliCommand.IsShellScript(exe));
        Assert.Equal(exe, ClaudeCliCommand.PreferNative(exe, new[] { Path.GetTempPath() }));

        var psi = ClaudeCliCommand.BuildStartInfo(exe, args, "/tmp/dp/work", profile);
        // No cmd.exe wrapper: the CLI itself, with an argument vector (empty arguments survive as such).
        Assert.Equal(exe, psi.FileName);
        Assert.Equal("", psi.Arguments);
        Assert.Equal(args, psi.ArgumentList);
        Assert.False(psi.UseShellExecute);
        Assert.True(psi.RedirectStandardInput && psi.RedirectStandardOutput && psi.RedirectStandardError);
        Assert.Equal("/tmp/dp/work", psi.WorkingDirectory);
    }

    [LinuxFact]
    public void Mcp_config_points_the_cli_at_the_bridge_executable()
    {
        var server = new McpPipeServer(new FakeTools());
        var endpoint = server.GetEndpoint("/opt/deskpilot/usr/bin/deskpilot");
        using var doc = JsonDocument.Parse(ClaudeCliCommand.BuildMcpConfigJson(endpoint));
        var entry = doc.RootElement.GetProperty("mcpServers").GetProperty("deskpilot");
        Assert.Equal("stdio", entry.GetProperty("type").GetString());
        Assert.Equal("/opt/deskpilot/usr/bin/deskpilot", entry.GetProperty("command").GetString());
        Assert.Equal(new[] { McpBridge.Switch, server.PipeName, server.Token },
            entry.GetProperty("args").EnumerateArray().Select(a => a.GetString()).ToArray());
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessStateCollection
{
    public const string Name = "Core on Linux: process-wide state";
}

/// <summary>
/// Core checks that change process-wide state (AppPaths, environment variables, HOME) or start child processes
/// from freshly written scripts. They run on their own, after the parallel tests, and restore what they change.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public class CoreOnLinuxProcessStateTests
{
    private const UnixFileMode Rw = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode Rwx755 = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;

    private sealed class EnvScope : IDisposable
    {
        private readonly Dictionary<string, string?> _saved = new();

        public EnvScope Set(string name, string? value)
        {
            if (!_saved.ContainsKey(name)) _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return this;
        }

        public void Dispose()
        {
            foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    // ---------------------------------------------------------------- API key storage

    [LinuxFact]
    public void Secret_protector_uses_aes_gcm_and_a_key_file_only_the_user_can_read()
    {
        var saved = AppPaths.DataRoot;
        var root = CoreOnLinuxTests.TempDir();
        var other = CoreOnLinuxTests.TempDir();
        AppPaths.OverrideRoot(root);
        SecretProtector.ResetKeyCacheForTests();
        try
        {
            var keyFile = Path.Combine(root, ".secret-key");
            Assert.False(File.Exists(keyFile));

            var secret = "sk-test-" + Guid.NewGuid().ToString("N");
            var a = SecretProtector.Protect(secret);
            Assert.StartsWith("aesgcm:", a);
            Assert.DoesNotContain(secret, a);

            Assert.True(File.Exists(keyFile));
            Assert.False(File.Exists(keyFile + ".tmp"));
            Assert.Equal(Rw, File.GetUnixFileMode(keyFile));
            Assert.Equal(32, Convert.FromBase64String(File.ReadAllText(keyFile).Trim()).Length);

            Assert.Equal(secret, SecretProtector.Unprotect(a));
            var b = SecretProtector.Protect(secret);
            Assert.NotEqual(a, b); // fresh nonce every time

            // A new process reads the same key back from the file.
            SecretProtector.ResetKeyCacheForTests();
            Assert.Equal(secret, SecretProtector.Unprotect(b));
            Assert.Equal(Rw, File.GetUnixFileMode(keyFile));

            // Tampered text, Windows DPAPI text and another user's key all fail closed.
            var blob = Convert.FromBase64String(a["aesgcm:".Length..]);
            blob[^1] ^= 0x5a;
            Assert.Equal("", SecretProtector.Unprotect("aesgcm:" + Convert.ToBase64String(blob)));
            Assert.Equal("", SecretProtector.Unprotect("AQAAANCMnd8BFdERjHoAwE/Cl+sBAAAA"));
            Assert.Equal("", SecretProtector.Protect(""));

            AppPaths.OverrideRoot(other);
            SecretProtector.ResetKeyCacheForTests();
            Assert.Equal("", SecretProtector.Unprotect(a));
        }
        finally
        {
            AppPaths.OverrideRoot(saved);
            SecretProtector.ResetKeyCacheForTests();
            CoreOnLinuxTests.DeleteQuietly(root);
            CoreOnLinuxTests.DeleteQuietly(other);
        }
    }

    [LinuxFact]
    public void Api_key_comes_from_the_environment_variable_when_none_is_stored()
    {
        var name = "DESKPILOT_TEST_KEY_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var profile = new ProviderProfile { ApiKeyEnvVar = name };
        Assert.Equal("", SecretProtector.ResolveApiKey(profile));
        using (new EnvScope().Set(name, " test-value-123 "))
            Assert.Equal("test-value-123", SecretProtector.ResolveApiKey(profile));
        Assert.Equal("", SecretProtector.ResolveApiKey(profile));
    }

    // ---------------------------------------------------------------- Obsidian

    [LinuxFact]
    public void Obsidian_config_is_looked_up_in_xdg_flatpak_and_snap_locations()
    {
        var home = CoreOnLinuxTests.TempDir();
        var vaultA = Path.Combine(home, "Notes A");
        var vaultB = Path.Combine(home, "Notes B");
        Directory.CreateDirectory(vaultA);
        Directory.CreateDirectory(vaultB);
        // .NET reports ApplicationData as "" while ~/.config does not exist (SpecialFolderOption.None verifies it).
        Directory.CreateDirectory(Path.Combine(home, ".config"));
        try
        {
            using (new EnvScope().Set("HOME", home).Set("XDG_CONFIG_HOME", null))
            {
                var candidates = ObsidianVaultDetector.ConfigCandidates().ToList();
                var native = Path.Combine(home, ".config", "obsidian", "obsidian.json");
                var flatpak = Path.Combine(home, ".var", "app", "md.obsidian.Obsidian", "config", "obsidian", "obsidian.json");
                var snap = Path.Combine(home, "snap", "obsidian", "current", ".config", "obsidian", "obsidian.json");
                Assert.Equal(new[] { native, flatpak, snap }, candidates);

                // Vaults from the native and the Flatpak config, newest first; missing folders are dropped.
                Directory.CreateDirectory(Path.GetDirectoryName(native)!);
                File.WriteAllText(native, JsonSerializer.Serialize(new
                {
                    vaults = new Dictionary<string, object> { ["a"] = new { path = vaultA, ts = 100 } },
                }));
                Directory.CreateDirectory(Path.GetDirectoryName(flatpak)!);
                File.WriteAllText(flatpak, JsonSerializer.Serialize(new
                {
                    vaults = new Dictionary<string, object>
                    {
                        ["b"] = new { path = vaultB, ts = 200, open = true },
                        ["gone"] = new { path = Path.Combine(home, "deleted vault"), ts = 300 },
                    },
                }));
                Assert.Equal(new[] { vaultA, vaultB }, ObsidianVaultDetector.FindVaults());
            }

            // XDG_CONFIG_HOME moves the native config, as it does for Obsidian itself.
            var xdg = Path.Combine(home, "xdg-config");
            Directory.CreateDirectory(xdg);
            using (new EnvScope().Set("HOME", home).Set("XDG_CONFIG_HOME", xdg))
                Assert.Equal(Path.Combine(xdg, "obsidian", "obsidian.json"), ObsidianVaultDetector.ConfigCandidates().First());
        }
        finally
        {
            CoreOnLinuxTests.DeleteQuietly(home);
        }
    }

    // ---------------------------------------------------------------- Claude Code detection

    [LinuxFact]
    public async Task Claude_probe_reports_not_installed_without_throwing()
    {
        Assert.SkipWhen(ExecutableLocator.Find("claude") != null, "Claude Code is installed on this machine");
        Assert.Null(ClaudeCliCommand.ResolveExecutable(null));
        var status = await ClaudeCliProbe.ProbeAsync(null, CancellationToken.None).WaitAsync(CoreOnLinuxTests.Patience);
        Assert.Equal(ClaudeCliProbe.ToolName, status.Name);
        Assert.Null(status.Path);
        Assert.Null(status.Version);
        Assert.Null(status.LoggedIn);
        Assert.Equal(ClaudeCliProbe.NotInstalledDetail, status.Detail);
    }

    [LinuxFact]
    public async Task Claude_probe_runs_a_configured_cli_without_the_parent_session_variables()
    {
        var dir = CoreOnLinuxTests.TempDir();
        try
        {
            // A stand-in for the claude CLI. It reports CLAUDECODE as its plan, so a leaked variable would show.
            var fake = Path.Combine(dir, "claude");
            File.WriteAllText(fake,
                "#!/bin/sh\n" +
                "case \"$1\" in\n" +
                "  --version) echo '2.1.293 (Claude Code)' ;;\n" +
                "  auth) printf '{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"subscriptionType\":\"%s\"}\\n' \"${CLAUDECODE:-clean}\" ;;\n" +
                "  *) exit 2 ;;\n" +
                "esac\n");
            File.SetUnixFileMode(fake, Rwx755);

            Assert.Equal(fake, ClaudeCliCommand.ResolveExecutable(fake));

            using var env = new EnvScope().Set("CLAUDECODE", "1").Set("CLAUDE_CODE_ENTRYPOINT", "cli");
            var status = await ClaudeCliProbe.ProbeAsync(fake, CancellationToken.None).WaitAsync(CoreOnLinuxTests.Patience);
            Assert.Equal(fake, status.Path);
            Assert.Equal("2.1.293", status.Version);
            Assert.True(status.LoggedIn);
            Assert.Equal("Logged in with a Claude subscription (clean)", status.Detail);

            // The start info for the real agent drops them too, and applies the profile's own variables.
            var profile = new ProviderProfile { Thinking = ThinkingMode.Off, ExtraEnv = new() { ["DESKPILOT_EXTRA"] = "yes" } };
            env.Set("ANTHROPIC_API_KEY", "not-a-real-key");
            var psi = ClaudeCliCommand.BuildStartInfo(fake, new[] { "-p" }, dir, profile);
            Assert.False(psi.Environment.ContainsKey("CLAUDECODE"));
            Assert.False(psi.Environment.ContainsKey("CLAUDE_CODE_ENTRYPOINT"));
            Assert.False(psi.Environment.ContainsKey("ANTHROPIC_API_KEY"));
            Assert.Equal("0", psi.Environment["MAX_THINKING_TOKENS"]);
            Assert.Equal("yes", psi.Environment["DESKPILOT_EXTRA"]);
            Assert.True(psi.Environment.ContainsKey("PATH"));
        }
        finally
        {
            CoreOnLinuxTests.DeleteQuietly(dir);
        }
    }
}

/// <summary>
/// packaging/linux: the menu entry, the icons, and install.sh / uninstall.sh run with sh (dash on Debian and
/// Ubuntu) inside the real CI sessions. The scripts only ever see a scratch HOME.
/// </summary>
public class PackagingScriptTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DeskPilot.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string Packaging(string name) => Path.Combine(RepoRoot(), "packaging", "linux", name);

    private static (int Exit, string Output) Run(string file, IReadOnlyDictionary<string, string?>? env, params string[] args)
    {
        var psi = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null)
        {
            foreach (var (k, v) in env)
            {
                if (v == null) psi.Environment.Remove(k);
                else psi.Environment[k] = v;
            }
        }
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } }
        p.WaitForExit();
        return (p.HasExited ? p.ExitCode : -1, stdout.Result + stderr.Result);
    }

    private static Dictionary<string, string> DesktopEntry(string path)
    {
        var lines = File.ReadAllLines(path);
        Assert.Equal("[Desktop Entry]", lines[0]);
        return lines.Skip(1).Where(l => l.Contains('=') && !l.StartsWith('#'))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
    }

    private static (int Width, int Height) PngSize(string path)
    {
        var b = File.ReadAllBytes(path);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, b[..4]);
        return ((b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19], (b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]);
    }

    [Fact]
    public void Menu_entry_matches_the_app_and_icons_have_their_sizes()
    {
        var entry = DesktopEntry(Packaging("deskpilot.desktop"));
        Assert.Equal("Application", entry["Type"]);
        Assert.Equal("DeskPilot", entry["Name"]);
        Assert.Equal("deskpilot", entry["Exec"]);
        Assert.Equal("deskpilot", entry["Icon"]);
        Assert.Equal("false", entry["Terminal"]);
        Assert.Equal("Utility;", entry["Categories"]);
        Assert.False(string.IsNullOrWhiteSpace(entry["Comment"]));

        // Avalonia's X11 backend sets WM_CLASS to (process name, entry assembly name); both are "deskpilot".
        Assert.Equal("deskpilot", typeof(DeskPilot.Linux.Program).Assembly.GetName().Name);
        Assert.Equal("deskpilot", entry["StartupWMClass"]);

        Assert.Equal((256, 256), PngSize(Packaging("deskpilot.png")));
        Assert.Equal((128, 128), PngSize(Packaging("deskpilot-128.png")));
    }

    [LinuxFact]
    public void Menu_entry_passes_desktop_file_validate()
    {
        var validator = ExecutableLocator.Find("desktop-file-validate");
        Assert.SkipWhen(validator == null, "desktop-file-validate (desktop-file-utils) is not installed");
        var (exit, output) = Run(validator!, null, Packaging("deskpilot.desktop"));
        Assert.True(exit == 0, output);
        Assert.DoesNotContain("error", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("warning", output, StringComparison.OrdinalIgnoreCase);
    }

    [LinuxFact]
    public void Install_check_reports_this_session()
    {
        var (exit, output) = Run("sh", null, Packaging("install.sh"), "--check");
        Assert.True(exit == 0, output);
        switch (Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION"))
        {
            case "x11":
                // Xvfb + openbox with the base packages: Xlib, XTest, xclip and AT-SPI are all there.
                Assert.Contains("Desktop session: X11 (openbox)", output);
                Assert.Contains("All helper tools for this session are installed.", output);
                break;
            case "wayland":
                // Headless sway: grim, wtype, swaymsg, wl-clipboard and AT-SPI are installed; Xwayland is off.
                Assert.Contains("Desktop session: Wayland (sway)", output);
                Assert.Contains("All helper tools for this session are installed.", output);
                Assert.Contains("Xwayland", output);
                break;
            default:
                Assert.Contains("Desktop session:", output);
                break;
        }
    }

    /// <summary>A PATH with only the basic commands the scripts use, so every helper tool looks missing.</summary>
    private static string BarePath(string dir)
    {
        var bin = Path.Combine(dir, "bin");
        Directory.CreateDirectory(bin);
        foreach (var tool in new[] { "sed", "tr", "awk", "grep", "id", "uname", "dirname", "cat", "mkdir", "cp", "chmod", "mv", "rm", "touch", "ldconfig" })
        {
            var real = ExecutableLocator.Find(tool, extraCandidates: new[] { "/usr/sbin", "/sbin" });
            if (real != null) File.CreateSymbolicLink(Path.Combine(bin, tool), real);
        }
        return bin;
    }

    [LinuxFact]
    public void Install_check_names_the_missing_packages_for_each_route()
    {
        var dir = CoreOnLinuxTests.TempDir();
        try
        {
            var path = BarePath(dir);
            (int, string) Check(params (string, string?)[] vars)
            {
                var env = new Dictionary<string, string?>
                {
                    ["PATH"] = path, ["HOME"] = dir, ["DISPLAY"] = null, ["WAYLAND_DISPLAY"] = null,
                    ["XDG_SESSION_TYPE"] = null, ["XDG_CURRENT_DESKTOP"] = null, ["DESKPILOT_SESSION"] = null,
                };
                foreach (var (k, v) in vars) env[k] = v;
                return Run("/bin/sh", env, Packaging("install.sh"), "--check");
            }

            var (exit, sway) = Check(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-9"), ("XDG_CURRENT_DESKTOP", "sway"));
            Assert.True(exit == 0, sway);
            Assert.Contains("Desktop session: Wayland (sway)", sway);
            Assert.Contains("Screenshots need grim.", sway);
            Assert.Contains("Typing and key presses need wtype.", sway);
            Assert.Contains("swaymsg", sway);
            Assert.Contains("wl-clipboard", sway);
            Assert.Contains("Xwayland", sway);
            Assert.Contains("Claude Code (the default model provider) was not found", sway);
            var install = sway.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("sudo ", StringComparison.Ordinal));
            if (install != null)
            {
                foreach (var pkg in new[] { "grim", "wtype", "sway", "wl-clipboard" }) Assert.Contains(" " + pkg, install);
            }

            var (_, x11) = Check(("XDG_SESSION_TYPE", "x11"), ("DISPLAY", ":9"), ("XDG_CURRENT_DESKTOP", "XFCE"));
            Assert.Contains("Desktop session: X11 (XFCE)", x11);
            Assert.Contains("xclip", x11);
            Assert.DoesNotContain("grim", x11);

            var (_, forced) = Check(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("DESKPILOT_SESSION", "x11"));
            Assert.Contains("Desktop session: X11", forced);

            var (_, gnome) = Check(("XDG_SESSION_TYPE", "wayland"), ("WAYLAND_DISPLAY", "wayland-0"), ("DISPLAY", ":0"), ("XDG_CURRENT_DESKTOP", "ubuntu:GNOME"));
            Assert.Contains("Desktop session: Wayland (ubuntu:GNOME)", gnome);
            Assert.Contains("permission prompt", gnome);
            Assert.DoesNotContain("Xwayland", gnome);
            if (!File.Exists("/usr/share/dbus-1/services/org.freedesktop.portal.Desktop.service"))
                Assert.Contains("xdg-desktop-portal", gnome);
            if (!File.Exists("/usr/share/xdg-desktop-portal/portals/gnome.portal"))
                Assert.Contains("xdg-desktop-portal-gnome", gnome);

            var (_, none) = Check();
            Assert.Contains("Desktop session: none detected", none);
            Assert.Contains("No graphical session found", none);
        }
        finally
        {
            CoreOnLinuxTests.DeleteQuietly(dir);
        }
    }

    [LinuxFact]
    public void Install_and_uninstall_touch_exactly_their_own_files()
    {
        Assert.SkipWhen(DeskPilot.Desktop.Linux.Services.LinuxProcessInfo.IsCurrentProcessElevated, "install.sh refuses to run as root");
        var dir = CoreOnLinuxTests.TempDir();
        try
        {
            var home = Path.Combine(dir, "home with space");
            var program = Path.Combine(dir, "deskpilot-build");
            File.WriteAllText(program, "#!/bin/sh\necho fake deskpilot\n");
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            // Files of other programs in the same folders must survive.
            var others = new[] { ".local/bin/other-tool", ".local/share/applications/other.desktop", ".local/share/icons/hicolor/256x256/apps/other.png" };
            foreach (var o in others)
            {
                var p = Path.Combine(home, o);
                Directory.CreateDirectory(Path.GetDirectoryName(p)!);
                File.WriteAllText(p, "keep");
            }

            var env = new Dictionary<string, string?> { ["HOME"] = home, ["XDG_DATA_HOME"] = null, ["XDG_CONFIG_HOME"] = null };
            var (exit, output) = Run("sh", env, Packaging("install.sh"), program);
            Assert.True(exit == 0, output);
            Assert.Contains("DeskPilot is installed for", output);
            Assert.Contains("Desktop session:", output);

            var installed = Path.Combine(home, ".local", "bin", "deskpilot");
            Assert.Equal(File.ReadAllText(program), File.ReadAllText(installed));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead |
                         UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute, File.GetUnixFileMode(installed));

            var entryPath = Path.Combine(home, ".local", "share", "applications", "deskpilot.desktop");
            var entry = DesktopEntry(entryPath);
            // A path with a space is quoted, as the Desktop Entry spec requires for Exec.
            Assert.Equal("\"" + installed + "\"", entry["Exec"]);
            Assert.Equal("deskpilot", entry["StartupWMClass"]);
            var validator = ExecutableLocator.Find("desktop-file-validate");
            if (validator != null)
            {
                var (vExit, vOut) = Run(validator, null, entryPath);
                Assert.True(vExit == 0, vOut);
            }
            Assert.Equal((256, 256), PngSize(Path.Combine(home, ".local/share/icons/hicolor/256x256/apps/deskpilot.png")));
            Assert.Equal((128, 128), PngSize(Path.Combine(home, ".local/share/icons/hicolor/128x128/apps/deskpilot.png")));

            // Installing again updates in place.
            Assert.Equal(0, Run("sh", env, Packaging("install.sh"), program).Exit);

            (exit, output) = Run("sh", env, Packaging("uninstall.sh"));
            Assert.True(exit == 0, output);
            Assert.Contains("DeskPilot is uninstalled.", output);
            var left = Directory.EnumerateFiles(home, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(home, f)).OrderBy(f => f, StringComparer.Ordinal).ToArray();
            Assert.Equal(others.OrderBy(f => f, StringComparer.Ordinal).ToArray(), left);

            (exit, output) = Run("sh", env, Packaging("uninstall.sh"));
            Assert.True(exit == 0, output);
            Assert.Contains("nothing to remove", output);
        }
        finally
        {
            CoreOnLinuxTests.DeleteQuietly(dir);
        }
    }
}

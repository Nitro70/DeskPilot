using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Mcp;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;
using DeskPilot.Core.Tools;
using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.X11;

namespace DeskPilot.Linux.Tests;

/// <summary>
/// The whole stack the model drives, on a real Linux session: the factory's desktop services, the safety guard and
/// the computer tools, against a real GTK dialog. Same calls the MCP server and the HTTP agent loop make.
/// </summary>
internal static class ToolStackHarness
{
    public static async Task RunAsync(string gdkBackend)
    {
        var settings = SettingsStore.CreateDefault();
        settings.Safety.FailsafeCorner = false;
        settings.Screen.ScreenshotAfterAction = false;
        settings.Screen.ActionSettleDelayMs = 150;
        var desktop = LinuxDesktopFactory.CreateFor(LinuxSession.Detect(), () => settings);
        var control = new AgentRunControl();
        control.BeginTurn();
        var tools = new ComputerToolHost(desktop, new SafetyGuard(() => settings, desktop.Windows, desktop.Ui), () => settings, control);

        var title = "DeskPilotStack-" + Guid.NewGuid().ToString("N")[..10];
        var psi = new ProcessStartInfo("zenity")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "--entry", "--title=" + title, "--text=Type here" }) psi.ArgumentList.Add(a);
        psi.Environment["GDK_BACKEND"] = gdkBackend;
        psi.Environment["GSK_RENDERER"] = "cairo";
        using var zenity = Process.Start(psi)!;
        try
        {
            async Task<ToolResult> Call(string name, string json)
            {
                using var doc = JsonDocument.Parse(json);
                var r = await tools.ExecuteAsync(name, doc.RootElement.Clone(), CancellationToken.None);
                Assert.False(r.IsError, $"{name} failed: {r.Text}");
                return r;
            }

            // Wait until the window manager lists the dialog.
            var sw = Stopwatch.StartNew();
            string list = "";
            while (sw.ElapsedMilliseconds < 20000)
            {
                list = (await Call("list_windows", "{}")).Text;
                if (list.Contains(title, StringComparison.Ordinal)) break;
                await Task.Delay(250);
            }
            Assert.Contains(title, list);

            var shot = await Call("screenshot", "{}");
            Assert.Single(shot.Images);
            Assert.True(shot.Images[0].Width > 100 && shot.Images[0].Height > 100);

            await Call("focus_window", JsonSerializer.Serialize(new { title }));
            await Task.Delay(300);
            const string text = "stack ok ✓ é";
            await Call("type_text", JsonSerializer.Serialize(new { text, press_enter = true }));

            var output = zenity.StandardOutput.ReadToEndAsync();
            Assert.True(zenity.WaitForExit(15000), "zenity did not close after Enter");
            Assert.Equal(text, (await output).TrimEnd('\n'));
        }
        finally
        {
            try { if (!zenity.HasExited) zenity.Kill(true); } catch (InvalidOperationException) { }
            (desktop.Input as IDisposable)?.Dispose();
        }
    }
}

[Collection("X11 desktop session")]
public sealed class LinuxIntegrationX11Tests
{
    [X11Fact]
    public Task Tools_drive_a_real_dialog_on_x11() => ToolStackHarness.RunAsync("x11");

    /// <summary>The real app starts on X11, and its window carries its pid (the safety guard's own-window check needs it).</summary>
    [X11Fact]
    public async Task Real_app_starts_and_its_window_has_its_pid()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "deskpilot");
        Assert.True(File.Exists(exe), "deskpilot apphost not found next to the tests");

        var home = Path.Combine(Path.GetTempPath(), "deskpilot-apphome-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(home);
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.Environment["HOME"] = home;
        psi.Environment["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        psi.Environment["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        using var app = Process.Start(psi)!;
        using var windows = new X11WindowManager();
        try
        {
            WindowInfo? mine = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 30000 && !app.HasExited)
            {
                mine = windows.ListWindows().FirstOrDefault(w => w.ProcessId == app.Id);
                if (mine != null) break;
                await Task.Delay(300);
            }
            Assert.False(app.HasExited, "the app exited: " + await app.StandardError.ReadToEndAsync());
            Assert.NotNull(mine);
            Assert.Contains("DeskPilot", mine!.Title, StringComparison.OrdinalIgnoreCase);

            var log = Directory.GetFiles(Path.Combine(home, ".local", "share", "DeskPilot", "logs"), "*.log").Single();
            var text = await File.ReadAllTextAsync(log);
            Assert.Contains("Session: X11", text);
            Assert.DoesNotContain(" ERROR ", text);
        }
        finally
        {
            try { if (!app.HasExited) app.Kill(true); } catch (InvalidOperationException) { }
            try { Directory.Delete(home, true); } catch (IOException) { }
        }
    }
}

[Collection("Wayland session")]
public sealed class LinuxIntegrationWaylandTests
{
    [WaylandFact]
    public async Task Tools_drive_a_real_dialog_on_wayland()
    {
        if (Environment.GetEnvironmentVariable("DESKPILOT_TEST_SESSION") != "wayland") return;
        await ToolStackHarness.RunAsync("wayland");
    }
}

/// <summary>The real "deskpilot --mcp-bridge" process relays an MCP client to the in-app server over a Unix socket.</summary>
public sealed class LinuxBridgeProcessTests
{
    private sealed class EchoTools : IToolHost
    {
        public IReadOnlyList<ToolSpec> GetTools() => new[] { ToolSpec.Create("echo", "Echo text", "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\"}}}") };
        public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct) =>
            Task.FromResult(ToolResult.Ok("echo: " + JsonArgs.GetString(arguments, "text")));
    }

    [LinuxFact]
    public async Task Bridge_process_round_trip()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "deskpilot");
        Assert.True(File.Exists(exe), "deskpilot apphost not found next to the tests");
        await using var server = new McpPipeServer(new EchoTools());
        server.Start();
        var endpoint = server.GetEndpoint(exe);

        var psi = new ProcessStartInfo(endpoint.Command)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in endpoint.Args) psi.ArgumentList.Add(a);
        using var bridge = Process.Start(psi)!;
        try
        {
            var stdin = bridge.StandardInput;
            var stdout = bridge.StandardOutput;
            async Task<JsonElement> Request(int id, string method, object? @params)
            {
                await stdin.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params }));
                await stdin.FlushAsync();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var line = await stdout.ReadLineAsync(cts.Token);
                Assert.False(string.IsNullOrEmpty(line), "no reply; stderr: " + bridge.StandardError.ReadToEnd());
                using var doc = JsonDocument.Parse(line!);
                return doc.RootElement.Clone();
            }

            var init = await Request(1, "initialize", new { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
            Assert.Equal("deskpilot", init.GetProperty("result").GetProperty("serverInfo").GetProperty("name").GetString());
            var list = await Request(2, "tools/list", new { });
            Assert.Equal("echo", list.GetProperty("result").GetProperty("tools")[0].GetProperty("name").GetString());
            var call = await Request(3, "tools/call", new { name = "echo", arguments = new { text = "hi from linux" } });
            Assert.Equal("echo: hi from linux", call.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString());
        }
        finally
        {
            try { bridge.StandardInput.Close(); } catch (IOException) { }
            if (!bridge.WaitForExit(5000)) bridge.Kill(true);
        }
    }
}

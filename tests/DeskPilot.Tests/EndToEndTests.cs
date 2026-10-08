using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Agent;
using DeskPilot.Core.Desktop;
using DeskPilot.Core.Settings;

namespace DeskPilot.Tests;

/// <summary>
/// Real end-to-end runs through the Claude Code CLI, the MCP bridge process (DeskPilot.exe --mcp-bridge)
/// and the named-pipe MCP server, with DRY RUN on so nothing on the desktop is clicked or typed.
/// Only run when DESKPILOT_LIVE_TESTS=1 (they use a little of the Claude subscription).
/// Set DESKPILOT_EXE to test a published exe instead of the build output.
/// </summary>
[Collection("Live")]
public class EndToEndTests
{
    private static bool Live => Environment.GetEnvironmentVariable("DESKPILOT_LIVE_TESTS") == "1";

    private static string FindExe()
    {
        var overridePath = Environment.GetEnvironmentVariable("DESKPILOT_EXE");
        if (!string.IsNullOrWhiteSpace(overridePath)) return overridePath;
        var local = Path.Combine(AppContext.BaseDirectory, "DeskPilot.exe");
        if (File.Exists(local)) return local;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "DeskPilot.sln"))) dir = dir.Parent;
        if (dir == null) throw new FileNotFoundException("DeskPilot.exe not found");
        var built = Directory.GetFiles(Path.Combine(dir.FullName, "src", "DeskPilot", "bin"), "DeskPilot.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        return built ?? throw new FileNotFoundException("DeskPilot.exe not found; build the app first");
    }

    private static async Task<(TurnResult Result, List<AgentEvent> Events)> RunAsync(string prompt, Action<AppSettings>? configure = null)
    {
        var dir = Path.Combine(Path.GetTempPath(), "deskpilot-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(Path.Combine(dir, "settings.json"));
        store.Update(s =>
        {
            s.FirstRunCompleted = true;
            s.Safety.DryRun = true;
            s.Safety.MaxStepsPerTurn = 8;
            s.Safety.FailsafeCorner = false;
            var p = s.ActiveProfile!;
            p.Model = "haiku";
            p.Thinking = ThinkingMode.Off;
            configure?.Invoke(s);
        });

        var events = new List<AgentEvent>();
        await using var session = new AgentSession(store, DesktopFactory.CreateDefault(), confirmation: null, observer: null, FindExe());
        session.EventRaised += e => { lock (events) events.Add(e); };
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var result = await session.SendAsync(prompt, cts.Token);
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        lock (events) return (result, events.ToList());
    }

    [Fact]
    public async Task Claude_cli_takes_a_screenshot_through_the_mcp_bridge()
    {
        if (!Live) return;
        var (result, events) = await RunAsync(
            "Call the screenshot tool exactly once, then answer in one short sentence: which window is in the foreground? Do nothing else.");

        Assert.True(result.Outcome == TurnOutcome.Completed, $"{result.Outcome}: {result.Error}\n{Dump(events)}");
        var shot = events.OfType<ToolResultEvent>().FirstOrDefault(e => e.ToolName == "screenshot");
        Assert.True(shot != null, "no screenshot tool call\n" + Dump(events));
        Assert.False(shot!.IsError, shot.Text);
        Assert.NotNull(shot.Image);
        Assert.False(string.IsNullOrWhiteSpace(result.FinalText));
    }

    [Fact]
    public async Task Dry_run_click_is_reported_not_performed()
    {
        if (!Live) return;
        var (result, events) = await RunAsync(
            "This is a test of the click tool. Call the click tool once at x=5, y=5 (left button), then reply DONE. Do not call any other tool.",
            s => s.Screen.ScreenshotAfterAction = false);

        Assert.True(result.Outcome == TurnOutcome.Completed, $"{result.Outcome}: {result.Error}\n{Dump(events)}");
        var click = events.OfType<ToolResultEvent>().FirstOrDefault(e => e.ToolName == "click");
        Assert.True(click != null, "no click tool call\n" + Dump(events));
        Assert.Contains("DRY RUN", click!.Text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Dump(IEnumerable<AgentEvent> events) =>
        string.Join("\n", events.Select(e => e switch
        {
            ToolCallEvent c => $"call {c.ToolName} {c.ArgumentsJson}",
            ToolResultEvent r => $"result {r.ToolName} error={r.IsError} {r.Text[..Math.Min(200, r.Text.Length)]}",
            AssistantTextEvent t => $"text {t.Text}",
            StatusEvent s => $"status {s.Level} {s.Message}",
            TurnCompletedEvent t => $"done {t.Result.Outcome} {t.Result.Error}",
            _ => e.GetType().Name,
        }));
}

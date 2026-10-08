using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Cli;

public static partial class ClaudeCliProbe
{
    public const string ToolName = "Claude Code";

    internal const string NotInstalledDetail = "Not installed. Install Claude Code, then run 'claude' once to log in.";
    internal const string NotLoggedInDetail = "Not logged in: run 'claude auth login'";

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Finds the claude CLI and reports its version and login state (`claude auth status`). Never throws.</summary>
    public static async Task<CliToolStatus> ProbeAsync(string? configuredPath, CancellationToken ct)
    {
        string? path = null;
        string? version = null;
        try
        {
            path = ClaudeCliCommand.ResolveExecutable(configuredPath);
            if (path == null) return new CliToolStatus(ToolName, null, null, null, NotInstalledDetail);

            var v = await RunAsync(path, new[] { "--version" }, ct).ConfigureAwait(false);
            version = ParseVersion(v.StdOut) ?? ParseVersion(v.StdErr);

            var auth = await RunAsync(path, new[] { "auth", "status" }, ct).ConfigureAwait(false);
            var (loggedIn, detail) = auth.TimedOut
                ? (null, "Claude Code did not answer 'claude auth status' in time.")
                : DescribeAuthStatus(auth.StdOut);
            return new CliToolStatus(ToolName, path, version, loggedIn, detail);
        }
        catch (OperationCanceledException)
        {
            return new CliToolStatus(ToolName, path, version, null, "Check cancelled.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Claude CLI probe failed: {ex.GetType().Name}: {ex.Message}");
            return new CliToolStatus(ToolName, path, version, null, "Could not run Claude Code: " + ex.Message);
        }
    }

    /// <summary>"2.1.293 (Claude Code)" -> "2.1.293".</summary>
    internal static string? ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        var m = VersionPattern().Match(output);
        return m.Success ? m.Value : null;
    }

    /// <summary>
    /// Reads `claude auth status` JSON ({"loggedIn":true,"authMethod":"claude.ai","subscriptionType":"max",...}).
    /// Only the login state, method, provider and plan are used; the email and organization never are.
    /// </summary>
    internal static (bool? LoggedIn, string Detail) DescribeAuthStatus(string? output)
    {
        var json = ExtractJsonObject(output);
        if (json == null)
            return (null, "Login state unknown (this Claude Code version has no 'claude auth status'). If DeskPilot cannot connect, run 'claude auth login'.");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("loggedIn", out var li) || li.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, "Login state unknown. If DeskPilot cannot connect, run 'claude auth login'.");

            if (li.ValueKind == JsonValueKind.False) return (false, NotLoggedInDetail);

            var method = Str(root, "authMethod") ?? "";
            var plan = Str(root, "subscriptionType");
            var provider = Str(root, "apiProvider");

            if (!string.IsNullOrEmpty(provider) && !provider.Equals("firstParty", StringComparison.OrdinalIgnoreCase))
                return (true, $"Logged in through {provider}");
            if (method.Contains("api", StringComparison.OrdinalIgnoreCase) && method.Contains("key", StringComparison.OrdinalIgnoreCase))
                return (true, "Logged in with an API key (usage is billed to that key)");
            if (method.Contains("claude", StringComparison.OrdinalIgnoreCase) || method.Contains("oauth", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(plan))
                return (true, "Logged in with a Claude subscription" + (string.IsNullOrWhiteSpace(plan) ? "" : $" ({plan.Trim()})"));
            return (true, string.IsNullOrWhiteSpace(method) ? "Logged in" : $"Logged in ({method.Trim()})");
        }
        catch (JsonException)
        {
            return (null, "Login state unknown. If DeskPilot cannot connect, run 'claude auth login'.");
        }
    }

    private static string? ExtractJsonObject(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        int start = output.IndexOf('{'), end = output.LastIndexOf('}');
        return start >= 0 && end > start ? output[start..(end + 1)] : null;
    }

    private static string? Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private sealed record RunOutput(string StdOut, string StdErr, int? ExitCode, bool TimedOut);

    private static async Task<RunOutput> RunAsync(string exePath, IReadOnlyList<string> args, CancellationToken ct)
    {
        // Same environment rules as a default Claude profile, so the login reported is the one DeskPilot will use.
        var psi = ClaudeCliCommand.BuildStartInfo(exePath, args, AppPaths.AgentWorkDirectory, new ProviderProfile());
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        try { process.StandardInput.Close(); }
        catch (IOException) { }

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout);
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            ct.ThrowIfCancellationRequested();
            return new RunOutput("", "", null, TimedOut: true);
        }

        var done = Task.WhenAll(stdout, stderr);
        await Task.WhenAny(done, Task.Delay(2000, CancellationToken.None)).ConfigureAwait(false);
        return new RunOutput(
            stdout.IsCompletedSuccessfully ? stdout.Result : "",
            stderr.IsCompletedSuccessfully ? stderr.Result : "",
            process.ExitCode,
            TimedOut: false);
    }

    private static void KillQuietly(Process p)
    {
        try { p.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?(?:-[0-9A-Za-z.]+)?")]
    private static partial Regex VersionPattern();
}

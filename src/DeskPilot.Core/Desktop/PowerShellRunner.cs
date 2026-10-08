using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Core.Desktop;

/// <summary>
/// Runs a PowerShell or cmd command in a hidden child process and captures its output. The child gets
/// DeskPilot's own token; nothing here ever asks for elevation.
/// </summary>
public sealed class PowerShellRunner : IShellRunner
{
    internal const int MaxStreamChars = 64 * 1024;
    internal const int DefaultTimeoutMs = 60_000;
    // After the shell exits, a background grandchild may still hold the output pipes open; stop waiting after this.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    internal const string PowerShellPreamble =
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$OutputEncoding=[Text.Encoding]::UTF8;$ProgressPreference='SilentlyContinue';";

    public async Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        command ??= "";
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        var (psi, note) = BuildStartInfo(command, shell, ResolveWorkingDirectory(workingDirectory));
        var stdout = new CappedText(MaxStreamChars);
        var stderr = new CappedText(MaxStreamChars);
        if (note != null) stderr.AppendLine(note);

        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start()) return new ShellResult(-1, "", stderr + $"Could not start {psi.FileName}.", false);
        }
        catch (Win32Exception ex)
        {
            return new ShellResult(-1, "", stderr + $"Could not start {psi.FileName}: {ex.Message}", false);
        }

        // Commands must never wait for keyboard input that will not come.
        try { process.StandardInput.Close(); } catch (IOException) { }

        var readOut = Pump(process.StandardOutput, stdout);
        var readErr = Pump(process.StandardError, stderr);

        bool timedOut = false;
        using (var timeout = new CancellationTokenSource(timeoutMs))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct))
        {
            try
            {
                await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                KillTree(process);
                if (ct.IsCancellationRequested) throw new OperationCanceledException("The command was cancelled.", ct);
                timedOut = true;
            }
        }

        try { await Task.WhenAll(readOut, readErr).WaitAsync(DrainTimeout, CancellationToken.None).ConfigureAwait(false); }
        catch (TimeoutException) { }

        int exitCode = -1;
        if (!timedOut)
        {
            try { exitCode = process.ExitCode; } catch (InvalidOperationException) { }
        }
        if (timedOut) stderr.AppendLine($"[timed out after {timeoutMs / 1000.0:0.#} s; the process and its children were stopped]");
        return new ShellResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
    }

    internal static (ProcessStartInfo Psi, string? Note) BuildStartInfo(string command, string shell, string workingDirectory)
    {
        var kind = (shell ?? "").Trim().ToLowerInvariant();
        string fileName;
        string arguments;
        string? note = null;
        switch (kind)
        {
            case "" or "powershell" or "powershell.exe" or "ps" or "windowspowershell":
                fileName = WindowsPowerShellPath();
                arguments = PowerShellArguments(command);
                break;
            case "pwsh" or "pwsh.exe" or "powershell7" or "powershellcore":
                var pwsh = ExecutableLocator.Find("pwsh");
                if (pwsh != null && pwsh.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    fileName = pwsh;
                }
                else
                {
                    fileName = WindowsPowerShellPath();
                    note = "[pwsh (PowerShell 7) was not found; ran Windows PowerShell instead]";
                }
                arguments = PowerShellArguments(command);
                break;
            case "cmd" or "cmd.exe":
                fileName = CmdPath();
                arguments = CmdArguments(command);
                break;
            default:
                throw new ArgumentException($"Unknown shell '{shell}'. Use \"powershell\", \"pwsh\" or \"cmd\".", nameof(shell));
        }

        var psi = new ProcessStartInfo(fileName, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };
        // A PowerShell 7 parent leaves its own module folders in PSModulePath, which makes Windows PowerShell 5.1
        // load incompatible core modules. Give the child the module path a fresh logon session would have.
        if (fileName.EndsWith("powershell.exe", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith("pwsh.exe", StringComparison.OrdinalIgnoreCase))
        {
            var fresh = FreshPsModulePath();
            if (fresh == null) psi.Environment.Remove("PSModulePath");
            else psi.Environment["PSModulePath"] = fresh;
        }
        return (psi, note);
    }

    internal static string? FreshPsModulePath()
    {
        var parts = new[] { EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine }
            .Select(t => Environment.GetEnvironmentVariable("PSModulePath", t))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();
        return parts.Count == 0 ? null : string.Join(";", parts);
    }

    /// <summary>-EncodedCommand (base64 of UTF-16LE) sidesteps every quoting problem.</summary>
    internal static string PowerShellArguments(string command)
    {
        var script = PowerShellPreamble + "\n" + command;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded;
    }

    /// <summary>
    /// cmd reads the console code page once at startup, so "chcp 65001 &amp; echo ..." still writes OEM text.
    /// The outer cmd therefore only switches the hidden console to UTF-8 and starts an inner cmd that runs the
    /// command. /s makes each cmd strip exactly the outer quotes; <see cref="EscapeForOuterCmd"/> keeps the outer
    /// parse from acting on the command, so the inner cmd receives it verbatim.
    /// </summary>
    internal static string CmdArguments(string command)
    {
        var inner = "\"" + CmdPath() + "\" /d /s /c \"" + EscapeForOuterCmd(command) + "\"";
        return "/d /s /c \"chcp 65001>nul & " + inner + "\"";
    }

    /// <summary>
    /// Caret-escapes cmd's special characters wherever the outer parser would be outside double quotes
    /// (the command starts inside the quote that precedes it), so the outer cmd passes the text through unchanged.
    /// </summary>
    internal static string EscapeForOuterCmd(string command)
    {
        var sb = new StringBuilder(command.Length + 8);
        bool insideQuotes = true;
        foreach (var ch in JoinLines(command))
        {
            if (ch == '"')
            {
                insideQuotes = !insideQuotes;
                sb.Append(ch);
            }
            else if (!insideQuotes && ch is '^' or '&' or '|' or '<' or '>' or '(' or ')')
            {
                sb.Append('^').Append(ch);
            }
            else
            {
                sb.Append(ch);
            }
        }
        return sb.ToString();
    }

    /// <summary>cmd /c stops at the first line break; run every non-blank line, one after another.</summary>
    internal static string JoinLines(string command)
    {
        if (command.IndexOfAny(new[] { '\r', '\n' }) < 0) return command;
        var lines = command.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
            .Where(l => !string.IsNullOrWhiteSpace(l));
        return string.Join(" & ", lines);
    }

    internal static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(requested.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(expanded) && Directory.Exists(expanded)) return expanded;
            }
            catch (ArgumentException) { }
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(home) ? home : Path.GetTempPath();
    }

    private static string WindowsPowerShellPath()
    {
        var p = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        return File.Exists(p) ? p : "powershell.exe";
    }

    private static string CmdPath()
    {
        var comspec = Environment.GetEnvironmentVariable("ComSpec");
        if (!string.IsNullOrWhiteSpace(comspec) && File.Exists(comspec)) return comspec;
        var p = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return File.Exists(p) ? p : "cmd.exe";
    }

    private static async Task Pump(StreamReader reader, CappedText target)
    {
        var buffer = new char[4096];
        try
        {
            int n;
            while ((n = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                target.Append(buffer, n);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    private static void KillTree(Process process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        catch (NotSupportedException) { }
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { }
    }

    /// <summary>Accumulates text up to a limit, counts what was dropped, and says so at the end.</summary>
    internal sealed class CappedText
    {
        private readonly StringBuilder _sb = new();
        private readonly int _limit;
        private long _dropped;

        public CappedText(int limit) => _limit = limit;

        public void Append(char[] buffer, int count)
        {
            lock (_sb)
            {
                int room = _limit - _sb.Length;
                int take = Math.Clamp(room, 0, count);
                if (take > 0) _sb.Append(buffer, 0, take);
                _dropped += count - take;
            }
        }

        public void Append(string text) => Append(text.ToCharArray(), text.Length);

        public void AppendLine(string text)
        {
            lock (_sb)
            {
                if (_sb.Length > 0 && _sb[^1] != '\n') _sb.Append('\n');
                _sb.Append(text).Append('\n');
            }
        }

        public override string ToString()
        {
            lock (_sb)
            {
                if (_dropped == 0) return _sb.ToString();
                var head = _sb.ToString();
                return head + (head.EndsWith('\n') ? "" : "\n") + $"[output truncated: {_dropped} more characters not shown]";
            }
        }
    }
}

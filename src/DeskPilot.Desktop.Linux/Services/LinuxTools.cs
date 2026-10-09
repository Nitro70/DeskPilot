using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>Process helpers shared by the launcher, the clipboard and the shell runner.</summary>
internal static class LinuxTools
{
    private const int SigKill = 9;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int SysKill(int pid, int sig);

    /// <summary>Full path of a helper program (xclip, wl-copy, setsid, gtk-launch...), or null when it is not installed.</summary>
    internal static string? FindTool(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        // ExecutableLocator searches PATH plus the usual user folders; the sbin folders matter for setsid on some distros.
        var found = ExecutableLocator.Find(name);
        if (found != null) return found;
        if (OperatingSystem.IsWindows()) return null;
        foreach (var dir in new[] { "/usr/bin", "/bin", "/usr/sbin", "/sbin", "/usr/local/bin" })
        {
            var candidate = Path.Combine(dir, name);
            if (IsExecutableFile(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>True for a regular file with an execute bit.</summary>
    internal static bool IsExecutableFile(string path)
    {
        if (OperatingSystem.IsWindows()) return false;
        try
        {
            if (!File.Exists(path)) return false;
            const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            return (File.GetUnixFileMode(path) & anyExec) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Sends SIGKILL to every process in a process group (best effort, Linux only).</summary>
    internal static void KillProcessGroup(int pgid)
    {
        if (!OperatingSystem.IsLinux() || pgid <= 1) return;
        try { SysKill(-pgid, SigKill); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
    }

    internal static string HomeDirectory()
    {
        var home = Environment.GetEnvironmentVariable("HOME");
        if (!string.IsNullOrWhiteSpace(home) && Directory.Exists(home)) return home;
        home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) && Directory.Exists(home) ? home : Path.GetTempPath();
    }

    /// <summary>The result of a short helper-program run.</summary>
    internal sealed record CaptureResult(int ExitCode, byte[] StdOut, string StdErr, bool TimedOut);

    /// <summary>
    /// Runs a short-lived helper (wl-paste, xclip -o...) with stdin closed, collects stdout (capped) and stderr, and
    /// kills it when it does not finish in time. Throws Win32Exception when the program cannot be started.
    /// </summary>
    internal static CaptureResult RunCapture(string fileName, IEnumerable<string> args, TimeSpan timeout, int maxStdOutBytes)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = HomeDirectory(),
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi) ?? throw new Win32Exception($"Could not start {fileName}.");
        try { p.StandardInput.Close(); } catch (IOException) { }

        var outTask = ReadCappedAsync(p.StandardOutput.BaseStream, maxStdOutBytes);
        var errTask = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit((int)timeout.TotalMilliseconds);
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
            try { p.WaitForExit(1000); } catch (InvalidOperationException) { }
        }

        byte[] stdout = Array.Empty<byte>();
        string stderr = "";
        try { if (outTask.Wait(TimeSpan.FromSeconds(1))) stdout = outTask.Result; } catch (AggregateException) { }
        try { if (errTask.Wait(TimeSpan.FromSeconds(1))) stderr = errTask.Result; } catch (AggregateException) { }
        int code = -1;
        if (exited) { try { code = p.ExitCode; } catch (InvalidOperationException) { } }
        return new CaptureResult(code, stdout, stderr.Trim(), !exited);
    }

    private static async Task<byte[]> ReadCappedAsync(Stream stream, int max)
    {
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        try
        {
            int n;
            while ((n = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                int take = Math.Min(n, max - (int)ms.Length);
                if (take > 0) ms.Write(buffer, 0, take);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        return ms.ToArray();
    }

    /// <summary>
    /// Starts a program detached from DeskPilot: in its own session (setsid, so closing DeskPilot or its terminal does not
    /// end it, and it has no controlling terminal) with stdin, stdout and stderr on /dev/null (so it never holds or
    /// writes into our pipes). The process stays the same pid through the exec chain sh -> setsid -> program.
    /// Returns the started process; the caller disposes it (the child keeps running).
    /// </summary>
    internal static Process StartDetached(IReadOnlyList<string> argv, string workingDirectory, IReadOnlyDictionary<string, string?>? environment = null)
    {
        if (argv.Count == 0) throw new ArgumentException("Nothing to start.", nameof(argv));
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            // Pipes instead of inheriting DeskPilot's stdio; the script replaces all three with /dev/null before exec.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Directory.Exists(workingDirectory) ? workingDirectory : HomeDirectory(),
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(DetachScript(FindTool("setsid") != null));
        psi.ArgumentList.Add("deskpilot-launch");
        foreach (var a in argv) psi.ArgumentList.Add(a);
        ApplyEnvironment(psi, environment);
        var p = Process.Start(psi) ?? throw new Win32Exception($"Could not start {argv[0]}.");
        try { p.StandardInput.Close(); } catch (IOException) { }
        return p;
    }

    /// <summary>The sh script StartDetached runs; exec keeps one pid from sh to the program.</summary>
    internal static string DetachScript(bool haveSetsid) => haveSetsid
        ? "exec setsid \"$@\" </dev/null >/dev/null 2>&1"
        : "exec \"$@\" </dev/null >/dev/null 2>&1";

    /// <summary>Copies environment overrides onto a start info; a null value removes the variable.</summary>
    internal static void ApplyEnvironment(ProcessStartInfo psi, IReadOnlyDictionary<string, string?>? environment)
    {
        if (environment == null) return;
        foreach (var (key, value) in environment)
        {
            if (value == null) psi.Environment.Remove(key);
            else psi.Environment[key] = value;
        }
    }

    /// <summary>Quotes a string for /bin/sh (single quotes, with embedded single quotes closed and escaped).</summary>
    internal static string ShellQuote(string s)
    {
        if (s.Length > 0 && s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '/' or ',' or ':' or '=' or '+' or '@'))
            return s;
        return "'" + s.Replace("'", "'\\''") + "'";
    }

    /// <summary>
    /// Splits an argument string the way a POSIX shell would for plain words: whitespace separates, single quotes are
    /// literal, double quotes group with backslash escapes for \ " $ and `, and a backslash outside quotes escapes the
    /// next character. No expansion of any kind.
    /// </summary>
    internal static List<string> SplitArguments(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var sb = new StringBuilder();
        bool any = false;
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c))
            {
                if (any) { result.Add(sb.ToString()); sb.Clear(); any = false; }
                i++;
                continue;
            }
            any = true;
            if (c == '\'')
            {
                int end = text.IndexOf('\'', i + 1);
                if (end < 0) { sb.Append(text, i + 1, text.Length - i - 1); i = text.Length; }
                else { sb.Append(text, i + 1, end - i - 1); i = end + 1; }
                continue;
            }
            if (c == '"')
            {
                i++;
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is '"' or '\\' or '$' or '`')
                    {
                        sb.Append(text[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(text[i]);
                    i++;
                }
                i++; // closing quote
                continue;
            }
            if (c == '\\' && i + 1 < text.Length)
            {
                sb.Append(text[i + 1]);
                i += 2;
                continue;
            }
            sb.Append(c);
            i++;
        }
        if (any) result.Add(sb.ToString());
        return result;
    }
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

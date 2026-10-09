using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>
/// Runs a bash or sh command in a child process and captures its output. The child runs with DeskPilot's own user
/// and never asks for elevation: it gets its own session (setsid) so it has no controlling terminal, which makes sudo
/// and other password prompts fail at once instead of waiting on a terminal, and SUDO_ASKPASS is removed so no
/// graphical password helper can appear.
/// </summary>
public sealed class LinuxShellRunner : IShellRunner
{
    internal const int MaxStreamChars = 64 * 1024;
    internal const int DefaultTimeoutMs = 60_000;
    // After the shell exits, a background grandchild may still hold the output pipes open; stop waiting after this.
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(2);

    private readonly Func<string, string?> _findTool;

    public LinuxShellRunner() : this(LinuxTools.FindTool) { }

    internal LinuxShellRunner(Func<string, string?> findTool) => _findTool = findTool;

    public async Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        command ??= "";
        if (timeoutMs <= 0) timeoutMs = DefaultTimeoutMs;

        var (psi, note, ownSession) = BuildStartInfo(command, shell, ResolveWorkingDirectory(workingDirectory), _findTool, Environment.GetEnvironmentVariable);
        var stdout = new CappedText(MaxStreamChars);
        var stderr = new CappedText(MaxStreamChars);
        if (note != null) stderr.AppendLine(note);

        var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                process.Dispose();
                return new ShellResult(-1, "", stderr + $"Could not start {psi.FileName}.", false);
            }
        }
        catch (Win32Exception ex)
        {
            process.Dispose();
            return new ShellResult(-1, "", stderr + $"Could not start {psi.FileName}: {ex.Message}", false);
        }

        // Commands must never wait for keyboard input that will not come.
        try { process.StandardInput.Close(); } catch (IOException) { }

        var readOut = Pump(process.StandardOutput, stdout);
        var readErr = Pump(process.StandardError, stderr);
        var pumps = Task.WhenAll(readOut, readErr);

        bool timedOut = false;
        try
        {
            using (var timeout = new CancellationTokenSource(timeoutMs))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, ct))
            {
                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    KillTree(process, ownSession);
                    if (ct.IsCancellationRequested) throw new OperationCanceledException("The command was cancelled.", ct);
                    timedOut = true;
                }
            }

            try { await pumps.WaitAsync(DrainTimeout, CancellationToken.None).ConfigureAwait(false); }
            catch (TimeoutException) { }

            int exitCode = -1;
            if (!timedOut)
            {
                try { exitCode = process.ExitCode; } catch (InvalidOperationException) { }
            }
            if (timedOut) stderr.AppendLine($"[timed out after {timeoutMs / 1000.0:0.#} s; the process and its children were stopped]");
            return new ShellResult(exitCode, stdout.ToString(), stderr.ToString(), timedOut);
        }
        finally
        {
            // A program the command started in the background (e.g. "gedit &") may still hold the output pipes. Closing our
            // ends now would kill it with SIGPIPE on its next write, so keep draining (output past the cap is dropped) and
            // release the pipes when it is done.
            if (pumps.IsCompleted) process.Dispose();
            else _ = pumps.ContinueWith(_ => process.Dispose(), TaskScheduler.Default);
        }
    }

    /// <summary>
    /// The process to start. ownSession is true when the command runs under setsid, so its pid is also its process
    /// group id and the whole group can be stopped at once.
    /// </summary>
    internal static (ProcessStartInfo Psi, string? Note, bool OwnSession) BuildStartInfo(
        string command, string shell, string workingDirectory, Func<string, string?> findTool, Func<string, string?> env)
    {
        var kind = (shell ?? "").Trim().ToLowerInvariant();
        string? note = null;
        var shellArgs = new List<string>();
        string shellPath;
        switch (kind)
        {
            case "" or "bash" or "/bin/bash" or "/usr/bin/bash":
                var bash = FirstExisting(new[] { "/bin/bash", "/usr/bin/bash" }) ?? findTool("bash");
                if (bash != null)
                {
                    shellPath = bash;
                    shellArgs.AddRange(new[] { "--noprofile", "--norc", "-c", command });
                }
                else
                {
                    shellPath = ShPath(findTool);
                    shellArgs.AddRange(new[] { "-c", command });
                    note = "[bash was not found; ran sh instead]";
                }
                break;
            case "sh" or "/bin/sh" or "dash":
                shellPath = ShPath(findTool);
                shellArgs.AddRange(new[] { "-c", command });
                break;
            default:
                throw new ArgumentException($"Unknown shell '{shell}'. Use \"bash\" or \"sh\".", nameof(shell));
        }

        var setsid = findTool("setsid");
        var psi = new ProcessStartInfo(setsid ?? shellPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory,
        };
        // setsid without --fork execs in place (our child is never a process group leader), so the pid stays the shell's.
        if (setsid != null) psi.ArgumentList.Add(shellPath);
        foreach (var a in shellArgs) psi.ArgumentList.Add(a);

        psi.Environment.Remove("SUDO_ASKPASS");
        // Without any locale the C locale makes tools print non-ASCII names as escapes; the output is read as UTF-8.
        if (string.IsNullOrEmpty(env("LC_ALL")) && string.IsNullOrEmpty(env("LC_CTYPE")) && string.IsNullOrEmpty(env("LANG")))
            psi.Environment["LANG"] = "C.UTF-8";
        return (psi, note, setsid != null);
    }

    private static string ShPath(Func<string, string?> findTool) =>
        FirstExisting(new[] { "/bin/sh", "/usr/bin/sh" }) ?? findTool("sh") ?? "/bin/sh";

    private static string? FirstExisting(IEnumerable<string> paths) => paths.FirstOrDefault(File.Exists);

    /// <summary>The requested folder when it exists (~ and $VARS expanded), else the home folder.</summary>
    internal static string ResolveWorkingDirectory(string? requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            try
            {
                var expanded = LinuxAppLauncher.ExpandPath(requested.Trim().Trim('"'), Environment.GetEnvironmentVariable, LinuxTools.HomeDirectory());
                if (Path.IsPathFullyQualified(expanded) && Directory.Exists(expanded)) return expanded;
            }
            catch (ArgumentException) { }
        }
        return LinuxTools.HomeDirectory();
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

    private static void KillTree(Process process, bool ownSession)
    {
        // The group first: it also reaches background jobs that were already re-parented away from the shell.
        if (ownSession) KillGroupQuietly(process);
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (Win32Exception) { }
        catch (NotSupportedException) { }
        try { process.WaitForExit(2000); } catch (InvalidOperationException) { } catch (Win32Exception) { }
    }

    private static void KillGroupQuietly(Process process)
    {
        try { LinuxTools.KillProcessGroup(process.Id); }
        catch (InvalidOperationException) { }
    }
}

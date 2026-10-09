using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>Result of a helper tool run. StdOut is raw bytes because grim writes images to it.</summary>
internal sealed record CommandResult(int ExitCode, byte[] StdOut, string StdErr, bool TimedOut)
{
    public bool Ok => ExitCode == 0 && !TimedOut;
    public string Text => Encoding.UTF8.GetString(StdOut);

    /// <summary>A one-line reason for error messages: stderr, else the exit code.</summary>
    public string Describe(string command)
    {
        if (TimedOut) return $"{command} timed out";
        var err = StdErr.Trim();
        if (err.Length > 300) err = err[..300] + "...";
        return err.Length > 0 ? $"{command} failed: {err}" : $"{command} exited with code {ExitCode}";
    }

    public static CommandResult NotFound(string command) => new(127, Array.Empty<byte>(), $"{command} is not installed", false);
}

/// <summary>A helper process that keeps running (e.g. wtype holding a key down) until it is stopped.</summary>
internal interface IRunningProcess : IDisposable
{
    bool HasExited { get; }
    void Stop();
}

/// <summary>Runs the compositor helper tools (grim, wtype, swaymsg, hyprctl...). Injected so tests can fake them.</summary>
internal interface ICommandRunner
{
    /// <summary>Full path of a command, or null when it is not installed.</summary>
    string? Find(string command);
    CommandResult Run(string command, IReadOnlyList<string> args, int timeoutMs, byte[]? stdin = null);
    IRunningProcess? Start(string command, IReadOnlyList<string> args);
}

internal sealed class ProcessCommandRunner : ICommandRunner
{
    public static readonly ProcessCommandRunner Instance = new();

    private readonly ConcurrentDictionary<string, string?> _found = new(StringComparer.Ordinal);

    public string? Find(string command) => _found.GetOrAdd(command, c => ExecutableLocator.Find(c));

    public CommandResult Run(string command, IReadOnlyList<string> args, int timeoutMs, byte[]? stdin = null)
    {
        var path = Find(command);
        if (path == null) return CommandResult.NotFound(command);
        var psi = CreateStartInfo(path, args);
        psi.RedirectStandardInput = stdin != null;
        Process p;
        try
        {
            p = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new CommandResult(126, Array.Empty<byte>(), $"could not start {command}: {ex.Message}", false);
        }

        using (p)
        {
            using var stdout = new MemoryStream();
            var outTask = p.StandardOutput.BaseStream.CopyToAsync(stdout);
            var errTask = p.StandardError.ReadToEndAsync();
            if (stdin != null)
            {
                try
                {
                    p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                    p.StandardInput.Close();
                }
                catch (IOException) { /* the tool exited early; its exit code says why */ }
            }

            bool exited = p.WaitForExit(Math.Max(100, timeoutMs));
            if (!exited)
            {
                try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                try { p.WaitForExit(2000); } catch (InvalidOperationException) { }
            }
            try { Task.WaitAll(new Task[] { outTask, errTask }, 2000); } catch (AggregateException) { }
            string err = errTask.IsCompletedSuccessfully ? errTask.Result : "";
            int code = exited ? p.ExitCode : -1;
            return new CommandResult(code, stdout.ToArray(), err, !exited);
        }
    }

    public IRunningProcess? Start(string command, IReadOnlyList<string> args)
    {
        var path = Find(command);
        if (path == null) return null;
        var psi = CreateStartInfo(path, args);
        psi.RedirectStandardOutput = false;
        psi.RedirectStandardError = false;
        try
        {
            var p = Process.Start(psi);
            return p == null ? null : new RunningProcess(p);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static ProcessStartInfo CreateStartInfo(string path, IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // wtype and dotool decode their text arguments with the C locale functions; without a UTF-8 locale
        // anything outside ASCII fails to type.
        if (!HasUtf8Locale(n => Environment.GetEnvironmentVariable(n))) psi.Environment["LC_ALL"] = "C.UTF-8";
        return psi;
    }

    internal static bool HasUtf8Locale(Func<string, string?> env)
    {
        // LC_ALL overrides LC_CTYPE, which overrides LANG.
        foreach (var name in new[] { "LC_ALL", "LC_CTYPE", "LANG" })
        {
            var v = env(name);
            if (string.IsNullOrEmpty(v)) continue;
            return v.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) || v.Contains("utf8", StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process _p;
        public RunningProcess(Process p) => _p = p;

        public bool HasExited
        {
            get
            {
                try { return _p.HasExited; }
                catch (InvalidOperationException) { return true; }
            }
        }

        public void Stop()
        {
            try
            {
                if (!_p.HasExited)
                {
                    _p.Kill(entireProcessTree: true);
                    _p.WaitForExit(2000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            Stop();
            _p.Dispose();
        }
    }
}

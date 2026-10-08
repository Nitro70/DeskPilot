using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskPilot.Core.Agent;

/// <summary>Runs a command-line tool briefly (e.g. "--version") with no window and returns what it printed.</summary>
internal static partial class ProcessProbe
{
    /// <summary>Returns stdout (or stderr when stdout is empty), or null if the tool could not run or timed out. Never throws.</summary>
    public static async Task<string?> RunAsync(string path, string arguments, TimeSpan timeout, CancellationToken ct)
    {
        Process? process = null;
        try
        {
            process = new Process { StartInfo = BuildStartInfo(path, arguments) };
            if (!process.Start()) return null;
            // Close stdin so a tool that reads it cannot wait forever.
            try { process.StandardInput.Close(); } catch (IOException) { }

            var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                return null;
            }

            // A child process that inherited the pipes can keep them open after the tool exits; do not wait on it.
            var output = await WithTimeout(stdout, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            var error = await WithTimeout(stderr, TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(output) ? error : output;
        }
        catch (Exception)
        {
            if (process != null) Kill(process);
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    /// <summary>.cmd/.bat shims (npm installs) have to run through cmd.exe; everything else starts directly.</summary>
    internal static ProcessStartInfo BuildStartInfo(string path, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // A neutral folder, so no tool picks up project files from wherever DeskPilot was started.
            WorkingDirectory = Path.GetTempPath(),
        };
        var ext = Path.GetExtension(path);
        if (ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || ext.Equals(".bat", StringComparison.OrdinalIgnoreCase))
        {
            var comSpec = Environment.GetEnvironmentVariable("ComSpec");
            psi.FileName = string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;
            // /s strips the outer quotes, leaving "path" args intact even when the path has spaces.
            psi.Arguments = $"/d /s /c \"\"{path}\" {arguments}\"";
        }
        else
        {
            psi.FileName = path;
            psi.Arguments = arguments;
        }
        psi.Environment["NO_COLOR"] = "1";
        return psi;
    }

    /// <summary>Extracts a version from "--version" output: (version or null, first meaningful line).</summary>
    internal static (string? Version, string? FirstLine) ParseVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return (null, null);
        var clean = AnsiEscape().Replace(output, "");
        var line = clean.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (line == null) return (null, null);
        if (line.Length > 120) line = line[..120];
        var match = VersionNumber().Match(line);
        return (match.Success ? match.Value : null, line);
    }

    private static async Task<string> WithTimeout(Task<string> task, TimeSpan timeout)
    {
        if (await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false) != task) return "";
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            return "";
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone or not ours to kill.
        }
    }

    [GeneratedRegex(@"\x1B\[[0-9;?]*[ -/]*[@-~]")]
    private static partial Regex AnsiEscape();

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)*(?:-[0-9A-Za-z][0-9A-Za-z.\-]*)?")]
    private static partial Regex VersionNumber();
}

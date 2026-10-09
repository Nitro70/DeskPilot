using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>
/// Text clipboard through the standard command-line tools: wl-paste / wl-copy under Wayland, xclip (or xsel) under
/// X11. A Wayland session without wl-clipboard falls back to xclip/xsel through Xwayland when DISPLAY is set.
/// </summary>
public sealed class LinuxClipboard : IClipboardService
{
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    internal const int MaxReadBytes = 8 * 1024 * 1024;

    private readonly LinuxSessionInfo _session;
    private readonly Func<string, string?> _findTool;

    public LinuxClipboard(LinuxSessionInfo session) : this(session, LinuxTools.FindTool) { }

    internal LinuxClipboard(LinuxSessionInfo session, Func<string, string?> findTool)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _findTool = findTool;
    }

    /// <summary>A planned tool invocation: the program and its arguments.</summary>
    internal sealed record ClipboardCommand(string FileName, IReadOnlyList<string> Arguments)
    {
        public string Tool => Path.GetFileName(FileName);
    }

    internal enum ClipboardOp { Read, Write, Clear }

    public string? GetText()
    {
        var cmd = Plan(_session, _findTool, ClipboardOp.Read);
        LinuxTools.CaptureResult r;
        try
        {
            r = LinuxTools.RunCapture(cmd.FileName, cmd.Arguments, Timeout, MaxReadBytes);
        }
        catch (Win32Exception ex)
        {
            throw new InvalidOperationException($"Could not run {cmd.Tool} to read the clipboard: {ex.Message}", ex);
        }
        if (r.TimedOut)
            throw new InvalidOperationException($"Reading the clipboard timed out ({cmd.Tool} did not answer within {Timeout.TotalSeconds:0} s).");
        if (r.ExitCode != 0)
        {
            if (IsNoTextMessage(r.StdErr)) return null;
            throw new InvalidOperationException($"{cmd.Tool} could not read the clipboard (exit code {r.ExitCode}){Detail(r.StdErr)}");
        }
        return new UTF8Encoding(false).GetString(r.StdOut);
    }

    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var cmd = Plan(_session, _findTool, text.Length == 0 ? ClipboardOp.Clear : ClipboardOp.Write);
        RunServing(cmd, text);
    }

    /// <summary>
    /// Picks the tool and arguments for a clipboard operation. Throws InvalidOperationException with an install hint
    /// when the session has no usable tool.
    /// </summary>
    internal static ClipboardCommand Plan(LinuxSessionInfo session, Func<string, string?> findTool, ClipboardOp op)
    {
        if (session.Kind == LinuxSessionKind.None)
            throw new InvalidOperationException("There is no graphical session (neither WAYLAND_DISPLAY nor DISPLAY is set), so there is no clipboard.");

        if (session.Kind == LinuxSessionKind.Wayland)
        {
            var wl = findTool(op == ClipboardOp.Read ? "wl-paste" : "wl-copy");
            if (wl != null)
            {
                return op switch
                {
                    // "text" asks wl-paste for any textual type, never an image; --no-newline keeps the text as it is.
                    ClipboardOp.Read => new ClipboardCommand(wl, new[] { "--no-newline", "--type", "text" }),
                    ClipboardOp.Clear => new ClipboardCommand(wl, new[] { "--clear" }),
                    _ => new ClipboardCommand(wl, new[] { "--type", "text/plain;charset=utf-8" }),
                };
            }
            // Xwayland keeps the X11 and Wayland clipboards in sync, so the X11 tools still work when they are present.
            if (!string.IsNullOrEmpty(session.Display) && FindX11(findTool, op) is { } x) return x;
            throw new InvalidOperationException(
                "Clipboard access under Wayland needs wl-clipboard (the wl-copy and wl-paste commands). Install the wl-clipboard package with your package manager, e.g. sudo apt install wl-clipboard.");
        }

        return FindX11(findTool, op) ?? throw new InvalidOperationException(
            "Clipboard access under X11 needs xclip or xsel. Install one with your package manager, e.g. sudo apt install xclip.");
    }

    private static ClipboardCommand? FindX11(Func<string, string?> findTool, ClipboardOp op)
    {
        if (findTool("xclip") is { } xclip)
        {
            return op == ClipboardOp.Read
                ? new ClipboardCommand(xclip, new[] { "-selection", "clipboard", "-o" })
                : new ClipboardCommand(xclip, new[] { "-selection", "clipboard", "-i" });
        }
        if (findTool("xsel") is { } xsel)
        {
            return op switch
            {
                ClipboardOp.Read => new ClipboardCommand(xsel, new[] { "--clipboard", "--output" }),
                ClipboardOp.Clear => new ClipboardCommand(xsel, new[] { "--clipboard", "--clear" }),
                _ => new ClipboardCommand(xsel, new[] { "--clipboard", "--input" }),
            };
        }
        return null;
    }

    /// <summary>The messages the tools print when the clipboard is empty or holds no text (not an error for us).</summary>
    internal static bool IsNoTextMessage(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("Nothing is copied", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("No suitable type", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("No selection", StringComparison.OrdinalIgnoreCase)
               || stderr.Contains("not available", StringComparison.OrdinalIgnoreCase);
    }

    private static string Detail(string? stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return ".";
        var line = stderr.Trim();
        if (line.Length > 300) line = line[..300];
        return ": " + line;
    }

    /// <summary>
    /// wl-copy, xclip and xsel read the text and then fork a background process that serves the selection until
    /// something else is copied. That server inherits the tool's stdio, so it must not get our pipes: stdout goes to
    /// /dev/null and stderr to a temp file (read after the foreground process exits, then deleted). Only the foreground
    /// process is waited for; the server is left running on purpose.
    /// </summary>
    private static void RunServing(ClipboardCommand cmd, string text)
    {
        var errFile = Path.Combine(Path.GetTempPath(), $"deskpilot-clip-{Environment.ProcessId}-{Guid.NewGuid():N}.err");
        var psi = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = LinuxTools.HomeDirectory(),
        };
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("f=$1; shift; exec \"$@\" >/dev/null 2>\"$f\"");
        psi.ArgumentList.Add("deskpilot-clipboard");
        psi.ArgumentList.Add(errFile);
        psi.ArgumentList.Add(cmd.FileName);
        foreach (var a in cmd.Arguments) psi.ArgumentList.Add(a);

        try
        {
            Process p;
            try
            {
                p = Process.Start(psi) ?? throw new Win32Exception("Process.Start returned nothing.");
            }
            catch (Win32Exception ex)
            {
                throw new InvalidOperationException($"Could not run {cmd.Tool} to set the clipboard: {ex.Message}", ex);
            }

            using (p)
            {
                // Write on a worker: a tool that stops reading must not block the caller past the timeout.
                var writer = Task.Run(() =>
                {
                    try
                    {
                        p.StandardInput.Write(text);
                        p.StandardInput.Close();
                    }
                    catch (IOException) { }
                    catch (ObjectDisposedException) { }
                });
                bool written = writer.Wait(Timeout);
                bool exited = written && p.WaitForExit((int)Timeout.TotalMilliseconds);
                if (!exited)
                {
                    try { p.Kill(entireProcessTree: true); } catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { }
                    throw new InvalidOperationException($"Setting the clipboard timed out ({cmd.Tool} did not finish within {Timeout.TotalSeconds:0} s).");
                }
                if (p.ExitCode != 0)
                {
                    string err = "";
                    try { if (File.Exists(errFile)) err = File.ReadAllText(errFile); } catch (IOException) { }
                    throw new InvalidOperationException($"{cmd.Tool} could not set the clipboard (exit code {p.ExitCode}){Detail(err)}");
                }
            }
        }
        finally
        {
            try { File.Delete(errFile); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}

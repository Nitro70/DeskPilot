using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace DeskPilot.Core.Backends.Acp;

/// <summary>How to start one ACP agent process. Built by AcpLaunchBuilder.</summary>
internal sealed record AcpLaunchSpec
{
    /// <summary>The program actually started (cmd.exe for .cmd/.bat shims).</summary>
    public required string FileName { get; init; }
    /// <summary>Arguments passed one by one (normal executables). Ignored when RawArguments is set.</summary>
    public IReadOnlyList<string> ArgumentList { get; init; } = Array.Empty<string>();
    /// <summary>A complete, already escaped argument string (cmd.exe running a shim).</summary>
    public string? RawArguments { get; init; }
    public required string WorkingDirectory { get; init; }
    /// <summary>Environment changes for the child: a null value removes the variable.</summary>
    public IReadOnlyDictionary<string, string?> Environment { get; init; } = new Dictionary<string, string?>();
    /// <summary>The agent executable that was resolved (the .exe or the .cmd shim).</summary>
    public required string ExecutablePath { get; init; }
    /// <summary>The arguments the agent receives.</summary>
    public required IReadOnlyList<string> AgentArguments { get; init; }
    /// <summary>Human-readable agent name for messages, e.g. "Gemini CLI".</summary>
    public required string DisplayName { get; init; }
    public bool IsGemini { get; init; }
    /// <summary>The agent was told to load no MCP server except DeskPilot's (Gemini: --allowed-mcp-server-names).</summary>
    public bool McpRestrictedToDeskPilot { get; init; }
    public string ServerName { get; init; } = AcpLaunchBuilder.DefaultServerName;
    public string? SystemPromptFile { get; init; }
    public string? PolicyFile { get; init; }
    /// <summary>Temporary files written for this launch; deleted when the backend is disposed.</summary>
    public IReadOnlyList<string> TempFiles { get; init; } = Array.Empty<string>();

    /// <summary>For logs: the command line without environment values.</summary>
    public string DisplayCommandLine =>
        ExecutablePath + (AgentArguments.Count == 0 ? "" : " " + string.Join(" ", AgentArguments.Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? "\"" + a + "\"" : a)));
}

/// <summary>The agent's stdio plus process lifetime. Tests replace it with an in-memory fake.</summary>
internal interface IAcpTransport : IAsyncDisposable
{
    /// <summary>What the agent writes (its stdout).</summary>
    Stream FromAgent { get; }
    /// <summary>Where we write (its stdin).</summary>
    Stream ToAgent { get; }
    /// <summary>Completes when the process has exited.</summary>
    Task Exited { get; }
    int? ExitCode { get; }
    /// <summary>The last lines the agent wrote to stderr, for error messages.</summary>
    string StderrTail { get; }
    /// <summary>Kills the agent and its child processes. Safe to call more than once.</summary>
    void Kill();
}

/// <summary>Runs the agent as a hidden child process with redirected stdio.</summary>
internal sealed class AcpProcessTransport : IAcpTransport
{
    private const int MaxStderrLines = 40;
    private const int MaxStderrLineLength = 600;
    private static readonly Regex AnsiEscape = new(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B\][^\x07]*\x07", RegexOptions.Compiled);

    private readonly Process _process;
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Queue<string> _stderr = new();
    private readonly object _stderrGate = new();
    private int _disposed;

    private AcpProcessTransport(Process process) => _process = process;

    public static IAcpTransport Start(AcpLaunchSpec spec)
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var psi = new ProcessStartInfo
        {
            FileName = spec.FileName,
            WorkingDirectory = spec.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = utf8,
            StandardOutputEncoding = utf8,
            StandardErrorEncoding = utf8,
        };
        if (spec.RawArguments != null) psi.Arguments = spec.RawArguments;
        else foreach (var a in spec.ArgumentList) psi.ArgumentList.Add(a);

        foreach (var (name, value) in spec.Environment)
        {
            if (value == null) psi.Environment.Remove(name);
            else psi.Environment[name] = value;
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var transport = new AcpProcessTransport(process);
        process.Exited += (_, _) => transport._exited.TrySetResult();
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) transport.AppendStderr(e.Data); };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("The process did not start.");
        }
        catch
        {
            process.Dispose();
            throw;
        }
        process.BeginErrorReadLine();
        try
        {
            if (process.HasExited) transport._exited.TrySetResult();
        }
        catch (InvalidOperationException) { }
        return transport;
    }

    public Stream FromAgent => _process.StandardOutput.BaseStream;
    public Stream ToAgent => _process.StandardInput.BaseStream;
    public Task Exited => _exited.Task;

    public int? ExitCode
    {
        get
        {
            try { return _process.HasExited ? _process.ExitCode : null; }
            catch (Exception) { return null; }
        }
    }

    public string StderrTail
    {
        get { lock (_stderrGate) return string.Join("\n", _stderr); }
    }

    private void AppendStderr(string line)
    {
        line = AnsiEscape.Replace(line, "").TrimEnd();
        if (line.Length == 0) return;
        if (line.Length > MaxStderrLineLength) line = line[..MaxStderrLineLength] + "...";
        lock (_stderrGate)
        {
            _stderr.Enqueue(line);
            while (_stderr.Count > MaxStderrLines) _stderr.Dequeue();
        }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone or access denied; nothing else to do.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Kill();
        try { await _exited.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (TimeoutException) { }
        try { _process.Dispose(); }
        catch (Exception) { }
    }
}

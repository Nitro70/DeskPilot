using System.ComponentModel;
using System.Diagnostics;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Backends.Cli;

/// <summary>What is needed to start one CLI process.</summary>
internal sealed record ClaudeLaunchSpec(string ExePath, IReadOnlyList<string> Args, string WorkingDirectory, ProviderProfile Profile);

/// <summary>
/// One running CLI process. Stdout lines are delivered in order to the callback given at start;
/// <see cref="Exited"/> completes after the last line was delivered and the process ended.
/// Abstracted so the turn logic can be tested without starting real programs.
/// </summary>
internal interface IClaudeProcess : IAsyncDisposable
{
    bool HasExited { get; }
    int? ExitCode { get; }
    Task Exited { get; }
    /// <summary>The last stderr lines, oldest first.</summary>
    IReadOnlyList<string> StderrTail { get; }
    /// <summary>Writes one line to stdin (serialized; safe from any thread).</summary>
    Task WriteLineAsync(string line, CancellationToken ct);
    /// <summary>Closes stdin so the CLI exits once idle.</summary>
    void CloseInput();
    /// <summary>Kills the process and everything it started (MCP bridge, node).</summary>
    void Kill();
}

/// <summary>Keeps the last N lines (stderr) for error messages.</summary>
internal sealed class TailBuffer
{
    private readonly Queue<string> _lines = new();
    private readonly int _capacity;

    public TailBuffer(int capacity = 50) => _capacity = Math.Max(1, capacity);

    public void Add(string line)
    {
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > _capacity) _lines.Dequeue();
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lines) return _lines.ToArray();
    }
}

/// <summary>The real System.Diagnostics.Process behind <see cref="IClaudeProcess"/>.</summary>
internal sealed class ClaudeCliProcess : IClaudeProcess
{
    private readonly Process _process;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly TailBuffer _stderr = new(50);
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _onLine;
    private readonly Task _stderrPump;
    private int? _exitCode;
    private int _disposed;

    private ClaudeCliProcess(Process process, Action<string> onLine)
    {
        _process = process;
        _onLine = onLine;
        _stdin = process.StandardInput;
        _stdin.NewLine = "\n";
        _stdin.AutoFlush = false;
        _stderrPump = Task.Run(PumpStderrAsync);
        _ = Task.Run(PumpStdoutAsync);
    }

    /// <summary>Starts the CLI. Throws InvalidOperationException with a readable message when it cannot start.</summary>
    public static ClaudeCliProcess Start(ClaudeLaunchSpec spec, Action<string> onLine)
    {
        Directory.CreateDirectory(spec.WorkingDirectory);
        var psi = ClaudeCliCommand.BuildStartInfo(spec.ExePath, spec.Args, spec.WorkingDirectory, spec.Profile);
        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            throw new InvalidOperationException($"Could not start Claude Code ({spec.ExePath}): {ex.Message}", ex);
        }
        Log.Info($"Claude CLI started (pid {SafePid(process)}): {spec.ExePath}");
        return new ClaudeCliProcess(process, onLine);
    }

    public bool HasExited
    {
        get
        {
            if (_exited.Task.IsCompleted) return true;
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }
    }

    public int? ExitCode => _exitCode;
    public Task Exited => _exited.Task;
    public IReadOnlyList<string> StderrTail => _stderr.Snapshot();

    public async Task WriteLineAsync(string line, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stdin.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
            await _stdin.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void CloseInput()
    {
        try { _stdin.Close(); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public void Kill()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            Log.Warn($"Could not kill the Claude CLI process: {ex.Message}");
        }
    }

    private async Task PumpStdoutAsync()
    {
        try
        {
            var reader = _process.StandardOutput;
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                try { _onLine(line); }
                catch (Exception ex) { Log.Error("Claude CLI output handler failed", ex); }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // Pipe closed under us (killed): treat as end of output.
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException) { }

        // Let stderr drain so the tail includes the reason the process stopped.
        await Task.WhenAny(_stderrPump, Task.Delay(1000)).ConfigureAwait(false);

        try { if (_process.HasExited) _exitCode = _process.ExitCode; }
        catch (InvalidOperationException) { }
        Log.Info($"Claude CLI exited (code {(_exitCode?.ToString() ?? "unknown")})");
        _exited.TrySetResult();
    }

    private async Task PumpStderrAsync()
    {
        try
        {
            var reader = _process.StandardError;
            while (true)
            {
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (line.Length == 0) continue;
                _stderr.Add(line);
                Log.Info("claude stderr: " + ClaudeStreamParser.Clip(line, 500));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        CloseInput();
        Kill();
        await Task.WhenAny(_exited.Task, Task.Delay(2000)).ConfigureAwait(false);
        // _writeLock is left alone: a racing writer must get an IOException, not a disposed semaphore.
        _process.Dispose();
    }

    private static string SafePid(Process p)
    {
        try { return p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        catch (InvalidOperationException) { return "?"; }
    }
}

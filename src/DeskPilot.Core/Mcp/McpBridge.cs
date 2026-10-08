using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace DeskPilot.Core.Mcp;

/// <summary>The "--mcp-bridge PIPE TOKEN" process mode: relays stdin/stdout to the app's named pipe.</summary>
/// <remarks>
/// Runs inside the GUI-subsystem exe, started by a CLI agent with redirected handles. It must stay tiny:
/// no settings, no WPF, no DPI changes, and nothing but relayed MCP bytes on stdout.
/// </remarks>
public static class McpBridge
{
    public const string Switch = "--mcp-bridge";

    public const int ExitOk = 0;
    public const int ExitBadArguments = 2;
    public const int ExitCannotConnect = 3;

    public const string NotRunningMessage = "DeskPilot is not running or this session has ended.";

    internal static readonly TimeSpan DefaultConnectTimeout = TimeSpan.FromSeconds(10);
    private const int BufferSize = 64 * 1024;

    /// <summary>args = the full command line args (Switch, pipe name, token). Returns the process exit code.</summary>
    public static int Run(string[] args)
    {
        var stderr = Console.OpenStandardError();
        if (!TryParseArgs(args, out var pipe, out var token))
        {
            WriteLine(stderr, $"Usage: DeskPilot.exe {Switch} <pipe-name> <token>");
            return ExitBadArguments;
        }

        // Raw handle streams: no console encoding, no buffering, no BOM. Not disposed on purpose: a read
        // blocked on stdin cannot be cancelled, and the process exits right after this returns anyway.
        var stdin = Console.OpenStandardInput();
        var stdout = Console.OpenStandardOutput();
        return RunAsync(stdin, stdout, stderr, pipe, token, CancellationToken.None).GetAwaiter().GetResult();
    }

    internal static bool TryParseArgs(string[]? args, out string pipe, out string token)
    {
        pipe = token = "";
        if (args is not { Length: 3 } || !string.Equals(args[0], Switch, StringComparison.OrdinalIgnoreCase)) return false;
        pipe = args[1]?.Trim() ?? "";
        token = args[2]?.Trim() ?? "";
        return IsValidPipeName(pipe) && IsValidToken(token);
    }

    private static bool IsValidPipeName(string pipe) =>
        pipe.Length is > 0 and <= 200 &&
        !string.Equals(pipe, "anonymous", StringComparison.OrdinalIgnoreCase) &&
        pipe.All(c => c > ' ' && c != '\\' && c != '/' && c != ':' && c != '"');

    // The token goes into a JSON string verbatim, so allow only characters that need no escaping.
    private static bool IsValidToken(string token) =>
        token.Length is > 0 and <= 512 && token.All(c => c > ' ' && c < 127 && c != '"' && c != '\\');

    /// <summary>Connects to the pipe, authenticates and relays until either side closes.</summary>
    internal static Task<int> RunAsync(Stream stdin, Stream stdout, Stream stderr, string pipe, string token, CancellationToken ct) =>
        RunAsync(stdin, stdout, stderr, pipe, token, DefaultConnectTimeout, ct);

    internal static async Task<int> RunAsync(Stream stdin, Stream stdout, Stream stderr, string pipe, string token, TimeSpan connectTimeout, CancellationToken ct)
    {
        if (pipe is null || token is null || !IsValidPipeName(pipe) || !IsValidToken(token))
        {
            WriteLine(stderr, $"Usage: DeskPilot.exe {Switch} <pipe-name> <token>");
            return ExitBadArguments;
        }

        NamedPipeClientStream? client;
        try
        {
            client = await ConnectAsync(pipe, connectTimeout, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            client = null;
        }
        if (client == null)
        {
            WriteLine(stderr, NotRunningMessage);
            return ExitCannotConnect;
        }

        using (client)
        {
            try
            {
                var line = Encoding.UTF8.GetBytes(McpPipeServer.BuildTokenLine(token) + "\n");
                await client.WriteAsync(line, ct).ConfigureAwait(false);
                await client.FlushAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                WriteLine(stderr, NotRunningMessage);
                return ExitCannotConnect;
            }

            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var up = PumpAsync(stdin, client, stop.Token);
            var down = PumpAsync(client, stdout, stop.Token);
            var first = await Task.WhenAny(up, down).ConfigureAwait(false);
            stop.Cancel();

            if (first == down && !ct.IsCancellationRequested)
                WriteLine(stderr, "DeskPilot closed the connection.");

            // stdin reads on a real handle ignore cancellation, so only wait for the pipe side.
            if (first == up)
            {
                try { await down.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
                catch (TimeoutException) { }
            }
        }
        return ExitOk;
    }

    private static async Task<NamedPipeClientStream?> ConnectAsync(string pipe, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero) return null;

            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                // ConnectAsync itself waits while the pipe does not exist yet or all instances are busy.
                var slice = (int)Math.Clamp(remaining.TotalMilliseconds, 1, 1000);
                await client.ConnectAsync(slice, ct).ConfigureAwait(false);
                return client;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException)
            {
                client.Dispose();
            }
            catch (UnauthorizedAccessException)
            {
                // The pipe belongs to another user (or elevation level); retrying cannot help.
                client.Dispose();
                return null;
            }
            catch
            {
                client.Dispose();
                throw;
            }

            if (timeout - sw.Elapsed <= TimeSpan.Zero) return null;
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Copies bytes until end of stream, cancellation or an I/O error; flushes after every chunk.</summary>
    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[BufferSize];
        try
        {
            while (true)
            {
                var n = await from.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (n == 0) return;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
        {
        }
    }

    private static void WriteLine(Stream stderr, string message)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message + Environment.NewLine);
            stderr.Write(bytes, 0, bytes.Length);
            stderr.Flush();
        }
        catch (Exception)
        {
            // Nowhere left to report to.
        }
    }
}

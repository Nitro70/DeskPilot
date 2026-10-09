using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Linux.Services;

/// <summary>
/// One DeskPilot per user: the first instance listens on a Unix domain socket in $XDG_RUNTIME_DIR (or the temp
/// folder) named deskpilot-&lt;uid&gt;.sock; a second launch connects, sends "activate" and exits, and the first one
/// brings its window forward. A socket file left behind by a crash is detected (nobody answers) and replaced.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    public const string ActivateMessage = "activate";

    private readonly string _socketPath;
    private Socket? _listener;
    private CancellationTokenSource? _cts;
    private bool _owned;
    private bool _disposed;

    public SingleInstanceGuard(string socketPath) => _socketPath = socketPath;

    public string SocketPath => _socketPath;

    /// <summary>Raised on a background thread when another launch asks this instance to show itself.</summary>
    public event Action? ActivationRequested;

    public bool IsOwner => _owned;

    /// <summary>$XDG_RUNTIME_DIR/deskpilot-&lt;uid&gt;.sock, or the same name in the temp folder.</summary>
    public static string DefaultSocketPath()
    {
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) dir = Path.GetTempPath();
        return Path.Combine(dir, $"deskpilot-{CurrentUserId()}.sock");
    }

    /// <summary>The numeric user id on Linux; elsewhere a stable value derived from the user name.</summary>
    public static string CurrentUserId()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() || OperatingSystem.IsFreeBSD())
        {
            try { return geteuid().ToString(System.Globalization.CultureInfo.InvariantCulture); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
        var name = Environment.UserName;
        uint hash = 2166136261;
        foreach (var c in name) hash = (hash ^ c) * 16777619;
        return hash.ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
    }

    [DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>
    /// True when this is the first instance (it then starts listening for activation requests).
    /// False when another instance answers on the socket.
    /// </summary>
    public bool TryAcquire()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_owned) return true;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (IsAnotherInstanceListening()) return false;

            // Nobody answered: any file there is stale (a crashed instance). Replace it.
            TryDeleteSocketFile();
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_socketPath)!);
                listener.Bind(new UnixDomainSocketEndPoint(_socketPath));
                listener.Listen(4);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                // Another launch bound it between our check and our bind: ask again.
                listener.Dispose();
                continue;
            }
            catch (Exception ex) when (ex is SocketException or IOException or UnauthorizedAccessException)
            {
                listener.Dispose();
                Log.Warn($"Single-instance socket unavailable ({ex.Message}); continuing without it");
                _owned = true;
                return true;
            }

            RestrictToOwner();
            _listener = listener;
            _owned = true;
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
            return true;
        }

        // Could not settle who owns it; better a second window than no window at all.
        _owned = true;
        return true;
    }

    /// <summary>Asks the running instance to show its window. Returns false when nobody answered.</summary>
    public bool SignalFirstInstance()
    {
        try
        {
            using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            client.Connect(new UnixDomainSocketEndPoint(_socketPath));
            client.Send(Encoding.UTF8.GetBytes(ActivateMessage + "\n"));
            client.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException)
        {
            return false;
        }
    }

    private bool IsAnotherInstanceListening()
    {
        if (!File.Exists(_socketPath)) return false;
        try
        {
            using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            probe.Connect(new UnixDomainSocketEndPoint(_socketPath));
            // A bare connection without a message is ignored by the listener.
            probe.Shutdown(SocketShutdown.Both);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or IOException)
        {
            return false;
        }
    }

    private async Task AcceptLoopAsync(Socket listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await listener.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                if (ct.IsCancellationRequested) return;
                if (ex is ObjectDisposedException) return;
                continue;
            }

            _ = Task.Run(async () =>
            {
                using (client)
                {
                    try
                    {
                        if (await ReadMessageAsync(client, ct).ConfigureAwait(false) == ActivateMessage)
                            ActivationRequested?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Activation request failed: {ex.Message}");
                    }
                }
            }, CancellationToken.None);
        }
    }

    private static async Task<string> ReadMessageAsync(Socket client, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        var buffer = new byte[64];
        int total = 0;
        try
        {
            while (total < buffer.Length)
            {
                int n = await client.ReceiveAsync(buffer.AsMemory(total), SocketFlags.None, timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                total += n;
                if (Array.IndexOf(buffer, (byte)'\n', 0, total) >= 0) break;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException)
        {
            // A probe that never sends anything, or a client that went away.
        }
        return Encoding.UTF8.GetString(buffer, 0, total).Trim();
    }

    private void RestrictToOwner()
    {
        if (OperatingSystem.IsWindows()) return;
        try { File.SetUnixFileMode(_socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { }
    }

    private void TryDeleteSocketFile()
    {
        try
        {
            if (File.Exists(_socketPath)) File.Delete(_socketPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Stale single-instance socket could not be removed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
        if (_listener != null)
        {
            try { _listener.Dispose(); }
            catch (Exception) { }
            _listener = null;
            TryDeleteSocketFile();
        }
        _cts?.Dispose();
    }
}

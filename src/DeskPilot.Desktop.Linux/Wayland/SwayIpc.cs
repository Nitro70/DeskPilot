using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>
/// sway's IPC (the i3 protocol: "i3-ipc", payload length, message type, JSON) over $SWAYSOCK, with swaymsg as the
/// fallback when the socket is not reachable. Talking to the socket directly saves a process start per query.
/// </summary>
internal sealed class SwayIpc
{
    public const int RunCommand = 0;
    public const int GetWorkspaces = 1;
    public const int GetOutputs = 3;
    public const int GetTree = 4;
    public const int GetSeats = 101;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("i3-ipc");

    private readonly string? _socketPath;
    private readonly ICommandRunner _runner;

    public SwayIpc(string? socketPath, ICommandRunner runner)
    {
        _socketPath = string.IsNullOrWhiteSpace(socketPath) ? null : socketPath;
        _runner = runner;
    }

    public bool Available => (_socketPath != null && File.Exists(_socketPath)) || _runner.Find("swaymsg") != null;

    /// <summary>Sends one message and returns the JSON reply, or null with error set.</summary>
    public string? Request(int type, string payload, out string error, int timeoutMs = 3000)
    {
        error = "";
        if (_socketPath != null)
        {
            try { return RequestSocket(_socketPath, type, payload, timeoutMs); }
            catch (Exception ex) when (ex is SocketException or IOException or TimeoutException or InvalidDataException)
            {
                error = ex.Message;
            }
        }

        var args = new List<string> { "-r", "-t", TypeName(type) };
        if (payload.Length > 0) args.Add(payload);
        var r = _runner.Run("swaymsg", args, timeoutMs);
        // swaymsg exits with 2 when a command fails but still prints the JSON reply.
        if (r.StdOut.Length > 0 && !r.TimedOut) return r.Text;
        error = error.Length > 0 ? error + "; " + r.Describe("swaymsg") : r.Describe("swaymsg");
        return null;
    }

    /// <summary>Runs sway commands; true when every command succeeded.</summary>
    public bool Command(string command, out string error)
    {
        var reply = Request(RunCommand, command, out error);
        if (reply == null) return false;
        return CommandSucceeded(reply, out error);
    }

    internal static bool CommandSucceeded(string reply, out string error)
    {
        error = "";
        try
        {
            using var doc = JsonDocument.Parse(reply);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { error = "unexpected reply from sway"; return false; }
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True) continue;
                error = WaylandOutputParsers.Str(r, "error") ?? "sway refused the command";
                return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            error = "unreadable reply from sway";
            return false;
        }
    }

    internal static string TypeName(int type) => type switch
    {
        RunCommand => "command",
        GetWorkspaces => "get_workspaces",
        GetOutputs => "get_outputs",
        GetTree => "get_tree",
        GetSeats => "get_seats",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    internal static byte[] Encode(int type, string payload)
    {
        var body = Encoding.UTF8.GetBytes(payload);
        var msg = new byte[14 + body.Length];
        Magic.CopyTo(msg, 0);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(6), body.Length);
        BinaryPrimitives.WriteInt32LittleEndian(msg.AsSpan(10), type);
        body.CopyTo(msg, 14);
        return msg;
    }

    private static string RequestSocket(string path, int type, string payload, int timeoutMs)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)
        {
            ReceiveTimeout = timeoutMs,
            SendTimeout = timeoutMs,
        };
        socket.Connect(new UnixDomainSocketEndPoint(path));
        var msg = Encode(type, payload);
        int sent = 0;
        while (sent < msg.Length) sent += socket.Send(msg, sent, msg.Length - sent, SocketFlags.None);

        var header = ReadExactly(socket, 14);
        if (!header.AsSpan(0, 6).SequenceEqual(Magic)) throw new InvalidDataException("Not an i3/sway IPC reply");
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(6));
        if (length < 0 || length > 64 * 1024 * 1024) throw new InvalidDataException("Bad sway IPC reply length");
        return Encoding.UTF8.GetString(ReadExactly(socket, length));
    }

    private static byte[] ReadExactly(Socket socket, int count)
    {
        var buf = new byte[count];
        int got = 0;
        while (got < count)
        {
            int n = socket.Receive(buf, got, count - got, SocketFlags.None);
            if (n == 0) throw new IOException("sway closed the IPC connection");
            got += n;
        }
        return buf;
    }
}

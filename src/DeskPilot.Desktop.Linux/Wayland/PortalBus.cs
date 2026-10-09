using System.Security.Cryptography;
using Tmds.DBus.Protocol;

namespace DeskPilot.Desktop.Linux.Wayland;

/// <summary>One D-Bus argument: its signature ("s", "o", "u", "i", "d", "b" or "a{sv}") and value.</summary>
internal readonly record struct PortalArg(string Signature, object Value)
{
    public static PortalArg Str(string s) => new("s", s);
    public static PortalArg Path(string p) => new("o", p);
    public static PortalArg U32(uint v) => new("u", v);
    public static PortalArg I32(int v) => new("i", v);
    public static PortalArg F64(double v) => new("d", v);
    public static PortalArg Options(IReadOnlyDictionary<string, object> options) => new("a{sv}", options);
}

/// <summary>
/// A call to xdg-desktop-portal described as data, so building the requests can be unit-tested without a bus.
/// HandleToken is set for methods that answer through a Request object and its Response signal.
/// </summary>
internal sealed record PortalCall(string Interface, string Method, IReadOnlyList<PortalArg> Args, string? HandleToken = null)
{
    public string Signature => string.Concat(Args.Select(a => a.Signature));
}

/// <summary>The Response signal of a portal Request: 0 = success, 1 = cancelled by the user, 2 = other error.</summary>
internal sealed record PortalResponse(uint Code, IReadOnlyDictionary<string, object?> Results)
{
    public string? GetString(string key) => Results.TryGetValue(key, out var v) ? v as string : null;
}

internal interface IPortalBus
{
    /// <summary>The caller's unique bus name (":1.42"); connects when needed.</summary>
    Task<string> GetUniqueNameAsync(CancellationToken ct);
    /// <summary>Calls a method that returns a Request handle and waits for its Response.</summary>
    Task<PortalResponse> RequestAsync(PortalCall call, TimeSpan timeout, CancellationToken ct);
    /// <summary>Calls a method with no interesting reply (the RemoteDesktop Notify* methods).</summary>
    Task CallAsync(PortalCall call, CancellationToken ct);
    /// <summary>The "version" property of a portal interface, or 0 when the interface is missing.</summary>
    Task<uint> GetVersionAsync(string iface, CancellationToken ct);
}

internal static class PortalNames
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string Screenshot = "org.freedesktop.portal.Screenshot";
    public const string RemoteDesktop = "org.freedesktop.portal.RemoteDesktop";
    public const string ScreenCast = "org.freedesktop.portal.ScreenCast";
    public const string Request = "org.freedesktop.portal.Request";

    /// <summary>A fresh handle token (letters, digits and underscores only, as the portal requires).</summary>
    public static string NewToken() => "deskpilot_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    /// <summary>The Request object the portal will create: predictable, so the Response can be subscribed before calling.</summary>
    public static string RequestPath(string uniqueName, string token) =>
        $"{ObjectPath}/request/{Sender(uniqueName)}/{token}";

    public static string SessionPath(string uniqueName, string token) =>
        $"{ObjectPath}/session/{Sender(uniqueName)}/{token}";

    private static string Sender(string uniqueName) => uniqueName.TrimStart(':').Replace('.', '_');

    /// <summary>A readable error for a non-zero Response code.</summary>
    public static string DescribeFailure(string what, uint code) => code switch
    {
        1 => $"{what} was cancelled or not allowed (the request was dismissed).",
        _ => $"{what} failed in xdg-desktop-portal (response {code}).",
    };
}

/// <summary>The real portal bus over the D-Bus session bus (Tmds.DBus.Protocol).</summary>
internal sealed class PortalBus : IPortalBus, IDisposable
{
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly string? _address;
    private DBusConnection? _connection;

    /// <param name="address">A D-Bus address; null for the session bus.</param>
    public PortalBus(string? address = null) => _address = address;

    private async Task<DBusConnection> ConnectionAsync(CancellationToken ct)
    {
        if (_connection != null) return _connection;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connection != null) return _connection;
            var address = _address ?? DBusAddress.Session;
            if (string.IsNullOrEmpty(address)) throw new InvalidOperationException("There is no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set), so xdg-desktop-portal cannot be reached.");
            var c = new DBusConnection(address);
            try
            {
                await c.ConnectAsync().ConfigureAwait(false);
            }
            catch
            {
                c.Dispose();
                throw;
            }
            _connection = c;
            _ = c.DisconnectedAsync().ContinueWith(_ => { if (ReferenceEquals(_connection, c)) _connection = null; }, TaskScheduler.Default);
            return c;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<string> GetUniqueNameAsync(CancellationToken ct)
    {
        var c = await ConnectionAsync(ct).ConfigureAwait(false);
        return c.UniqueName ?? throw new InvalidOperationException("The D-Bus connection has no unique name.");
    }

    public async Task<PortalResponse> RequestAsync(PortalCall call, TimeSpan timeout, CancellationToken ct)
    {
        if (call.HandleToken == null) throw new ArgumentException("A portal request needs a handle token", nameof(call));
        var c = await ConnectionAsync(ct).ConfigureAwait(false);
        var expected = PortalNames.RequestPath(c.UniqueName!, call.HandleToken);
        var tcs = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Subscribe before calling: the portal may answer before the method reply arrives.
        using var watch = await WatchResponseAsync(c, expected, tcs).ConfigureAwait(false);
        var handle = await c.CallMethodAsync(Build(c, call), (m, _) => m.GetBodyReader().ReadObjectPathAsString(), null).ConfigureAwait(false);
        IDisposable? legacy = null;
        // Portals older than 0.9 do not use the predictable path.
        if (!string.Equals(handle, expected, StringComparison.Ordinal)) legacy = await WatchResponseAsync(c, handle, tcs).ConfigureAwait(false);
        try
        {
            return await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"xdg-desktop-portal did not answer {call.Method} within {timeout.TotalSeconds:0} seconds (a permission dialog may be waiting).");
        }
        finally
        {
            legacy?.Dispose();
        }
    }

    private static async Task<IDisposable> WatchResponseAsync(DBusConnection c, string path, TaskCompletionSource<PortalResponse> tcs) =>
        await c.WatchSignalAsync<PortalResponse>(null, path, PortalNames.Request, "Response",
            (m, _) =>
            {
                var r = m.GetBodyReader();
                uint code = r.ReadUInt32();
                var results = r.ReadDictionaryOfStringToVariantValue();
                return new PortalResponse(code, results.ToDictionary(kv => kv.Key, kv => PortalValues.ToPlain(kv.Value)));
            },
            n =>
            {
                if (n.HasValue) tcs.TrySetResult(n.Value);
                else if (n.IsCompletion) tcs.TrySetException(n.Exception ?? new InvalidOperationException("The portal request ended without a response."));
            },
            ObserverFlags.None, false, null).ConfigureAwait(false);

    public async Task CallAsync(PortalCall call, CancellationToken ct)
    {
        var c = await ConnectionAsync(ct).ConfigureAwait(false);
        await c.CallMethodAsync(Build(c, call)).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    public async Task<uint> GetVersionAsync(string iface, CancellationToken ct)
    {
        var c = await ConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            var msg = BuildPropertyGet(c, iface, "version");
            var v = await c.CallMethodAsync(msg, (m, _) => m.GetBodyReader().ReadVariantValue(), null).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return PortalValues.ToPlain(v) is uint u ? u : 0;
        }
        catch (DBusErrorReplyException)
        {
            return 0;
        }
    }

    // MessageWriter is a ref struct, so messages are built in synchronous helpers.
    private static MessageBuffer BuildPropertyGet(DBusConnection c, string iface, string property)
    {
        using var writer = c.GetMessageWriter();
        writer.WriteMethodCallHeader(PortalNames.Service, PortalNames.ObjectPath, "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
        writer.WriteString(iface);
        writer.WriteString(property);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildServiceCall(DBusConnection c, string destination, string path, string iface, string member, uint? arg)
    {
        using var writer = c.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, path, iface, member, arg.HasValue ? "u" : null, MessageFlags.None);
        if (arg.HasValue) writer.WriteUInt32(arg.Value);
        return writer.CreateMessage();
    }

    private static MessageBuffer BuildNameHasOwner(DBusConnection c, string name)
    {
        using var writer = c.GetMessageWriter();
        writer.WriteMethodCallHeader("org.freedesktop.DBus", "/org/freedesktop/DBus", "org.freedesktop.DBus", "NameHasOwner", "s", MessageFlags.None);
        writer.WriteString(name);
        return writer.CreateMessage();
    }

    /// <summary>
    /// Calls a method on another session-bus service that takes no argument or one uint and returns a string or
    /// nothing (the GNOME Window Calls extension). Returns null for methods without a reply value.
    /// </summary>
    public async Task<string?> CallServiceAsync(string destination, string path, string iface, string member, uint? arg, bool returnsString, CancellationToken ct)
    {
        var c = await ConnectionAsync(ct).ConfigureAwait(false);
        var msg = BuildServiceCall(c, destination, path, iface, member, arg);
        if (!returnsString)
        {
            await c.CallMethodAsync(msg).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            return null;
        }
        return await c.CallMethodAsync(msg, (m, _) => m.GetBodyReader().ReadString(), null).WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    /// <summary>True when a bus name is owned now or can be started on demand (portals are activatable).</summary>
    public static async Task<bool> NameAvailableAsync(string name, CancellationToken ct)
    {
        var address = DBusAddress.Session;
        if (string.IsNullOrEmpty(address)) return false;
        using var c = new DBusConnection(address);
        await c.ConnectAsync().ConfigureAwait(false);
        bool owned = await c.CallMethodAsync(BuildNameHasOwner(c, name), (m, _) => m.GetBodyReader().ReadBool(), null).WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        if (owned) return true;
        var activatable = await c.ListActivatableServicesAsync().WaitAsync(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
        return activatable.Contains(name);
    }

    private static MessageBuffer Build(DBusConnection c, PortalCall call)
    {
        using var writer = c.GetMessageWriter();
        writer.WriteMethodCallHeader(PortalNames.Service, PortalNames.ObjectPath, call.Interface, call.Method, call.Signature, MessageFlags.None);
        foreach (var a in call.Args)
        {
            switch (a.Signature)
            {
                case "s": writer.WriteString((string)a.Value); break;
                case "o": writer.WriteObjectPath((string)a.Value); break;
                case "u": writer.WriteUInt32((uint)a.Value); break;
                case "i": writer.WriteInt32((int)a.Value); break;
                case "d": writer.WriteDouble((double)a.Value); break;
                case "b": writer.WriteBool((bool)a.Value); break;
                case "a{sv}": writer.WriteDictionary(PortalValues.ToVariants((IReadOnlyDictionary<string, object>)a.Value)); break;
                default: throw new NotSupportedException($"Portal argument type {a.Signature}");
            }
        }
        return writer.CreateMessage();
    }

    public void Dispose()
    {
        _connection?.Dispose();
        _connection = null;
    }
}

/// <summary>Converts between D-Bus variants and plain .NET values (string, uint, int, bool, double, object?[] for arrays and structs, dictionaries).</summary>
internal static class PortalValues
{
    public static Dictionary<string, VariantValue> ToVariants(IReadOnlyDictionary<string, object> options)
    {
        var d = new Dictionary<string, VariantValue>(StringComparer.Ordinal);
        foreach (var (k, v) in options)
        {
            d[k] = v switch
            {
                string s => VariantValue.String(s),
                uint u => VariantValue.UInt32(u),
                int i => VariantValue.Int32(i),
                bool b => VariantValue.Bool(b),
                double x => VariantValue.Double(x),
                _ => throw new NotSupportedException($"Portal option {k} has unsupported type {v.GetType().Name}"),
            };
        }
        return d;
    }

    public static object? ToPlain(VariantValue v)
    {
        switch (v.Type)
        {
            case VariantValueType.Byte: return v.GetByte();
            case VariantValueType.Bool: return v.GetBool();
            case VariantValueType.Int16: return (int)v.GetInt16();
            case VariantValueType.UInt16: return (uint)v.GetUInt16();
            case VariantValueType.Int32: return v.GetInt32();
            case VariantValueType.UInt32: return v.GetUInt32();
            case VariantValueType.Int64: return v.GetInt64();
            case VariantValueType.UInt64: return v.GetUInt64();
            case VariantValueType.Double: return v.GetDouble();
            case VariantValueType.String: return v.GetString();
            case VariantValueType.ObjectPath: return v.GetObjectPathAsString();
            case VariantValueType.Signature: return v.GetSignature().ToString();
            case VariantValueType.Variant: return ToPlain(v.GetVariantValue());
            case VariantValueType.Array:
            case VariantValueType.Struct:
            {
                var items = new object?[v.Count];
                for (int i = 0; i < items.Length; i++) items[i] = ToPlain(v.GetItem(i));
                return items;
            }
            case VariantValueType.Dictionary:
            {
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                for (int i = 0; i < v.Count; i++)
                {
                    var e = v.GetDictionaryEntry(i);
                    var key = ToPlain(e.Key)?.ToString();
                    if (key != null) d[key] = ToPlain(e.Value);
                }
                return d;
            }
            default: return null;
        }
    }

    public static bool TryGetInt(object? value, out int result)
    {
        switch (value)
        {
            case int i: result = i; return true;
            case uint u when u <= int.MaxValue: result = (int)u; return true;
            case long l when l is >= int.MinValue and <= int.MaxValue: result = (int)l; return true;
            case double d: result = (int)Math.Round(d); return true;
            default: result = 0; return false;
        }
    }
}

using Tmds.DBus.Protocol;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>An accessible object on the AT-SPI bus: the owning connection's bus name and the object path.</summary>
internal readonly record struct AtspiRef(string Bus, string Path)
{
    public const string NullPath = "/org/a11y/atspi/null";
    public bool IsNull => string.IsNullOrEmpty(Bus) || string.IsNullOrEmpty(Path) || Path == NullPath;
    public override string ToString() => $"{Bus}{Path}";
}

internal delegate void ArgWriter(ref MessageWriter writer);

/// <summary>
/// The few AT-SPI2 calls DeskPilot needs, over Tmds.DBus.Protocol on the accessibility bus. Every call takes a token;
/// a cancelled call is abandoned (D-Bus has no way to cancel a call already sent).
/// </summary>
internal sealed class AtSpiConnection : IDisposable
{
    public const string AccessibleInterface = "org.a11y.atspi.Accessible";
    public const string ComponentInterface = "org.a11y.atspi.Component";
    public const string TextInterface = "org.a11y.atspi.Text";
    public const string ValueInterface = "org.a11y.atspi.Value";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    public static readonly AtspiRef RegistryRoot = new("org.a11y.atspi.Registry", "/org/a11y/atspi/accessible/root");

    /// <summary>Calls in flight at once; keeps a big list from flooding the bus and the app.</summary>
    private readonly SemaphoreSlim _inFlight = new(48, 48);
    private readonly DBusConnection _connection;
    private volatile bool _closed;

    private AtSpiConnection(DBusConnection connection) => _connection = connection;

    public bool IsClosed => _closed;

    /// <summary>
    /// Connects to the accessibility bus: $AT_SPI_BUS_ADDRESS when set, otherwise the address the session bus's
    /// org.a11y.Bus service hands out (at-spi-bus-launcher, D-Bus activated when needed).
    /// </summary>
    public static async Task<AtSpiConnection> ConnectAsync(CancellationToken ct)
    {
        var address = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
        if (string.IsNullOrWhiteSpace(address))
        {
            var session = DBusAddress.Session;
            if (string.IsNullOrWhiteSpace(session))
                throw new InvalidOperationException("there is no D-Bus session bus (DBUS_SESSION_BUS_ADDRESS is not set).");
            using var sessionConnection = new DBusConnection(session);
            await sessionConnection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
            address = await CallRawAsync(sessionConnection, "org.a11y.Bus", "/org/a11y/bus", "org.a11y.Bus", "GetAddress", null, null,
                static (m, _) => m.GetBodyReader().ReadString()).WaitAsync(ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(address))
                throw new InvalidOperationException("the session bus did not return an accessibility bus address.");
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return new AtSpiConnection(connection);
    }

    public void Dispose()
    {
        _closed = true;
        _connection.Dispose();
    }

    private static Task<T> CallRawAsync<T>(DBusConnection connection, string destination, string path, string @interface, string member,
        string? signature, ArgWriter? args, MessageValueReader<T> reader)
    {
        MessageBuffer buffer;
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(destination: destination, path: path, @interface: @interface, member: member, signature: signature);
            args?.Invoke(ref writer);
            buffer = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return connection.CallMethodAsync(buffer, reader, null);
    }

    private async Task<T> CallAsync<T>(AtspiRef target, string @interface, string member, string? signature, ArgWriter? args,
        MessageValueReader<T> reader, CancellationToken ct)
    {
        await _inFlight.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await CallRawAsync(_connection, target.Bus, target.Path, @interface, member, signature, args, reader)
                .WaitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusConnectionClosedException or ObjectDisposedException or DBusConnectionException)
        {
            _closed = true;
            throw;
        }
        finally
        {
            _inFlight.Release();
        }
    }

    // ------------------------------------------------------------------ readers

    private static List<AtspiRef> ReadRefArray(Message m, object? _)
    {
        var r = m.GetBodyReader();
        var list = new List<AtspiRef>();
        var end = r.ReadArrayStart(DBusType.Struct);
        while (r.HasNext(end))
        {
            r.AlignStruct();
            var bus = r.ReadString();
            var path = r.ReadObjectPathAsString();
            list.Add(new AtspiRef(bus, path));
        }
        return list;
    }

    private static AtspiRef ReadRef(Message m, object? _)
    {
        var r = m.GetBodyReader();
        r.AlignStruct();
        var bus = r.ReadString();
        var path = r.ReadObjectPathAsString();
        return new AtspiRef(bus, path);
    }

    private static VariantValue ReadVariant(Message m, object? _) => m.GetBodyReader().ReadVariantValue();

    // ------------------------------------------------------------------ org.a11y.atspi.Accessible

    public Task<List<AtspiRef>> GetChildrenAsync(AtspiRef node, CancellationToken ct) =>
        CallAsync(node, AccessibleInterface, "GetChildren", null, null, ReadRefArray, ct);

    public Task<AtspiRef> GetChildAtIndexAsync(AtspiRef node, int index, CancellationToken ct) =>
        CallAsync(node, AccessibleInterface, "GetChildAtIndex", "i", (ref MessageWriter w) => w.WriteInt32(index), ReadRef, ct);

    public Task<uint> GetRoleAsync(AtspiRef node, CancellationToken ct) =>
        CallAsync(node, AccessibleInterface, "GetRole", null, null, static (m, _) => m.GetBodyReader().ReadUInt32(), ct);

    public Task<string> GetRoleNameAsync(AtspiRef node, CancellationToken ct) =>
        CallAsync(node, AccessibleInterface, "GetRoleName", null, null, static (m, _) => m.GetBodyReader().ReadString(), ct);

    /// <summary>The state set as one 64-bit mask (bit n = AtspiStateType n).</summary>
    public Task<ulong> GetStateAsync(AtspiRef node, CancellationToken ct) =>
        CallAsync(node, AccessibleInterface, "GetState", null, null, static (m, _) =>
        {
            var words = m.GetBodyReader().ReadArrayOfUInt32();
            ulong bits = 0;
            if (words.Length > 0) bits |= words[0];
            if (words.Length > 1) bits |= (ulong)words[1] << 32;
            return bits;
        }, ct);

    public Task<VariantValue> GetPropertyAsync(AtspiRef node, string @interface, string property, CancellationToken ct) =>
        CallAsync(node, PropertiesInterface, "Get", "ss", (ref MessageWriter w) =>
        {
            w.WriteString(@interface);
            w.WriteString(property);
        }, ReadVariant, ct);

    public async Task<string> GetNameAsync(AtspiRef node, CancellationToken ct) =>
        AsString(await GetPropertyAsync(node, AccessibleInterface, "Name", ct).ConfigureAwait(false));

    public async Task<AtspiRef> GetParentAsync(AtspiRef node, CancellationToken ct)
    {
        var v = Unwrap(await GetPropertyAsync(node, AccessibleInterface, "Parent", ct).ConfigureAwait(false));
        if (v.Type != VariantValueType.Struct || v.Count < 2) return default;
        return new AtspiRef(AsString(v.GetItem(0)), AsString(v.GetItem(1)));
    }

    public async Task<int> GetChildCountAsync(AtspiRef node, CancellationToken ct)
    {
        var v = Unwrap(await GetPropertyAsync(node, AccessibleInterface, "ChildCount", ct).ConfigureAwait(false));
        return v.Type == VariantValueType.Int32 ? v.GetInt32() : 0;
    }

    // ------------------------------------------------------------------ Component, Text, Value

    /// <summary>coordType: 0 = screen, 1 = window, 2 = parent.</summary>
    public Task<(int X, int Y, int Width, int Height)> GetExtentsAsync(AtspiRef node, uint coordType, CancellationToken ct) =>
        CallAsync(node, ComponentInterface, "GetExtents", "u", (ref MessageWriter w) => w.WriteUInt32(coordType), static (m, _) =>
        {
            var r = m.GetBodyReader();
            r.AlignStruct();
            return (r.ReadInt32(), r.ReadInt32(), r.ReadInt32(), r.ReadInt32());
        }, ct);

    public Task<AtspiRef> GetAccessibleAtPointAsync(AtspiRef node, int x, int y, uint coordType, CancellationToken ct) =>
        CallAsync(node, ComponentInterface, "GetAccessibleAtPoint", "iiu", (ref MessageWriter w) =>
        {
            w.WriteInt32(x);
            w.WriteInt32(y);
            w.WriteUInt32(coordType);
        }, ReadRef, ct);

    public Task<string> GetTextAsync(AtspiRef node, int start, int end, CancellationToken ct) =>
        CallAsync(node, TextInterface, "GetText", "ii", (ref MessageWriter w) =>
        {
            w.WriteInt32(start);
            w.WriteInt32(end);
        }, static (m, _) => m.GetBodyReader().ReadString(), ct);

    public async Task<int> GetCharacterCountAsync(AtspiRef node, CancellationToken ct)
    {
        var v = Unwrap(await GetPropertyAsync(node, TextInterface, "CharacterCount", ct).ConfigureAwait(false));
        return v.Type == VariantValueType.Int32 ? v.GetInt32() : -1;
    }

    public async Task<double?> GetCurrentValueAsync(AtspiRef node, CancellationToken ct)
    {
        var v = Unwrap(await GetPropertyAsync(node, ValueInterface, "CurrentValue", ct).ConfigureAwait(false));
        return v.Type switch
        {
            VariantValueType.Double => v.GetDouble(),
            VariantValueType.Int32 => v.GetInt32(),
            VariantValueType.UInt32 => v.GetUInt32(),
            VariantValueType.Int64 => v.GetInt64(),
            _ => null,
        };
    }

    // ------------------------------------------------------------------ bus

    /// <summary>The process id behind a connection on the accessibility bus.</summary>
    public Task<uint> GetConnectionPidAsync(string busName, CancellationToken ct) =>
        CallAsync(new AtspiRef("org.freedesktop.DBus", "/org/freedesktop/DBus"), "org.freedesktop.DBus", "GetConnectionUnixProcessID", "s",
            (ref MessageWriter w) => w.WriteString(busName), static (m, _) => m.GetBodyReader().ReadUInt32(), ct);

    private static VariantValue Unwrap(VariantValue v)
    {
        // Some implementations wrap the value in one more variant.
        for (int i = 0; i < 3 && v.Type == VariantValueType.Variant; i++) v = v.GetVariantValue();
        return v;
    }

    private static string AsString(VariantValue v)
    {
        v = Unwrap(v);
        return v.Type switch
        {
            VariantValueType.String => v.GetString(),
            VariantValueType.ObjectPath => v.GetObjectPathAsString(),
            _ => "",
        };
    }
}

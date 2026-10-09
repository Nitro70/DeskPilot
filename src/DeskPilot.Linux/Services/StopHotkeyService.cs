using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Desktop.Linux;
using DeskPilot.Desktop.Linux.X11;

namespace DeskPilot.Linux.Services;

/// <summary>
/// The global stop hotkey. X11 only (XGrabKey through <see cref="X11GlobalHotkey"/>); Wayland compositors do not
/// let apps grab keys, so there the overlay, the tray and the failsafe corner are the stop controls.
/// </summary>
public sealed class StopHotkeyService : IDisposable
{
    /// <summary>Registers a combo and returns a handle that unregisters on dispose, or null with an error.</summary>
    public delegate IDisposable? RegisterFunc(KeyCombo combo, Action pressed, out string error);

    private readonly LinuxSessionKind _session;
    private readonly RegisterFunc _register;
    private IDisposable? _handle;
    private bool _disposed;

    public StopHotkeyService(LinuxSessionKind session, RegisterFunc? register = null)
    {
        _session = session;
        _register = register ?? RegisterX11;
    }

    /// <summary>Raised on a background thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    public bool IsSupported => StopHint.HotkeySupported(_session);
    public bool IsRegistered => _handle != null;

    /// <summary>(Re)registers the hotkey. Returns false with a readable reason when it is not active.</summary>
    public bool Register(string? text, out string error)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Unregister();
        if (!IsSupported)
        {
            error = "global hotkeys are not available on Wayland";
            return false;
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "no hotkey is set";
            return false;
        }
        if (!StopHotkey.TryParse(text, out var combo, out _, out error)) return false;

        try
        {
            _handle = _register(combo, () => Pressed?.Invoke(), out error);
        }
        catch (Exception ex)
        {
            // The X11 layer may be missing pieces (no display, a stub build): never fail startup over it.
            Log.Warn($"Stop hotkey registration failed: {ex.Message}");
            error = ex is NotImplementedException ? "the hotkey is not available in this build" : ex.Message;
            _handle = null;
        }
        if (_handle == null && string.IsNullOrWhiteSpace(error)) error = "it could not be registered";
        return _handle != null;
    }

    public void Unregister()
    {
        var h = _handle;
        _handle = null;
        try { h?.Dispose(); }
        catch (Exception ex) { Log.Warn($"Stop hotkey cleanup failed: {ex.Message}"); }
    }

    private static IDisposable? RegisterX11(KeyCombo combo, Action pressed, out string error) =>
        X11GlobalHotkey.TryRegister(combo, pressed, out error);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
    }
}

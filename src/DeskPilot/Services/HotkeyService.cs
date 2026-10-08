using System.Runtime.InteropServices;
using System.Windows.Interop;
using DeskPilot.Core.Runtime;

namespace DeskPilot.Services;

/// <summary>
/// One global hotkey registered with RegisterHotKey on a message-only window.
/// Must be created and used on the UI thread.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xD1F7;
    private HwndSource? _source;
    private bool _registered;

    /// <summary>Raised on the UI thread when the hotkey is pressed.</summary>
    public event Action? Pressed;

    public HotkeyGesture? Current { get; private set; }

    /// <summary>
    /// (Re)registers the hotkey. Returns false with a readable error when the text is invalid or the
    /// combination is already taken by another program; the previous hotkey is released either way.
    /// </summary>
    public bool Register(string? text, out string error)
    {
        Unregister();
        if (!HotkeyGesture.TryParse(text, out var gesture, out error)) return false;

        EnsureSource();
        if (_source == null)
        {
            error = "could not create the hotkey window";
            return false;
        }

        if (!Win32.RegisterHotKey(_source.Handle, HotkeyId, gesture!.Modifiers | HotkeyGesture.ModNoRepeat, gesture.VirtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? $"{gesture.Display} is already used by another program"
                : $"Windows refused {gesture.Display} (error {code})";
            Log.Warn($"Stop hotkey not registered: {error}");
            return false;
        }

        _registered = true;
        Current = gesture;
        Log.Info($"Stop hotkey registered: {gesture.Display}");
        return true;
    }

    public void Unregister()
    {
        if (_registered && _source != null) Win32.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        Current = null;
    }

    private void EnsureSource()
    {
        if (_source != null) return;
        var parameters = new HwndSourceParameters("DeskPilotHotkeySink")
        {
            ParentWindow = Win32.HWND_MESSAGE,
            WindowStyle = 0,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && wParam == HotkeyId)
        {
            handled = true;
            try { Pressed?.Invoke(); }
            catch (Exception ex) { Log.Error("Hotkey handler failed", ex); }
        }
        return 0;
    }

    public void Dispose()
    {
        Unregister();
        if (_source != null)
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
            _source = null;
        }
    }
}

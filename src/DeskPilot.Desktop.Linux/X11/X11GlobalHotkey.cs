using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.X11;

// STUB: owned by the X11 agent.
/// <summary>A global hotkey on X11 (XGrabKey on its own display connection and thread). Not available on Wayland.</summary>
public sealed class X11GlobalHotkey : IDisposable
{
    private X11GlobalHotkey() { }

    /// <summary>Registers the combo; pressed is raised on a background thread. Returns null with an error message when it cannot.</summary>
    public static X11GlobalHotkey? TryRegister(KeyCombo combo, Action pressed, out string error) => throw new NotImplementedException("STUB");

    public void Dispose() { }
}

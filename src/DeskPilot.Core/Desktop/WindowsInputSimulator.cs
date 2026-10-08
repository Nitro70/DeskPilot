using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class WindowsInputSimulator : IInputSimulator
{
    public void MoveMouse(int x, int y) => throw new NotImplementedException("STUB");
    public void MoveMouseSmooth(int x, int y, int durationMs) => throw new NotImplementedException("STUB");
    public void MouseDown(MouseButton button) => throw new NotImplementedException("STUB");
    public void MouseUp(MouseButton button) => throw new NotImplementedException("STUB");
    public void Click(MouseButton button, int clicks) => throw new NotImplementedException("STUB");
    public void Scroll(int dx, int dy) => throw new NotImplementedException("STUB");
    public void TypeText(string text, int delayMsPerChar) => throw new NotImplementedException("STUB");
    public void PressCombo(KeyCombo combo) => throw new NotImplementedException("STUB");
    public void KeyDown(string key) => throw new NotImplementedException("STUB");
    public void KeyUp(string key) => throw new NotImplementedException("STUB");
    public void ReleaseAll() => throw new NotImplementedException("STUB");
    public ScreenPoint GetCursorPosition() => throw new NotImplementedException("STUB");
}

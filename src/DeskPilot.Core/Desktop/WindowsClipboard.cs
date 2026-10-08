using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class WindowsClipboard : IClipboardService
{
    public string? GetText() => throw new NotImplementedException("STUB");
    public void SetText(string text) => throw new NotImplementedException("STUB");
}

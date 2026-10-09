using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

// STUB: owned by the Linux services agent.
public sealed class LinuxClipboard : IClipboardService
{
    public LinuxClipboard(LinuxSessionInfo session) { }
    public string? GetText() => throw new NotImplementedException("STUB");
    public void SetText(string text) => throw new NotImplementedException("STUB");
}

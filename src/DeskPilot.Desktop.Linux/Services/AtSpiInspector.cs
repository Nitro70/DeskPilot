using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Linux.Services;

// STUB: owned by the Linux services agent.
public sealed class AtSpiInspector : IUiInspector
{
    /// <param name="windows">Used to map a window handle to its process, and to tell whether screen coordinates are available.</param>
    public AtSpiInspector(IWindowManager windows) { }
    public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct) => throw new NotImplementedException("STUB");
}

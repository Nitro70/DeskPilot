using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Desktop;

// STUB: owned by the Desktop module agent.
public sealed class UiAutomationInspector : IUiInspector
{
    public Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct) => throw new NotImplementedException("STUB");
    public Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct) => throw new NotImplementedException("STUB");
}

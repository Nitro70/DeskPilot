using System.Runtime.CompilerServices;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Safety;

/// <summary>
/// Attaches the exact target window to a ProposedAction. ProposedAction (a shared contract) only carries a
/// screen point, which cannot identify the window focus_window is about to raise (it may be minimized or
/// covered). The table is keyed by object identity, so it never keeps an action alive.
/// </summary>
internal static class ActionTargets
{
    private static readonly ConditionalWeakTable<ProposedAction, WindowInfo> Windows = new();

    public static ProposedAction WithWindow(this ProposedAction action, WindowInfo window)
    {
        Windows.AddOrUpdate(action, window);
        return action;
    }

    public static WindowInfo? GetWindow(ProposedAction action) =>
        Windows.TryGetValue(action, out var window) ? window : null;
}

using DeskPilot.Core.Abstractions;
using static DeskPilot.Desktop.Windows.NativeMethods;

namespace DeskPilot.Desktop.Windows;

/// <summary>
/// Mouse and keyboard through SendInput, in physical virtual-desktop pixels. Keeps track of every key and
/// button it pressed so <see cref="ReleaseAll"/> can lift anything left down after an error or a stop.
/// </summary>
public sealed class WindowsInputSimulator : IInputSimulator
{
    internal const int ClickHoldMs = 30;
    internal const int KeyHoldMs = 20;
    internal const int ModifierSettleMs = 10;
    internal const int TypingBatchSize = 16;
    internal const int TypingBatchPauseMs = 2;
    internal const int SmoothStepMs = 16;
    internal const int MoveTolerancePx = 2;

    private readonly IInputBackend _backend;
    private readonly object _gate = new();
    // Insertion order matters: keys are released in reverse order of pressing.
    private readonly List<KeyStroke> _pressedKeys = new();
    private readonly HashSet<MouseButton> _pressedButtons = new();

    public WindowsInputSimulator() : this(new Win32InputBackend()) { }

    internal WindowsInputSimulator(IInputBackend backend) => _backend = backend;

    internal IReadOnlyCollection<MouseButton> PressedButtons { get { lock (_gate) return _pressedButtons.ToList(); } }
    internal IReadOnlyCollection<ushort> PressedKeys { get { lock (_gate) return _pressedKeys.Select(k => k.Vk).ToList(); } }

    public void MoveMouse(int x, int y)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            MoveCore(x, y, verify: true);
        }
    }

    public void MoveMouseSmooth(int x, int y, int durationMs)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            if (durationMs <= 0)
            {
                MoveCore(x, y, verify: true);
                return;
            }
            var from = _backend.GetCursorPos();
            int steps = Math.Clamp(durationMs / SmoothStepMs, 2, 150);
            int pause = Math.Max(1, durationMs / steps);
            var path = InputBuilder.SmoothPath(from, new ScreenPoint(x, y), steps);
            var vs = _backend.GetVirtualScreen();
            for (int i = 0; i < path.Count - 1; i++)
            {
                SendOrThrow(new[] { InputBuilder.MoveAbsolute(path[i].X, path[i].Y, vs) }, "move the mouse");
                _backend.Sleep(pause);
            }
            MoveCore(x, y, verify: true);
        }
    }

    private void MoveCore(int x, int y, bool verify)
    {
        var vs = _backend.GetVirtualScreen();
        SendOrThrow(new[] { InputBuilder.MoveAbsolute(x, y, vs) }, "move the mouse");
        if (!verify) return;

        // The raw input thread applies the move asynchronously; give it a moment before judging.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            var p = _backend.GetCursorPos();
            if (Math.Abs(p.X - x) <= MoveTolerancePx && Math.Abs(p.Y - y) <= MoveTolerancePx) return;
            _backend.Sleep(5);
        }
        _backend.SetCursorPos(x, y);
    }

    public void MouseDown(MouseButton button)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            ButtonCore(button, up: false);
        }
    }

    public void MouseUp(MouseButton button)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            ButtonCore(button, up: true);
        }
    }

    private void ButtonCore(MouseButton button, bool up)
    {
        var physical = InputBuilder.ToPhysical(button, _backend.ButtonsSwapped);
        SendOrThrow(new[] { InputBuilder.Button(physical, up) }, up ? "release the mouse button" : "press the mouse button");
        if (up) _pressedButtons.Remove(button);
        else _pressedButtons.Add(button);
    }

    public void Click(MouseButton button, int clicks)
    {
        clicks = Math.Clamp(clicks, 1, 3);
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            int gap = ClickGapMs(_backend.DoubleClickTimeMs);
            for (int i = 0; i < clicks; i++)
            {
                ButtonCore(button, up: false);
                _backend.Sleep(ClickHoldMs);
                ButtonCore(button, up: true);
                if (i < clicks - 1) _backend.Sleep(gap);
            }
        }
    }

    /// <summary>Pause between the clicks of a double/triple click: well inside the system double-click time.</summary>
    internal static int ClickGapMs(int doubleClickTimeMs) => Math.Clamp(doubleClickTimeMs / 8, 15, 70);

    public void Scroll(int dx, int dy)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            // One event per notch: some apps only scroll one step per wheel message, whatever its delta.
            for (int i = 0; i < Math.Abs(dy); i++)
            {
                SendOrThrow(new[] { InputBuilder.VerticalWheel(Math.Sign(dy)) }, "scroll");
                _backend.Sleep(10);
            }
            for (int i = 0; i < Math.Abs(dx); i++)
            {
                SendOrThrow(new[] { InputBuilder.HorizontalWheel(Math.Sign(dx)) }, "scroll");
                _backend.Sleep(10);
            }
        }
    }

    public void TypeText(string text, int delayMsPerChar)
    {
        if (string.IsNullOrEmpty(text)) return;
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            var chunks = InputBuilder.TextChunks(text, _backend.ScanCode);
            if (delayMsPerChar > 0)
            {
                foreach (var chunk in chunks)
                {
                    SendOrThrow(chunk, "type");
                    _backend.Sleep(delayMsPerChar);
                }
                return;
            }
            for (int i = 0; i < chunks.Count; i += TypingBatchSize)
            {
                var batch = chunks.Skip(i).Take(TypingBatchSize).SelectMany(c => c).ToArray();
                SendOrThrow(batch, "type");
                if (i + TypingBatchSize < chunks.Count) _backend.Sleep(TypingBatchPauseMs);
            }
        }
    }

    public void PressCombo(KeyCombo combo)
    {
        ArgumentNullException.ThrowIfNull(combo);
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            KeyStroke? key = combo.Key == null ? null : KeyMap.Resolve(combo.Key, _backend.VkKeyScan);
            var modifiers = KeyMap.ModifiersFor(combo.Modifiers, key);
            var pressedHere = new List<KeyStroke>();
            try
            {
                foreach (var m in modifiers)
                {
                    // A modifier already held through KeyDown stays held; the combo must not release it.
                    if (_pressedKeys.Any(p => p.Vk == m.Vk)) continue;
                    KeyCore(m, up: false);
                    pressedHere.Add(m);
                }
                if (modifiers.Count > 0) _backend.Sleep(ModifierSettleMs);
                if (key is { } k)
                {
                    KeyCore(k, up: false);
                    pressedHere.Add(k);
                    _backend.Sleep(KeyHoldMs);
                    KeyCore(k, up: true);
                    pressedHere.Remove(k);
                }
                else
                {
                    _backend.Sleep(KeyHoldMs);
                }
                if (modifiers.Count > 0) _backend.Sleep(ModifierSettleMs);
            }
            finally
            {
                // Modifiers up in reverse order, also when something above threw.
                for (int i = pressedHere.Count - 1; i >= 0; i--)
                {
                    try { KeyCore(pressedHere[i], up: true); }
                    catch (InvalidOperationException) { }
                }
            }
        }
    }

    public void KeyDown(string key)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            var stroke = KeyMap.Resolve(key, _backend.VkKeyScan);
            foreach (var m in KeyMap.ModifiersFor(Array.Empty<string>(), stroke)) KeyCore(m, up: false);
            KeyCore(stroke, up: false);
        }
    }

    public void KeyUp(string key)
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            var stroke = KeyMap.Resolve(key, _backend.VkKeyScan);
            KeyCore(stroke, up: true);
            var implicitMods = KeyMap.ModifiersFor(Array.Empty<string>(), stroke);
            for (int i = implicitMods.Count - 1; i >= 0; i--)
                if (_pressedKeys.Any(p => p.Vk == implicitMods[i].Vk)) KeyCore(implicitMods[i], up: true);
        }
    }

    private void KeyCore(KeyStroke stroke, bool up)
    {
        SendOrThrow(new[] { InputBuilder.Key(stroke.Vk, _backend.ScanCode(stroke.Vk), up, stroke.Extended) }, up ? "release a key" : "press a key");
        if (up) _pressedKeys.RemoveAll(p => p.Vk == stroke.Vk);
        else if (!_pressedKeys.Any(p => p.Vk == stroke.Vk)) _pressedKeys.Add(stroke);
    }

    public void ReleaseAll()
    {
        lock (_gate)
        {
            using var dpi = _backend.EnterDpiScope();
            for (int i = _pressedKeys.Count - 1; i >= 0; i--)
            {
                var k = _pressedKeys[i];
                try { _backend.Send(new[] { InputBuilder.Key(k.Vk, _backend.ScanCode(k.Vk), true, k.Extended) }); }
                catch (Exception) { }
            }
            _pressedKeys.Clear();
            bool swapped = _backend.ButtonsSwapped;
            foreach (var b in _pressedButtons.ToList())
            {
                try { _backend.Send(new[] { InputBuilder.Button(InputBuilder.ToPhysical(b, swapped), up: true) }); }
                catch (Exception) { }
            }
            _pressedButtons.Clear();
        }
    }

    public ScreenPoint GetCursorPosition()
    {
        using var dpi = _backend.EnterDpiScope();
        return _backend.GetCursorPos();
    }

    private void SendOrThrow(INPUT[] inputs, string what)
    {
        if (inputs.Length == 0) return;
        int sent = _backend.Send(inputs);
        if (sent != inputs.Length)
            throw new InvalidOperationException(
                $"Windows rejected the input while trying to {what} ({sent} of {inputs.Length} events sent). " +
                "The desktop may be locked, a UAC prompt or another secure screen may be showing, or the target runs as administrator.");
    }
}

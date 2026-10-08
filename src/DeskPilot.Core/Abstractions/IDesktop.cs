namespace DeskPilot.Core.Abstractions;

// Everything that touches the real Windows desktop sits behind these interfaces so the
// tool layer, safety guard and agent loop can be unit-tested with fakes.

public sealed record MonitorInfo(
    int Index,              // 0-based, in enumeration order
    string DeviceName,      // e.g. \\.\DISPLAY1
    ScreenRect Bounds,      // physical pixels, virtual-desktop coordinates
    ScreenRect WorkArea,    // bounds minus taskbar
    bool IsPrimary,
    double Scale);          // DPI scale, 1.0 = 96 dpi

public enum ImageFormatKind { Jpeg, Png }

public sealed record CaptureRequest(
    ScreenRect Source,          // region of the virtual desktop to capture
    int TargetWidth,            // output image size (already chosen by the caller, aspect preserved)
    int TargetHeight,
    ImageFormatKind Format,
    int JpegQuality,            // 1-100
    bool DrawCursor,            // draw the mouse cursor into the image
    int GridSpacing);           // 0 = no grid; otherwise draw a labelled coordinate grid every N output pixels

public sealed record CapturedFrame(
    byte[] Data,
    string MediaType,           // image/jpeg or image/png
    int Width,                  // output image size
    int Height,
    ScreenRect Source);         // what was captured

public interface IScreenCapture
{
    IReadOnlyList<MonitorInfo> GetMonitors();
    ScreenRect GetVirtualScreen();
    CapturedFrame Capture(CaptureRequest request);
}

public enum MouseButton { Left, Right, Middle }

public interface IInputSimulator
{
    /// <summary>Absolute move, physical virtual-desktop pixels.</summary>
    void MoveMouse(int x, int y);
    /// <summary>Smoothly moves the cursor (used for drags so apps register them).</summary>
    void MoveMouseSmooth(int x, int y, int durationMs);
    void MouseDown(MouseButton button);
    void MouseUp(MouseButton button);
    /// <summary>Clicks at the current position. clicks = 1, 2 or 3.</summary>
    void Click(MouseButton button, int clicks);
    /// <summary>Wheel scroll in notches. Positive dy = down, positive dx = right.</summary>
    void Scroll(int dx, int dy);
    /// <summary>Types Unicode text (SendInput KEYEVENTF_UNICODE). Newlines become Enter.</summary>
    void TypeText(string text, int delayMsPerChar);
    /// <summary>Presses a key combination parsed by <see cref="KeyCombo"/>, e.g. "ctrl+shift+t".</summary>
    void PressCombo(KeyCombo combo);
    void KeyDown(string key);
    void KeyUp(string key);
    /// <summary>Releases any modifier/mouse buttons this simulator may have left pressed.</summary>
    void ReleaseAll();
    ScreenPoint GetCursorPosition();
}

public sealed record WindowInfo(
    nint Handle,
    string Title,
    string ClassName,
    string ProcessName,     // without .exe
    int ProcessId,
    ScreenRect Bounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsForeground,
    bool IsElevated,        // owning process runs elevated (or we cannot open it because it is elevated)
    bool IsUacPrompt);      // consent.exe / credential UI

public interface IWindowManager
{
    /// <summary>Visible top-level windows with a title, front to back (z-order).</summary>
    IReadOnlyList<WindowInfo> ListWindows();
    WindowInfo? GetForegroundWindow();
    /// <summary>The top-level window under a physical screen point.</summary>
    WindowInfo? GetWindowAt(int x, int y);
    /// <summary>Restores (if minimized) and brings the window to the foreground.</summary>
    bool FocusWindow(nint handle);
    bool IsCurrentProcessElevated { get; }
    /// <summary>True while a UAC consent prompt (consent.exe) is running.</summary>
    bool IsUacPromptActive();
}

public sealed record UiElementInfo(
    string Name,
    string ControlType,         // e.g. Button, Edit, MenuItem, Hyperlink, ListItem
    ScreenRect Bounds,          // physical pixels
    string? AutomationId,
    bool IsEnabled,
    bool IsFocusable,
    string? Value);

public interface IUiInspector
{
    /// <summary>Interactive elements of a window (buttons, edits, links, menu items...), capped at maxElements.</summary>
    Task<IReadOnlyList<UiElementInfo>> GetElementsAsync(nint window, int maxElements, CancellationToken ct);
    /// <summary>The element at a physical screen point, or null. Must return within ~1s.</summary>
    Task<UiElementInfo?> GetElementAtAsync(int x, int y, CancellationToken ct);
}

public sealed record LaunchResult(bool Success, string Message, int? ProcessId);

public interface IAppLauncher
{
    /// <summary>
    /// Opens an app, file, folder or URL with the shell (never elevated unless allowElevation).
    /// target may be an absolute path, a program on PATH, a URL, or a Start-menu app name.
    /// </summary>
    LaunchResult Launch(string target, string? arguments, bool allowElevation);
}

public interface IClipboardService
{
    string? GetText();
    void SetText(string text);
}

public sealed record ShellResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public interface IShellRunner
{
    /// <summary>Runs a command non-elevated with no visible window. shell = "powershell" or "cmd".</summary>
    Task<ShellResult> RunAsync(string command, string shell, string workingDirectory, int timeoutMs, CancellationToken ct);
}

/// <summary>All desktop services in one bundle.</summary>
public sealed record DesktopServices(
    IScreenCapture Screen,
    IInputSimulator Input,
    IWindowManager Windows,
    IUiInspector Ui,
    IAppLauncher Launcher,
    IClipboardService Clipboard,
    IShellRunner Shell);

/// <summary>
/// Lets the UI react around real input actions, e.g. hide the floating status overlay when
/// the agent is about to click where it sits.
/// </summary>
public interface IInputActionObserver
{
    Task BeforeInputAsync(ScreenPoint? target, CancellationToken ct);
    void AfterInput();
}

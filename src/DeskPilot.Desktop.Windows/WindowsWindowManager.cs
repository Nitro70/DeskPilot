using System.Diagnostics;
using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;
using static DeskPilot.Desktop.Windows.NativeMethods;

namespace DeskPilot.Desktop.Windows;

/// <summary>Top-level window enumeration, hit testing, focus and elevation checks.</summary>
public sealed class WindowsWindowManager : IWindowManager
{
    internal const string UacProcessName = "consent";
    internal const string CredentialDialogClass = "Credential Dialog Xaml Host";

    private static readonly Lazy<bool> SelfElevated = new(QueryCurrentProcessElevated);

    public bool IsCurrentProcessElevated => SelfElevated.Value;

    public IReadOnlyList<WindowInfo> ListWindows()
    {
        using var dpi = DpiScope.PerMonitorV2();
        var handles = new List<nint>();
        EnumWindowsProc proc = (h, _) => { handles.Add(h); return true; };
        EnumWindows(proc, 0);
        GC.KeepAlive(proc);

        var foreground = NativeMethods.GetForegroundWindow();
        var cache = new Dictionary<uint, ProcessFacts>();
        var result = new List<WindowInfo>();
        foreach (var h in handles)
        {
            if (!IsWindowVisible(h)) continue;
            if (IsCloaked(h)) continue;
            var title = GetTitle(h);
            var cls = GetClass(h);
            long exStyle = GetWindowLongPtr(h, GWL_EXSTYLE);
            if (!ShouldList(true, false, title, exStyle, cls)) continue;
            result.Add(Build(h, title, cls, foreground, cache));
        }
        return result;
    }

    /// <summary>The filter for "app windows" a person would see in the taskbar or Alt+Tab.</summary>
    internal static bool ShouldList(bool visible, bool cloaked, string title, long exStyle, string className)
    {
        if (!visible || cloaked) return false;
        if (string.IsNullOrWhiteSpace(title)) return false;
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0) return false;
        // The desktop itself ("Program Manager") is not an app window.
        if (className == "Progman") return false;
        return true;
    }

    internal static bool IsUacPromptWindow(string processName, string className) =>
        string.Equals(processName, UacProcessName, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(className, CredentialDialogClass, StringComparison.Ordinal);

    /// <summary>
    /// When we are not elevated, failing to open a process or its token with "access denied" almost always means
    /// the process runs elevated (or is a protected system process); either way the agent must treat it as elevated.
    /// </summary>
    internal static bool InferElevated(bool? tokenElevated, bool accessDenied, bool selfElevated) =>
        tokenElevated ?? (accessDenied && !selfElevated);

    public WindowInfo? GetForegroundWindow()
    {
        using var dpi = DpiScope.PerMonitorV2();
        var h = NativeMethods.GetForegroundWindow();
        return h == 0 ? null : Build(h, GetTitle(h), GetClass(h), h, new Dictionary<uint, ProcessFacts>());
    }

    public WindowInfo? GetWindowAt(int x, int y)
    {
        using var dpi = DpiScope.PerMonitorV2();
        var child = WindowFromPoint(new POINT { X = x, Y = y });
        if (child == 0) return null;
        var root = GetAncestor(child, GA_ROOT);
        if (root == 0) root = child;
        return Build(root, GetTitle(root), GetClass(root), NativeMethods.GetForegroundWindow(), new Dictionary<uint, ProcessFacts>());
    }

    public bool FocusWindow(nint handle)
    {
        if (handle == 0 || !IsWindow(handle)) return false;
        using var dpi = DpiScope.PerMonitorV2();

        if (IsIconic(handle)) ShowWindow(handle, SW_RESTORE);
        if (IsForegroundOrOwned(handle)) return true;

        // AttachThreadInput needs a message queue on this thread; peeking creates one.
        PeekMessage(out _, 0, 0, 0, PM_NOREMOVE);
        uint self = GetCurrentThreadId();
        var fg = NativeMethods.GetForegroundWindow();
        uint fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        uint targetThread = GetWindowThreadProcessId(handle, out _);

        bool attachedFg = false, attachedTarget = false;
        try
        {
            // Sharing input state with the current foreground thread lets SetForegroundWindow pass the
            // foreground lock without synthetic Alt presses.
            if (fgThread != 0 && fgThread != self) attachedFg = AttachThreadInput(self, fgThread, true);
            if (targetThread != 0 && targetThread != self && targetThread != fgThread) attachedTarget = AttachThreadInput(self, targetThread, true);
            BringWindowToTop(handle);
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attachedTarget) AttachThreadInput(self, targetThread, false);
            if (attachedFg) AttachThreadInput(self, fgThread, false);
        }

        if (WaitForForeground(handle, 300)) return true;

        // Last resort that still sends no keystrokes.
        SwitchToThisWindow(handle, true);
        return WaitForForeground(handle, 300);
    }

    private static bool WaitForForeground(nint handle, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (IsForegroundOrOwned(handle)) return true;
            if (sw.ElapsedMilliseconds >= timeoutMs) return false;
            Thread.Sleep(15);
        }
    }

    /// <summary>The window is in front, or one of its own dialogs is (activating an owner can activate its modal popup).</summary>
    private static bool IsForegroundOrOwned(nint handle)
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg == 0) return false;
        if (fg == handle) return true;
        var fgOwner = GetAncestor(fg, GA_ROOTOWNER);
        return fgOwner != 0 && fgOwner == GetAncestor(handle, GA_ROOTOWNER) && GetAncestor(handle, GA_ROOTOWNER) == handle;
    }

    public bool IsUacPromptActive()
    {
        Process[] procs;
        try { procs = Process.GetProcessesByName(UacProcessName); }
        catch (InvalidOperationException) { return false; }
        try { return procs.Length > 0; }
        finally { foreach (var p in procs) p.Dispose(); }
    }

    internal const string UwpFrameClass = "ApplicationFrameWindow";
    private const string UwpCoreWindowClass = "Windows.UI.Core.CoreWindow";

    private WindowInfo Build(nint h, string title, string cls, nint foreground, Dictionary<uint, ProcessFacts> cache)
    {
        GetWindowThreadProcessId(h, out var pid);
        if (cls == UwpFrameClass) pid = HostedAppProcess(h, pid);
        if (!cache.TryGetValue(pid, out var facts))
        {
            facts = QueryProcess(pid, IsCurrentProcessElevated);
            cache[pid] = facts;
        }
        return new WindowInfo(
            h,
            title,
            cls,
            facts.Name,
            (int)pid,
            GetBounds(h),
            IsWindowVisible(h),
            IsIconic(h),
            h == foreground,
            facts.Elevated,
            IsUacPromptWindow(facts.Name, cls));
    }

    /// <summary>
    /// Store/UWP apps draw inside a frame window owned by ApplicationFrameHost; the real app is the process of the
    /// CoreWindow child. Without this, blocking or elevation checks would look at the frame host instead of the app.
    /// (A minimized UWP app has no CoreWindow child; the frame host is reported then.)
    /// </summary>
    private static uint HostedAppProcess(nint frame, uint framePid)
    {
        uint found = 0;
        EnumWindowsProc proc = (child, _) =>
        {
            if (GetClass(child) != UwpCoreWindowClass) return true;
            GetWindowThreadProcessId(child, out var childPid);
            if (childPid == 0 || childPid == framePid) return true;
            found = childPid;
            return false;
        };
        EnumChildWindows(frame, proc, 0);
        GC.KeepAlive(proc);
        return found != 0 ? found : framePid;
    }

    internal readonly record struct ProcessFacts(string Name, bool Elevated);

    private static ProcessFacts QueryProcess(uint pid, bool selfElevated)
    {
        if (pid == 0) return new ProcessFacts("", false);
        string? name = null;
        bool? elevated = null;
        bool denied = false;

        var hProcess = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProcess == 0)
        {
            denied = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
        }
        else
        {
            try
            {
                name = ImageName(hProcess);
                if (OpenProcessToken(hProcess, TOKEN_QUERY, out var token))
                {
                    try
                    {
                        if (GetTokenInformation(token, TokenElevation, out int isElevated, sizeof(int), out _)) elevated = isElevated != 0;
                    }
                    finally { CloseHandle(token); }
                }
                else
                {
                    denied = Marshal.GetLastWin32Error() == ERROR_ACCESS_DENIED;
                }
            }
            finally { CloseHandle(hProcess); }
        }

        if (string.IsNullOrEmpty(name))
        {
            // The process snapshot works even for processes we may not open.
            try
            {
                using var p = Process.GetProcessById((int)pid);
                name = p.ProcessName;
            }
            catch (ArgumentException) { name = ""; }
            catch (InvalidOperationException) { name = ""; }
        }
        return new ProcessFacts(name ?? "", InferElevated(elevated, denied, selfElevated));
    }

    private static unsafe string? ImageName(nint hProcess)
    {
        const int Max = 1024;
        char* buf = stackalloc char[Max];
        int size = Max;
        if (!QueryFullProcessImageName(hProcess, 0, buf, ref size) || size <= 0) return null;
        var path = new string(buf, 0, size);
        return Path.GetFileNameWithoutExtension(path);
    }

    private static bool QueryCurrentProcessElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out var token)) return false;
        try
        {
            return GetTokenInformation(token, TokenElevation, out int isElevated, sizeof(int), out _) && isElevated != 0;
        }
        finally { CloseHandle(token); }
    }

    private static bool IsCloaked(nint h) =>
        DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    private static ScreenRect GetBounds(nint h)
    {
        // The extended frame excludes the invisible resize borders Windows 10/11 add around windows.
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0 && r.Right > r.Left)
            return r.ToScreenRect();
        return GetWindowRect(h, out var wr) ? wr.ToScreenRect() : default;
    }

    internal static unsafe string GetTitle(nint h)
    {
        int len = GetWindowTextLength(h);
        if (len <= 0) return "";
        len = Math.Min(len, 2048);
        char* buf = stackalloc char[len + 1];
        int n = GetWindowText(h, buf, len + 1);
        return n <= 0 ? "" : new string(buf, 0, n);
    }

    internal static unsafe string GetClass(nint h)
    {
        char* buf = stackalloc char[257];
        int n = GetClassName(h, buf, 257);
        return n <= 0 ? "" : new string(buf, 0, n);
    }
}

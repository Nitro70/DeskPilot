namespace DeskPilot.Desktop.Linux.Services;

// STUB: owned by the Linux services agent.
/// <summary>Facts about Linux processes from /proc, used by the window managers and the safety checks.</summary>
public static class LinuxProcessInfo
{
    /// <summary>The process's name (comm, or the executable's file name), or null when it cannot be read.</summary>
    public static string? GetProcessName(int pid) => throw new NotImplementedException("STUB");

    /// <summary>True when the process runs as root (real or effective uid 0), or cannot be inspected while we are not root.</summary>
    public static bool IsElevated(int pid) => throw new NotImplementedException("STUB");

    /// <summary>True when the current process runs as root.</summary>
    public static bool IsCurrentProcessElevated => throw new NotImplementedException("STUB");

    /// <summary>True for polkit authentication agents and password prompts (polkit-gnome, polkit-kde, lxpolkit, ...).</summary>
    public static bool IsPrivilegePromptProcess(string processName) => throw new NotImplementedException("STUB");

    /// <summary>True while a privilege prompt is open: pkexec waiting for authentication, or a polkit agent dialog.</summary>
    public static bool IsPrivilegePromptActive() => throw new NotImplementedException("STUB");
}

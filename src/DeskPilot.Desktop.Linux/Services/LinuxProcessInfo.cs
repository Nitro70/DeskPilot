namespace DeskPilot.Desktop.Linux.Services;

/// <summary>Facts about Linux processes from /proc, used by the window managers and the safety checks.</summary>
public static class LinuxProcessInfo
{
    // comm is truncated to 15 characters, so agents are matched by prefix as well as by full name.
    private static readonly string[] PromptPrefixes =
    {
        "polkit-gnome-authentication-agent", "polkit-kde-authentication-agent", "polkit-mate-authentication-agent",
        "polkit-agent-helper", "lxqt-policykit", "lxpolkit", "xfce-polkit", "mate-polkit", "hyprpolkitagent",
        "cosmic-osd", "pkexec", "gcr-prompter", "ssh-askpass", "ksshaskpass", "x11-ssh-askpass", "gksu", "kdesu",
    };

    internal static string ProcRoot { get; set; } = "/proc";

    /// <summary>The process's name (comm, or the executable's file name), or null when it cannot be read.</summary>
    public static string? GetProcessName(int pid)
    {
        if (pid <= 0) return null;
        try
        {
            var comm = File.ReadAllText(Path.Combine(ProcRoot, pid.ToString(), "comm")).Trim();
            if (comm.Length > 0 && comm.Length < 15) return comm;
            // comm is cut at 15 characters; the executable's real name is better when readable.
            try
            {
                var exe = new FileInfo(Path.Combine(ProcRoot, pid.ToString(), "exe")).LinkTarget;
                if (!string.IsNullOrEmpty(exe)) return Path.GetFileName(exe.Replace(" (deleted)", ""));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return comm.Length > 0 ? comm : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the process runs as root (real or effective uid 0). A window whose process cannot be seen at all
    /// (/proc mounted with hidepid) counts as elevated while we are not root, to stay on the safe side.
    /// </summary>
    public static bool IsElevated(int pid)
    {
        if (pid <= 0) return false;
        var uids = ReadUids(pid, out bool visible);
        if (uids == null) return !visible && !IsCurrentProcessElevated && Directory.Exists(ProcRoot);
        return uids.Value.Real == 0 || uids.Value.Effective == 0;
    }

    /// <summary>True when the current process runs as root.</summary>
    public static bool IsCurrentProcessElevated
    {
        get
        {
            var uids = ReadUids(Environment.ProcessId, out _);
            return uids is { Effective: 0 };
        }
    }

    /// <summary>True for polkit authentication agents and password prompts (polkit-gnome, polkit-kde, lxpolkit, ...).</summary>
    public static bool IsPrivilegePromptProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var n = processName.Trim();
        foreach (var p in PromptPrefixes)
        {
            if (n.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
            // A 15-character comm is a prefix of the real name.
            if (n.Length == 15 && p.StartsWith(n, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True while a privilege prompt is open: pkexec waiting for authentication, or polkit's helper running
    /// (it only runs while an authentication dialog is in progress).
    /// </summary>
    public static bool IsPrivilegePromptActive()
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(ProcRoot))
            {
                var name = Path.GetFileName(dir);
                if (name.Length == 0 || !char.IsDigit(name[0])) continue;
                string comm;
                try { comm = File.ReadAllText(Path.Combine(dir, "comm")).Trim(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
                if (comm is "pkexec" || comm.StartsWith("polkit-agent-he", StringComparison.Ordinal)) return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return false;
    }

    /// <summary>Parses the "Uid:" line of /proc/PID/status. visible = the process directory exists.</summary>
    internal static (int Real, int Effective)? ReadUids(int pid, out bool visible)
    {
        var dir = Path.Combine(ProcRoot, pid.ToString());
        visible = Directory.Exists(dir);
        if (!visible) return null;
        try
        {
            foreach (var line in File.ReadLines(Path.Combine(dir, "status")))
            {
                if (!line.StartsWith("Uid:", StringComparison.Ordinal)) continue;
                var parts = line[4..].Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[0], out var real) && int.TryParse(parts[1], out var eff))
                    return (real, eff);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return null;
    }
}

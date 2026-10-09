namespace DeskPilot.Core.Runtime;

/// <summary>Finds command-line tools the way a user's shell would, plus the usual install folders.</summary>
public static class ExecutableLocator
{
    private static readonly string[] Extensions = { ".exe", ".cmd", ".bat", ".com" };

    /// <summary>
    /// Returns the full path of a command (e.g. "claude"), or null. A configured path wins when it exists.
    /// Searches PATH (process + user + machine), then common install locations for npm, Node, Python, Scoop, etc.
    /// </summary>
    public static string? Find(string command, string? configuredPath = null, IEnumerable<string>? extraCandidates = null)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            var p = ExpandUserPath(configuredPath.Trim().Trim('"'));
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        if (Path.IsPathRooted(command) && File.Exists(command)) return command;

        if (!OperatingSystem.IsWindows())
        {
            foreach (var dir in UnixSearchDirectories(extraCandidates))
            {
                try
                {
                    var candidate = Path.Combine(dir, command);
                    if (IsUnixExecutable(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        foreach (var dir in SearchDirectories(extraCandidates))
        {
            foreach (var ext in Extensions)
            {
                try
                {
                    var candidate = Path.Combine(dir, command + ext);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (ArgumentException) { }
            }
        }
        return null;
    }

    /// <summary>Expands %VAR% everywhere, and on Linux also a leading ~ and $VAR / ${VAR}.</summary>
    internal static string ExpandUserPath(string path)
    {
        var p = Environment.ExpandEnvironmentVariables(path);
        if (OperatingSystem.IsWindows()) return p;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (p == "~") return home;
        if (p.StartsWith("~/", StringComparison.Ordinal)) p = Path.Combine(home, p[2..]);
        return System.Text.RegularExpressions.Regex.Replace(p, @"\$\{?([A-Za-z_][A-Za-z0-9_]*)\}?",
            m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? m.Value);
    }

    private static bool IsUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path)) return false;
        const UnixFileMode anyExec = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        return (File.GetUnixFileMode(path) & anyExec) != 0;
    }

    /// <summary>PATH, then the places Linux installers put user tools (Claude Code native, npm, bun, nvm, snap...).</summary>
    private static IEnumerable<string> UnixSearchDirectories(IEnumerable<string>? extra)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>();
        if (extra != null) dirs.AddRange(extra);
        dirs.Add(Path.Combine(home, ".local", "bin"));
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path)) dirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        dirs.Add(Path.Combine(home, ".claude", "local"));
        dirs.Add(Path.Combine(home, ".npm-global", "bin"));
        dirs.Add(Path.Combine(home, ".bun", "bin"));
        dirs.Add(Path.Combine(home, ".volta", "bin"));
        dirs.Add(Path.Combine(home, ".deno", "bin"));
        dirs.Add(Path.Combine(home, "bin"));
        try
        {
            var nvm = Path.Combine(home, ".nvm", "versions", "node");
            if (Directory.Exists(nvm))
                dirs.AddRange(Directory.GetDirectories(nvm).OrderByDescending(d => d, StringComparer.Ordinal).Select(d => Path.Combine(d, "bin")));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        dirs.AddRange(new[] { "/usr/local/bin", "/usr/bin", "/bin", "/snap/bin", "/var/lib/flatpak/exports/bin", "/home/linuxbrew/.linuxbrew/bin" });

        foreach (var d in dirs)
        {
            var t = d.Trim();
            if (t.StartsWith("~/", StringComparison.Ordinal)) t = Path.Combine(home, t[2..]);
            if (t.Length == 0 || !seen.Add(t)) continue;
            if (Directory.Exists(t)) yield return t;
        }
    }

    private static IEnumerable<string> SearchDirectories(IEnumerable<string>? extra)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        var dirs = new List<string>();
        if (extra != null) dirs.AddRange(extra);
        // Native installers first (Claude Code's native build lives here).
        dirs.Add(Path.Combine(home, ".local", "bin"));
        foreach (var target in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = Environment.GetEnvironmentVariable("PATH", target);
            if (!string.IsNullOrEmpty(path)) dirs.AddRange(path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        }
        dirs.Add(Path.Combine(appData, "npm"));
        dirs.Add(Path.Combine(home, ".npm-global"));
        dirs.Add(Path.Combine(home, ".npm-global", "bin"));
        dirs.Add(Path.Combine(local, "Programs"));
        dirs.Add(Path.Combine(home, "scoop", "shims"));
        dirs.Add(Path.Combine(home, ".bun", "bin"));
        dirs.Add(Path.Combine(local, "pnpm"));
        dirs.Add(Path.Combine(local, "Volta", "bin"));
        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));

        foreach (var d in dirs)
        {
            var trimmed = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            if (Directory.Exists(trimmed)) yield return trimmed;
        }
    }
}

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
            var p = Environment.ExpandEnvironmentVariables(configuredPath.Trim().Trim('"'));
            if (File.Exists(p)) return Path.GetFullPath(p);
        }

        if (Path.IsPathRooted(command) && File.Exists(command)) return command;

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

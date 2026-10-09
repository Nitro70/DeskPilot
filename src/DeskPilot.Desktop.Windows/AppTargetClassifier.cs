using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DeskPilot.Desktop.Windows;

internal enum LaunchKind
{
    /// <summary>A URL or protocol link (https:, mailto:, ms-settings:, steam:, shell: ...): opened by the shell.</summary>
    Uri,
    /// <summary>An existing file or folder.</summary>
    Path,
    /// <summary>Looks like a path but nothing exists there.</summary>
    MissingPath,
    /// <summary>A program found on PATH or in the App Paths registry.</summary>
    Command,
    /// <summary>Anything else: treated as a Start-menu app name.</summary>
    AppName,
}

/// <summary>What a launch target was understood as. Value is the URI, full path, resolved program, or the name.</summary>
internal sealed record LaunchTarget(LaunchKind Kind, string Value);

internal static partial class AppTargetClassifier
{
    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]+):", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    public static LaunchTarget Classify(string target) =>
        Classify(target, File.Exists, Directory.Exists, ResolveCommand);

    /// <summary>Pure classification; the file-system and PATH probes are injected so tests control them.</summary>
    public static LaunchTarget Classify(string target, Func<string, bool> fileExists, Func<string, bool> dirExists, Func<string, string?> resolveCommand)
    {
        var t = (target ?? "").Trim().Trim('"').Trim();
        if (t.Length == 0) throw new ArgumentException("No launch target given.", nameof(target));

        // A scheme needs two or more letters, so "C:\..." stays a drive path.
        if (SchemeRegex().IsMatch(t) && !LooksLikeDrivePath(t)) return new LaunchTarget(LaunchKind.Uri, t);
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ')) return new LaunchTarget(LaunchKind.Uri, "https://" + t);

        var expanded = ExpandPath(t);
        // "D:" alone means the drive root, not the process's current directory on that drive.
        if (expanded.Length == 2 && LooksLikeDrivePath(expanded)) expanded += "\\";
        if (LooksLikePath(expanded))
        {
            string full;
            try { full = System.IO.Path.GetFullPath(expanded); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return new LaunchTarget(LaunchKind.MissingPath, expanded); }
            if (System.IO.Path.IsPathFullyQualified(full) && (fileExists(full) || dirExists(full))) return new LaunchTarget(LaunchKind.Path, full);
            return new LaunchTarget(LaunchKind.MissingPath, full);
        }

        var resolved = resolveCommand(expanded);
        if (!string.IsNullOrEmpty(resolved)) return new LaunchTarget(LaunchKind.Command, resolved);

        return new LaunchTarget(LaunchKind.AppName, t);
    }

    internal static string ExpandPath(string t)
    {
        var expanded = Environment.ExpandEnvironmentVariables(t);
        if (expanded == "~" || expanded.StartsWith("~\\", StringComparison.Ordinal) || expanded.StartsWith("~/", StringComparison.Ordinal))
            expanded = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + expanded[1..];
        return expanded;
    }

    private static bool LooksLikeDrivePath(string t) =>
        t.Length >= 2 && char.IsAsciiLetter(t[0]) && t[1] == ':';

    /// <summary>Rooted ("C:\x", "\\server\share") or containing a directory separator.</summary>
    internal static bool LooksLikePath(string t) =>
        LooksLikeDrivePath(t) || t.StartsWith(@"\\", StringComparison.Ordinal) || t.Contains('\\') || t.Contains('/');

    /// <summary>
    /// Resolves a bare program name like Win+R does: PATH with PATHEXT (or the name as given when it has an
    /// extension), then the App Paths registry (HKCU, then HKLM in both registry views).
    /// </summary>
    public static string? ResolveCommand(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' }) >= 0) return null;
        name = name.Trim();

        var pathExt = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool hasExtension = System.IO.Path.HasExtension(name);

        foreach (var dir in PathDirectories())
        {
            try
            {
                if (hasExtension)
                {
                    var direct = System.IO.Path.Combine(dir, name);
                    if (File.Exists(direct)) return direct;
                }
                else
                {
                    foreach (var ext in pathExt)
                    {
                        var candidate = System.IO.Path.Combine(dir, name + ext);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch (ArgumentException) { }
        }

        var exeName = hasExtension ? name : name + ".exe";
        return FromAppPaths(exeName);
    }

    private static IEnumerable<string> PathDirectories()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Win+R checks the system folder first, whatever PATH says.
        var dirs = new List<string> { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) };
        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path)) dirs.AddRange(path.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var d in dirs)
        {
            var trimmed = Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'));
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            yield return trimmed;
        }
    }

    private static string? FromAppPaths(string exeName)
    {
        const string Key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\";
        var roots = new (RegistryHive Hive, RegistryView View)[]
        {
            (RegistryHive.CurrentUser, RegistryView.Default),
            (RegistryHive.LocalMachine, RegistryView.Registry64),
            (RegistryHive.LocalMachine, RegistryView.Registry32),
        };
        foreach (var (hive, view) in roots)
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                using var key = baseKey.OpenSubKey(Key + exeName);
                if (key?.GetValue("") is not string value || value.Length == 0) continue;
                var candidate = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { }
        }
        return null;
    }
}

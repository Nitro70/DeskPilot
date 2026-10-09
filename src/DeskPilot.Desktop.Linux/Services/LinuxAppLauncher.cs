using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Safety;
using DeskPilot.Core.Settings;

namespace DeskPilot.Desktop.Linux.Services;

internal enum LinuxLaunchKind
{
    /// <summary>A URL or scheme link (https:, mailto:, file:, steam: ...): opened with xdg-open.</summary>
    Uri,
    /// <summary>An existing file or folder.</summary>
    Path,
    /// <summary>Looks like a path but nothing exists there.</summary>
    MissingPath,
    /// <summary>A program found on PATH.</summary>
    Command,
    /// <summary>Anything else: an application name, looked up in the .desktop files.</summary>
    AppName,
}

/// <summary>What a launch target was understood as. Value is the URI, full path, resolved program, or the name.</summary>
internal sealed record LinuxLaunchTarget(LinuxLaunchKind Kind, string Value);

/// <summary>
/// Opens apps, files, folders and URLs on Linux. Everything is started detached: in its own session (setsid) with its
/// stdio on /dev/null, so it outlives DeskPilot and never writes into DeskPilot's pipes. Programs that ask for root
/// (pkexec, sudo, gksu, kdesu, doas, X-KDE-SubstituteUID, or the configured elevated launch targets) are refused unless
/// elevation is allowed.
/// </summary>
public sealed partial class LinuxAppLauncher : IAppLauncher
{
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan EarlyExitWindow = TimeSpan.FromMilliseconds(300);

    private static readonly HashSet<string> EscalationCommands = new(StringComparer.Ordinal)
    {
        "pkexec", "sudo", "sudoedit", "gksu", "gksudo", "kdesu", "kdesudo", "doas", "su", "run0", "beesu", "lxsudo", "lxqt-sudo",
    };

    private readonly Func<IEnumerable<string>> _elevatedTargets;
    private readonly Func<string, string?> _env;
    private readonly Func<string, string?> _findTool;
    private readonly string? _home;

    public LinuxAppLauncher() : this(null) { }

    /// <param name="elevatedLaunchTargets">
    /// The live Safety.ElevatedLaunchTargets list (programs that always ask for root). Defaults to the built-in list.
    /// </param>
    public LinuxAppLauncher(Func<IEnumerable<string>>? elevatedLaunchTargets)
        : this(elevatedLaunchTargets, Environment.GetEnvironmentVariable, LinuxTools.FindTool, null) { }

    /// <summary>For tests: the environment (XDG folders, locale, desktop) and tool lookup are injected.</summary>
    internal LinuxAppLauncher(Func<IEnumerable<string>>? elevatedLaunchTargets, Func<string, string?> env, Func<string, string?> findTool, string? home)
    {
        _elevatedTargets = elevatedLaunchTargets ?? (() => new SafetySettings().ElevatedLaunchTargets);
        _env = env;
        _findTool = findTool;
        _home = home;
    }

    private string Home => _home ?? LinuxTools.HomeDirectory();

    public LaunchResult Launch(string target, string? arguments, bool allowElevation)
    {
        if (string.IsNullOrWhiteSpace(target)) return new LaunchResult(false, "No launch target given.", null);
        arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();
        var args = LinuxTools.SplitArguments(arguments);

        // "pkexec", "sudo ..." and friends are refused by name, whether or not they are installed.
        var firstWord = LinuxTools.SplitArguments(target).FirstOrDefault() ?? "";
        if (!allowElevation && EscalationCommands.Contains(System.IO.Path.GetFileName(firstWord)))
            return Refused(System.IO.Path.GetFileName(firstWord), "asks for root rights");

        LinuxLaunchTarget classified;
        try { classified = Classify(target, File.Exists, Directory.Exists, ResolveCommand, _env, Home); }
        catch (ArgumentException ex) { return new LaunchResult(false, ex.Message, null); }

        try
        {
            return classified.Kind switch
            {
                LinuxLaunchKind.Uri => OpenWithDefaultApp(classified.Value, classified.Value),
                LinuxLaunchKind.Path => OpenPath(classified.Value, args, allowElevation),
                LinuxLaunchKind.MissingPath => new LaunchResult(false, $"Nothing exists at '{classified.Value}'. Check the path (list the folder with run_command or open its parent folder).", null),
                LinuxLaunchKind.Command => StartProgram(classified.Value, args, allowElevation, Home),
                _ => LaunchByName(classified.Value, args, allowElevation),
            };
        }
        catch (Win32Exception ex)
        {
            return new LaunchResult(false, $"Could not open '{target}': {ex.Message}", null);
        }
        catch (InvalidOperationException ex)
        {
            return new LaunchResult(false, $"Could not open '{target}': {ex.Message}", null);
        }
        catch (IOException ex)
        {
            return new LaunchResult(false, $"Could not open '{target}': {ex.Message}", null);
        }
    }

    // ------------------------------------------------------------------ classification

    [GeneratedRegex(@"^(?<scheme>[A-Za-z][A-Za-z0-9+.\-]+):(?<rest>\S.*)?$", RegexOptions.CultureInvariant)]
    private static partial Regex SchemeRegex();

    [GeneratedRegex(@"\$(?:\{(?<n>[A-Za-z_][A-Za-z0-9_]*)\}|(?<n>[A-Za-z_][A-Za-z0-9_]*))", RegexOptions.CultureInvariant)]
    private static partial Regex VariableRegex();

    /// <summary>Pure classification; the file system, PATH and environment are injected so tests control them.</summary>
    internal static LinuxLaunchTarget Classify(string target, Func<string, bool> fileExists, Func<string, bool> dirExists,
        Func<string, string?> resolveCommand, Func<string, string?> env, string home)
    {
        var t = (target ?? "").Trim();
        if (t.Length >= 2 && ((t[0] == '"' && t[^1] == '"') || (t[0] == '\'' && t[^1] == '\''))) t = t[1..^1].Trim();
        if (t.Length == 0) throw new ArgumentException("No launch target given.", nameof(target));

        // "mailto:x", "https://..", "steam://run/1", "file:///..": a scheme of two or more characters before the colon.
        var m = SchemeRegex().Match(t);
        if (m.Success && !t.StartsWith('/') && !t.StartsWith('~') && !t.Contains(' ', StringComparison.Ordinal))
            return new LinuxLaunchTarget(LinuxLaunchKind.Uri, t);
        if (t.StartsWith("www.", StringComparison.OrdinalIgnoreCase) && !t.Contains(' ')) return new LinuxLaunchTarget(LinuxLaunchKind.Uri, "https://" + t);

        var expanded = ExpandPath(t, env, home);
        if (LooksLikePath(expanded))
        {
            // Relative paths are taken from the home folder, like a file manager would.
            var full = NormalizePath(expanded.StartsWith('/') ? expanded : home.TrimEnd('/') + "/" + expanded);
            if (fileExists(full) || dirExists(full)) return new LinuxLaunchTarget(LinuxLaunchKind.Path, full);
            // "example.com/page": a host name, not a relative path.
            var first = expanded.Split('/')[0];
            if (!expanded.StartsWith('/') && !expanded.StartsWith('.') && first.Contains('.') && !first.Contains(' ') && first.IndexOf('.') > 0
                && !System.IO.Path.HasExtension(expanded.TrimEnd('/')) && first.Split('.').Last().All(char.IsAsciiLetter))
                return new LinuxLaunchTarget(LinuxLaunchKind.Uri, "https://" + expanded);
            return new LinuxLaunchTarget(LinuxLaunchKind.MissingPath, full);
        }

        if (!expanded.Any(char.IsWhiteSpace))
        {
            var resolved = resolveCommand(expanded);
            if (!string.IsNullOrEmpty(resolved)) return new LinuxLaunchTarget(LinuxLaunchKind.Command, resolved);
        }
        return new LinuxLaunchTarget(LinuxLaunchKind.AppName, t);
    }

    /// <summary>Expands a leading ~ (the home folder) and $VAR / ${VAR} (unset variables are left as written).</summary>
    internal static string ExpandPath(string t, Func<string, string?> env, string home)
    {
        var expanded = VariableRegex().Replace(t, m =>
        {
            var v = env(m.Groups["n"].Value);
            return string.IsNullOrEmpty(v) ? m.Value : v;
        });
        if (expanded == "~") return home;
        if (expanded.StartsWith("~/", StringComparison.Ordinal)) return home.TrimEnd('/') + expanded[1..];
        return expanded;
    }

    /// <summary>Resolves "." and ".." and repeated slashes in an absolute POSIX path (no file-system access).</summary>
    internal static string NormalizePath(string absolute)
    {
        var parts = new List<string>();
        foreach (var segment in absolute.Split('/'))
        {
            if (segment.Length == 0 || segment == ".") continue;
            if (segment == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(segment);
        }
        return "/" + string.Join('/', parts);
    }

    internal static bool LooksLikePath(string t) =>
        t.StartsWith('/') || t.StartsWith("./", StringComparison.Ordinal) || t.StartsWith("../", StringComparison.Ordinal) || t.Contains('/');

    private string? ResolveCommand(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('/')) return null;
        return _findTool(name);
    }

    // ------------------------------------------------------------------ launching

    private LaunchResult OpenPath(string path, IReadOnlyList<string> args, bool allowElevation)
    {
        if (Directory.Exists(path)) return OpenWithDefaultApp(path, $"folder {path}");
        if (path.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase))
        {
            var entry = DesktopEntries.Parse(SafeRead(path), System.IO.Path.GetFileName(path), path, 0, false, DesktopEntries.CurrentLocales(_env));
            if (entry is { Type: "Application" } && !string.IsNullOrWhiteSpace(entry.Exec)) return LaunchEntry(entry, args, allowElevation);
        }
        // A program file runs (in its own folder, like a double-click); a script only when arguments were given,
        // because without them "open" more likely means "show it", which the default app does.
        if (LinuxTools.IsExecutableFile(path) && (args.Count > 0 || IsNativeProgram(path)))
            return StartProgram(path, args, allowElevation, System.IO.Path.GetDirectoryName(path) ?? Home);
        if (args.Count > 0)
            return OpenWithDefaultApp(path, $"{System.IO.Path.GetFileName(path)} ({path})", " The arguments were ignored: the file is not a program.");
        return OpenWithDefaultApp(path, $"{System.IO.Path.GetFileName(path)} ({path})");
    }

    /// <summary>ELF binaries (AppImages included).</summary>
    private static bool IsNativeProgram(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            return fs.Read(head) == 4 && head[0] == 0x7F && head[1] == (byte)'E' && head[2] == (byte)'L' && head[3] == (byte)'F';
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    private LaunchResult LaunchByName(string name, IReadOnlyList<string> args, bool allowElevation)
    {
        var (dirs, xdgCount) = DesktopEntries.ApplicationDirectories(_env, Home);
        var entries = DesktopEntries.Load(dirs, xdgCount, DesktopEntries.CurrentLocales(_env))
            .Where(e => TryExecAvailable(e) && !string.IsNullOrWhiteSpace(e.Exec))
            .ToList();
        var best = DesktopEntries.FindBest(entries, name, DesktopEntries.CurrentDesktops(_env));
        if (best != null) return LaunchEntry(best.Entry, args, allowElevation);

        // "gedit notes.txt": a command with its arguments written into the target.
        var words = LinuxTools.SplitArguments(name);
        if (words.Count > 1 && ResolveCommand(words[0]) is { } program)
            return StartProgram(program, words.Skip(1).Concat(args).ToList(), allowElevation, Home);

        return new LaunchResult(false,
            $"Could not find an app, file, folder or URL called '{name}'. Try the name shown in the app menu, the program's command name, a full path, or a URL.",
            null);
    }

    /// <summary>TryExec names a program that must exist for the entry to count as installed.</summary>
    private bool TryExecAvailable(DesktopEntry e)
    {
        if (string.IsNullOrWhiteSpace(e.TryExec)) return true;
        var t = e.TryExec.Trim();
        return t.StartsWith('/') ? LinuxTools.IsExecutableFile(t) : _findTool(t) != null;
    }

    private LaunchResult LaunchEntry(DesktopEntry e, IReadOnlyList<string> args, bool allowElevation)
    {
        var display = string.IsNullOrWhiteSpace(e.DisplayName) ? e.IdStem : e.DisplayName;
        if (!allowElevation && ElevationReason(e, _elevatedTargets()) is { } why)
            return Refused($"{display} ({e.Id})", why);

        var env = ChildEnvironment();
        // gtk-launch resolves the ID through the XDG folders, so only entries from those folders can use it.
        if (e.InXdgDirectory)
        {
            var gtkLaunch = _findTool("gtk-launch") ?? _findTool("gtk4-launch");
            if (gtkLaunch != null && RunHelper(new[] { gtkLaunch, e.Id }.Concat(args).ToList(), env) is null or 0)
                return new LaunchResult(true, $"Opened {display} ({e.Id})", null);
        }
        var gio = _findTool("gio");
        if (gio != null && RunHelper(new[] { gio, "launch", e.FilePath }.Concat(args).ToList(), env) is null or 0)
            return new LaunchResult(true, $"Opened {display} ({e.Id})", null);

        var command = DesktopEntries.BuildExecCommand(e.Exec!, args);
        if (command.Length == 0) return new LaunchResult(false, $"{display} ({e.Id}) has no command to run.", null);
        var argv = new List<string> { "/bin/sh", "-c", command };
        if (e.Terminal)
        {
            var terminal = TerminalPrefix();
            if (terminal == null)
                return new LaunchResult(false, $"{display} ({e.Id}) runs in a terminal, and no terminal emulator was found (install one such as xterm, or set up x-terminal-emulator).", null);
            argv.InsertRange(0, terminal);
        }
        return StartDetachedChecked(argv, Home, env, $"{display} ({e.Id})");
    }

    /// <summary>The terminal to wrap Terminal=true entries in when gtk-launch and gio are not available.</summary>
    private List<string>? TerminalPrefix()
    {
        if (_findTool("xdg-terminal-exec") is { } xte) return new List<string> { xte };
        foreach (var (name, flag) in new[] { ("x-terminal-emulator", "-e"), ("gnome-terminal", "--"), ("konsole", "-e"), ("xfce4-terminal", "-x"), ("kitty", ""), ("alacritty", "-e"), ("foot", ""), ("xterm", "-e") })
        {
            if (_findTool(name) is { } path) return flag.Length == 0 ? new List<string> { path } : new List<string> { path, flag };
        }
        return null;
    }

    private LaunchResult StartProgram(string path, IReadOnlyList<string> args, bool allowElevation, string workingDirectory)
    {
        var display = System.IO.Path.GetFileName(path);
        if (!allowElevation && ProgramElevationReason(path, _elevatedTargets()) is { } why)
            return Refused(display, why);
        var argv = new List<string> { path };
        argv.AddRange(args);
        return StartDetachedChecked(argv, workingDirectory, ChildEnvironment(), $"{display} ({path})");
    }

    /// <summary>Starts detached, then watches a moment for an immediate failure (program missing or not executable).</summary>
    private static LaunchResult StartDetachedChecked(IReadOnlyList<string> argv, string workingDirectory, IReadOnlyDictionary<string, string?> env, string description)
    {
        using var p = LinuxTools.StartDetached(argv, workingDirectory, env);
        int pid = p.Id;
        if (p.WaitForExit((int)EarlyExitWindow.TotalMilliseconds))
        {
            int code;
            try { code = p.ExitCode; } catch (InvalidOperationException) { code = 0; }
            if (code is 126 or 127)
                return new LaunchResult(false, $"Could not start {description}: the program was not found or is not executable (exit code {code}).", null);
            // Many programs hand off to a running instance and exit at once; that is a successful open.
            return new LaunchResult(true, $"Opened {description}", null);
        }
        return new LaunchResult(true, $"Opened {description}", pid);
    }

    /// <summary>Opens a URL, file or folder with the user's default app (xdg-open, else gio open).</summary>
    private LaunchResult OpenWithDefaultApp(string target, string description, string note = "")
    {
        var env = ChildEnvironment();
        var xdgOpen = _findTool("xdg-open");
        if (xdgOpen != null)
        {
            var code = RunHelper(new[] { xdgOpen, target }, env);
            if (code is null or 0) return new LaunchResult(true, $"Opened {description}{note}", null);
            var gioFallback = _findTool("gio");
            if (gioFallback != null && RunHelper(new[] { gioFallback, "open", target }, env) is null or 0)
                return new LaunchResult(true, $"Opened {description}{note}", null);
            return new LaunchResult(false, XdgOpenError(code.Value, target), null);
        }
        var gio = _findTool("gio");
        if (gio != null)
        {
            var code = RunHelper(new[] { gio, "open", target }, env);
            if (code is null or 0) return new LaunchResult(true, $"Opened {description}{note}", null);
            return new LaunchResult(false, $"Could not open '{target}': gio open failed (exit code {code}). There may be no app set up for this kind of file or link.", null);
        }
        return new LaunchResult(false,
            "Opening files and links needs xdg-open (package xdg-utils) or gio (package libglib2.0-bin or glib2). Install one with your package manager.",
            null);
    }

    internal static string XdgOpenError(int code, string target) => code switch
    {
        2 => $"Could not open '{target}': it does not exist or cannot be read (xdg-open exit code 2).",
        3 => $"Could not open '{target}': no app is set up to open it (xdg-open exit code 3).",
        4 => $"Could not open '{target}': the app that should open it failed to start (xdg-open exit code 4).",
        _ => $"Could not open '{target}' (xdg-open exit code {code}).",
    };

    /// <summary>
    /// Runs a launcher helper (xdg-open, gtk-launch, gio) detached and waits for it to finish. Returns its exit code, or
    /// null when it is still running after the timeout (some xdg-open setups keep running as long as the app does,
    /// which counts as success).
    /// </summary>
    private static int? RunHelper(IReadOnlyList<string> argv, IReadOnlyDictionary<string, string?> env)
    {
        using var p = LinuxTools.StartDetached(argv, LinuxTools.HomeDirectory(), env);
        if (!p.WaitForExit((int)HelperTimeout.TotalMilliseconds)) return null;
        try { return p.ExitCode; } catch (InvalidOperationException) { return null; }
    }

    /// <summary>
    /// The XDG folder variables the launched program gets: the ones this launcher searched (they differ from the
    /// process environment only in tests), so gtk-launch resolves the same desktop IDs.
    /// </summary>
    private IReadOnlyDictionary<string, string?> ChildEnvironment()
    {
        var d = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var name in new[] { "XDG_DATA_HOME", "XDG_DATA_DIRS" })
        {
            var mine = _env(name);
            if (!string.Equals(mine, Environment.GetEnvironmentVariable(name), StringComparison.Ordinal)) d[name] = string.IsNullOrEmpty(mine) ? null : mine;
        }
        return d;
    }

    private static LaunchResult Refused(string display, string reason) => new(false,
        $"{display} {reason}, and DeskPilot does not start programs with administrator (root) rights while Administrator mode is off. " +
        "Ask the user to start it themselves, or to enable Administrator mode in DeskPilot settings.",
        null);

    // ------------------------------------------------------------------ elevation checks

    [GeneratedRegex(@"(?:^|[\s;&|'""(`])(?:\S*/)?(?<cmd>pkexec|sudo|sudoedit|gksu|gksudo|kdesu|kdesudo|doas|su|run0|beesu|lxsudo|lxqt-sudo)(?=$|[\s;&|'"")`])", RegexOptions.CultureInvariant)]
    private static partial Regex EscalationInExecRegex();

    /// <summary>Why a desktop entry would run as root, or null when it would not.</summary>
    internal static string? ElevationReason(DesktopEntry e, IEnumerable<string> elevatedTargets)
    {
        if (e.SubstituteUid) return "is set up to run as another user (X-KDE-SubstituteUID)";
        if (!string.IsNullOrWhiteSpace(e.Exec))
        {
            var m = EscalationInExecRegex().Match(e.Exec);
            if (m.Success) return $"asks for root rights through {m.Groups["cmd"].Value}";
        }
        var targets = elevatedTargets.ToList();
        var hit = SafetyGuard.MatchElevatedLaunchTarget(e.ExecProgram, null, targets)
                  ?? SafetyGuard.MatchElevatedLaunchTarget(e.IdStem, null, targets);
        return hit != null ? $"always asks for root rights ('{hit}' is in the elevated launch targets list)" : null;
    }

    /// <summary>Why starting a program file would run as root, or null.</summary>
    internal static string? ProgramElevationReason(string path, IEnumerable<string> elevatedTargets)
    {
        var name = System.IO.Path.GetFileName(path);
        if (EscalationCommands.Contains(name)) return $"is {name}, which asks for root rights";
        var hit = SafetyGuard.MatchElevatedLaunchTarget(name, null, elevatedTargets);
        return hit != null ? $"always asks for root rights ('{hit}' is in the elevated launch targets list)" : null;
    }
}

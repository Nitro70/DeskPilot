using System.Text;

namespace DeskPilot.Linux.Services;

/// <summary>
/// "Start when I log in": an XDG autostart entry, <c>$XDG_CONFIG_HOME/autostart/deskpilot.desktop</c>
/// (<c>~/.config/autostart</c> when the variable is not set), that starts DeskPilot minimized. GNOME, KDE,
/// Xfce, Cinnamon, MATE, LXQt and most other desktops read it; no root rights are needed. The folder and the
/// program path are injectable so tests never touch the real autostart folder.
/// </summary>
public sealed class LinuxAutostart
{
    public const string FileName = "deskpilot.desktop";
    public const string MinimizedArgument = "--minimized";

    private readonly string _directory;
    private readonly Func<string?> _exePath;

    /// <param name="autostartDirectory">The autostart folder; null = the user's XDG autostart folder.</param>
    /// <param name="exePath">The program to start; defaults to the running executable.</param>
    public LinuxAutostart(string? autostartDirectory = null, Func<string?>? exePath = null)
    {
        _directory = string.IsNullOrWhiteSpace(autostartDirectory) ? DefaultDirectory() : autostartDirectory;
        _exePath = exePath ?? (() => Environment.ProcessPath);
    }

    /// <summary>The real autostart entry for the running DeskPilot.</summary>
    public static LinuxAutostart Default => new();

    public string Directory => _directory;

    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>$XDG_CONFIG_HOME/autostart when that is an absolute path, otherwise ~/.config/autostart.</summary>
    public static string DefaultDirectory() => DefaultDirectory(Environment.GetEnvironmentVariable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public static string DefaultDirectory(Func<string, string?> env, string home)
    {
        var config = env("XDG_CONFIG_HOME")?.Trim();
        // The XDG spec says a relative value is invalid and must be ignored.
        if (string.IsNullOrEmpty(config) || !config.StartsWith('/')) config = Path.Combine(home, ".config");
        return Path.Combine(config, "autostart");
    }

    /// <summary>The Exec line value for this program, or null when it cannot be registered (no executable, or running under the dotnet host).</summary>
    public string? ExpectedExec
    {
        get
        {
            var exe = _exePath();
            if (string.IsNullOrWhiteSpace(exe)) return null;
            if (string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase)) return null;
            return BuildExec(exe);
        }
    }

    /// <summary>"/opt/DeskPilot/deskpilot" --minimized, quoted the way the Desktop Entry spec wants it.</summary>
    public static string BuildExec(string exePath) => QuoteExecArgument(exePath) + " " + MinimizedArgument;

    /// <summary>
    /// Quotes one Exec argument: wrapped in double quotes, with ", `, $ and \ escaped by a backslash, and then
    /// (because Exec is also a string value) every backslash doubled again. A literal % is written %%.
    /// </summary>
    public static string QuoteExecArgument(string argument)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in argument)
        {
            switch (c)
            {
                case '"' or '`' or '$':
                    sb.Append("\\\\").Append(c);
                    break;
                case '\\':
                    sb.Append("\\\\\\\\");
                    break;
                case '%':
                    sb.Append("%%");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The whole .desktop file for a program path.</summary>
    public static string BuildDesktopEntry(string exePath) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        "Name=DeskPilot\n" +
        "Comment=Let an AI model operate your computer\n" +
        "Exec=" + BuildExec(exePath) + "\n" +
        "Icon=deskpilot\n" +
        "Terminal=false\n" +
        "Categories=Utility;\n" +
        "X-GNOME-Autostart-enabled=true\n";

    /// <summary>The Exec value of the existing entry, or null when there is no entry.</summary>
    public string? GetRegisteredExec()
    {
        if (!File.Exists(FilePath)) return null;
        foreach (var raw in File.ReadLines(FilePath))
        {
            var line = raw.Trim();
            if (line.StartsWith("Exec=", StringComparison.Ordinal)) return line[5..];
        }
        return "";
    }

    public bool IsEnabled() => File.Exists(FilePath);

    /// <summary>True when the entry exists and starts this program.</summary>
    public bool IsCurrent()
    {
        var expected = ExpectedExec;
        return expected != null && string.Equals(GetRegisteredExec(), expected, StringComparison.Ordinal);
    }

    public void Enable()
    {
        var exe = _exePath();
        if (ExpectedExec == null || exe == null)
            throw new InvalidOperationException("\"Start when I log in\" needs the deskpilot executable; it cannot be set up while running under the dotnet host.");
        System.IO.Directory.CreateDirectory(_directory);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, BuildDesktopEntry(exe), new UTF8Encoding(false));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public void Disable()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Enable();
        else Disable();
    }

    /// <summary>
    /// Makes the autostart folder match the setting: writes the entry, rewrites it when the program moved, or
    /// removes it. Returns true when something was changed.
    /// </summary>
    public bool Sync(bool enabled)
    {
        if (enabled)
        {
            if (IsCurrent()) return false;
            Enable();
            return true;
        }
        if (!IsEnabled()) return false;
        Disable();
        return true;
    }
}

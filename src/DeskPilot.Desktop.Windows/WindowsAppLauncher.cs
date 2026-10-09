using System.ComponentModel;
using System.Diagnostics;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Desktop.Windows;

/// <summary>
/// Opens apps, files, folders and URLs. Never uses the "runas" verb. When elevation is not allowed, programs
/// are started with CreateProcess, which refuses (ERROR_ELEVATION_REQUIRED) instead of raising a UAC prompt.
/// </summary>
public sealed class WindowsAppLauncher : IAppLauncher
{
    private const int ErrorElevationRequired = 740;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorNoAssociation = 1155;
    private static readonly TimeSpan AppsFolderTimeout = TimeSpan.FromSeconds(6);

    public LaunchResult Launch(string target, string? arguments, bool allowElevation)
    {
        if (string.IsNullOrWhiteSpace(target)) return new LaunchResult(false, "No launch target given.", null);
        arguments = string.IsNullOrWhiteSpace(arguments) ? null : arguments.Trim();

        LaunchTarget classified;
        try { classified = AppTargetClassifier.Classify(target); }
        catch (ArgumentException ex) { return new LaunchResult(false, ex.Message, null); }

        try
        {
            return classified.Kind switch
            {
                LaunchKind.Uri => ShellOpen(classified.Value, null, null, classified.Value),
                LaunchKind.Path => OpenPath(classified.Value, arguments, allowElevation),
                LaunchKind.MissingPath => new LaunchResult(false, $"Nothing exists at '{classified.Value}'. Check the path (list the folder with run_command or open its parent folder).", null),
                LaunchKind.Command => StartProgram(classified.Value, arguments, allowElevation),
                _ => LaunchByName(classified.Value, arguments, allowElevation),
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
    }

    private static LaunchResult OpenPath(string path, string? arguments, bool allowElevation)
    {
        if (Directory.Exists(path)) return ShellOpen(path, null, path, $"folder {path}");
        // Like a double-click in Explorer: a program started by path runs in its own folder.
        if (IsExe(path)) return StartProgram(path, arguments, allowElevation, Path.GetDirectoryName(path));
        return ShellOpen(path, arguments, Path.GetDirectoryName(path), $"{Path.GetFileName(path)} ({path})");
    }

    private static LaunchResult LaunchByName(string name, string? arguments, bool allowElevation)
    {
        var shortcuts = StartMenuSearch.EnumerateShortcuts(StartMenuSearch.DefaultShortcutRoots());
        var best = StartMenuSearch.FindBest(shortcuts, name);
        if (best is not { Tier: StartMenuSearch.MatchTier.Exact })
        {
            var apps = StartMenuSearch.EnumerateAppsFolder(AppsFolderTimeout);
            // Shortcuts first so a tie prefers the .lnk, which can take arguments.
            best = StartMenuSearch.FindBest(shortcuts.Concat(apps), name);
        }

        if (best is { } hit)
        {
            var entry = hit.Entry;
            if (entry.ShortcutPath != null)
                return ShellOpen(entry.ShortcutPath, arguments, null, $"{entry.Name} (Start menu shortcut)");
            if (entry.AppUserModelId != null)
                return OpenAppsFolderItem(entry, arguments);
        }

        // Last resort: let the shell try to make sense of it (registered app names, protocol aliases...).
        try
        {
            return ShellOpen(name, arguments, null, name);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode is ErrorFileNotFound or ErrorPathNotFound or ErrorNoAssociation)
        {
            return new LaunchResult(false,
                $"Could not find an app, file, folder or URL called '{name}'. Try the exact name shown in the Start menu, a full path, or press the win key and search for it.",
                null);
        }
    }

    private static LaunchResult OpenAppsFolderItem(StartMenuEntry entry, string? arguments)
    {
        var aumid = entry.AppUserModelId!;
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var psi = new ProcessStartInfo(File.Exists(explorer) ? explorer : "explorer.exe", "shell:AppsFolder\\" + aumid)
        {
            UseShellExecute = false,
            WorkingDirectory = UserProfile(),
        };
        using var p = Process.Start(psi);
        var note = arguments != null ? " Arguments are not supported for this kind of app and were ignored." : "";
        // explorer.exe hands the activation off and exits, so there is no useful process id.
        return new LaunchResult(true, $"Opened {entry.Name} (app {aumid}).{note}", null);
    }

    /// <summary>Starts a program file. Without allowElevation this goes through CreateProcess so no UAC prompt can appear.</summary>
    private static LaunchResult StartProgram(string path, string? arguments, bool allowElevation, string? workingDirectory = null)
    {
        var display = DisplayName(path);
        // A bare command (Win+R style) starts in the user's profile folder.
        var cwd = !string.IsNullOrEmpty(workingDirectory) && Directory.Exists(workingDirectory) ? workingDirectory : UserProfile();
        if (!IsExe(path) || allowElevation)
            return ShellOpen(path, arguments, cwd, $"{display} ({path})");

        var psi = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            WorkingDirectory = cwd,
        };
        if (arguments != null) psi.Arguments = arguments;
        try
        {
            using var p = Process.Start(psi);
            return new LaunchResult(true, $"Opened {display} ({path})", SafeId(p));
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorElevationRequired)
        {
            return new LaunchResult(false,
                $"{display} needs administrator rights, and DeskPilot does not start elevated programs while administrator mode is off. Ask the user to start it themselves or to enable administrator mode.",
                null);
        }
    }

    private static LaunchResult ShellOpen(string file, string? arguments, string? workingDirectory, string description)
    {
        var psi = new ProcessStartInfo(file)
        {
            UseShellExecute = true,
            WorkingDirectory = workingDirectory ?? UserProfile(),
        };
        if (arguments != null) psi.Arguments = arguments;
        using var p = Process.Start(psi);
        return new LaunchResult(true, $"Opened {description}", SafeId(p));
    }

    private static int? SafeId(Process? p)
    {
        if (p == null) return null;
        try { return p.Id; }
        catch (InvalidOperationException) { return null; }
    }

    private static bool IsExe(string path) =>
        path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".com", StringComparison.OrdinalIgnoreCase);

    internal static string DisplayName(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            var name = !string.IsNullOrWhiteSpace(info.FileDescription) ? info.FileDescription : info.ProductName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch (Exception ex) when (ex is FileNotFoundException or ArgumentException or IOException) { }
        return Path.GetFileNameWithoutExtension(path);
    }

    private static string UserProfile()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(home) ? home : Path.GetTempPath();
    }
}

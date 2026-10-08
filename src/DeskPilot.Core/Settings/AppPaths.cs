namespace DeskPilot.Core.Settings;

/// <summary>
/// Where DeskPilot keeps its files. Portable mode: if a file named "portable.txt" sits next
/// to the exe, everything is stored in a "DeskPilotData" folder beside the exe instead.
/// </summary>
public static class AppPaths
{
    public const string AppName = "DeskPilot";

    private static string? _overrideRoot;

    /// <summary>Tests use this to redirect all storage to a temp folder.</summary>
    public static void OverrideRoot(string? root) => _overrideRoot = root;

    public static string ExeDirectory =>
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static bool IsPortable => _overrideRoot == null && File.Exists(Path.Combine(ExeDirectory, "portable.txt"));

    public static string DataRoot
    {
        get
        {
            if (_overrideRoot != null) return _overrideRoot;
            if (IsPortable) return Path.Combine(ExeDirectory, "DeskPilotData");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        }
    }

    public static string LocalRoot
    {
        get
        {
            if (_overrideRoot != null) return Path.Combine(_overrideRoot, "local");
            if (IsPortable) return Path.Combine(ExeDirectory, "DeskPilotData", "local");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
        }
    }

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string LogsDirectory => EnsureDir(Path.Combine(LocalRoot, "logs"));
    /// <summary>Empty working directory for CLI agents so they never pick up a project's files or instructions.</summary>
    public static string AgentWorkDirectory => EnsureDir(Path.Combine(LocalRoot, "agent-workdir"));
    public static string TempDirectory => EnsureDir(Path.Combine(LocalRoot, "temp"));

    public static string EnsureDir(string path)
    {
        Directory.CreateDirectory(path);
        return path;
    }
}

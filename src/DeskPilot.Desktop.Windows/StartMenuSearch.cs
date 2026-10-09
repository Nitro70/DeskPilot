namespace DeskPilot.Desktop.Windows;

/// <summary>A launchable Start-menu entry: a shortcut file, or an app in shell:AppsFolder identified by its AUMID.</summary>
internal sealed record StartMenuEntry(string Name, string? ShortcutPath, string? AppUserModelId);

/// <summary>Finds apps by display name in the Start menu shortcuts and the shell's Apps folder (Store/UWP apps).</summary>
internal static class StartMenuSearch
{
    public enum MatchTier { Exact = 0, StartsWith = 1, Contains = 2, AllWords = 3, None = 99 }

    public static IEnumerable<string> DefaultShortcutRoots()
    {
        foreach (var folder in new[] { Environment.SpecialFolder.Programs, Environment.SpecialFolder.CommonPrograms })
        {
            var p = Environment.GetFolderPath(folder);
            if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) yield return p;
        }
    }

    /// <summary>All .lnk / .url files under the roots (read-only directory walk).</summary>
    public static List<StartMenuEntry> EnumerateShortcuts(IEnumerable<string> roots)
    {
        var list = new List<StartMenuEntry>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            MaxRecursionDepth = 8,
            AttributesToSkip = FileAttributes.System,
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*", options).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f);
                if (!ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".url", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(new StartMenuEntry(Path.GetFileNameWithoutExtension(f), f, null));
            }
        }
        return list;
    }

    /// <summary>
    /// Apps in shell:AppsFolder (what the Start menu's "All apps" shows, including Store apps) via the
    /// Shell.Application COM object on an STA thread. Returns an empty list when the shell is unavailable.
    /// </summary>
    public static List<StartMenuEntry> EnumerateAppsFolder(TimeSpan timeout)
    {
        try
        {
            return StaThread.Run(() =>
            {
                var list = new List<StartMenuEntry>();
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) return list;
                dynamic? shell = Activator.CreateInstance(type);
                if (shell == null) return list;
                try
                {
                    dynamic? folder = shell.NameSpace("shell:AppsFolder");
                    if (folder == null) return list;
                    foreach (dynamic item in folder.Items())
                    {
                        string? name = item.Name;
                        string? path = item.Path;
                        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(path)) list.Add(new StartMenuEntry(name, null, path));
                    }
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell);
                }
                return list;
            }, timeout, "list the Start menu apps");
        }
        catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException or InvalidCastException
                                       or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidOperationException)
        {
            return new List<StartMenuEntry>();
        }
    }

    public static MatchTier Match(string candidate, string query)
    {
        var c = candidate.Trim();
        var q = query.Trim();
        if (c.Length == 0 || q.Length == 0) return MatchTier.None;
        if (c.Equals(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.Exact;
        if (c.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.StartsWith;
        if (c.Contains(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.Contains;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => c.Contains(w, StringComparison.OrdinalIgnoreCase))) return MatchTier.AllWords;
        return MatchTier.None;
    }

    /// <summary>
    /// Best match: lowest tier, then the shortest name (closest to the query), then the earlier entry
    /// (shortcuts are listed before Apps-folder items). Uninstallers only match when the query asks for one.
    /// </summary>
    public static (StartMenuEntry Entry, MatchTier Tier)? FindBest(IEnumerable<StartMenuEntry> entries, string query)
    {
        bool wantsUninstall = query.Contains("uninstall", StringComparison.OrdinalIgnoreCase);
        (StartMenuEntry Entry, MatchTier Tier)? best = null;
        int index = 0, bestIndex = 0;
        foreach (var e in entries)
        {
            index++;
            if (!wantsUninstall && IsNoise(e.Name)) continue;
            var tier = Match(e.Name, query);
            if (tier == MatchTier.None) continue;
            if (best is null || IsBetter(e, tier, index, best.Value.Entry, best.Value.Tier, bestIndex))
            {
                best = (e, tier);
                bestIndex = index;
            }
        }
        return best;
    }

    private static bool IsBetter(StartMenuEntry e, MatchTier tier, int index, StartMenuEntry current, MatchTier currentTier, int currentIndex)
    {
        if (tier != currentTier) return tier < currentTier;
        if (e.Name.Length != current.Name.Length) return e.Name.Length < current.Name.Length;
        return index < currentIndex;
    }

    private static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("Uninst", StringComparison.OrdinalIgnoreCase);
}

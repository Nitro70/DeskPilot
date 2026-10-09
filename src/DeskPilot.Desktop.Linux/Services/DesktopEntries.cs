using System.Text;

namespace DeskPilot.Desktop.Linux.Services;

/// <summary>One parsed [Desktop Entry] of a .desktop file (only the keys the launcher needs).</summary>
internal sealed class DesktopEntry
{
    /// <summary>Desktop file ID, e.g. "org.gnome.TextEditor.desktop" (subfolders joined with '-').</summary>
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    /// <summary>Index of the applications folder it came from (lower = higher precedence).</summary>
    public int DirectoryIndex { get; init; }
    /// <summary>True when the folder is one GLib also searches (from XDG_DATA_HOME / XDG_DATA_DIRS), so gtk-launch finds the ID.</summary>
    public bool InXdgDirectory { get; init; }

    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Name for the current locale, when the file has one.</summary>
    public string? LocalizedName { get; set; }
    /// <summary>Every Name[xx] value in the file.</summary>
    public List<string> AllLocalizedNames { get; } = new();
    public string? GenericName { get; set; }
    public string? LocalizedGenericName { get; set; }
    public List<string> Keywords { get; } = new();
    public string? Exec { get; set; }
    public string? TryExec { get; set; }
    public string? Icon { get; set; }
    public bool NoDisplay { get; set; }
    public bool Hidden { get; set; }
    public bool Terminal { get; set; }
    public bool SubstituteUid { get; set; }
    public List<string> OnlyShowIn { get; } = new();
    public List<string> NotShowIn { get; } = new();

    /// <summary>The name to show: localized when available.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(LocalizedName) ? LocalizedName! : Name;

    /// <summary>The ID without ".desktop".</summary>
    public string IdStem => Id.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase) ? Id[..^8] : Id;

    /// <summary>File name of the program Exec starts (after "env VAR=x" prefixes), e.g. "gnome-text-editor".</summary>
    public string? ExecProgram
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Exec)) return null;
            var words = LinuxTools.SplitArguments(Exec);
            int i = 0;
            if (i < words.Count && Path.GetFileName(words[i]) == "env")
            {
                i++;
                while (i < words.Count && (words[i].StartsWith('-') || words[i].Contains('='))) i++;
            }
            return i < words.Count ? Path.GetFileName(words[i]) : null;
        }
    }
}

/// <summary>Reads .desktop files from the XDG application folders and ranks them against an app name.</summary>
internal static class DesktopEntries
{
    public enum MatchTier { Exact = 0, StartsWith = 1, Contains = 2, AllWords = 3, None = 99 }

    private const int MaxFiles = 6000;
    private const int MaxDepth = 4;

    /// <summary>
    /// The applications folders in precedence order: $XDG_DATA_HOME/applications, each $XDG_DATA_DIRS/applications
    /// (default /usr/local/share:/usr/share), then the Flatpak and Snap export folders when not already listed.
    /// XdgCount is how many of them came from the XDG variables.
    /// </summary>
    public static (IReadOnlyList<string> Directories, int XdgCount) ApplicationDirectories(Func<string, string?> env, string home)
    {
        var dirs = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? dir)
        {
            if (string.IsNullOrWhiteSpace(dir)) return;
            var d = dir.Trim();
            if (!d.StartsWith('/')) return;
            d = d.TrimEnd('/');
            if (d.Length == 0) return;
            if (seen.Add(d)) dirs.Add(d);
        }

        var dataHome = env("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !dataHome.Trim().StartsWith('/')) dataHome = home.TrimEnd('/') + "/.local/share";
        Add(dataHome.Trim().TrimEnd('/') + "/applications");

        var dataDirs = env("XDG_DATA_DIRS");
        if (string.IsNullOrWhiteSpace(dataDirs)) dataDirs = "/usr/local/share:/usr/share";
        foreach (var d in dataDirs.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (d.StartsWith('/')) Add(d.TrimEnd('/') + "/applications");
        }
        int xdgCount = dirs.Count;

        Add("/var/lib/flatpak/exports/share/applications");
        Add(home.TrimEnd('/') + "/.local/share/flatpak/exports/share/applications");
        Add("/var/lib/snapd/desktop/applications");
        return (dirs, xdgCount);
    }

    /// <summary>
    /// Reads every .desktop file under the folders. The first file with a given ID wins, as in the spec; a Hidden
    /// entry in a higher-precedence folder hides the ID entirely, so it is left out.
    /// </summary>
    public static List<DesktopEntry> Load(IReadOnlyList<string> directories, int xdgCount, IReadOnlyList<string> locales)
    {
        var byId = new Dictionary<string, DesktopEntry?>(StringComparer.Ordinal);
        var order = new List<string>();
        int files = 0;
        for (int i = 0; i < directories.Count; i++)
        {
            var root = directories[i];
            foreach (var (path, id) in Enumerate(root))
            {
                if (++files > MaxFiles) break;
                if (byId.ContainsKey(id)) continue;
                DesktopEntry? entry = null;
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length <= 512 * 1024)
                        entry = Parse(File.ReadAllText(path), id, path, i, i < xdgCount, locales);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                byId[id] = entry;
                order.Add(id);
            }
        }
        return order.Select(id => byId[id]).Where(e => e is { Hidden: false } && e.Type == "Application").Select(e => e!).ToList();
    }

    private static IEnumerable<(string Path, string Id)> Enumerate(string root)
    {
        if (!Directory.Exists(root)) yield break;
        var stack = new Stack<(string Dir, string Prefix, int Depth)>();
        stack.Push((root, "", 0));
        while (stack.Count > 0)
        {
            var (dir, prefix, depth) = stack.Pop();
            string[] files, subdirs;
            try
            {
                files = Directory.GetFiles(dir, "*.desktop");
                subdirs = depth < MaxDepth ? Directory.GetDirectories(dir) : Array.Empty<string>();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            Array.Sort(files, StringComparer.Ordinal);
            foreach (var f in files) yield return (f, prefix + Path.GetFileName(f));
            Array.Sort(subdirs, StringComparer.Ordinal);
            for (int i = subdirs.Length - 1; i >= 0; i--)
                stack.Push((subdirs[i], prefix + Path.GetFileName(subdirs[i]) + "-", depth + 1));
        }
    }

    /// <summary>The locale names to try for Name[xx], most specific first (from LANGUAGE, LC_ALL, LC_MESSAGES, LANG).</summary>
    public static IReadOnlyList<string> CurrentLocales(Func<string, string?> env)
    {
        var raw = new List<string>();
        var language = env("LANGUAGE");
        if (!string.IsNullOrWhiteSpace(language)) raw.AddRange(language.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        foreach (var name in new[] { "LC_ALL", "LC_MESSAGES", "LANG" })
        {
            var v = env(name);
            if (!string.IsNullOrWhiteSpace(v)) { raw.Add(v.Trim()); break; }
        }
        var result = new List<string>();
        foreach (var r in raw)
        {
            foreach (var variant in LocaleVariants(r))
                if (!result.Contains(variant)) result.Add(variant);
        }
        return result;
    }

    /// <summary>lang_COUNTRY.ENCODING@MODIFIER gives lang_COUNTRY@MODIFIER, lang_COUNTRY, lang@MODIFIER, lang.</summary>
    internal static IEnumerable<string> LocaleVariants(string locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale is "C" or "POSIX" || locale.StartsWith("C.", StringComparison.Ordinal)) yield break;
        string modifier = "";
        int at = locale.IndexOf('@');
        if (at >= 0) { modifier = locale[at..]; locale = locale[..at]; }
        int dot = locale.IndexOf('.');
        if (dot >= 0) locale = locale[..dot];
        int us = locale.IndexOf('_');
        string lang = us >= 0 ? locale[..us] : locale;
        if (lang.Length == 0) yield break;
        if (us >= 0 && modifier.Length > 0) yield return locale + modifier;
        if (us >= 0) yield return locale;
        if (modifier.Length > 0) yield return lang + modifier;
        yield return lang;
    }

    /// <summary>Parses the [Desktop Entry] group. Returns null for files without one.</summary>
    public static DesktopEntry? Parse(string text, string id, string path, int directoryIndex, bool inXdgDirectory, IReadOnlyList<string> locales)
    {
        var entry = new DesktopEntry { Id = id, FilePath = path, DirectoryIndex = directoryIndex, InXdgDirectory = inXdgDirectory };
        bool inGroup = false, sawGroup = false;
        int nameRank = int.MaxValue, genericRank = int.MaxValue, keywordsRank = int.MaxValue;
        List<string>? localizedKeywords = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            if (line[0] == '[')
            {
                inGroup = line == "[Desktop Entry]";
                if (inGroup) sawGroup = true;
                continue;
            }
            if (!inGroup) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            string? locale = null;
            int bracket = key.IndexOf('[');
            if (bracket > 0 && key.EndsWith(']'))
            {
                locale = key[(bracket + 1)..^1];
                key = key[..bracket];
            }

            if (locale != null)
            {
                int rank = IndexOf(locales, locale);
                switch (key)
                {
                    case "Name":
                        var localized = Unescape(value);
                        if (localized.Length > 0) entry.AllLocalizedNames.Add(localized);
                        if (rank >= 0 && rank < nameRank) { nameRank = rank; entry.LocalizedName = localized; }
                        break;
                    case "GenericName":
                        if (rank >= 0 && rank < genericRank) { genericRank = rank; entry.LocalizedGenericName = Unescape(value); }
                        break;
                    case "Keywords":
                        if (rank >= 0 && rank < keywordsRank) { keywordsRank = rank; localizedKeywords = SplitList(value); }
                        break;
                }
                continue;
            }

            switch (key)
            {
                case "Type": entry.Type = Unescape(value); break;
                case "Name": entry.Name = Unescape(value); break;
                case "GenericName": entry.GenericName = Unescape(value); break;
                case "Keywords": entry.Keywords.AddRange(SplitList(value)); break;
                case "Exec": entry.Exec = Unescape(value); break;
                case "TryExec": entry.TryExec = Unescape(value); break;
                case "Icon": entry.Icon = Unescape(value); break;
                case "NoDisplay": entry.NoDisplay = IsTrue(value); break;
                case "Hidden": entry.Hidden = IsTrue(value); break;
                case "Terminal": entry.Terminal = IsTrue(value); break;
                case "X-KDE-SubstituteUID": entry.SubstituteUid = IsTrue(value); break;
                case "OnlyShowIn": entry.OnlyShowIn.AddRange(SplitList(value)); break;
                case "NotShowIn": entry.NotShowIn.AddRange(SplitList(value)); break;
            }
        }
        if (!sawGroup) return null;
        if (localizedKeywords != null)
        {
            foreach (var k in localizedKeywords)
                if (!entry.Keywords.Contains(k, StringComparer.OrdinalIgnoreCase)) entry.Keywords.Add(k);
        }
        return entry;
    }

    private static int IndexOf(IReadOnlyList<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
            if (string.Equals(list[i], value, StringComparison.Ordinal)) return i;
        return -1;
    }

    private static bool IsTrue(string value) => value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) || value.Trim() == "1";

    /// <summary>Undoes the desktop-entry string escapes \s \n \t \r \\ (other sequences are kept as they are).</summary>
    internal static string Unescape(string value)
    {
        if (value.IndexOf('\\') < 0) return value;
        var sb = new StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (c == '\\' && i + 1 < value.Length)
            {
                char n = value[i + 1];
                switch (n)
                {
                    case 's': sb.Append(' '); i++; continue;
                    case 'n': sb.Append('\n'); i++; continue;
                    case 't': sb.Append('\t'); i++; continue;
                    case 'r': sb.Append('\r'); i++; continue;
                    case '\\': sb.Append('\\'); i++; continue;
                }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Splits a ';' list ("\;" is a literal semicolon), dropping empty items.</summary>
    internal static List<string> SplitList(string value)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] == '\\' && i + 1 < value.Length && value[i + 1] == ';') { sb.Append(';'); i++; continue; }
            if (value[i] == ';')
            {
                var item = Unescape(sb.ToString()).Trim();
                if (item.Length > 0) result.Add(item);
                sb.Clear();
                continue;
            }
            sb.Append(value[i]);
        }
        var last = Unescape(sb.ToString()).Trim();
        if (last.Length > 0) result.Add(last);
        return result;
    }

    public static MatchTier Match(string? candidate, string query)
    {
        var c = Collapse(candidate);
        var q = Collapse(query);
        if (c.Length == 0 || q.Length == 0) return MatchTier.None;
        if (c.Equals(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.Exact;
        if (c.StartsWith(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.StartsWith;
        if (c.Contains(q, StringComparison.OrdinalIgnoreCase)) return MatchTier.Contains;
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => c.Contains(w, StringComparison.OrdinalIgnoreCase))) return MatchTier.AllWords;
        return MatchTier.None;
    }

    private static string Collapse(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The query as typed, minus surrounding quotes and a ".desktop" suffix.</summary>
    internal static string NormalizeQuery(string query)
    {
        var q = Collapse(query.Trim().Trim('"', '\''));
        if (q.EndsWith(".desktop", StringComparison.OrdinalIgnoreCase)) q = q[..^8];
        return q;
    }

    /// <summary>A scored match: lower is better on every key.</summary>
    public sealed record Ranked(DesktopEntry Entry, MatchTier Tier, int Field, bool Penalized);

    /// <summary>
    /// The best entry for an app name: exact beats starts-with beats contains beats all-words. Within a tier, a match
    /// on Name beats the ID or program name, which beat GenericName, which beats Keywords; then entries shown in menus
    /// for this desktop beat hidden ones (NoDisplay, OnlyShowIn/NotShowIn), then the shorter name, then folder
    /// precedence. NoDisplay entries only match exactly or by prefix, and keywords never by "contains".
    /// </summary>
    public static Ranked? FindBest(IEnumerable<DesktopEntry> entries, string query, IReadOnlyCollection<string> currentDesktops)
    {
        var q = NormalizeQuery(query);
        if (q.Length == 0) return null;
        Ranked? best = null;
        foreach (var e in entries)
        {
            var r = Score(e, q, currentDesktops);
            if (r == null) continue;
            if (best == null || Compare(r, best) < 0) best = r;
        }
        return best;
    }

    /// <summary>All matches, best first (used by tests and for diagnostics).</summary>
    public static List<Ranked> RankAll(IEnumerable<DesktopEntry> entries, string query, IReadOnlyCollection<string> currentDesktops)
    {
        var q = NormalizeQuery(query);
        var list = entries.Select(e => Score(e, q, currentDesktops)).Where(r => r != null).Select(r => r!).ToList();
        list.Sort(Compare);
        return list;
    }

    private static Ranked? Score(DesktopEntry e, string q, IReadOnlyCollection<string> currentDesktops)
    {
        bool hiddenHere = !ShownIn(e, currentDesktops);
        bool penalized = e.NoDisplay || hiddenHere;
        MatchTier bestTier = MatchTier.None;
        int bestField = int.MaxValue;
        void Consider(string? candidate, int field, MatchTier worstAllowed)
        {
            var t = Match(candidate, q);
            if (t == MatchTier.None || t > worstAllowed) return;
            if (penalized && t > MatchTier.StartsWith) return;
            if (t < bestTier || (t == bestTier && field < bestField)) { bestTier = t; bestField = field; }
        }

        Consider(e.Name, 0, MatchTier.AllWords);
        Consider(e.LocalizedName, 0, MatchTier.AllWords);
        Consider(e.IdStem, 1, MatchTier.Contains);
        // Reverse-DNS IDs: "org.gnome.TextEditor" also answers to "TextEditor".
        var lastDot = e.IdStem.LastIndexOf('.');
        if (lastDot >= 0 && lastDot < e.IdStem.Length - 1) Consider(e.IdStem[(lastDot + 1)..], 1, MatchTier.StartsWith);
        Consider(e.ExecProgram, 1, MatchTier.StartsWith);
        foreach (var n in e.AllLocalizedNames) Consider(n, 1, MatchTier.Exact);
        Consider(e.GenericName, 2, MatchTier.AllWords);
        Consider(e.LocalizedGenericName, 2, MatchTier.AllWords);
        foreach (var k in e.Keywords) Consider(k, 3, MatchTier.StartsWith);

        return bestTier == MatchTier.None ? null : new Ranked(e, bestTier, bestField, penalized);
    }

    private static int Compare(Ranked a, Ranked b)
    {
        int c = a.Tier.CompareTo(b.Tier);
        if (c != 0) return c;
        c = a.Field.CompareTo(b.Field);
        if (c != 0) return c;
        c = a.Penalized.CompareTo(b.Penalized);
        if (c != 0) return c;
        c = a.Entry.DisplayName.Length.CompareTo(b.Entry.DisplayName.Length);
        if (c != 0) return c;
        return a.Entry.DirectoryIndex.CompareTo(b.Entry.DirectoryIndex);
    }

    /// <summary>OnlyShowIn / NotShowIn against $XDG_CURRENT_DESKTOP (an unknown desktop never satisfies OnlyShowIn).</summary>
    internal static bool ShownIn(DesktopEntry e, IReadOnlyCollection<string> currentDesktops)
    {
        if (e.NotShowIn.Count > 0 && e.NotShowIn.Any(d => currentDesktops.Contains(d, StringComparer.OrdinalIgnoreCase))) return false;
        if (e.OnlyShowIn.Count > 0 && !e.OnlyShowIn.Any(d => currentDesktops.Contains(d, StringComparer.OrdinalIgnoreCase))) return false;
        return true;
    }

    public static IReadOnlyCollection<string> CurrentDesktops(Func<string, string?> env)
    {
        var v = env("XDG_CURRENT_DESKTOP");
        return string.IsNullOrWhiteSpace(v)
            ? Array.Empty<string>()
            : v.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    /// Builds the Exec command for /bin/sh: field codes removed (%f %F %u %U %i %c %k and the deprecated ones, %% becomes
    /// %). When arguments are given they replace the file/URL codes (%f/%u take the first, %F/%U all of them), or are
    /// appended when the line has no such code. Arguments are shell-quoted.
    /// </summary>
    public static string BuildExecCommand(string exec, IReadOnlyList<string> arguments)
    {
        var sb = new StringBuilder(exec.Length + 16);
        bool usedArgs = false;
        for (int i = 0; i < exec.Length; i++)
        {
            char c = exec[i];
            if (c != '%' || i + 1 >= exec.Length)
            {
                sb.Append(c);
                continue;
            }
            char code = exec[i + 1];
            i++;
            switch (code)
            {
                case '%':
                    sb.Append('%');
                    break;
                case 'f' or 'u':
                    if (arguments.Count > 0) sb.Append(LinuxTools.ShellQuote(arguments[0]));
                    usedArgs = true;
                    break;
                case 'F' or 'U':
                    if (arguments.Count > 0) sb.Append(string.Join(' ', arguments.Select(LinuxTools.ShellQuote)));
                    usedArgs = true;
                    break;
                case 'i' or 'c' or 'k' or 'd' or 'D' or 'n' or 'N' or 'v' or 'm':
                    break;
                default:
                    // Not a field code: keep it as written.
                    sb.Append('%').Append(code);
                    break;
            }
        }
        // Spaces left where codes were removed are harmless to the shell; collapsing them could change quoted text.
        var command = sb.ToString().Trim();
        if (!usedArgs && arguments.Count > 0) command += " " + string.Join(' ', arguments.Select(LinuxTools.ShellQuote));
        return command.Trim();
    }
}

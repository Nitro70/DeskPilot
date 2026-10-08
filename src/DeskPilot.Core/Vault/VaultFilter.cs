namespace DeskPilot.Core.Vault;

/// <summary>Which files belong to the vault: included extensions and excluded folders (case-insensitive).</summary>
internal sealed class VaultFilter
{
    public const long MaxIndexedFileBytes = 2 * 1024 * 1024;

    private readonly HashSet<string> _extensions;
    private readonly HashSet<string> _excludedNames;
    private readonly List<string> _excludedPrefixes;

    public VaultFilter(IEnumerable<string>? includeExtensions, IEnumerable<string>? excludeFolders)
    {
        _extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in includeExtensions ?? Array.Empty<string>())
        {
            var t = (e ?? "").Trim().TrimStart('*');
            if (t.Length == 0) continue;
            if (!t.StartsWith('.')) t = "." + t;
            _extensions.Add(t.ToLowerInvariant());
        }
        // An empty list would make the vault useless; notes are markdown first.
        if (_extensions.Count == 0) _extensions.Add(".md");

        _excludedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _excludedPrefixes = new List<string>();
        foreach (var f in excludeFolders ?? Array.Empty<string>())
        {
            var t = (f ?? "").Trim().Replace('\\', '/').Trim('/');
            if (t.Length == 0) continue;
            if (t.Contains('/')) _excludedPrefixes.Add(t);
            else _excludedNames.Add(t);
        }
    }

    public IReadOnlyCollection<string> Extensions => _extensions;

    /// <summary>Stable text describing this filter; the index is rebuilt when it changes.</summary>
    public string Signature =>
        string.Join(",", _extensions.OrderBy(x => x, StringComparer.Ordinal)) + "|" +
        string.Join(",", _excludedNames.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal)) + "|" +
        string.Join(",", _excludedPrefixes.Select(x => x.ToLowerInvariant()).OrderBy(x => x, StringComparer.Ordinal));

    public bool IsIncludedFile(string fileName) => _extensions.Contains(Path.GetExtension(fileName));

    /// <summary>True when a directory (by its name and vault-relative path) must not be scanned or shown.</summary>
    public bool IsExcludedDirectory(string name, string relativePath)
    {
        if (_excludedNames.Contains(name)) return true;
        foreach (var p in _excludedPrefixes)
            if (relativePath.Equals(p, StringComparison.OrdinalIgnoreCase) ||
                relativePath.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True when any segment of a vault-relative path (forward slashes) is excluded.</summary>
    public bool IsExcludedRelativePath(string relativePath, out string matched)
    {
        matched = "";
        if (relativePath.Length == 0) return false;
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var prefix = "";
        foreach (var seg in segments)
        {
            prefix = prefix.Length == 0 ? seg : prefix + "/" + seg;
            if (IsExcludedDirectory(seg, prefix))
            {
                matched = prefix;
                return true;
            }
        }
        return false;
    }

    /// <summary>Hidden or system entries, dot-names (".obsidian", ".git"), links and offline cloud placeholders are skipped.</summary>
    public static bool IsSkippedEntry(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.')) return true;
        var attrs = info.Attributes;
        if ((attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0) return true;
        if ((attrs & FileAttributes.ReparsePoint) != 0)
        {
            // Junctions and symlinks are never followed. Other reparse points (cloud-synced folders) are fine.
            try
            {
                if (info.LinkTarget != null) return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                return true;
            }
        }
        // Files that are only in the cloud would be downloaded just to index them.
        const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        if (info is FileInfo && (attrs & (FileAttributes.Offline | RecallOnDataAccess)) != 0) return true;
        return false;
    }
}

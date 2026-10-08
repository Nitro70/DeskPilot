namespace DeskPilot.Core.Vault;

/// <summary>A model-supplied path that was checked to stay inside the vault.</summary>
internal sealed record VaultPath(
    string Relative,        // forward slashes, as the model should refer to it ("" = vault root)
    string LogicalFull,     // root + relative, links not followed
    string RealFull);       // with every symlink/junction followed; what is actually opened

/// <summary>
/// Confines model-supplied paths to the vault folder. Rejects absolute, drive-relative and UNC paths,
/// ".." segments, alternate data streams, device names, wildcards, hidden and excluded segments, and any
/// path whose existing components resolve (through symlinks or junctions) outside the vault root.
/// </summary>
internal static class VaultPathGuard
{
    private static readonly char[] InvalidNameChars = Path.GetInvalidFileNameChars();

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Turns model input into validated relative segments. Accepts / and \, strips leading "./" and "/".</summary>
    public static bool TryNormalize(string? input, bool allowEmpty, out List<string> segments, out string error)
    {
        segments = new List<string>();
        error = "";
        var raw = (input ?? "").Trim();
        if (raw.Length >= 2 && ((raw[0] == '"' && raw[^1] == '"') || (raw[0] == '\'' && raw[^1] == '\''))) raw = raw[1..^1].Trim();

        if (raw.IndexOf('\0') >= 0 || raw.Any(char.IsControl))
        {
            error = "The path contains control characters.";
            return false;
        }

        var p = raw.Replace('\\', '/');
        if (p.StartsWith("//", StringComparison.Ordinal))
        {
            error = "Network (UNC) and device paths are not allowed. Use a path relative to the vault root, e.g. Projects/Note.md.";
            return false;
        }
        if (p.Contains(':'))
        {
            error = "Absolute paths, drive letters and ':' (alternate data streams) are not allowed. Use a path relative to the vault root, e.g. Projects/Note.md.";
            return false;
        }

        // Leading "./" and "/" mean the vault root here.
        while (true)
        {
            if (p.StartsWith("./", StringComparison.Ordinal)) p = p[2..];
            else if (p.StartsWith('/')) p = p[1..];
            else break;
        }
        if (p == ".") p = "";

        foreach (var seg in p.Split('/'))
        {
            if (seg.Length == 0 || seg == ".") continue;
            if (seg.Trim('.', ' ').Length == 0)
            {
                error = "'..' and dot-only path segments are not allowed: paths must stay inside the vault.";
                return false;
            }
            if (seg.EndsWith('.') || seg.EndsWith(' ') || seg.StartsWith(' '))
            {
                error = $"Invalid path segment '{seg}' (names may not start with a space or end with a dot or space).";
                return false;
            }
            if (seg.IndexOfAny(InvalidNameChars) >= 0)
            {
                error = $"Invalid characters in path segment '{seg}' (wildcards and <>\"| are not allowed).";
                return false;
            }
            var dot = seg.IndexOf('.');
            var stem = (dot < 0 ? seg : seg[..dot]).TrimEnd(' ');
            if (ReservedNames.Contains(stem))
            {
                error = $"'{seg}' is a reserved device name.";
                return false;
            }
            segments.Add(seg);
        }

        if (segments.Count == 0 && !allowEmpty)
        {
            error = "A path relative to the vault root is required, e.g. Projects/Note.md.";
            return false;
        }
        return true;
    }

    /// <summary>
    /// Maps validated segments onto the file system under root, following links one component at a time.
    /// Components that do not exist yet are appended as they are (they cannot be links).
    /// </summary>
    public static bool TryResolve(string root, IReadOnlyList<string> segments, VaultFilter filter, bool rejectHidden,
        out VaultPath result, out string error)
    {
        result = null!;
        error = "";
        try
        {
            var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            // Compare fully resolved paths on both sides, so a vault that itself lives below a link still works.
            var realRoot = ResolveExistingChain(rootFull);

            var rel = string.Join('/', segments);
            if (filter.IsExcludedRelativePath(rel, out var excluded))
            {
                error = $"'{excluded}' is excluded from the vault (see the vault's excluded folders setting).";
                return false;
            }

            if (rejectHidden && segments.FirstOrDefault(x => x.StartsWith('.')) is { } dotted)
            {
                error = $"'{dotted}' is hidden (starts with a dot) and not part of the vault.";
                return false;
            }

            var current = realRoot;
            var exists = true;
            for (var i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var next = Path.Combine(current, seg);
                if (!exists)
                {
                    current = next;
                    continue;
                }

                FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
                if (info == null)
                {
                    exists = false;
                    current = next;
                    continue;
                }

                if (rejectHidden && (info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                {
                    error = $"'{seg}' is hidden and not part of the vault.";
                    return false;
                }

                if (info.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target == null)
                    {
                        error = $"'{seg}' is a link that cannot be resolved.";
                        return false;
                    }
                    var targetFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target.FullName));
                    // The target may itself sit below another link; resolve its parent chain too.
                    var targetReal = ResolveExistingChain(targetFull);
                    if (!IsUnder(targetReal, realRoot))
                    {
                        error = $"'{string.Join('/', segments.Take(i + 1))}' is a link that points outside the vault. Access refused.";
                        return false;
                    }
                    // A link must not become a back door into excluded or hidden folders (".obsidian" holds plugin settings).
                    var targetRel = Path.GetRelativePath(realRoot, targetReal).Replace('\\', '/');
                    if (targetRel == ".") targetRel = "";
                    if (filter.IsExcludedRelativePath(targetRel, out _) ||
                        (rejectHidden && targetRel.Split('/').Any(x => x.StartsWith('.'))))
                    {
                        error = $"'{string.Join('/', segments.Take(i + 1))}' is a link into an excluded or hidden folder. Access refused.";
                        return false;
                    }
                    current = targetReal;
                }
                else
                {
                    current = next;
                }
            }

            var finalFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(current));
            if (!IsUnder(finalFull, realRoot))
            {
                error = "The path resolves outside the vault. Access refused.";
                return false;
            }

            var logical = segments.Count == 0 ? rootFull : Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments.ToArray())));
            result = new VaultPath(rel, logical, finalFull);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            error = $"The path could not be resolved: {ex.Message}";
            return false;
        }
    }

    /// <summary>Follows links on every existing component of an absolute path, from the drive root down.</summary>
    private static string ResolveExistingChain(string fullPath)
    {
        var rootPart = Path.GetPathRoot(fullPath) ?? "";
        var rest = fullPath[rootPart.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var current = rootPart;
        for (var guard = 0; guard < 64 && rest.Length > 0; guard++)
        {
            var changed = false;
            current = rootPart;
            for (var i = 0; i < rest.Length; i++)
            {
                var next = Path.Combine(current, rest[i]);
                FileSystemInfo? info = Directory.Exists(next) ? new DirectoryInfo(next) : File.Exists(next) ? new FileInfo(next) : null;
                if (info?.LinkTarget != null)
                {
                    var target = info.ResolveLinkTarget(returnFinalTarget: true);
                    if (target != null)
                    {
                        var tail = rest.Skip(i + 1).ToArray();
                        var resolved = Path.GetFullPath(tail.Length == 0 ? target.FullName : Path.Combine(target.FullName, Path.Combine(tail)));
                        rootPart = Path.GetPathRoot(resolved) ?? "";
                        rest = resolved[rootPart.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
                        changed = true;
                        break;
                    }
                }
                current = next;
            }
            if (!changed) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(rootPart, Path.Combine(rest))));
        }
        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    public static bool IsUnder(string path, string root)
    {
        path = Path.TrimEndingDirectorySeparator(path);
        root = Path.TrimEndingDirectorySeparator(root);
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return true;
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}

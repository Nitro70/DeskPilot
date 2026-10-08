using System.Text;
using System.Text.Json;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Runtime;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Vault;

/// <summary>
/// The optional notes vault tools: vault_search, vault_read, vault_list and (when writes are allowed)
/// vault_append. Settings are read on every call; the index is rebuilt when the vault folder or its
/// include/exclude lists change. Every path the model passes is confined to the vault folder.
/// </summary>
public sealed class VaultToolHost : IToolHost
{
    public const int SearchResultsCap = 25;
    public const int MaxAppendChars = 20000;
    public const int MaxListEntries = 200;
    internal const long MaxReadBytes = 8 * 1024 * 1024;
    internal const int MaxLineChars = 2000;
    internal const int MaxReadOutputChars = 60000;

    private const int DefaultSearchResults = 8;
    private const int DefaultReadLines = 400;

    private readonly Func<AppSettings> _settings;
    private readonly object _gate = new();
    private VaultIndex? _index;
    private string? _indexKey;
    private (bool Writes, int Results, int Lines)? _toolsKey;
    private IReadOnlyList<ToolSpec> _tools = Array.Empty<ToolSpec>();

    public VaultToolHost(Func<AppSettings> settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    /// <summary>Tests replace the clock that drives the index's 30 second refresh interval.</summary>
    internal Func<DateTime>? Clock { get; set; }

    /// <summary>True when a vault folder is configured, enabled and exists.</summary>
    public bool IsAvailable => TryGetRoot(ReadSettings()?.Vault, out _);

    public IReadOnlyList<ToolSpec> GetTools()
    {
        var v = ReadSettings()?.Vault;
        if (v == null || !TryGetRoot(v, out _)) return Array.Empty<ToolSpec>();
        var key = (v.AllowWrites, SearchDefault(v), ReadCap(v));
        lock (_gate)
        {
            if (_toolsKey != key)
            {
                _tools = BuildTools(key.Item1, key.Item2, key.Item3);
                _toolsKey = key;
            }
            return _tools;
        }
    }

    public Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Scanning and file reads are synchronous; keep them off the caller's thread.
        return Task.Run(() => Execute(name, arguments, ct), ct);
    }

    /// <summary>The vault index for the current settings, or null when no vault is available. Used by the settings UI ("test search").</summary>
    public VaultIndex? GetIndex()
    {
        var v = ReadSettings()?.Vault;
        return v != null && TryGetRoot(v, out var root) ? GetIndex(root, v) : null;
    }

    private AppSettings? ReadSettings()
    {
        try
        {
            return _settings();
        }
        catch (Exception)
        {
            return null;
        }
    }

    internal static bool TryGetRoot(VaultSettings? v, out string root)
    {
        root = "";
        if (v == null || !v.Enabled || string.IsNullOrWhiteSpace(v.Path)) return false;
        try
        {
            var p = v.Path.Trim().Trim('"').Trim();
            p = Environment.ExpandEnvironmentVariables(p);
            if (p.Length == 0) return false;
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
            if (!Directory.Exists(full)) return false;
            root = full;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static int SearchDefault(VaultSettings v) => Math.Clamp(v.MaxSearchResults > 0 ? v.MaxSearchResults : DefaultSearchResults, 1, SearchResultsCap);

    private static int ReadCap(VaultSettings v) => v.MaxReadLines > 0 ? v.MaxReadLines : DefaultReadLines;

    private VaultIndex GetIndex(string root, VaultSettings v)
    {
        var filter = new VaultFilter(v.IncludeExtensions, v.ExcludeFolders);
        var key = root.ToUpperInvariant() + "|" + filter.Signature;
        lock (_gate)
        {
            if (_index == null || _indexKey != key)
            {
                _index = new VaultIndex(root, filter, Clock);
                _indexKey = key;
            }
            return _index;
        }
    }

    // ---------------------------------------------------------------- tool specs

    private static IReadOnlyList<ToolSpec> BuildTools(bool allowWrites, int searchDefault, int readCap)
    {
        var tools = new List<ToolSpec>
        {
            ToolSpec.Create("vault_search",
                "Search the user's notes vault (their own markdown notes) by keywords. Use it only when the request depends on something " +
                "the user has likely written down: one of their projects, where a file or folder is, how they set something up, their " +
                "preferences or a past decision. Most requests do not need it. Pass a few specific keywords (names of projects, tools, " +
                "files or people), not a sentence. Returns note paths, titles and the best matching lines with line numbers, never whole " +
                "notes; then read only the most relevant note with vault_read, starting near a matching line.",
                $$"""
                {"type":"object","properties":{
                  "query":{"type":"string","description":"A few specific keywords, e.g. \"homelab backup script\"."},
                  "max_results":{"type":"integer","minimum":1,"maximum":{{SearchResultsCap}},"description":"How many notes to return (default {{searchDefault}})."},
                  "folder":{"type":"string","description":"Optional vault-relative folder to search in, e.g. \"Projects\"."}
                },"required":["query"]}
                """),
            ToolSpec.Create("vault_read",
                $"Read lines of one note in the user's notes vault, by its vault-relative path as shown by vault_search or vault_list " +
                $"(the .md extension may be omitted). Returns numbered lines, at most {readCap} per call. Read narrowly: pass start_line " +
                "near the match vault_search reported and a small max_lines instead of reading whole notes.",
                $$"""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Vault-relative path, e.g. \"Projects/DeskPilot.md\"."},
                  "start_line":{"type":"integer","minimum":1,"description":"First line to return, 1-based (default 1)."},
                  "max_lines":{"type":"integer","minimum":1,"maximum":{{readCap}},"description":"How many lines to return (default and maximum {{readCap}})."}
                },"required":["path"]}
                """),
            ToolSpec.Create("vault_list",
                "List one folder of the user's notes vault: its immediate subfolders (with note counts) and its notes. Use it to browse " +
                "when you know roughly where something is filed; use vault_search to find specific information.",
                """
                {"type":"object","properties":{
                  "folder":{"type":"string","description":"Vault-relative folder, e.g. \"Projects\". Omit for the vault root."}
                }}
                """),
        };
        if (allowWrites)
        {
            tools.Add(ToolSpec.Create("vault_append",
                "Append text to a markdown note in the user's notes vault, creating the note (and its folders) when it does not exist. " +
                "Only use it when the user asks you to record or note something down. The text is added at the end after a blank line; " +
                $"existing content is never changed. .md notes only, at most {MaxAppendChars} characters per call.",
                $$"""
                {"type":"object","properties":{
                  "path":{"type":"string","description":"Vault-relative note path, e.g. \"Inbox/Ideas.md\" (.md is added when missing)."},
                  "text":{"type":"string","description":"Markdown text to append (at most {{MaxAppendChars}} characters)."}
                },"required":["path","text"]}
                """));
        }
        return tools;
    }

    // ---------------------------------------------------------------- dispatch

    private ToolResult Execute(string name, JsonElement args, CancellationToken ct)
    {
        try
        {
            if (name is not ("vault_search" or "vault_read" or "vault_list" or "vault_append"))
                return ToolResult.Error($"Unknown tool '{name}'. Vault tools: vault_search, vault_read, vault_list, vault_append.");

            var v = ReadSettings()?.Vault;
            if (v == null || !TryGetRoot(v, out var root))
                return ToolResult.Error("No notes vault is available (it is not configured, turned off, or the folder no longer exists).");

            var index = GetIndex(root, v);
            return name switch
            {
                "vault_search" => Search(index, v, args, ct),
                "vault_read" => Read(index, v, args, ct),
                "vault_list" => List(index, args, ct),
                _ => Append(index, v, args, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ToolResult.Error($"{name} failed: {ex.Message}");
        }
    }

    private static bool TryResolveFolder(VaultIndex index, string? folderArg, out VaultPath folder, out string error)
    {
        folder = null!;
        if (!VaultPathGuard.TryNormalize(folderArg, allowEmpty: true, out var segments, out error)) return false;
        if (!VaultPathGuard.TryResolve(index.RootPath, segments, index.Filter, rejectHidden: true, out folder, out error)) return false;
        if (!Directory.Exists(folder.RealFull))
        {
            error = File.Exists(folder.RealFull)
                ? $"'{folder.Relative}' is a note, not a folder. Read it with vault_read."
                : $"Folder '{folder.Relative}' does not exist in the vault. Use vault_list to see the folders.";
            return false;
        }
        return true;
    }

    // ---------------------------------------------------------------- vault_search

    private static ToolResult Search(VaultIndex index, VaultSettings v, JsonElement args, CancellationToken ct)
    {
        var query = JsonArgs.GetString(args, "query")?.Trim() ?? "";
        if (query.Length == 0) return ToolResult.Error("vault_search needs a query: a few specific keywords.");
        if (query.Length > 500) query = query[..500];

        var max = Math.Clamp(JsonArgs.GetInt(args, "max_results") ?? SearchDefault(v), 1, SearchResultsCap);

        var folderRel = "";
        var folderArg = JsonArgs.GetString(args, "folder");
        if (!string.IsNullOrWhiteSpace(folderArg))
        {
            if (!TryResolveFolder(index, folderArg, out var folder, out var error)) return ToolResult.Error(error);
            folderRel = folder.Relative;
        }

        var result = index.Search(query, max, folderRel, ct);
        var where = folderRel.Length > 0 ? $" in {folderRel}/" : "";

        if (result.NoKeywords)
            return ToolResult.Ok($"The query \"{query}\" has no searchable keywords (only very common words). Search with specific keywords such as project, tool, file or people names.");

        var sb = new StringBuilder();
        if (result.Hits.Count == 0)
        {
            sb.Append($"No notes matched \"{query}\"{where} ({result.NotesSearched} notes searched). ");
            sb.Append("Try other or fewer keywords (a synonym, a project, tool or file name), or browse folders with vault_list.");
            AppendIndexNotes(sb, index);
            return ToolResult.Ok(sb.ToString());
        }

        sb.Append(result.TotalMatches > result.Hits.Count
            ? $"Top {result.Hits.Count} of {result.TotalMatches} notes matching \"{query}\"{where}:\n"
            : $"{result.Hits.Count} note{(result.Hits.Count == 1 ? "" : "s")} matching \"{query}\"{where}:\n");
        for (var i = 0; i < result.Hits.Count; i++)
        {
            var h = result.Hits[i];
            sb.Append($"{i + 1}. {h.Path}  ({h.Title})\n");
            foreach (var line in h.Lines) sb.Append($"   L{line.LineNumber}: {line.Text}\n");
        }

        var first = result.Hits[0];
        var startLine = first.Lines.Count > 0 ? Math.Max(1, first.Lines[0].LineNumber - 5) : 1;
        sb.Append($"\nTo see more, call vault_read with the path and a start_line a few lines above a match (e.g. path \"{first.Path}\", start_line {startLine}). Read only what you need.");
        AppendIndexNotes(sb, index);
        return ToolResult.Ok(sb.ToString());
    }

    private static void AppendIndexNotes(StringBuilder sb, VaultIndex index)
    {
        if (index.Truncated) sb.Append($"\nNote: the vault has more than {VaultIndex.MaxFiles} files; only the first {VaultIndex.MaxFiles} are searchable.");
    }

    // ---------------------------------------------------------------- vault_read

    private static ToolResult Read(VaultIndex index, VaultSettings v, JsonElement args, CancellationToken ct)
    {
        var pathArg = JsonArgs.GetString(args, "path");
        if (!VaultPathGuard.TryNormalize(pathArg, allowEmpty: false, out var segments, out var error)) return ToolResult.Error(error);
        if (!TryResolveNote(index, segments, out var note, out error)) return ToolResult.Error(error);

        if (!index.Filter.IsIncludedFile(note.Relative))
        {
            var ext = Path.GetExtension(note.Relative);
            return ToolResult.Error($"'{note.Relative}' is not a note type the vault reads ({(ext.Length == 0 ? "no extension" : ext)}). Readable types: {string.Join(", ", index.Filter.Extensions)}.");
        }

        ct.ThrowIfCancellationRequested();
        var bytes = VaultText.ReadAllBytesShared(note.RealFull, MaxReadBytes);
        if (bytes == null)
            return ToolResult.Error($"'{note.Relative}' is too large to read (over {MaxReadBytes / (1024 * 1024)} MB).");
        if (!VaultText.TryDecode(bytes, out var text))
            return ToolResult.Error($"'{note.Relative}' looks like a binary file, not a text note. Refusing to read it.");
        var lines = VaultText.SplitLines(text);
        var total = lines.Count;
        if (total == 0) return ToolResult.Ok($"File: {note.Relative} (empty, 0 lines)");

        var cap = ReadCap(v);
        var start = Math.Max(1, JsonArgs.GetInt(args, "start_line") ?? 1);
        int maxLines;
        if (JsonArgs.GetInt(args, "max_lines") is { } requested) maxLines = requested;
        else if (JsonArgs.GetInt(args, "end_line") is { } endLine) maxLines = endLine - start + 1;
        else maxLines = cap;
        maxLines = Math.Clamp(maxLines, 1, cap);

        if (start > total)
            return ToolResult.Error($"start_line {start} is past the end of {note.Relative}, which has {total} lines.");

        var end = Math.Min(total, start + maxLines - 1);
        var body = new StringBuilder();
        var last = start - 1;
        for (var n = start; n <= end; n++)
        {
            var line = lines[n - 1];
            if (line.Length > MaxLineChars) line = line[..MaxLineChars] + $" ... [line cut, {lines[n - 1].Length} characters]";
            if (body.Length + line.Length > MaxReadOutputChars && n > start) break;
            body.Append(n).Append("| ").Append(line).Append('\n');
            last = n;
        }

        var sb = new StringBuilder();
        sb.Append($"File: {note.Relative} (lines {start}-{last} of {total})\n");
        sb.Append(body);
        if (last < total)
            sb.Append($"[{total - last} more lines. Continue with vault_read path \"{note.Relative}\" start_line {last + 1} only if you need them.]");
        return ToolResult.Ok(sb.ToString().TrimEnd('\n'));
    }

    /// <summary>Resolves a note path, allowing the ".md" extension to be omitted.</summary>
    private static bool TryResolveNote(VaultIndex index, List<string> segments, out VaultPath note, out string error)
    {
        if (!VaultPathGuard.TryResolve(index.RootPath, segments, index.Filter, rejectHidden: true, out note, out error)) return false;
        if (File.Exists(note.RealFull)) return true;
        var isFolder = Directory.Exists(note.RealFull);

        var lastSeg = segments[^1];
        if (!lastSeg.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            var withMd = new List<string>(segments);
            withMd[^1] = lastSeg + ".md";
            if (VaultPathGuard.TryResolve(index.RootPath, withMd, index.Filter, rejectHidden: true, out var md, out var mdError))
            {
                if (File.Exists(md.RealFull))
                {
                    note = md;
                    return true;
                }
            }
            else if (!isFolder)
            {
                error = mdError;
                return false;
            }
        }

        if (isFolder)
        {
            error = $"'{note.Relative}' is a folder. Use vault_list to see what is in it.";
            return false;
        }

        var suggestions = index.FindByFileName(lastSeg);
        error = suggestions.Count > 0
            ? $"Note not found: {note.Relative}. Did you mean: {string.Join(", ", suggestions)}?"
            : $"Note not found: {note.Relative}. Use vault_search or vault_list to find the right path.";
        return false;
    }

    // ---------------------------------------------------------------- vault_list

    private static ToolResult List(VaultIndex index, JsonElement args, CancellationToken ct)
    {
        if (!TryResolveFolder(index, JsonArgs.GetString(args, "folder"), out var folder, out var error)) return ToolResult.Error(error);

        var counts = index.CountNotesBySubfolder(folder.Relative, ct);
        var prefix = folder.Relative.Length == 0 ? "" : folder.Relative + "/";
        var dirs = new List<string>();
        var notes = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var entry in new DirectoryInfo(folder.RealFull).EnumerateFileSystemInfos("*", options))
        {
            if (VaultFilter.IsSkippedEntry(entry)) continue;
            if (entry is DirectoryInfo)
            {
                if (!index.Filter.IsExcludedDirectory(entry.Name, prefix + entry.Name)) dirs.Add(entry.Name);
            }
            else if (index.Filter.IsIncludedFile(entry.Name))
            {
                notes.Add(entry.Name);
            }
        }
        dirs.Sort(StringComparer.OrdinalIgnoreCase);
        notes.Sort(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.Append(folder.Relative.Length == 0 ? "Vault root" : $"Folder {folder.Relative}/");
        sb.Append($": {dirs.Count} folder{(dirs.Count == 1 ? "" : "s")}, {notes.Count} note{(notes.Count == 1 ? "" : "s")}\n");
        if (dirs.Count == 0 && notes.Count == 0)
        {
            sb.Append("(empty)");
            return ToolResult.Ok(sb.ToString());
        }

        var shown = 0;
        if (dirs.Count > 0)
        {
            sb.Append("Folders:\n");
            foreach (var d in dirs)
            {
                if (shown >= MaxListEntries) break;
                var c = counts.GetValueOrDefault(d);
                sb.Append($"  {prefix}{d}/  ({c} note{(c == 1 ? "" : "s")})\n");
                shown++;
            }
        }
        if (notes.Count > 0 && shown < MaxListEntries)
        {
            sb.Append("Notes:\n");
            foreach (var n in notes)
            {
                if (shown >= MaxListEntries) break;
                sb.Append($"  {prefix}{n}\n");
                shown++;
            }
        }
        var hidden = dirs.Count + notes.Count - shown;
        if (hidden > 0) sb.Append($"... and {hidden} more entries not shown. Use vault_search to find specific notes.\n");
        return ToolResult.Ok(sb.ToString().TrimEnd('\n'));
    }

    // ---------------------------------------------------------------- vault_append

    private static ToolResult Append(VaultIndex index, VaultSettings v, JsonElement args, CancellationToken ct)
    {
        if (!v.AllowWrites)
            return ToolResult.Error("Writing to the vault is turned off in DeskPilot's settings (Vault: allow writes).");

        var text = JsonArgs.GetString(args, "text") ?? "";
        if (text.Trim().Length == 0) return ToolResult.Error("vault_append needs some text to add.");
        if (text.Length > MaxAppendChars)
            return ToolResult.Error($"The text is {text.Length} characters; the limit is {MaxAppendChars} per call. Split it into several calls.");

        if (!VaultPathGuard.TryNormalize(JsonArgs.GetString(args, "path"), allowEmpty: false, out var segments, out var error)) return ToolResult.Error(error);
        var last = segments[^1];
        if (!last.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            // "notes.txt" or "run.ps1" name another file type; "Meeting 2026.10.08" just has dots in its name.
            var ext = Path.GetExtension(last);
            if (ext.Length is >= 2 and <= 6 && ext.Skip(1).All(char.IsAsciiLetterOrDigit) && ext.Skip(1).Any(char.IsAsciiLetter))
                return ToolResult.Error($"Only markdown notes (.md) can be written; '{last}' has the extension {ext}.");
            segments[^1] = last + ".md";
        }

        if (!VaultPathGuard.TryResolve(index.RootPath, segments, index.Filter, rejectHidden: true, out var note, out error)) return ToolResult.Error(error);
        if (Directory.Exists(note.RealFull)) return ToolResult.Error($"'{note.Relative}' is a folder, not a note.");
        ct.ThrowIfCancellationRequested();

        var created = !File.Exists(note.RealFull);
        if (created)
        {
            var parent = Path.GetDirectoryName(note.RealFull);
            if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
            {
                Directory.CreateDirectory(parent);
                // Re-check after creating folders, in case something changed underneath in between.
                if (!VaultPathGuard.TryResolve(index.RootPath, segments, index.Filter, rejectHidden: true, out note, out error)) return ToolResult.Error(error);
            }
        }

        int addedLines;
        int totalLines;
        using (var fs = new FileStream(note.RealFull, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read))
        {
            if (fs.Length > MaxReadBytes) return ToolResult.Error($"'{note.Relative}' is too large to append to.");
            var existingBytes = new byte[fs.Length];
            fs.ReadExactly(existingBytes);
            if (!VaultText.IsUtf8Compatible(existingBytes) || !VaultText.TryDecode(existingBytes, out var existing))
                return ToolResult.Error($"'{note.Relative}' is not a UTF-8 text note; refusing to append to it.");

            var nl = existing.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var separator = existing.Length == 0 ? ""
                : existing.EndsWith(nl + nl, StringComparison.Ordinal) ? ""
                : existing.EndsWith('\n') || existing.EndsWith('\r') ? nl
                : nl + nl;

            var addition = text.Replace("\r\n", "\n").Replace('\r', '\n').Trim('\n');
            addedLines = VaultText.SplitLines(addition).Count;
            addition = addition.Replace("\n", nl) + nl;

            var bytes = new UTF8Encoding(false).GetBytes(separator + addition);
            fs.Seek(0, SeekOrigin.End);
            fs.Write(bytes);
            fs.Flush(true);
            totalLines = VaultText.SplitLines(existing + separator + addition).Count;
        }
        index.MarkDirty();

        return ToolResult.Ok(created
            ? $"Created {note.Relative} with {totalLines} line{(totalLines == 1 ? "" : "s")}."
            : $"Appended {addedLines} line{(addedLines == 1 ? "" : "s")} to {note.Relative}; it now has {totalLines} lines.");
    }
}

using System.Text;

namespace DeskPilot.Core.Vault;

public sealed record VaultSnippet(int LineNumber, string Text);

public sealed record VaultSearchHit(string Path, string Title, double Score, IReadOnlyList<VaultSnippet> Lines);

public sealed record VaultSearchResult(
    IReadOnlyList<VaultSearchHit> Hits,
    int TotalMatches,
    int NotesSearched,
    bool NoKeywords);

/// <summary>
/// In-memory keyword index of a notes folder. BM25 per field (title, aliases, tags, headings, body, path) with
/// field boosts, half-weight prefix matches, and a phrase bonus. Only the term statistics are kept in memory;
/// snippets are read from disk for the few notes that are returned. Refreshes incrementally: before a search,
/// when the last scan is older than <see cref="RefreshInterval"/>, only changed, added and removed files are
/// re-indexed. All public members are thread-safe.
/// </summary>
public sealed class VaultIndex
{
    public const int MaxFiles = 20000;

    private const double K1 = 1.2;
    private const double B = 0.75;
    private const double PrefixWeight = 0.5;
    private const int MinPrefixLength = 4;
    private const int MaxPrefixExpansions = 64;
    private static readonly double[] Boosts = { 4.0, 3.0, 2.5, 2.0, 1.0, 0.5 };

    private readonly object _gate = new();
    private readonly VaultFilter _filter;
    private readonly Func<DateTime> _utcNow;

    private readonly Dictionary<string, NoteEntry> _notes = new(StringComparer.OrdinalIgnoreCase);
    // Files that were looked at but could not be indexed (binary, unreadable), remembered so they are not re-read every scan.
    private readonly Dictionary<string, (DateTime Mtime, long Length)> _unindexable = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _termIds = new(StringComparer.Ordinal);
    private readonly List<string> _terms = new();
    private readonly List<int> _df = new();
    private readonly long[] _fieldTotals = new long[NoteParser.FieldCount];

    private DateTime? _lastScanUtc;
    private bool _dirty = true;
    private int _parseCount;

    public VaultIndex(string rootPath, IEnumerable<string>? includeExtensions = null, IEnumerable<string>? excludeFolders = null)
        : this(rootPath, new VaultFilter(includeExtensions ?? new[] { ".md" }, excludeFolders), null)
    {
    }

    internal VaultIndex(string rootPath, VaultFilter filter, Func<DateTime>? utcNow)
    {
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _filter = filter;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string RootPath { get; }

    internal VaultFilter Filter => _filter;

    /// <summary>How old the last scan may be before a search rescans modification times.</summary>
    public TimeSpan RefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>The file cap (<see cref="MaxFiles"/>); tests lower it.</summary>
    internal int FileLimit { get; set; } = MaxFiles;

    /// <summary>True when the last scan stopped at the file cap.</summary>
    public bool Truncated { get; private set; }

    /// <summary>Files skipped by the last scan because they exceed the size limit.</summary>
    public int SkippedLargeFiles { get; private set; }

    public TimeSpan LastScanDuration { get; private set; }

    /// <summary>How many files were parsed since this index was created (tests use it to verify incremental refresh).</summary>
    internal int ParseCount => Volatile.Read(ref _parseCount);

    public int NoteCount
    {
        get { lock (_gate) return _notes.Count; }
    }

    /// <summary>Forces a rescan before the next query (after the vault was written to).</summary>
    public void MarkDirty()
    {
        lock (_gate) _dirty = true;
    }

    /// <summary>Rescans now: re-indexes changed and added files and drops removed ones.</summary>
    public void Refresh(CancellationToken ct = default)
    {
        lock (_gate) ScanLocked(ct);
    }

    private void EnsureFreshLocked(CancellationToken ct)
    {
        if (!_dirty && _lastScanUtc is { } last && _utcNow() - last < RefreshInterval) return;
        ScanLocked(ct);
    }

    // ---------------------------------------------------------------- scanning

    private sealed class NoteEntry
    {
        public required string Rel;
        public required string Full;
        public required DateTime Mtime;
        public required long Length;
        public required string Title;
        public required int[] FieldLengths;
        public required TermPosting[] Terms;     // sorted by TermId
    }

    /// <summary>
    /// One term of one note: 12 bytes. Counts saturate at 255, which changes nothing in practice because the
    /// BM25 term-frequency curve is flat long before that.
    /// </summary>
    private struct TermPosting
    {
        public int TermId;
        public byte Title, Aliases, Tags, Headings, Body, Path;

        public TermPosting(int termId, FieldCounts c)
        {
            TermId = termId;
            Title = Sat(c.Title);
            Aliases = Sat(c.Aliases);
            Tags = Sat(c.Tags);
            Headings = Sat(c.Headings);
            Body = Sat(c.Body);
            Path = Sat(c.Path);
        }

        private static byte Sat(int v) => (byte)Math.Min(v, byte.MaxValue);

        public readonly int Get(int field) => field switch
        {
            0 => Title,
            1 => Aliases,
            2 => Tags,
            3 => Headings,
            4 => Body,
            _ => Path,
        };
    }

    private readonly record struct FileEntry(string Rel, string Full, DateTime Mtime, long Length);

    private void ScanLocked(CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var files = EnumerateFiles(ct, out var truncated, out var skippedLarge);
        ct.ThrowIfCancellationRequested();

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var toParse = new List<FileEntry>();
        foreach (var f in files)
        {
            seen.Add(f.Rel);
            if (_notes.TryGetValue(f.Rel, out var existing) && existing.Mtime == f.Mtime && existing.Length == f.Length) continue;
            if (_unindexable.TryGetValue(f.Rel, out var bad) && bad.Mtime == f.Mtime && bad.Length == f.Length) continue;
            toParse.Add(f);
        }

        var parsed = new ParsedNote?[toParse.Count];
        if (toParse.Count > 0)
        {
            var options = new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 8) };
            Parallel.For(0, toParse.Count, options, i =>
            {
                parsed[i] = TryParse(toParse[i]);
                Interlocked.Increment(ref _parseCount);
            });
        }

        // Nothing below can be cancelled, so the index never ends up half-updated.
        foreach (var rel in _notes.Keys.Where(k => !seen.Contains(k)).ToList()) RemoveLocked(rel);
        foreach (var rel in _unindexable.Keys.Where(k => !seen.Contains(k)).ToList()) _unindexable.Remove(rel);

        for (var i = 0; i < toParse.Count; i++)
        {
            var f = toParse[i];
            RemoveLocked(f.Rel);
            _unindexable.Remove(f.Rel);
            if (parsed[i] is { } p) AddLocked(f, p);
            else _unindexable[f.Rel] = (f.Mtime, f.Length);
        }

        Truncated = truncated;
        SkippedLargeFiles = skippedLarge;
        _lastScanUtc = _utcNow();
        _dirty = false;
        LastScanDuration = DateTime.UtcNow - started;
    }

    private List<FileEntry> EnumerateFiles(CancellationToken ct, out bool truncated, out int skippedLarge)
    {
        truncated = false;
        skippedLarge = 0;
        var result = new List<FileEntry>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0,
        };

        var stack = new Stack<(DirectoryInfo Dir, string Rel)>();
        stack.Push((new DirectoryInfo(RootPath), ""));
        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, relDir) = stack.Pop();
            var subdirs = new List<(DirectoryInfo, string)>();
            try
            {
                foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
                {
                    if (VaultFilter.IsSkippedEntry(entry)) continue;
                    var rel = relDir.Length == 0 ? entry.Name : relDir + "/" + entry.Name;
                    if (entry is DirectoryInfo sub)
                    {
                        if (!_filter.IsExcludedDirectory(sub.Name, rel)) subdirs.Add((sub, rel));
                        continue;
                    }
                    if (entry is not FileInfo file || !_filter.IsIncludedFile(file.Name)) continue;
                    if (file.Length > VaultFilter.MaxIndexedFileBytes)
                    {
                        skippedLarge++;
                        continue;
                    }
                    if (result.Count >= FileLimit)
                    {
                        truncated = true;
                        return result;
                    }
                    result.Add(new FileEntry(rel, file.FullName, file.LastWriteTimeUtc, file.Length));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that vanished or cannot be read is simply not indexed.
            }
            // Reverse so folders are visited in enumeration order (keeps the MaxFiles cut deterministic).
            for (var i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
        }
        return result;
    }

    private static ParsedNote? TryParse(FileEntry f)
    {
        try
        {
            var bytes = VaultText.ReadAllBytesShared(f.Full, VaultFilter.MaxIndexedFileBytes);
            if (bytes == null || !VaultText.TryDecode(bytes, out var text)) return null;
            return NoteParser.Parse(f.Rel, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private void AddLocked(FileEntry f, ParsedNote p)
    {
        var postings = new TermPosting[p.Terms.Count];
        var n = 0;
        foreach (var (term, counts) in p.Terms)
        {
            if (!_termIds.TryGetValue(term, out var id))
            {
                id = _terms.Count;
                _termIds[term] = id;
                _terms.Add(term);
                _df.Add(0);
            }
            _df[id]++;
            postings[n++] = new TermPosting(id, counts);
        }
        Array.Sort(postings, (a, b) => a.TermId.CompareTo(b.TermId));
        for (var i = 0; i < _fieldTotals.Length; i++) _fieldTotals[i] += p.FieldLengths[i];

        _notes[f.Rel] = new NoteEntry
        {
            Rel = f.Rel,
            Full = f.Full,
            Mtime = f.Mtime,
            Length = f.Length,
            Title = p.DisplayTitle,
            FieldLengths = p.FieldLengths,
            Terms = postings,
        };
    }

    private void RemoveLocked(string rel)
    {
        if (!_notes.Remove(rel, out var e)) return;
        foreach (var t in e.Terms) _df[t.TermId]--;
        for (var i = 0; i < _fieldTotals.Length; i++) _fieldTotals[i] -= e.FieldLengths[i];
    }

    // ---------------------------------------------------------------- search

    private sealed record QueryTerm(string Text, double Idf, (int Id, double Weight, double Idf)[] Variants);

    private sealed record Candidate(string Rel, string Full, string Title, double Score);

    /// <summary>
    /// Ranked keyword search. <paramref name="folder"/> limits results to a vault-relative subfolder (forward slashes).
    /// Never returns whole files: each hit carries up to three of its best matching lines.
    /// </summary>
    public VaultSearchResult Search(string query, int maxResults, string? folder = null, CancellationToken ct = default)
    {
        maxResults = Math.Clamp(maxResults, 1, 100);
        var tokens = VaultText.Tokenize(query ?? "").Distinct(StringComparer.Ordinal).Take(32).ToList();
        if (tokens.Count == 0) return new VaultSearchResult(Array.Empty<VaultSearchHit>(), 0, NoteCount, NoKeywords: true);

        var folderPrefix = string.IsNullOrEmpty(folder) ? "" : folder.Trim('/') + "/";
        List<Candidate> candidates;
        List<QueryTerm> qterms;
        int searched;
        int totalMatches;

        lock (_gate)
        {
            EnsureFreshLocked(ct);
            var n = _notes.Count;
            qterms = tokens.Select(t => BuildQueryTermLocked(t, n)).ToList();
            var avg = new double[NoteParser.FieldCount];
            for (var i = 0; i < avg.Length; i++) avg[i] = n == 0 ? 1 : Math.Max(1e-9, (double)_fieldTotals[i] / n);

            var scored = new List<Candidate>();
            searched = 0;
            foreach (var note in _notes.Values)
            {
                if (folderPrefix.Length > 0 && !note.Rel.StartsWith(folderPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                searched++;
                double total = 0;
                foreach (var q in qterms)
                {
                    double best = 0;
                    foreach (var (id, weight, idf) in q.Variants)
                    {
                        var s = weight * Bm25F(note, id, idf, avg);
                        if (s > best) best = s;
                    }
                    total += best;
                }
                if (total > 0) scored.Add(new Candidate(note.Rel, note.Full, note.Title, total));
            }
            totalMatches = scored.Count;
            var keep = Math.Clamp(maxResults * 3, 30, 75);
            candidates = scored
                .OrderByDescending(c => c.Score)
                .ThenBy(c => c.Rel, StringComparer.OrdinalIgnoreCase)
                .Take(keep)
                .ToList();
        }

        // Disk reads happen outside the lock: phrase bonus and snippets use the current file contents.
        var phrase = VaultText.CountWords(query) >= 2 ? " " + VaultText.NormalizeForPhrase(query) + " " : null;
        var phraseBonus = qterms.Sum(q => q.Idf);
        var reranked = new List<(Candidate C, double Score, List<string>? Lines)>();
        var vanished = false;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var lines = TryReadLines(c.Full, out var missing);
            if (missing)
            {
                vanished = true;
                continue;
            }
            var score = c.Score;
            if (phrase != null && lines != null && ContainsPhrase(c, lines, phrase)) score += phraseBonus;
            reranked.Add((c, score, lines));
        }
        if (vanished) MarkDirty();

        var hits = reranked
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.C.Rel, StringComparer.OrdinalIgnoreCase)
            .Take(maxResults)
            .Select(r => new VaultSearchHit(r.C.Rel, r.C.Title, Math.Round(r.Score, 4), r.Lines == null ? Array.Empty<VaultSnippet>() : BestLines(r.Lines, qterms, phrase, 3)))
            .ToList();

        return new VaultSearchResult(hits, totalMatches, searched, NoKeywords: false);
    }

    private QueryTerm BuildQueryTermLocked(string token, int n)
    {
        var variants = new List<(int, double, double)>();
        var exactIdf = Idf(0, n);
        if (_termIds.TryGetValue(token, out var id) && _df[id] > 0)
        {
            exactIdf = Idf(_df[id], n);
            variants.Add((id, 1.0, exactIdf));
        }

        if (token.Length >= MinPrefixLength)
        {
            var prefixed = new List<(int Id, int Df)>();
            for (var i = 0; i < _terms.Count; i++)
            {
                var t = _terms[i];
                if (t.Length > token.Length && _df[i] > 0 && t.StartsWith(token, StringComparison.Ordinal)) prefixed.Add((i, _df[i]));
            }
            foreach (var (pid, df) in prefixed.OrderByDescending(p => p.Df).ThenBy(p => p.Id).Take(MaxPrefixExpansions))
                variants.Add((pid, PrefixWeight, Idf(df, n)));

            // A plural query term also finds the singular at the same reduced weight ("projects" -> "project").
            if (token.EndsWith('s') && _termIds.TryGetValue(token[..^1], out var sid) && _df[sid] > 0)
                variants.Add((sid, PrefixWeight, Idf(_df[sid], n)));
        }
        return new QueryTerm(token, exactIdf, variants.ToArray());
    }

    private static double Idf(int df, int n) => Math.Log(1 + (n - df + 0.5) / (df + 0.5));

    private static double Bm25F(NoteEntry note, int termId, double idf, double[] avg)
    {
        var terms = note.Terms;
        int lo = 0, hi = terms.Length - 1, at = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) >> 1;
            var v = terms[mid].TermId;
            if (v == termId) { at = mid; break; }
            if (v < termId) lo = mid + 1; else hi = mid - 1;
        }
        if (at < 0) return 0;

        var posting = terms[at];
        double score = 0;
        for (var f = 0; f < NoteParser.FieldCount; f++)
        {
            var tf = posting.Get(f);
            if (tf == 0) continue;
            var norm = 1 - B + B * note.FieldLengths[f] / avg[f];
            score += Boosts[f] * idf * (tf * (K1 + 1)) / (tf + K1 * norm);
        }
        return score;
    }

    private static List<string>? TryReadLines(string full, out bool missing)
    {
        missing = false;
        try
        {
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                missing = true;
                return null;
            }
            var bytes = VaultText.ReadAllBytesShared(full, VaultFilter.MaxIndexedFileBytes);
            return bytes != null && VaultText.TryDecode(bytes, out var text) ? VaultText.SplitLines(text) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static bool ContainsPhrase(Candidate c, List<string> lines, string phrase)
    {
        var fileTitle = Path.GetFileNameWithoutExtension(c.Rel);
        if ((" " + VaultText.NormalizeForPhrase(fileTitle) + " ").Contains(phrase, StringComparison.Ordinal)) return true;
        foreach (var line in lines)
        {
            if (line.Length < phrase.Length - 2) continue;
            if ((" " + VaultText.NormalizeForPhrase(line) + " ").Contains(phrase, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    private static IReadOnlyList<VaultSnippet> BestLines(List<string> lines, List<QueryTerm> qterms, string? phrase, int count)
    {
        var scored = new List<(int Index, double Score)>();
        var groupBest = new double[qterms.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            Array.Clear(groupBest);
            VaultText.Tokenize(line.AsSpan(), token =>
            {
                for (var g = 0; g < qterms.Count; g++)
                {
                    var q = qterms[g].Text;
                    double w = 0;
                    if (token.SequenceEqual(q.AsSpan())) w = 1;
                    else if (q.Length >= MinPrefixLength && token.Length > q.Length && token.StartsWith(q.AsSpan())) w = PrefixWeight;
                    else if (q.Length >= MinPrefixLength && q[^1] == 's' && token.SequenceEqual(q.AsSpan(0, q.Length - 1))) w = PrefixWeight;
                    if (w > groupBest[g]) groupBest[g] = w;
                }
            });
            double score = 0;
            for (var g = 0; g < qterms.Count; g++) score += groupBest[g] * Math.Max(qterms[g].Idf, 0.1);
            if (score <= 0) continue;
            if (phrase != null && (" " + VaultText.NormalizeForPhrase(line) + " ").Contains(phrase, StringComparison.Ordinal))
                score += qterms.Sum(q => Math.Max(q.Idf, 0.1));
            scored.Add((i, score));
        }

        if (scored.Count == 0)
        {
            // Matched only by file name or folder: show where the note starts instead.
            var s = NoteParser.ReadStructure(lines);
            for (var i = s.BodyStartLine; i < lines.Count; i++)
                if (lines[i].Trim().Length > 0)
                    return new[] { new VaultSnippet(i + 1, MakeSnippet(lines[i], qterms)) };
            return Array.Empty<VaultSnippet>();
        }

        return scored
            .OrderByDescending(s => s.Score)
            .ThenBy(s => s.Index)
            .Take(count)
            .OrderBy(s => s.Index)
            .Select(s => new VaultSnippet(s.Index + 1, MakeSnippet(lines[s.Index], qterms)))
            .ToList();
    }

    internal const int SnippetLength = 200;

    private static string MakeSnippet(string line, List<QueryTerm> qterms)
    {
        var text = CollapseWhitespace(line);
        if (text.Length <= SnippetLength) return text;

        var first = -1;
        foreach (var q in qterms)
        {
            var at = text.IndexOf(q.Text, StringComparison.OrdinalIgnoreCase);
            if (at >= 0 && (first < 0 || at < first)) first = at;
        }
        var start = first < 0 ? 0 : Math.Max(0, first - 60);
        if (start > 0)
        {
            // Start at a word boundary when one is close.
            var space = text.IndexOf(' ', start);
            if (space >= 0 && space < start + 15 && space < first) start = space + 1;
        }
        var end = Math.Min(text.Length, start + SnippetLength);
        var sb = new StringBuilder();
        if (start > 0) sb.Append("...");
        sb.Append(text, start, end - start);
        if (end < text.Length) sb.Append("...");
        return sb.ToString();
    }

    private static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var space = false;
        foreach (var c in s.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!space) sb.Append(' ');
                space = true;
            }
            else
            {
                sb.Append(c);
                space = false;
            }
        }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- other queries

    /// <summary>Indexed notes whose file name matches <paramref name="name"/> (with or without extension).</summary>
    public IReadOnlyList<string> FindByFileName(string name, int max = 5, CancellationToken ct = default)
    {
        var wanted = Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));
        if (wanted.Length == 0) return Array.Empty<string>();
        var wantedStem = Path.GetFileNameWithoutExtension(wanted);
        lock (_gate)
        {
            EnsureFreshLocked(ct);
            var exact = new List<string>();
            var loose = new List<string>();
            foreach (var rel in _notes.Keys)
            {
                var fileName = Path.GetFileName(rel);
                var stem = Path.GetFileNameWithoutExtension(rel);
                if (fileName.Equals(wanted, StringComparison.OrdinalIgnoreCase) || stem.Equals(wanted, StringComparison.OrdinalIgnoreCase)) exact.Add(rel);
                else if (wantedStem.Length >= 3 && stem.Contains(wantedStem, StringComparison.OrdinalIgnoreCase)) loose.Add(rel);
            }
            return exact.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Concat(loose.OrderBy(x => x.Length).ThenBy(x => x, StringComparer.OrdinalIgnoreCase))
                .Take(max)
                .ToList();
        }
    }

    /// <summary>Indexed note counts per immediate subfolder of <paramref name="folder"/> ("" = vault root).</summary>
    public IReadOnlyDictionary<string, int> CountNotesBySubfolder(string folder, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrEmpty(folder) ? "" : folder.Trim('/') + "/";
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            EnsureFreshLocked(ct);
            foreach (var rel in _notes.Keys)
            {
                if (!rel.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rest = rel.AsSpan(prefix.Length);
                var slash = rest.IndexOf('/');
                if (slash <= 0) continue;
                var name = rest[..slash].ToString();
                counts[name] = counts.GetValueOrDefault(name) + 1;
            }
        }
        return counts;
    }
}

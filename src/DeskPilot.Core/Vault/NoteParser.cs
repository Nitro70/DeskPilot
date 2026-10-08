namespace DeskPilot.Core.Vault;

internal enum NoteField { Title = 0, Aliases = 1, Tags = 2, Headings = 3, Body = 4, Path = 5 }

/// <summary>Per-term counts in each field of one note.</summary>
internal struct FieldCounts
{
    public int Title, Aliases, Tags, Headings, Body, Path;

    public readonly int Get(int field) => field switch
    {
        0 => Title,
        1 => Aliases,
        2 => Tags,
        3 => Headings,
        4 => Body,
        _ => Path,
    };

    public void Add(int field)
    {
        switch (field)
        {
            case 0: Title++; break;
            case 1: Aliases++; break;
            case 2: Tags++; break;
            case 3: Headings++; break;
            case 4: Body++; break;
            default: Path++; break;
        }
    }
}

/// <summary>The parsed, tokenized form of one note, before it is merged into the index.</summary>
internal sealed class ParsedNote
{
    public required string DisplayTitle { get; init; }
    public required Dictionary<string, FieldCounts> Terms { get; init; }
    public required int[] FieldLengths { get; init; }
    public IReadOnlyList<string> Tags { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
}

/// <summary>Extracts the searchable fields of a markdown-ish note: title, frontmatter aliases and tags, #tags, headings, body.</summary>
internal static class NoteParser
{
    public const int FieldCount = 6;
    private const int MaxFrontmatterLines = 500;

    public sealed record Structure(
        string? FirstH1,
        List<string> Aliases,
        List<string> Tags,
        List<string> Headings,
        int BodyStartLine,               // 0-based index of the first line after the frontmatter
        List<string> FrontmatterBodyText);

    public static ParsedNote Parse(string relativePath, string text)
    {
        var lines = VaultText.SplitLines(text);
        var s = ReadStructure(lines);
        var fileTitle = Path.GetFileNameWithoutExtension(relativePath);

        var terms = new Dictionary<string, FieldCounts>(StringComparer.Ordinal);
        var lookup = terms.GetAlternateLookup<ReadOnlySpan<char>>();
        var lengths = new int[FieldCount];

        void AddText(string value, NoteField field)
        {
            var f = (int)field;
            VaultText.Tokenize(value.AsSpan(), token =>
            {
                ref var counts = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(lookup, token, out _);
                counts.Add(f);
                lengths[f]++;
            });
        }

        AddText(fileTitle, NoteField.Title);
        if (s.FirstH1 != null) AddText(s.FirstH1, NoteField.Title);
        foreach (var a in s.Aliases) AddText(a, NoteField.Aliases);
        foreach (var t in s.Tags) AddText(t, NoteField.Tags);
        foreach (var h in s.Headings) AddText(h, NoteField.Headings);
        foreach (var fm in s.FrontmatterBodyText) AddText(fm, NoteField.Body);
        for (var i = s.BodyStartLine; i < lines.Count; i++) AddText(lines[i], NoteField.Body);

        var pathNoExt = relativePath;
        var ext = Path.GetExtension(relativePath);
        if (ext.Length > 0) pathNoExt = relativePath[..^ext.Length];
        AddText(pathNoExt, NoteField.Path);

        return new ParsedNote
        {
            DisplayTitle = string.IsNullOrWhiteSpace(s.FirstH1) ? fileTitle : s.FirstH1!.Trim(),
            Terms = terms,
            FieldLengths = lengths,
            Tags = s.Tags,
            Aliases = s.Aliases,
        };
    }

    public static Structure ReadStructure(IReadOnlyList<string> lines)
    {
        var aliases = new List<string>();
        var tags = new List<string>();
        var headings = new List<string>();
        var fmBody = new List<string>();
        string? h1 = null;

        var bodyStart = ParseFrontmatter(lines, aliases, tags, fmBody);

        var inFence = false;
        var fenceChar = '\0';
        var fenceLen = 0;
        for (var i = bodyStart; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = TrimIndent(line);
            if (IsFence(trimmed, out var fc, out var fl))
            {
                if (!inFence) { inFence = true; fenceChar = fc; fenceLen = fl; continue; }
                if (fc == fenceChar && fl >= fenceLen && trimmed.AsSpan(fl).Trim().Length == 0) { inFence = false; continue; }
            }
            if (inFence) continue;

            if (TryHeading(trimmed, out var level, out var headingText))
            {
                if (headingText.Length > 0)
                {
                    headings.Add(headingText);
                    if (level == 1 && h1 == null) h1 = headingText;
                }
                // Headings may carry tags too ("## Plan #todo").
            }
            CollectInlineTags(line, tags);
        }

        return new Structure(h1, aliases, tags, headings, bodyStart, fmBody);
    }

    private static string TrimIndent(string line)
    {
        var n = 0;
        while (n < line.Length && n < 3 && line[n] == ' ') n++;
        return n == 0 ? line : line[n..];
    }

    private static bool IsFence(string trimmed, out char fenceChar, out int length)
    {
        fenceChar = '\0';
        length = 0;
        if (trimmed.Length < 3 || (trimmed[0] != '`' && trimmed[0] != '~')) return false;
        var c = trimmed[0];
        var n = 0;
        while (n < trimmed.Length && trimmed[n] == c) n++;
        if (n < 3) return false;
        fenceChar = c;
        length = n;
        return true;
    }

    private static bool TryHeading(string trimmed, out int level, out string text)
    {
        level = 0;
        text = "";
        while (level < trimmed.Length && trimmed[level] == '#') level++;
        if (level is 0 or > 6) return false;
        if (level < trimmed.Length && trimmed[level] != ' ' && trimmed[level] != '\t') return false;
        var rest = trimmed[level..].Trim();
        // Closing sequence: "## Title ##"
        var end = rest.Length;
        while (end > 0 && rest[end - 1] == '#') end--;
        if (end < rest.Length && (end == 0 || rest[end - 1] == ' ' || rest[end - 1] == '\t')) rest = rest[..end].TrimEnd();
        text = rest;
        return true;
    }

    private static bool IsTagChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '/';

    /// <summary>Obsidian-style inline tags: "#name" at the start or after whitespace/bracket, not purely numeric.</summary>
    public static void CollectInlineTags(string line, List<string> tags)
    {
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] != '#') continue;
            if (i > 0 && !(char.IsWhiteSpace(line[i - 1]) || line[i - 1] is '(' or '[' or ',' or ';')) continue;
            var j = i + 1;
            while (j < line.Length && IsTagChar(line[j])) j++;
            if (j == i + 1) continue;
            var tag = line[(i + 1)..j].TrimEnd('/', '-');
            var hasNonDigit = false;
            foreach (var c in tag) if (!char.IsDigit(c)) { hasNonDigit = true; break; }
            if (tag.Length > 0 && hasNonDigit) tags.Add(tag);
            i = j - 1;
        }
    }

    /// <summary>Reads a leading YAML frontmatter block. Returns the index of the first body line.</summary>
    private static int ParseFrontmatter(IReadOnlyList<string> lines, List<string> aliases, List<string> tags, List<string> otherText)
    {
        if (lines.Count == 0 || lines[0].Trim() != "---") return 0;
        var end = -1;
        for (var i = 1; i < lines.Count && i <= MaxFrontmatterLines; i++)
        {
            var t = lines[i].Trim();
            if (t == "---" || t == "...") { end = i; break; }
        }
        if (end < 0) return 0;

        string? listKey = null;
        for (var i = 1; i < end; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;
            var startsIndented = char.IsWhiteSpace(line[0]) || line[0] == '-';
            if (startsIndented && listKey != null)
            {
                var item = line.Trim();
                if (item.StartsWith('-')) item = item[1..].Trim();
                AddFrontmatterValues(listKey, item, aliases, tags);
                continue;
            }

            var colon = line.IndexOf(':');
            if (!startsIndented && colon > 0)
            {
                var key = line[..colon].Trim().Trim('"', '\'').ToLowerInvariant();
                var value = line[(colon + 1)..].Trim();
                if (key is "aliases" or "alias" or "tags" or "tag")
                {
                    listKey = key;
                    if (value.Length > 0) AddFrontmatterValues(key, value, aliases, tags);
                }
                else
                {
                    listKey = null;
                    if (value.Length > 0) otherText.Add(value);
                }
                continue;
            }

            if (listKey == null) otherText.Add(line.Trim());
        }
        return end + 1;
    }

    private static void AddFrontmatterValues(string key, string value, List<string> aliases, List<string> tags)
    {
        value = value.Trim();
        if (value.StartsWith('[') && value.EndsWith(']')) value = value[1..^1];
        var isTags = key is "tags" or "tag";
        var separators = isTags ? new[] { ',', ' ', '\t' } : new[] { ',' };
        foreach (var raw in value.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var v = raw.Trim('"', '\'').Trim();
            if (isTags) v = v.TrimStart('#');
            if (v.Length == 0) continue;
            (isTags ? tags : aliases).Add(v);
        }
    }
}

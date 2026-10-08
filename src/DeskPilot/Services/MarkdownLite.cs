using System.Text;
using System.Text.RegularExpressions;

namespace DeskPilot.Services;

public enum MdInlineKind { Text, Bold, Code, Link, LineBreak }

public sealed record MdInline(MdInlineKind Kind, string Text, string? Url = null);

public enum MdBlockKind { Paragraph, Heading, Bullet, Numbered, Code }

/// <param name="Level">Heading level (1-6) or list nesting depth (0 = top level).</param>
/// <param name="Marker">Numbered lists: the marker as written, e.g. "3.".</param>
public sealed record MdBlock(MdBlockKind Kind, IReadOnlyList<MdInline> Inlines, string? Code = null, int Level = 0, string? Marker = null);

/// <summary>
/// The small subset of Markdown models actually use in chat replies: paragraphs, headings,
/// bullet and numbered lists, fenced code blocks, **bold**, `inline code` and [links](url).
/// Anything else stays literal text. Unclosed fences (still streaming) render as code.
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"^(\s*)[-*+]\s+(.*)$")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"^(\s*)(\d{1,4}[.)])\s+(.*)$")]
    private static partial Regex NumberedRegex();

    [GeneratedRegex(@"^\[([^\]\r\n]+)\]\(([^)\s]+)\)")]
    private static partial Regex LinkRegex();

    public static IReadOnlyList<MdBlock> Parse(string? text)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var paragraph = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            var inlines = new List<MdInline>();
            for (int i = 0; i < paragraph.Count; i++)
            {
                if (i > 0) inlines.Add(new MdInline(MdInlineKind.LineBreak, "\n"));
                inlines.AddRange(ParseInlines(paragraph[i]));
            }
            blocks.Add(new MdBlock(MdBlockKind.Paragraph, inlines));
            paragraph.Clear();
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                var language = trimmed[3..].Trim();
                var code = new StringBuilder();
                int j = i + 1;
                for (; j < lines.Length; j++)
                {
                    if (lines[j].TrimStart().StartsWith("```", StringComparison.Ordinal)) break;
                    if (code.Length > 0) code.Append('\n');
                    code.Append(lines[j]);
                }
                blocks.Add(new MdBlock(MdBlockKind.Code, Array.Empty<MdInline>(), code.ToString(), Marker: language.Length > 0 ? language : null));
                i = j; // skips the closing fence (or ends the loop when the fence is still open)
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var heading = HeadingRegex().Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, ParseInlines(heading.Groups[2].Value.TrimEnd('#', ' ')), Level: heading.Groups[1].Length));
                continue;
            }

            var bullet = BulletRegex().Match(line);
            if (bullet.Success && !IsHorizontalRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Bullet, ParseInlines(bullet.Groups[2].Value), Level: IndentLevel(bullet.Groups[1].Value)));
                continue;
            }

            var numbered = NumberedRegex().Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Numbered, ParseInlines(numbered.Groups[3].Value),
                    Level: IndentLevel(numbered.Groups[1].Value), Marker: numbered.Groups[2].Value.Replace(')', '.')));
                continue;
            }

            paragraph.Add(line.TrimEnd());
        }
        FlushParagraph();
        return blocks;
    }

    public static IReadOnlyList<MdInline> ParseInlines(string? text)
    {
        var result = new List<MdInline>();
        if (string.IsNullOrEmpty(text)) return result;
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new MdInline(MdInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i + 1)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }
            else if ((c == '*' || c == '_') && i + 1 < text.Length && text[i + 1] == c)
            {
                var marker = new string(c, 2);
                int end = text.IndexOf(marker, i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '[')
            {
                var m = LinkRegex().Match(text[i..]);
                if (m.Success)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Link, m.Groups[1].Value, m.Groups[2].Value));
                    i += m.Length;
                    continue;
                }
            }
            plain.Append(c);
            i++;
        }
        FlushPlain();
        return result;
    }

    private static int IndentLevel(string indent)
    {
        int width = 0;
        foreach (var ch in indent) width += ch == '\t' ? 4 : 1;
        return Math.Min(width / 2, 6);
    }

    private static bool IsHorizontalRule(string trimmed) =>
        trimmed.Length >= 3 && trimmed.All(ch => ch is '-' or '*' or '_' or ' ');
}

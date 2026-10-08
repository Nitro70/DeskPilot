using System.Globalization;
using System.Text;

namespace DeskPilot.Core.Vault;

internal delegate void TokenSink(ReadOnlySpan<char> token);

/// <summary>Tokenizing, decoding and line helpers shared by the index, the search and the tools.</summary>
internal static class VaultText
{
    public const int MaxTokenLength = 64;

    private static readonly HashSet<string> StopwordSet = new(StringComparer.Ordinal)
    {
        "a", "about", "after", "all", "also", "am", "an", "and", "any", "are", "as", "at", "be", "been", "but", "by",
        "can", "could", "did", "do", "does", "for", "from", "had", "has", "have", "he", "her", "here", "him", "his",
        "how", "i", "if", "in", "into", "is", "it", "its", "just", "me", "my", "of", "on", "or", "our", "she", "so",
        "than", "that", "the", "their", "them", "then", "there", "these", "they", "this", "those", "to", "too", "us",
        "was", "we", "were", "what", "when", "where", "which", "while", "who", "why", "will", "with", "would", "you",
        "your",
    };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Stopwords =
        StopwordSet.GetAlternateLookup<ReadOnlySpan<char>>();

    public static bool IsStopword(ReadOnlySpan<char> token) => Stopwords.Contains(token);

    private static bool IsWordChar(char c)
    {
        if (c < 128) return (uint)(c - 'a') <= 'z' - 'a' || (uint)(c - 'A') <= 'Z' - 'A' || (uint)(c - '0') <= 9;
        if (char.IsLetterOrDigit(c)) return true;
        var cat = CharUnicodeInfo.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark;
    }

    /// <summary>
    /// Splits text into lower-case search tokens: runs of Unicode letters/digits, further split on camelCase
    /// boundaries (a camelCase word also yields its joined form so "deskpilot" finds "DeskPilot"). Underscores,
    /// hyphens and all other punctuation separate tokens. Stopwords and one-letter ASCII tokens are dropped.
    /// </summary>
    public static void Tokenize(ReadOnlySpan<char> text, TokenSink sink, bool dropStopwords = true)
    {
        Span<char> buffer = stackalloc char[MaxTokenLength];
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length && IsWordChar(text[i])) i++;
            var word = text[start..i];

            var parts = 0;
            var partStart = 0;
            for (var k = 1; k < word.Length; k++)
            {
                if (IsCamelBoundary(word, k))
                {
                    Emit(word[partStart..k], sink, buffer, dropStopwords);
                    partStart = k;
                    parts++;
                }
            }
            Emit(word[partStart..], sink, buffer, dropStopwords);
            if (parts > 0) Emit(word, sink, buffer, dropStopwords);
        }
    }

    public static List<string> Tokenize(string text, bool dropStopwords = true)
    {
        var list = new List<string>();
        Tokenize(text.AsSpan(), t => list.Add(t.ToString()), dropStopwords);
        return list;
    }

    private static bool IsCamelBoundary(ReadOnlySpan<char> word, int k)
    {
        var prev = word[k - 1];
        var cur = word[k];
        if (char.IsLower(prev) && char.IsUpper(cur)) return true;
        // "HTTPServer": the boundary sits before the last capital of a capital run followed by a lower-case letter.
        return char.IsUpper(prev) && char.IsUpper(cur) && k + 1 < word.Length && char.IsLower(word[k + 1]);
    }

    private static void Emit(ReadOnlySpan<char> raw, TokenSink sink, Span<char> buffer, bool dropStopwords)
    {
        if (raw.Length == 0 || raw.Length > MaxTokenLength) return;
        if (raw.Length == 1 && raw[0] < 128) return;
        var lower = buffer[..raw.Length];
        for (var j = 0; j < raw.Length; j++) lower[j] = char.ToLowerInvariant(raw[j]);
        if (dropStopwords && IsStopword(lower)) return;
        sink(lower);
    }

    /// <summary>Lower-case word runs joined by single spaces, used for phrase matching ("Desk-Pilot  setup" -> "desk pilot setup").</summary>
    public static string NormalizeForPhrase(ReadOnlySpan<char> text)
    {
        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (!IsWordChar(text[i])) { i++; continue; }
            if (sb.Length > 0) sb.Append(' ');
            while (i < text.Length && IsWordChar(text[i])) sb.Append(char.ToLowerInvariant(text[i++]));
        }
        return sb.ToString();
    }

    /// <summary>Number of word runs (stopwords included); the phrase bonus needs at least two.</summary>
    public static int CountWords(ReadOnlySpan<char> text)
    {
        var count = 0;
        var inWord = false;
        foreach (var c in text)
        {
            var w = IsWordChar(c);
            if (w && !inWord) count++;
            inWord = w;
        }
        return count;
    }

    /// <summary>
    /// Decodes file bytes as text. Returns false for binary content: any NUL byte when there is no UTF-16/32 byte
    /// order mark. UTF-8 is the default; a UTF-8 BOM is removed.
    /// </summary>
    public static bool TryDecode(byte[] bytes, out string text)
    {
        text = "";
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0)
            return TryDecodeWith(new UTF32Encoding(false, true), bytes, 4, out text);
        if (bytes.Length >= 4 && bytes[0] == 0 && bytes[1] == 0 && bytes[2] == 0xFE && bytes[3] == 0xFF)
            return TryDecodeWith(new UTF32Encoding(true, true), bytes, 4, out text);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return TryDecodeWith(Encoding.Unicode, bytes, 2, out text);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return TryDecodeWith(Encoding.BigEndianUnicode, bytes, 2, out text);
        if (Array.IndexOf(bytes, (byte)0) >= 0) return false;
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        text = Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset);
        return true;
    }

    private static bool TryDecodeWith(Encoding encoding, byte[] bytes, int offset, out string text)
    {
        try
        {
            text = encoding.GetString(bytes, offset, bytes.Length - offset);
            return text.IndexOf('\0') < 0;
        }
        catch (DecoderFallbackException)
        {
            text = "";
            return false;
        }
    }

    /// <summary>
    /// Reads a whole file without blocking other programs: the note app may be saving it right now, so the
    /// file is opened with read, write and delete sharing. Returns null when it is larger than maxBytes.
    /// </summary>
    public static byte[]? ReadAllBytesShared(string path, long maxBytes)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        var length = fs.Length;
        if (length > maxBytes) return null;
        var buffer = new byte[length];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = fs.Read(buffer, read, buffer.Length - read);
            if (n == 0) break;
            read += n;
        }
        return read == buffer.Length ? buffer : buffer[..read];
    }

    /// <summary>True when the file starts with a UTF-8 BOM or has no BOM at all (so UTF-8 text can be appended).</summary>
    public static bool IsUtf8Compatible(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF))) return false;
        if (head.Length >= 4 && head[0] == 0 && head[1] == 0 && head[2] == 0xFE && head[3] == 0xFF) return false;
        return true;
    }

    /// <summary>Splits text into lines (\n, \r\n or a lone \r). A trailing newline does not start an extra line.</summary>
    public static List<string> SplitLines(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\n' && c != '\r') continue;
            lines.Add(text.Substring(start, i - start));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        if (start < text.Length) lines.Add(text.Substring(start));
        return lines;
    }
}

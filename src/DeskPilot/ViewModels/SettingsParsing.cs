using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskPilot.ViewModels;

/// <summary>
/// Parsing and validation rules for the text fields of the settings editor. Every parser returns
/// false with a readable message instead of throwing, so the UI can show it next to the field.
/// </summary>
public static class SettingsParsing
{
    private static readonly char[] ListSeparators = { ',', ';', '\n', '\r' };

    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);

    public static string FormatDouble(double? value) =>
        value is null ? "" : value.Value.ToString("0.###", CultureInfo.InvariantCulture);

    public static bool TryParseInt(string? text, int min, int max, out int value, out string error)
    {
        value = 0;
        error = "";
        var t = (text ?? "").Trim().Replace("_", "");
        if (t.Length == 0)
        {
            error = $"Enter a whole number from {FormatInt(min)} to {FormatInt(max)}.";
            return false;
        }
        if (!int.TryParse(t, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out value) &&
            !int.TryParse(t, NumberStyles.Integer | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out value))
        {
            error = $"'{t}' is not a whole number. Enter a value from {FormatInt(min)} to {FormatInt(max)}.";
            return false;
        }
        if (value < min || value > max)
        {
            error = $"Must be from {FormatInt(min)} to {FormatInt(max)}.";
            return false;
        }
        return true;
    }

    /// <summary>Empty text means "not set" (null). Accepts '.' or the current culture's decimal separator.</summary>
    public static bool TryParseOptionalDouble(string? text, double min, double max, out double? value, out string error)
    {
        value = null;
        error = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            !double.TryParse(t, NumberStyles.Float, CultureInfo.CurrentCulture, out d))
        {
            error = $"'{t}' is not a number. Leave it empty for the provider default.";
            return false;
        }
        if (double.IsNaN(d) || d < min || d > max)
        {
            error = $"Must be from {min.ToString(CultureInfo.InvariantCulture)} to {max.ToString(CultureInfo.InvariantCulture)}, or empty.";
            return false;
        }
        value = d;
        return true;
    }

    /// <summary>An absolute http:// or https:// URL with a host.</summary>
    public static bool TryValidateHttpUrl(string? text, out string error)
    {
        error = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            error = "Enter the server address, for example https://api.example.com/v1.";
            return false;
        }
        if (!Uri.TryCreate(t, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            error = "Must be a full http:// or https:// address, for example http://127.0.0.1:11434.";
            return false;
        }
        return true;
    }

    /// <summary>Empty is fine (nothing is merged); otherwise it must parse as a JSON object.</summary>
    public static bool TryValidateJsonObject(string? text, out string error)
    {
        error = "";
        var t = (text ?? "").Trim();
        if (t.Length == 0) return true;
        try
        {
            using var doc = JsonDocument.Parse(t, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "Must be a JSON object such as {\"top_p\": 0.9}, or empty.";
                return false;
            }
            return true;
        }
        catch (JsonException ex)
        {
            error = "Not valid JSON: " + ex.Message;
            return false;
        }
    }

    /// <summary>KEY=VALUE per line. Blank lines and lines starting with # are ignored.</summary>
    public static bool TryParseEnvLines(string? text, out Dictionary<string, string> values, out string error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        var lineNo = 0;
        foreach (var raw in SplitLines(text))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0)
            {
                error = $"Line {lineNo}: use NAME=value (got '{Clip(line)}').";
                return false;
            }
            var name = line[..eq].Trim();
            if (name.Length == 0 || name.Any(char.IsWhiteSpace) || name.Contains('\0'))
            {
                error = $"Line {lineNo}: '{Clip(name)}' is not a valid environment variable name.";
                return false;
            }
            values[name] = line[(eq + 1)..].Trim();
        }
        return true;
    }

    /// <summary>"Name: value" per line. Blank lines and lines starting with # are ignored.</summary>
    public static bool TryParseHeaderLines(string? text, out Dictionary<string, string> values, out string error)
    {
        values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        error = "";
        var lineNo = 0;
        foreach (var raw in SplitLines(text))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                error = $"Line {lineNo}: use Name: value (got '{Clip(line)}').";
                return false;
            }
            var name = line[..colon].Trim();
            if (!IsHeaderToken(name))
            {
                error = $"Line {lineNo}: '{Clip(name)}' is not a valid header name (letters, digits and - only, no spaces).";
                return false;
            }
            values[name] = line[(colon + 1)..].Trim();
        }
        return true;
    }

    public static string FormatEnvLines(IReadOnlyDictionary<string, string>? values) =>
        values == null ? "" : string.Join(Environment.NewLine, values.Select(kv => $"{kv.Key}={kv.Value}"));

    public static string FormatHeaderLines(IReadOnlyDictionary<string, string>? values) =>
        values == null ? "" : string.Join(Environment.NewLine, values.Select(kv => $"{kv.Key}: {kv.Value}"));

    /// <summary>Comma, semicolon or newline separated; trimmed, empty entries dropped, duplicates removed (case-insensitive).</summary>
    public static List<string> ParseList(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var part in (text ?? "").Split(ListSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (seen.Add(part)) result.Add(part);
        return result;
    }

    /// <summary>One entry per line (patterns may contain commas).</summary>
    public static List<string> ParseLines(string? text)
    {
        var result = new List<string>();
        foreach (var raw in SplitLines(text))
        {
            var line = raw.Trim();
            if (line.Length > 0 && !result.Contains(line, StringComparer.Ordinal)) result.Add(line);
        }
        return result;
    }

    public static string FormatList(IEnumerable<string>? items) => items == null ? "" : string.Join(", ", items);

    public static string FormatLines(IEnumerable<string>? items) => items == null ? "" : string.Join(Environment.NewLine, items);

    /// <summary>Process names without ".exe", e.g. "KeePass.exe, 1Password" -> KeePass, 1Password.</summary>
    public static List<string> ParseProcessNames(string? text)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var item in ParseList(text))
        {
            var name = item.Trim('"', ' ');
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            name = Path.GetFileName(name.Replace('/', '\\'));
            if (name.Length > 0 && seen.Add(name)) result.Add(name);
        }
        return result;
    }

    /// <summary>"md, .TXT, *.org" -> .md, .txt, .org.</summary>
    public static bool TryParseExtensions(string? text, out List<string> extensions, out string error)
    {
        extensions = new List<string>();
        error = "";
        foreach (var item in ParseList(text?.Replace(' ', ',')))
        {
            var ext = item.TrimStart('*').Trim().ToLowerInvariant();
            if (!ext.StartsWith('.')) ext = "." + ext;
            if (ext.Length < 2 || ext.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || ext.EndsWith('.'))
            {
                error = $"'{Clip(item)}' is not a file extension. Use a list like .md, .txt.";
                return false;
            }
            if (!extensions.Contains(ext)) extensions.Add(ext);
        }
        if (extensions.Count == 0)
        {
            error = "Add at least one extension, for example .md.";
            return false;
        }
        return true;
    }

    public static List<string> ParseFolderNames(string? text) =>
        ParseList(text).Select(f => f.Trim().Trim('\\', '/')).Where(f => f.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Every line must compile as a .NET regular expression.</summary>
    public static bool TryParseRegexLines(string? text, out List<string> patterns, out string error)
    {
        patterns = ParseLines(text);
        error = "";
        for (var i = 0; i < patterns.Count; i++)
        {
            try
            {
                _ = new Regex(patterns[i], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
            }
            catch (ArgumentException ex)
            {
                error = $"Pattern '{Clip(patterns[i])}' is not a valid regular expression: {ex.Message}";
                return false;
            }
        }
        return true;
    }

    /// <summary>Inserts text at the caret (replacing a selection) and returns the new caret position.</summary>
    public static string InsertAt(string? original, string insert, int selectionStart, int selectionLength, out int caret)
    {
        var s = original ?? "";
        var start = Math.Clamp(selectionStart, 0, s.Length);
        var length = Math.Clamp(selectionLength, 0, s.Length - start);
        var result = s[..start] + insert + s[(start + length)..];
        caret = start + insert.Length;
        return result;
    }

    /// <summary>Hides any occurrence of a secret inside a message (provider errors sometimes echo request data).</summary>
    public static string Redact(string? message, string? secret)
    {
        var m = message ?? "";
        if (string.IsNullOrEmpty(secret) || secret.Length < 4) return m;
        return m.Replace(secret, Core.Settings.SecretProtector.Mask(secret), StringComparison.Ordinal);
    }

    private static IEnumerable<string> SplitLines(string? text) =>
        (text ?? "").Replace("\r\n", "\n").Split('\n');

    private static bool IsHeaderToken(string name)
    {
        if (name.Length == 0) return false;
        const string extra = "!#$%&'*+-.^_`|~";
        foreach (var c in name)
            if (c > 127 || !(char.IsLetterOrDigit(c) || extra.Contains(c))) return false;
        return true;
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";
}

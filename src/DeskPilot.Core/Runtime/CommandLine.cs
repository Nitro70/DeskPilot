using System.Text;

namespace DeskPilot.Core.Runtime;

public static class CommandLine
{
    /// <summary>Splits a user-entered argument string like a shell would: spaces separate, double quotes group, \" escapes.</summary>
    public static List<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var sb = new StringBuilder();
        bool inQuotes = false, any = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; any = true; continue; }
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) { result.Add(sb.ToString()); sb.Clear(); any = false; }
                continue;
            }
            sb.Append(c);
            any = true;
        }
        if (any) result.Add(sb.ToString());
        return result;
    }
}

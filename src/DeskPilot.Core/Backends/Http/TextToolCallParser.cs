using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DeskPilot.Core.Backends.Http;

/// <summary>
/// Fallback for local models whose server does not turn their tool-call markup into native tool calls:
/// reads &lt;tool_call&gt;{json}&lt;/tool_call&gt; blocks, fenced JSON blocks, or a reply that is a single JSON
/// object, each shaped {"name": ..., "arguments": {...}}. Only names of known tools count.
/// </summary>
internal static class TextToolCallParser
{
    private const string OpenTag = "<tool_call>";
    private const string CloseTag = "</tool_call>";

    private static readonly Regex Fenced = new(@"```[ \t]*(?:json|tool_call|tool|function)?[ \t]*\r?\n?(?<json>\{.*?\})\s*```",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public sealed record ParsedCall(string Name, string ArgumentsJson);

    /// <summary>Returns true when at least one call was found; <paramref name="remainingText"/> is the text without the markup.</summary>
    public static bool TryParse(string? text, ISet<string> knownTools, out List<ParsedCall> calls, out string remainingText)
    {
        calls = new List<ParsedCall>();
        remainingText = text ?? "";
        if (string.IsNullOrWhiteSpace(text) || knownTools.Count == 0) return false;

        // 1. <tool_call>...</tool_call> (the closing tag may be missing at the end of the reply).
        if (text.Contains(OpenTag, StringComparison.OrdinalIgnoreCase))
        {
            var rest = new System.Text.StringBuilder();
            int pos = 0;
            while (true)
            {
                int open = text.IndexOf(OpenTag, pos, StringComparison.OrdinalIgnoreCase);
                if (open < 0) { rest.Append(text, pos, text.Length - pos); break; }
                rest.Append(text, pos, open - pos);
                int start = open + OpenTag.Length;
                int close = text.IndexOf(CloseTag, start, StringComparison.OrdinalIgnoreCase);
                var inner = close < 0 ? text[start..] : text[start..close];
                if (TryReadCall(inner, knownTools, out var call)) calls.Add(call);
                else rest.Append(text, open, (close < 0 ? text.Length : close + CloseTag.Length) - open);
                if (close < 0) break;
                pos = close + CloseTag.Length;
            }
            if (calls.Count > 0)
            {
                remainingText = rest.ToString().Trim();
                return true;
            }
        }

        // 2. Fenced ```json blocks.
        var found = new List<Match>();
        foreach (Match m in Fenced.Matches(text))
        {
            if (TryReadCall(m.Groups["json"].Value, knownTools, out var call))
            {
                calls.Add(call);
                found.Add(m);
            }
        }
        if (calls.Count > 0)
        {
            var sb = new System.Text.StringBuilder();
            int pos = 0;
            foreach (var m in found)
            {
                sb.Append(text, pos, m.Index - pos);
                pos = m.Index + m.Length;
            }
            sb.Append(text, pos, text.Length - pos);
            remainingText = sb.ToString().Trim();
            return true;
        }

        // 3. The whole reply is one JSON object.
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}') && TryReadCall(trimmed, knownTools, out var single))
        {
            calls.Add(single);
            remainingText = "";
            return true;
        }
        return false;
    }

    private static bool TryReadCall(string json, ISet<string> knownTools, out ParsedCall call)
    {
        call = null!;
        if (JsonUtil.TryParse(json.Trim()) is not JsonObject obj) return false;
        // {"function": {"name": ..., "arguments": ...}} wrapper used by some templates.
        if (obj["function"] is JsonObject fn && obj["name"] is null) obj = fn;
        var name = JsonUtil.Str(obj["name"])?.Trim();
        if (string.IsNullOrEmpty(name) || !knownTools.Contains(name)) return false;
        var args = obj["arguments"] ?? obj["parameters"] ?? obj["args"] ?? obj["input"];
        if (args is not null and not JsonObject && JsonUtil.Str(args) is null) return false;
        call = new ParsedCall(name, JsonUtil.ArgumentsText(args));
        return true;
    }
}

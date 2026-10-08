using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeskPilot.Core.Backends.Acp;

/// <summary>
/// Decides which tool calls an ACP agent may run: only DeskPilot's own MCP tools. Matching is on exact,
/// structured identities (never a substring of a title or command), because an agent's shell tool shows
/// the model-written command as its title and "deskpilot" inside a command must not unlock it.
/// </summary>
internal static class AcpPermissions
{
    public const string AllowOnce = "allow_once";
    public const string AllowAlways = "allow_always";
    public const string RejectOnce = "reject_once";
    public const string RejectAlways = "reject_always";

    private static readonly Regex GeminiMcpTitle = new(@"^(?<tool>[A-Za-z0-9_.\-]+) \((?<server>[^()]+) MCP Server\)$", RegexOptions.Compiled);
    private static readonly string[] ServerKeys = { "server", "serverName", "server_name", "mcpServer", "mcp_server", "mcpServerName", "mcp_server_name" };
    private static readonly string[] ToolKeys = { "tool", "toolName", "tool_name", "name", "mcpToolName" };

    /// <param name="toolCall">The ACP ToolCall / ToolCallUpdate object.</param>
    /// <param name="options">The permission options offered (may be Undefined for plain tool_call updates).</param>
    /// <param name="mcpRestrictedToDeskPilot">The agent loads no MCP server except DeskPilot's, so an MCP-type
    /// confirmation can only be for one of DeskPilot's tools.</param>
    public static bool IsDeskPilotToolCall(JsonElement toolCall, JsonElement options, string serverName,
        IReadOnlyCollection<string> toolNames, bool mcpRestrictedToDeskPilot)
    {
        if (toolCall.ValueKind != JsonValueKind.Object) return false;
        var names = new HashSet<string>(toolNames, StringComparer.OrdinalIgnoreCase);

        if (toolCall.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            && TitleNamesDeskPilotTool(t.GetString() ?? "", serverName, names))
            return true;

        if (toolCall.TryGetProperty("_meta", out var meta) && MetaNamesDeskPilotTool(meta, serverName, names, depth: 0))
            return true;

        // rawInput holds model-written arguments, so only its top level is read, and only as structure.
        if (toolCall.TryGetProperty("rawInput", out var raw) && IsStructuredDeskPilotCall(raw, serverName, names))
            return true;

        return mcpRestrictedToDeskPilot && IsMcpConfirmation(options);
    }

    /// <summary>"mcp__deskpilot__click", "mcp_deskpilot_click", "deskpilot/click", ... for a known tool.</summary>
    public static bool IsQualifiedToolName(string value, string serverName, ICollection<string> toolNames)
    {
        var s = value.Trim();
        foreach (var prefix in new[]
                 {
                     $"mcp__{serverName}__", $"mcp_{serverName}_", $"{serverName}__", $"{serverName}/",
                     $"{serverName}.", $"{serverName}:", $"{serverName}_",
                 })
        {
            if (s.Length > prefix.Length && s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && toolNames.Contains(s[prefix.Length..]))
                return true;
        }
        return false;
    }

    private static bool TitleNamesDeskPilotTool(string title, string serverName, ICollection<string> toolNames)
    {
        var t = title.Trim();
        if (IsQualifiedToolName(t, serverName, toolNames)) return true;
        // Gemini CLI titles an MCP call "<tool> (<server> MCP Server)".
        var m = GeminiMcpTitle.Match(t);
        return m.Success
               && string.Equals(m.Groups["server"].Value.Trim(), serverName, StringComparison.OrdinalIgnoreCase)
               && toolNames.Contains(m.Groups["tool"].Value);
    }

    private static bool MetaNamesDeskPilotTool(JsonElement element, string serverName, ICollection<string> toolNames, int depth)
    {
        if (depth > 4) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (IsStructuredDeskPilotCall(element, serverName, toolNames)) return true;
                foreach (var p in element.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String)
                    {
                        if (ToolKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase) && IsQualifiedToolName(p.Value.GetString() ?? "", serverName, toolNames))
                            return true;
                    }
                    else if (MetaNamesDeskPilotTool(p.Value, serverName, toolNames, depth + 1))
                    {
                        return true;
                    }
                }
                return false;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    if (MetaNamesDeskPilotTool(item, serverName, toolNames, depth + 1)) return true;
                return false;
            default:
                return false;
        }
    }

    /// <summary>An object such as {server: "deskpilot", tool: "click"} or {toolName: "mcp__deskpilot__click"}.</summary>
    private static bool IsStructuredDeskPilotCall(JsonElement obj, string serverName, ICollection<string> toolNames)
    {
        if (obj.ValueKind != JsonValueKind.Object) return false;
        string? server = null;
        var tools = new List<string>();
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            if (server == null && ServerKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase)) server = p.Value.GetString();
            else if (ToolKeys.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            {
                var v = p.Value.GetString() ?? "";
                if (IsQualifiedToolName(v, serverName, toolNames)) return true;
                tools.Add(v.Trim());
            }
        }
        return server != null
               && string.Equals(server.Trim(), serverName, StringComparison.OrdinalIgnoreCase)
               && tools.Any(toolNames.Contains);
    }

    /// <summary>Gemini CLI offers "allow all tools of this server" only when confirming an MCP tool.</summary>
    private static bool IsMcpConfirmation(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return false;
        foreach (var o in options.EnumerateArray())
        {
            var id = OptionId(o);
            if (id is "proceed_always_server" or "proceed_always_tool") return true;
        }
        return false;
    }

    /// <summary>Prefers allow_always for this session, then allow_once. Skips options that would save an
    /// approval into the agent's settings for future sessions unless nothing else allows the call.</summary>
    public static string? PickAllowOption(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return null;
        string? always = null, once = null, persistent = null;
        foreach (var o in options.EnumerateArray())
        {
            var id = OptionId(o);
            if (id == null) continue;
            var kind = Str(o, "kind");
            if (kind == AllowAlways)
            {
                if (IsPersistent(o, id)) persistent ??= id;
                else always ??= id;
            }
            else if (kind == AllowOnce)
            {
                once ??= id;
            }
        }
        return always ?? once ?? persistent;
    }

    public static string? PickRejectOption(JsonElement options)
    {
        if (options.ValueKind != JsonValueKind.Array) return null;
        foreach (var o in options.EnumerateArray())
            if (Str(o, "kind") == RejectOnce && OptionId(o) is { } id) return id;
        return null;
    }

    private static bool IsPersistent(JsonElement option, string id) =>
        id.Contains("save", StringComparison.OrdinalIgnoreCase)
        || (Str(option, "name") ?? "").Contains("future", StringComparison.OrdinalIgnoreCase);

    private static string? OptionId(JsonElement option) => Str(option, "optionId");

    private static string? Str(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

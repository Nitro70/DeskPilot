using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.Core.Backends.Http;

internal static class JsonUtil
{
    /// <summary>Compact output that keeps non-ASCII text readable (smaller request bodies).</summary>
    public static readonly JsonSerializerOptions WireOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Serialize(JsonNode node) => node.ToJsonString(WireOptions);

    public static JsonNode? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            return JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string? Str(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        return v.TryGetValue<string>(out var s) ? s : null;
    }

    public static int Int(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return (int)Math.Clamp(l, int.MinValue, int.MaxValue);
        if (v.TryGetValue<double>(out var d)) return (int)d;
        return 0;
    }

    /// <summary>Text of an OpenAI-style content field: a string, or an array of {type: text, text} parts.</summary>
    public static string ContentText(JsonNode? content)
    {
        if (content is null) return "";
        if (Str(content) is { } s) return s;
        if (content is JsonArray parts)
        {
            var texts = new List<string>();
            foreach (var part in parts)
            {
                if (part is JsonObject o && Str(o["text"]) is { Length: > 0 } t) texts.Add(t);
                else if (Str(part) is { Length: > 0 } plain) texts.Add(plain);
            }
            return string.Join("\n", texts);
        }
        return "";
    }

    /// <summary>Tool arguments as JSON text, whether the model sent an object or a JSON string.</summary>
    public static string ArgumentsText(JsonNode? args)
    {
        if (args is null) return "{}";
        if (Str(args) is { } s) return string.IsNullOrWhiteSpace(s) ? "{}" : s;
        return Serialize(args);
    }

    public static JsonNode SchemaNode(ToolSpec tool)
    {
        JsonNode? node = tool.InputSchema.ValueKind == JsonValueKind.Object ? JsonNode.Parse(tool.InputSchema.GetRawText()) : null;
        var schema = node as JsonObject ?? new JsonObject { ["type"] = "object" };
        // Some OpenAI-compatible servers reject an object schema without "properties".
        if (!schema.ContainsKey("properties")) schema["properties"] = new JsonObject();
        return schema;
    }

    /// <summary>OpenAI / Ollama function tool shape.</summary>
    public static JsonArray FunctionTools(IReadOnlyList<ToolSpec> tools)
    {
        var arr = new JsonArray();
        foreach (var t in tools)
        {
            arr.Add(new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = SchemaNode(t),
                },
            });
        }
        return arr;
    }

    /// <summary>Deep-merges <paramref name="source"/> into <paramref name="target"/>: objects merge, anything else replaces, null removes.</summary>
    public static void DeepMerge(JsonObject target, JsonObject source)
    {
        foreach (var (key, value) in source.ToList())
        {
            if (value is null)
            {
                target.Remove(key);
                continue;
            }
            if (value is JsonObject srcObj && target[key] is JsonObject dstObj)
            {
                DeepMerge(dstObj, srcObj);
                continue;
            }
            target[key] = value.DeepClone();
        }
    }

    /// <summary>Parses the profile's ExtraBodyJson. Returns null when blank; throws FormatException when not a JSON object.</summary>
    public static JsonObject? ParseExtraBody(string? extraBodyJson)
    {
        if (string.IsNullOrWhiteSpace(extraBodyJson)) return null;
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(extraBodyJson, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new FormatException("the extra body JSON is not valid JSON: " + ex.Message);
        }
        return node as JsonObject ?? throw new FormatException("the extra body JSON must be a JSON object like {\"key\": \"value\"}");
    }

    public static string NewToolCallId() => "call_" + Guid.NewGuid().ToString("N")[..24];
}

using System.Globalization;
using System.Text.Json;

namespace DeskPilot.Core.Runtime;

/// <summary>
/// Lenient readers for tool arguments. Models (especially local ones) send numbers as strings,
/// floats for ints, "true" for booleans and so on; accept all of that.
/// </summary>
public static class JsonArgs
{
    public static bool Has(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);

    public static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => v.GetRawText(),
            JsonValueKind.Array or JsonValueKind.Object => v.GetRawText(),
            _ => null,
        };
    }

    public static double? GetDouble(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        if (v.ValueKind == JsonValueKind.String &&
            double.TryParse(v.GetString()?.Trim().TrimEnd('p', 'x', 'P', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var s)) return s;
        return null;
    }

    public static int? GetInt(JsonElement args, string name)
    {
        var d = GetDouble(args, name);
        return d is null ? null : (int)Math.Round(d.Value, MidpointRounding.AwayFromZero);
    }

    public static bool? GetBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => v.TryGetDouble(out var d) && d != 0,
            JsonValueKind.String => v.GetString()?.Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" or "on" => true,
                "false" or "no" or "0" or "off" => false,
                _ => null,
            },
            _ => null,
        };
    }

    public static IReadOnlyList<string> GetStringList(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return Array.Empty<string>();
        if (v.ValueKind == JsonValueKind.Array)
            return v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).Where(s => s.Length > 0).ToList();
        if (v.ValueKind == JsonValueKind.String)
            return (v.GetString() ?? "").Split(new[] { ',', '+' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Array.Empty<string>();
    }

    /// <summary>Parses tool-call argument text from a model. Returns an empty object for blank/invalid input.</summary>
    public static JsonElement ParseArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return EmptyObject();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Some models double-encode: "{\"x\":1}"
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (!string.IsNullOrWhiteSpace(inner) && inner.TrimStart().StartsWith('{')) return ParseArguments(inner);
            }
            return root.ValueKind == JsonValueKind.Object ? root.Clone() : EmptyObject();
        }
        catch (JsonException)
        {
            return EmptyObject();
        }
    }

    public static JsonElement EmptyObject()
    {
        using var doc = JsonDocument.Parse("{}");
        return doc.RootElement.Clone();
    }
}

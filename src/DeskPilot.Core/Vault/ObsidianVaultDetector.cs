using System.Text.Json;

namespace DeskPilot.Core.Vault;

/// <summary>Finds the vault folders the Obsidian app knows about, most recently used first.</summary>
public static class ObsidianVaultDetector
{
    private const long MaxConfigBytes = 4 * 1024 * 1024;

    /// <summary>Vault folders Obsidian knows about (from %APPDATA%\\obsidian\\obsidian.json) that still exist.</summary>
    public static IReadOnlyList<string> FindVaults()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (string.IsNullOrEmpty(appData)) return Array.Empty<string>();
            return FindVaults(Path.Combine(appData, "obsidian", "obsidian.json"));
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Parses an obsidian.json file: {"vaults": {"&lt;id&gt;": {"path": "...", "ts": 123, "open": true}}}.
    /// Returns existing folders ordered by ts descending (open vaults first on ties). Never throws.
    /// </summary>
    internal static IReadOnlyList<string> FindVaults(string jsonPath)
    {
        try
        {
            var info = new FileInfo(jsonPath);
            if (!info.Exists || info.Length > MaxConfigBytes) return Array.Empty<string>();
            var json = File.ReadAllText(jsonPath);
            return ParseVaults(json).Where(Directory.Exists).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Vault paths from obsidian.json text, best first, without checking that they exist.</summary>
    internal static IReadOnlyList<string> ParseVaults(string json)
    {
        var found = new List<(string Path, double Ts, bool Open)>();
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("vaults", out var vaults)) return Array.Empty<string>();

            IEnumerable<JsonElement> entries = vaults.ValueKind switch
            {
                JsonValueKind.Object => vaults.EnumerateObject().Select(p => p.Value),
                JsonValueKind.Array => vaults.EnumerateArray(),
                _ => Array.Empty<JsonElement>(),
            };

            foreach (var entry in entries)
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                if (!entry.TryGetProperty("path", out var p) || p.ValueKind != JsonValueKind.String) continue;
                var raw = p.GetString();
                if (string.IsNullOrWhiteSpace(raw)) continue;

                string full;
                try
                {
                    full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw.Trim()));
                }
                catch (Exception)
                {
                    continue;
                }

                double ts = 0;
                if (entry.TryGetProperty("ts", out var t))
                {
                    if (t.ValueKind == JsonValueKind.Number && t.TryGetDouble(out var d)) ts = d;
                    else if (t.ValueKind == JsonValueKind.String && double.TryParse(t.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds)) ts = ds;
                }
                var open = entry.TryGetProperty("open", out var o) && o.ValueKind == JsonValueKind.True;
                found.Add((full, ts, open));
            }
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return found
            .OrderByDescending(v => v.Ts)
            .ThenByDescending(v => v.Open)
            .ThenBy(v => v.Path, StringComparer.OrdinalIgnoreCase)
            .Where(v => seen.Add(v.Path))
            .Select(v => v.Path)
            .ToList();
    }
}

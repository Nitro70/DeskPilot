using System.Text.Json;

namespace DeskPilot.Core.Abstractions;

/// <summary>A tool the model can call. InputSchema is a JSON Schema object (type: object).</summary>
public sealed record ToolSpec(string Name, string Description, JsonElement InputSchema)
{
    public static ToolSpec Create(string name, string description, string inputSchemaJson)
    {
        using var doc = JsonDocument.Parse(inputSchemaJson);
        return new ToolSpec(name, description, doc.RootElement.Clone());
    }
}

/// <summary>An image returned by a tool (screenshots, zooms). Base64 without data: prefix.</summary>
public sealed record ToolImage(string MediaType, string Base64Data, int Width, int Height)
{
    public static ToolImage FromBytes(byte[] data, string mediaType, int width, int height) =>
        new(mediaType, Convert.ToBase64String(data), width, height);

    public byte[] GetBytes() => Convert.FromBase64String(Base64Data);
}

public sealed class ToolResult
{
    public string Text { get; init; } = "";
    public IReadOnlyList<ToolImage> Images { get; init; } = Array.Empty<ToolImage>();
    public bool IsError { get; init; }

    public static ToolResult Ok(string text, params ToolImage[] images) => new() { Text = text, Images = images };
    public static ToolResult Error(string text) => new() { Text = text, IsError = true };
}

/// <summary>A set of tools. Hosts can be composed with CompositeToolHost.</summary>
public interface IToolHost
{
    /// <summary>The tools currently available (may depend on settings, e.g. vault configured).</summary>
    IReadOnlyList<ToolSpec> GetTools();

    /// <summary>
    /// Executes a tool. Never throws for tool-level failures: returns ToolResult.Error with a
    /// message the model can act on. Throws OperationCanceledException only when ct is cancelled.
    /// Unknown tool names return an error result.
    /// </summary>
    Task<ToolResult> ExecuteAsync(string name, JsonElement arguments, CancellationToken ct);
}

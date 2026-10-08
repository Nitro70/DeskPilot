using DeskPilot.Core.Abstractions;

namespace DeskPilot.ViewModels;

/// <summary>An entry of the profile picker.</summary>
public sealed record ProfileOption(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>An entry of the model picker. ToString is the id so the editable combo shows the id.</summary>
public sealed record ModelOption(string Id, string DisplayName, string Hints)
{
    public bool HasHints => Hints.Length > 0;
    public bool HasDisplayName => DisplayName.Length > 0 && !string.Equals(DisplayName, Id, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Id;

    public static ModelOption From(ModelInfo info) =>
        new(info.Id, info.DisplayName ?? "", BuildHints(info));

    public static ModelOption FromId(string id) => new(id, "", "");

    /// <summary>"vision · thinking", "text only", "no tools"... empty when nothing is known.</summary>
    public static string BuildHints(ModelInfo info)
    {
        var parts = new List<string>();
        if (info.SupportsVision == true) parts.Add("vision");
        else if (info.SupportsVision == false) parts.Add("text only");
        if (info.SupportsThinking == true) parts.Add("thinking");
        if (info.SupportsTools == false) parts.Add("no tools");
        return string.Join(" · ", parts);
    }
}

using System.Globalization;
using DeskPilot.Core.Abstractions;

namespace DeskPilot.ViewModels;

public sealed record ConfirmDetail(string Label, string Value, bool IsMono);

/// <summary>Everything the confirmation dialog shows about a proposed action.</summary>
public sealed class ConfirmViewModel
{
    public const int MaxTextChars = 600;

    public ConfirmViewModel(ProposedAction action)
    {
        Action = action;
        Summary = string.IsNullOrWhiteSpace(action.Summary) ? action.Tool : action.Summary;
        Risk = action.Risk;
        RiskText = RiskLabel(action.Risk);

        var details = new List<ConfirmDetail> { new("Tool", action.Tool, true) };
        if (action.Target is { } p) details.Add(new("Screen point", $"({p.X}, {p.Y})", false));
        if (!string.IsNullOrEmpty(action.Text)) details.Add(new("Text", Clip(action.Text), true));
        if (action.Keys != null) details.Add(new("Keys", action.Keys.ToString(), true));
        if (!string.IsNullOrWhiteSpace(action.LaunchTarget)) details.Add(new("Opens", Clip(action.LaunchTarget), true));
        if (!string.IsNullOrWhiteSpace(action.Command)) details.Add(new("Command", Clip(action.Command), true));
        Details = details;
    }

    public ProposedAction Action { get; }
    public string Summary { get; }
    public ActionRisk Risk { get; }
    public string RiskText { get; }
    public IReadOnlyList<ConfirmDetail> Details { get; }

    public static string RiskLabel(ActionRisk risk) => risk switch
    {
        ActionRisk.None => "No risk",
        ActionRisk.Low => "Low risk",
        ActionRisk.Medium => "Medium risk",
        ActionRisk.High => "High risk",
        _ => risk.ToString(),
    };

    private static string Clip(string text) =>
        text.Length <= MaxTextChars ? text : text[..MaxTextChars] + $"… ({text.Length.ToString(CultureInfo.InvariantCulture)} characters in total)";
}

using System.Globalization;
using System.Runtime.InteropServices;
using DeskPilot.Core.Abstractions;
using DeskPilot.Core.Settings;

namespace DeskPilot.Core.Prompts;

/// <param name="ScreenDescription">e.g. "screenshots are 1280x720 and show monitor 1 (2560x1440 physical pixels)".</param>
public sealed record PromptContext(
    AppSettings Settings,
    ProviderProfile Profile,
    IReadOnlyList<ToolSpec> Tools,
    string ScreenDescription,
    bool VaultAvailable,
    DateTime Now);

public static class PromptBuilder
{
    /// <summary>Placeholders a custom prompt can use (shown in the advanced prompt editor).</summary>
    public static readonly IReadOnlyList<(string Name, string Description)> Placeholders = new[]
    {
        ("{{OS}}", "Windows version"),
        ("{{DATE}}", "Local date, e.g. Wednesday, October 7, 2026"),
        ("{{TIME}}", "Local time"),
        ("{{SCREEN}}", "Screenshot size and which monitor it shows"),
        ("{{COORDINATES}}", "How click coordinates work (screenshot pixels or 0-1000)"),
        ("{{TOOLS}}", "Comma-separated list of available tool names"),
        ("{{SAFETY}}", "Administrator-mode rule (on/off)"),
        ("{{VAULT}}", "Vault instructions, or nothing when no vault is set"),
        ("{{VISION}}", "Note for text-only models, or nothing"),
        ("{{DRYRUN}}", "Dry-run note, or nothing"),
        ("{{MAX_STEPS}}", "Action limit per request"),
        ("{{USER_INSTRUCTIONS}}", "Your standing instructions from settings, or nothing"),
    };

    public static string Build(PromptContext ctx)
    {
        var s = ctx.Settings;
        var template = string.IsNullOrWhiteSpace(s.Prompt.CustomSystemPrompt) ? DefaultPrompts.ComputerUse : s.Prompt.CustomSystemPrompt;

        var coordinates = s.Screen.Coordinates == CoordinateMode.Normalized1000
            ? "Coordinates: x and y are on a 0-1000 scale across the screenshot (0,0 = top-left, 1000,1000 = bottom-right), whatever the real resolution."
            : "Coordinates: x and y are pixels of the screenshot image you see (0,0 = top-left). DeskPilot converts them to the real screen.";

        var vault = "";
        if (ctx.VaultAvailable)
            vault = DefaultPrompts.VaultSection.Replace("{{VAULT_WRITE}}", s.Vault.AllowWrites ? DefaultPrompts.VaultWriteNote : "");

        var userInstructions = string.IsNullOrWhiteSpace(s.Prompt.UserInstructions)
            ? ""
            : "\n# The user's standing instructions\n" + s.Prompt.UserInstructions.Trim() + "\n";

        var values = new Dictionary<string, string>
        {
            ["{{OS}}"] = DescribeOs(),
            ["{{DATE}}"] = ctx.Now.ToString("dddd, MMMM d, yyyy", CultureInfo.InvariantCulture),
            ["{{TIME}}"] = ctx.Now.ToString("HH:mm", CultureInfo.InvariantCulture),
            ["{{SCREEN}}"] = ctx.ScreenDescription,
            ["{{COORDINATES}}"] = coordinates,
            ["{{TOOLS}}"] = string.Join(", ", ctx.Tools.Select(t => t.Name)),
            ["{{SAFETY}}"] = (s.Safety.AllowAdmin ? DefaultPrompts.SafetyAdminOn : DefaultPrompts.SafetyAdminOff).TrimEnd(),
            ["{{VAULT}}"] = vault,
            ["{{VISION}}"] = ctx.Profile.SupportsVision ? "" : DefaultPrompts.NoVision,
            ["{{DRYRUN}}"] = s.Safety.DryRun ? DefaultPrompts.DryRun : "",
            ["{{MAX_STEPS}}"] = s.Safety.MaxStepsPerTurn.ToString(CultureInfo.InvariantCulture),
            ["{{USER_INSTRUCTIONS}}"] = userInstructions,
        };

        var result = template;
        foreach (var (k, v) in values) result = result.Replace(k, v, StringComparison.OrdinalIgnoreCase);
        return result.Trim() + "\n";
    }

    private static string DescribeOs()
    {
        var v = Environment.OSVersion.Version;
        var name = v.Major >= 10 && v.Build >= 22000 ? "Windows 11" : v.Major >= 10 ? "Windows 10" : "Windows";
        return $"{name} (build {v.Build}, {RuntimeInformation.OSArchitecture})";
    }
}

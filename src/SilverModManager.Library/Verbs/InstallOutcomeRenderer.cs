using System.Text.Json;
using SilverModManager.Abstractions.Cli;
using SilverModManager.Abstractions.Library;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Library.Verbs;

/// <summary>
/// How the install verbs print what an install did, so every mod source reads the same.
/// </summary>
internal static class InstallOutcomeRenderer
{
    private sealed record OutcomeRow(string Name, string Outcome, bool Downloaded, bool Dependency);

    public static async Task Print(IRenderer renderer, IReadOnlyList<InstallOutcome> outcomes)
    {
        var changed = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Downloaded) await renderer.TextLine($"[downloaded] {outcome.Name}");

            switch (outcome.Outcome)
            {
                case LoadoutPackageOutcome.AlreadyEnabled:
                    await renderer.TextLine($"[already in loadout] {outcome.Name}");
                    break;
                case LoadoutPackageOutcome.Enabled:
                    await renderer.TextLine($"[enabled] {outcome.Name}");
                    changed++;
                    break;
                case LoadoutPackageOutcome.Installed:
                    await renderer.TextLine($"[installed] {outcome.Name}");
                    changed++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(outcome), outcome.Outcome, null);
            }
        }

        if (changed == 0) await renderer.TextLine("Nothing new to install.");
    }

    /// <summary>The outcomes as one JSON array, then <paramref name="extra"/> as a second JSON value when given.</summary>
    public static string Json(IReadOnlyList<InstallOutcome> outcomes, object? extra = null)
    {
        var rows = outcomes.Select(o => new OutcomeRow(o.Name, OutcomeName(o.Outcome), o.Downloaded, o.Dependency));
        var json = JsonSerializer.Serialize(rows, CliJson.Options);
        return extra is null ? json : json + "\n" + JsonSerializer.Serialize(extra, CliJson.Options);
    }

    /// <summary>The outcome as the JSON names it.</summary>
    public static string OutcomeName(LoadoutPackageOutcome outcome) => outcome switch
    {
        LoadoutPackageOutcome.Installed => "installed",
        LoadoutPackageOutcome.Enabled => "enabled",
        LoadoutPackageOutcome.AlreadyEnabled => "alreadyEnabled",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null),
    };
}

using SilverModManager.Abstractions.Cli;
using SilverModManager.Games.FromSoft.Build;
using SilverModManager.Games.FromSoft.Formats;
using SilverModManager.Games.FromSoft.Merge;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// The command line side of a game update. It is the one verb of the merge work that writes, and what it writes is the
/// kept baseline under the data root; the merging itself happens when the loadout is applied.
/// </summary>
internal static class HealVerbs
{
    internal const string HealName = "fromsoft heal";
    internal const string NothingToHeal = "Nothing to heal: the installed build is the one that was stamped.";
    internal const string NextApply = "The next apply rebuilds every merge against this baseline and checks that each mod's rows survived. Re-stamp the build from Health Check once you have looked at the result.";

    [Verb(HealName, "Plans the steps that bring a loadout's merged regulation onto the installed game build, keeps the new baseline and checks the mods' param layouts against it")]
    internal static async Task<int> Heal(
        [Injected] IRenderer renderer,
        [Injected] BuildStampStore stamps,
        [Injected] RegulationBaselines baselines,
        [Injected] LoadoutMergeSources sources,
        [Option("l", "loadout", "The loadout to heal")] Loadout.ReadOnly loadout,
        [Option("d", "dry-run", "Print the steps and change nothing", true)] bool dryRun)
    {
        var installation = loadout.InstallationInstance;
        if (installation.Game is not IFromSoftGame game)
        {
            await renderer.TextLine($"{installation.Game.DisplayName} is not a FromSoft game, so there is nothing to heal.");
            return 1;
        }

        BuildIdentity live;
        try
        {
            live = BuildIdentity.Identify(installation);
        }
        catch (InvalidOperationException exception)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        var steps = HealPlan.Plan(stamps.Find(loadout.InstallationId), live, game.Formats);
        if (steps.Count == 0)
        {
            await renderer.TextLine(NothingToHeal);
            return 0;
        }

        foreach (var (step, index) in steps.Select((s, i) => (s, i + 1)))
            await renderer.TextLine($"{index}. {step.Detail}");

        // A refusal writes nothing, with or without --dry-run: there is no baseline to keep.
        if (steps.Any(s => s.Kind == HealStepKind.Refuse)) return 1;

        if (dryRun)
        {
            // The listing is for the reader; a mod regulation it can't read is the layout check's to report, not this one's.
            await VanillaVerbs.PrintGroups(renderer, sources, loadout, whenNone: null);
            await renderer.TextLine("Dry run: nothing was changed.");
            return 0;
        }

        if (!steps.Any(s => s.Kind == HealStepKind.AdoptBaseline))
        {
            await renderer.TextLine("The install was repackaged and its regulation did not move, so there is no baseline to keep.");
            return 0;
        }

        AbsolutePath kept;
        try
        {
            kept = baselines.Adopt(installation, live);
            await renderer.TextLine($"Kept {kept} as the baseline for build {live.RegulationVersion}.");
        }
        catch (HealException exception)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        await VanillaVerbs.PrintGroups(renderer, sources, loadout, whenNone: null);

        var problems = CheckLayouts(kept, sources, loadout, game);
        if (problems.Count > 0)
        {
            await renderer.TextLine("The param layout moved, so these mods' rows can't be transplanted onto the installed build:");
            foreach (var problem in problems) await renderer.TextLine("  " + problem);
            await renderer.TextLine("Those mods need updates built for this game version.");
            return 1;
        }

        await renderer.TextLine(NextApply);
        return 0;
    }

    private static IReadOnlyList<string> CheckLayouts(AbsolutePath baselineFile, LoadoutMergeSources sources, Loadout.ReadOnly loadout, IFromSoftGame game)
    {
        var regulationName = game.Formats.RegulationFileName;
        if (regulationName is null) return [];

        var mods = sources.Providers(loadout)
            .Where(p => p.RelPath.Equals(regulationName, StringComparison.OrdinalIgnoreCase))
            .Select(p => (ModId: p.ModName, Regulation: sources.ContributorBytes(p)))
            .ToArray();
        if (mods.Length == 0) return [];

        var baseline = File.ReadAllBytes(baselineFile.GetFullPath());
        try
        {
            return LayoutGate.Check(baseline, mods, game.Formats);
        }
        catch (Exception exception) when (exception is RegulationException or AesException or DcxException or KrakenException)
        {
            return [$"a regulation could not be read: {exception.Message}"];
        }
    }
}

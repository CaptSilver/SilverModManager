using SilverModManager.Abstractions.Cli;
using SilverModManager.Games.FromSoft.Merge;
using NexusMods.Paths;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.ProxyConsole;

namespace SilverModManager.Games.FromSoft.Heal;

/// <summary>
/// The command line side of the vanilla regulations the app keeps. What it writes is a copy under the data root, and
/// only of a file Steam's own manifests list; the game folder and the loadout are never touched.
/// </summary>
internal static class VanillaVerbs
{
    internal const string ImportName = "fromsoft vanilla import";
    internal const string ListName = "fromsoft vanilla list";
    internal const string NothingKept = "No vanilla regulation is kept yet.";

    [Verb(ImportName, "Keeps a regulation that Steam shipped, such as an older build another tool saved, as the vanilla baseline of the build stamped inside it")]
    internal static async Task<int> Import(
        [Injected] IRenderer renderer,
        [Injected] RegulationBaselines baselines,
        [Injected] LoadoutMergeSources sources,
        [Option("l", "loadout", "A loadout of the game the file is a regulation of")] Loadout.ReadOnly loadout,
        [Option("f", "file", "The regulation file to keep")] AbsolutePath file,
        [Option("v", "version", "The build stamp the file has to carry; refused when it carries another", true)] string? version,
        [Option("c", "check-against-mods", "Keep a file Steam's manifests don't list if it passes the checks of a vanilla against this loadout's mods of that build; it is kept as validated, not vouched", true)] bool checkAgainstMods)
    {
        var installation = loadout.InstallationInstance;
        if (installation.Game is not IFromSoftGame)
        {
            await renderer.TextLine($"{installation.Game.DisplayName} is not a FromSoft game, so it has no regulation to keep.");
            return 1;
        }

        RegulationImport kept;
        try
        {
            kept = checkAgainstMods ? sources.KeepUserVanilla(loadout, file, version) : baselines.Import(installation, file, version);
        }
        catch (HealException exception)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        await renderer.TextLine(kept.Outcome switch
        {
            ImportOutcome.Kept when kept.Provenance.Tier == BaselineTier.Validated => $"Kept {file} as the validated baseline for build {kept.Version}, usable as a merge ancestor but not vouched for by Steam: {kept.Provenance.Describe()}.",
            ImportOutcome.Kept => $"Kept {file} as the baseline for build {kept.Version}: {Describe(kept.Provenance)}.",
            ImportOutcome.AlreadyKept => $"{kept.File} already holds this file as the baseline for build {kept.Version}; nothing changed.",
            ImportOutcome.ReplacedValidated => $"Kept {file} as the baseline for build {kept.Version} in place of a copy that was only validated: {Describe(kept.Provenance)}.",
            ImportOutcome.KeptAlongside => $"Build {kept.Version} already had a different regulation from Steam. Kept {file} beside it as {kept.File.FileName}: {Describe(kept.Provenance)}.",
            _ => throw new NotSupportedException($"Unhandled import outcome {kept.Outcome}"),
        });
        return 0;
    }

    [Verb(ListName, "Lists the vanilla regulations the app keeps, where each came from and how far it is trusted, and which one each build of the loadout's mods would be compared against")]
    internal static async Task<int> List(
        [Injected] IRenderer renderer,
        [Injected] RegulationBaselines baselines,
        [Injected] LoadoutMergeSources sources,
        [Option("l", "loadout", "A loadout of the game whose regulations to list")] Loadout.ReadOnly loadout)
    {
        if (loadout.InstallationInstance.Game is not IFromSoftGame)
        {
            await renderer.TextLine($"{loadout.InstallationInstance.Game.DisplayName} is not a FromSoft game, so it has no regulation to keep.");
            return 1;
        }

        IReadOnlyList<KeptBaseline> kept;
        try
        {
            kept = baselines.List();
        }
        catch (HealException exception)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        if (kept.Count == 0) await renderer.TextLine(NothingKept);
        foreach (var baseline in kept)
            await renderer.TextLine($"{baseline.Version}  {baseline.Provenance.Describe()}  {baseline.File}{(baseline.IsAlternate ? " (alternate)" : "")}");

        return await PrintGroups(renderer, sources, loadout, "No mod of this loadout ships a regulation.");
    }

    /// <summary>
    /// Prints each build the loadout's regulation mods were made for with the vanilla they would be compared against,
    /// or what was tried when there is none. Reads only.
    /// </summary>
    /// <param name="whenNone">What to say when no mod ships a regulation; null says nothing.</param>
    /// <returns>0, or 1 when a mod's regulation cannot be read.</returns>
    internal static async Task<int> PrintGroups(IRenderer renderer, LoadoutMergeSources sources, Loadout.ReadOnly loadout, string? whenNone)
    {
        IReadOnlyList<VanillaGroupListing> groups;
        try
        {
            groups = sources.VanillaGroups(loadout);
        }
        catch (MergeException exception)
        {
            await renderer.TextLine(exception.Message);
            return 1;
        }

        if (groups.Count == 0)
        {
            if (whenNone is not null) await renderer.TextLine(whenNone);
            return 0;
        }

        await renderer.TextLine("Builds the mods were made for:");
        foreach (var group in groups)
        {
            var mods = string.Join(", ", group.Mods);
            await renderer.TextLine(group.Source is { } source
                ? $"  {group.Version}  {mods}: {source}"
                : $"  {group.Version}  {mods}: no vanilla regulation is available ({string.Join("; ", group.Tried)})");
        }
        return 0;
    }

    private static string Describe(RegulationProvenance provenance)
        => provenance.ManifestId is { } manifest ? $"vouched by Steam manifest {manifest}" : "vouched";
}

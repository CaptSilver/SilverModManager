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

    private static string Describe(RegulationProvenance provenance)
        => provenance.ManifestId is { } manifest ? $"vouched by Steam manifest {manifest}" : "vouched";
}

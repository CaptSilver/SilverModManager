using SilverModManager.Abstractions.Loadouts.Synchronizers;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>Which mods of a loadout provide loose params, and which of them provide the file the loader then turns off.</summary>
/// <param name="LooseParamMods">The mods that provide one of the rule's loose parameter files.</param>
/// <param name="RegulationMods">The mods that provide the file the loose params switch off; empty when there are none.</param>
public sealed record LooseParamsVerdict(IReadOnlyList<string> LooseParamMods, IReadOnlyList<string> RegulationMods);

/// <summary>An apply the app will not start because mods edit a file the game's loader is about to stop reading.</summary>
public sealed class LooseParamsRefusedException(string message) : SynchronizerPreflightException(message);

/// <summary>
/// Reads a game's <see cref="LooseParamsRule"/> against what the loadout's mods provide. me3 stops loading the game's
/// regulation as soon as any package maps a loose parameter file, so a mod that edits the regulation does nothing next
/// to a mod that ships loose params, and the app refuses to apply that without telling the user.
/// </summary>
public static class LooseParamsCheck
{
    /// <returns>Null when the game has no rule or no mod provides a loose parameter file.</returns>
    public static LooseParamsVerdict? Check(LooseParamsRule? rule, IReadOnlyList<ProviderFile> providers)
    {
        if (rule is null) return null;

        var loose = ModsProviding(providers, file => rule.Files.Contains(file, StringComparer.OrdinalIgnoreCase));
        if (loose.Count == 0) return null;

        return new LooseParamsVerdict(loose, ModsProviding(providers, file => file.Equals(rule.DisablesFile, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>The refusal for a verdict that has both kinds of mod.</summary>
    public static LooseParamsRefusedException Refusal(LooseParamsRule rule, LooseParamsVerdict verdict)
        => new($"me3 loads the loose params ({string.Join(", ", verdict.LooseParamMods)}) and ignores {rule.DisablesFile} for this game, so {string.Join(", ", verdict.RegulationMods)} would do nothing. Disable one of them.");

    private static List<string> ModsProviding(IReadOnlyList<ProviderFile> providers, Func<string, bool> matches)
        => providers.Where(p => matches(p.RelPath)).Select(p => p.ModName).Distinct(StringComparer.Ordinal).ToList();
}

using System.Collections.Immutable;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// What Health Check offers when a regulation merge has no vanilla file for the build some mods were made for. Both
/// actions carry the loadout items of the mods, so the handler acts on exactly the mods the diagnostic named.
/// </summary>
public static class VanillaMissingActions
{
    /// <summary>The id of the action that asks Nexus whether the mods have newer files.</summary>
    public const string CheckModUpdatesId = "fromsoft-check-mod-updates";

    /// <summary>The id of the action that disables the mods built for one build.</summary>
    public const string DisableModsId = "fromsoft-disable-mods";

    public static DiagnosticAction CheckModUpdates(IEnumerable<LoadoutItemId> mods)
        => new(CheckModUpdatesId, "Look for updates to these mods", Encode(mods));

    /// <param name="version">The build the mods were made for.</param>
    /// <param name="mods">The mods to disable.</param>
    /// <param name="onlyBuild">Whether this is the only build missing a vanilla, so the label need not say which mods it means.</param>
    public static DiagnosticAction DisableMods(string version, IEnumerable<LoadoutItemId> mods, bool onlyBuild)
        => new(DisableModsId, onlyBuild ? "Disable these mods in this loadout" : $"Disable the mods built for {version}", Encode(mods));

    /// <summary>The actions for a refusal; empty when it names no mod the app can act on.</summary>
    public static IEnumerable<DiagnosticAction> For(IReadOnlyList<VanillaMissing> missing)
    {
        var all = missing.SelectMany(m => m.ModIds).Distinct().ToArray();
        if (all.Length == 0) yield break;

        yield return CheckModUpdates(all);
        foreach (var build in missing.Where(m => m.ModIds.Count > 0).OrderBy(m => m.Version, StringComparer.Ordinal))
            yield return DisableMods(build.Version, build.ModIds, onlyBuild: missing.Count == 1);
    }

    /// <summary>The loadout items an action from this class names.</summary>
    /// <exception cref="ArgumentException">An argument is not a loadout item id.</exception>
    public static IReadOnlyList<LoadoutItemId> ParseIds(IEnumerable<string> arguments)
        => arguments.Select(a => ulong.TryParse(a, out var id)
            ? LoadoutItemId.From(EntityId.From(id))
            : throw new ArgumentException($"'{a}' is not a loadout item id, so the mods this action names can't be found", nameof(arguments))).ToArray();

    private static ImmutableArray<string> Encode(IEnumerable<LoadoutItemId> mods) => [..mods.Select(m => m.Value.Value.ToString())];
}

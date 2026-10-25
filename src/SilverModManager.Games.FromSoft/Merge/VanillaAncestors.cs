using SilverModManager.Abstractions.Loadouts;

namespace SilverModManager.Games.FromSoft.Merge;

/// <summary>
/// Mods built for one game build that share one vanilla file.
/// </summary>
/// <param name="Version">The build stamp the mods carry; null for a packed file, which has no stamp.</param>
/// <param name="Contributors">The mods' files that branched from <paramref name="Bytes"/>.</param>
/// <param name="Bytes">The vanilla file they branched from.</param>
public sealed record AncestorGroup(string? Version, IReadOnlyList<ProviderFile> Contributors, byte[] Bytes);

/// <summary>
/// The vanilla file each contributor of one path branched from. Contributors built for the same game build share a
/// group, so a loadout of mods from several builds has several.
/// </summary>
/// <param name="groups">One group per build the contributors were made for.</param>
/// <param name="sources">Where each group's vanilla came from, when the caller knows; empty for a packed file.</param>
public sealed class VanillaAncestors(IReadOnlyList<AncestorGroup> groups, IReadOnlyList<VanillaGroupSource>? sources = null)
{
    private readonly Dictionary<ProviderFile, byte[]> _byContributor =
        groups.SelectMany(g => g.Contributors.Select(c => (Contributor: c, g.Bytes))).ToDictionary(p => p.Contributor, p => p.Bytes);

    public IReadOnlyList<AncestorGroup> Groups { get; } = groups;

    /// <summary>Where each build's vanilla came from, one per group of a regulation, whether or not there is more than one.</summary>
    public IReadOnlyList<VanillaGroupSource> Sources { get; } = sources ?? [];

    /// <summary>The vanilla file <paramref name="contributor"/> branched from, or null when it is not one of this path's contributors.</summary>
    public byte[]? Of(ProviderFile contributor) => _byContributor.GetValueOrDefault(contributor);
}

/// <summary>A build whose mods have no vanilla regulation to be compared against.</summary>
/// <param name="Version">The build stamp the mods carry.</param>
/// <param name="Mods">The mods built for it.</param>
/// <param name="Tried">Each place a vanilla was looked for and why it gave none.</param>
/// <param name="ModIds">The loadout items of <paramref name="Mods"/> that are known, for the actions that act on them.</param>
public sealed record VanillaMissing(string Version, IReadOnlyList<string> Mods, IReadOnlyList<string> Tried, IReadOnlyList<LoadoutItemId>? ModIds = null)
{
    public IReadOnlyList<LoadoutItemId> ModIds { get; } = ModIds ?? [];
}

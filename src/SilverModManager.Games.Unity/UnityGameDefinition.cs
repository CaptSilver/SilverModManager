using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Games.Generic;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Unity;

/// <summary>
/// What sets one Unity game apart from the next: its identity, files, store id and where its mods come from. Rows
/// live in <see cref="UnityGames"/>; a missing member fails to compile at the row.
/// </summary>
public sealed record UnityGameDefinition : IGameDefinition
{
    /// <summary>"Unity.&lt;Name&gt;". Loadouts persist it, so it never changes.</summary>
    public required GameId GameId { get; init; }

    public required string DisplayName { get; init; }

    public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }

    /// <summary>The game's executable, relative to the game folder.</summary>
    public required RelativePath Executable { get; init; }

    /// <summary>The <c>&lt;Name&gt;_Data</c> folder next to the executable.</summary>
    public required string DataFolder { get; init; }

    /// <summary>The game's Steam app id.</summary>
    public required uint SteamAppId { get; init; }

    /// <summary>The Thunderstore community slug, e.g. <c>repo</c>.</summary>
    public required string ThunderstoreCommunity { get; init; }

    /// <summary>The <c>Namespace-Name</c> of the package that holds the game's BepInEx loader pack.</summary>
    public required string ThunderstoreLoaderPackage { get; init; }

    /// <summary>Other packages that are the same loader pack, which mods may depend on instead.</summary>
    public required IReadOnlyList<string> ThunderstoreLoaderAliases { get; init; }

    /// <summary>
    /// How long after an update the date note shows. Null by default: these games patch too often for a date to
    /// mean anything, so the note then only shows for a build change since the last apply.
    /// </summary>
    public TimeSpan? DateWindow { get; init; }

    public RelativePath? NativeLinuxExecutable { get; init; }

    public StoreIdentifiers Stores => new(GameId) { SteamAppIds = [SteamAppId] };

    public IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;

    /// <summary>The game folder is the only location; saves are not mapped, so removing a loadout can never touch them.</summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } }.ToImmutableDictionary();
}

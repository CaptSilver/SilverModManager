using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.Generic.Simple;

/// <summary>
/// A game that needs no logic of its own: files go to a folder, the game is started from its executable. Rows
/// live in <see cref="SimpleGames"/>; a missing member fails to compile at the row.
/// </summary>
public sealed record SimpleGameDefinition : IGameDefinition
{
    /// <summary>"Simple.&lt;Name&gt;" for a new game, and the old id for a migrated one. Loadouts persist it, so it never changes.</summary>
    public required GameId GameId { get; init; }

    public required string DisplayName { get; init; }

    public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }

    public required StoreIdentifiers Stores { get; init; }

    public required RelativePath Executable { get; init; }

    public RelativePath? NativeLinuxExecutable { get; init; }

    public required ModLocation ModRoot { get; init; }

    public required SimpleInstallRules Install { get; init; }

    /// <summary>
    /// Files only this game's install holds, for games that share a store id or a folder shape with another game.
    /// </summary>
    public IReadOnlyList<RelativePath> IdentifyingFiles { get; init; } = [];

    /// <summary>What a player calls <see cref="ModRoot"/>, keyed by its location id string. Needed when the root is outside the game folder.</summary>
    public IReadOnlyDictionary<string, string> LocationNames { get; init; } = ImmutableDictionary<string, string>.Empty;

    public IReadOnlyDictionary<string, string> LocationSentenceNames { get; init; } = ImmutableDictionary<string, string>.Empty;

    /// <summary>
    /// The game folder, plus the shared location of <see cref="ModRoot"/> when that is outside it. Saves stay
    /// unmapped so that removing a loadout can never delete them.
    /// </summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        var locations = ImmutableDictionary.CreateBuilder<LocationId, AbsolutePath>();
        locations.Add(LocationId.Game, gameLocatorResult.Path);
        if (ModRoot.Root != ModLocationRoot.GameFolder)
            locations.Add(SimpleLocations.For(ModRoot.Root), fileSystem.GetKnownPath(SimpleLocations.KnownFolderOf(ModRoot.Root)).Combine(ModRoot.Folder));
        return locations.ToImmutable();
    }
}

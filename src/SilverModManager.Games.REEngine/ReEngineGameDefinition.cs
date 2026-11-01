using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Games.Generic;
using SilverModManager.Sdk.Games;

namespace SilverModManager.Games.REEngine;

/// <summary>
/// What sets one RE Engine game apart from the next: its identity, Steam id, executable and the name of the REFramework
/// download built for it. Rows live in <see cref="ReEngineGames"/>; a missing member fails to compile at the row.
/// </summary>
public sealed record ReEngineGameDefinition : IGameDefinition
{
    /// <summary>"ReEngine.&lt;Name&gt;". Loadouts persist it, so it never changes.</summary>
    public required GameId GameId { get; init; }

    public required string DisplayName { get; init; }

    public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }

    /// <summary>The game's executable, relative to the game folder.</summary>
    public required RelativePath Executable { get; init; }

    public required uint SteamAppId { get; init; }

    /// <summary>
    /// The file in REFramework's GitHub releases built for this game, e.g. <c>RE2.zip</c>. Each is a separate build, so
    /// the download has to match the game.
    /// </summary>
    public required string ReFrameworkRelease { get; init; }

    public RelativePath? NativeLinuxExecutable => null;

    public StoreIdentifiers Stores => new(GameId) { SteamAppIds = [SteamAppId] };

    public IReadOnlyDictionary<string, string> LocationNames => ImmutableDictionary<string, string>.Empty;

    /// <summary>The game folder is the only location; saves are not mapped, so removing a loadout can never touch them.</summary>
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => new Dictionary<LocationId, AbsolutePath> { { LocationId.Game, gameLocatorResult.Path } }.ToImmutableDictionary();
}

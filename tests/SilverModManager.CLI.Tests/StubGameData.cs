using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.NexusModsApi;

namespace SilverModManager.CLI.Tests;

/// <summary>
/// A game that only has a name and an id. NSubstitute cannot stand in for <see cref="IGameData"/>: it builds a default
/// value for an unconfigured property first, and a <see cref="GameId"/> has no default.
/// </summary>
internal sealed class StubGameData(string displayName, GameId gameId) : IGameData
{
    public GameId GameId { get; } = gameId;
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName { get; } = displayName;
    public Optional<NexusModsGameId> NexusModsGameId => default;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
}

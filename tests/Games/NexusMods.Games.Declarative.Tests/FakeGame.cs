using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Sdk.NexusModsApi;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;

namespace NexusMods.Games.Declarative.Tests;

/// <summary>A coded game that answers only what the catalog asks: its id and primary file.</summary>
internal sealed class FakeGame(string id, Func<GamePath> primaryFile) : IGameData
{
    public GameId GameId { get; } = GameId.From(id);
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName => id;
    public Optional<NexusModsGameId> NexusModsGameId => default;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();

    public System.Collections.Immutable.ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => throw new NotSupportedException();

    public GamePath GetPrimaryFile(GameInstallation installation) => primaryFile();
}

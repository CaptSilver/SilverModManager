using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// A game that is only an id. A substitute can't carry a <see cref="GameId"/>: it builds the default
/// value, and the default throws.
/// </summary>
internal sealed class FakeGameData(string id, bool hasNativeLinuxBuild = false) : IGameData
{
    public GameId GameId { get; } = GameId.From(id);
    public StoreIdentifiers StoreIdentifiers => new(GameId);
    public string DisplayName => id;
    public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public bool HasNativeLinuxBuild { get; } = hasNativeLinuxBuild;

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => throw new NotSupportedException();

    public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
}

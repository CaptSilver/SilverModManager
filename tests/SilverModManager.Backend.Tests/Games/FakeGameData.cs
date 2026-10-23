using System.Collections.Immutable;
using DynamicData.Kernel;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Backend.Tests.Games;

/// <summary>
/// A game that is only an id. A substitute can't carry a <see cref="GameId"/>: it builds the default
/// value, and the default throws. It answers <see cref="GetPrimaryFile"/> only when given a file name.
/// </summary>
internal class FakeGameData(string id, bool hasNativeLinuxBuild = false, string? primaryFile = null) : IGameData
{
    public GameId GameId { get; } = GameId.From(id);
    public StoreIdentifiers StoreIdentifiers { get; init; } = new(GameId.From(id));
    public string DisplayName => id;
    public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
    public IStreamFactory IconImage => throw new NotSupportedException();
    public IStreamFactory TileImage => throw new NotSupportedException();
    public bool HasNativeLinuxBuild { get; } = hasNativeLinuxBuild;

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
        => throw new NotSupportedException();

    public GamePath GetPrimaryFile(GameInstallation installation)
        => primaryFile is null ? throw new NotSupportedException() : new GamePath(LocationId.Game, primaryFile);
}

/// <summary>A <see cref="FakeGameData"/> that names files only its own install has.</summary>
internal sealed class FakeIdentifiedGameData(string id, string primaryFile, IReadOnlyList<RelativePath> identifyingFiles, bool hasNativeLinuxBuild = false)
    : FakeGameData(id, hasNativeLinuxBuild, primaryFile), IGameIdentifyingFiles
{
    public IReadOnlyList<RelativePath> IdentifyingFiles => identifyingFiles;
}

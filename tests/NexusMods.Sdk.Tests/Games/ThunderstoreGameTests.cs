using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.NexusModsApi;

namespace NexusMods.Sdk.Tests.Games;

public class ThunderstoreGameTests
{
    private class PlainGame : IGameData
    {
        public GameId GameId { get; } = GameId.From("PlainGame");
        public StoreIdentifiers StoreIdentifiers => new(GameId);
        public string DisplayName => "Plain game";
        public Optional<NexusModsGameId> NexusModsGameId => Optional<NexusModsGameId>.None;
        public IStreamFactory IconImage => throw new NotSupportedException();
        public IStreamFactory TileImage => throw new NotSupportedException();
        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => throw new NotSupportedException();
        public GamePath GetPrimaryFile(GameInstallation installation) => throw new NotSupportedException();
    }

    private sealed class ThunderstorePlainGame : PlainGame, IThunderstoreGame
    {
        public string ThunderstoreCommunity => "repo";
        public string ThunderstoreLoaderPackage => "BepInEx-BepInExPack";
        public IReadOnlyList<string> ThunderstoreLoaderAliases => ["bbepis-BepInExPack"];
    }

    [Fact]
    public void For_ReturnsTheGameWhenItIsAThunderstoreGame()
    {
        var game = new ThunderstorePlainGame();

        ThunderstoreGame.For(game).Should().BeSameAs(game);
    }

    [Fact]
    public void For_ReturnsNullForAGameWithoutThunderstoreSupport()
    {
        ThunderstoreGame.For(new PlainGame()).Should().BeNull();
    }
}

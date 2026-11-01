using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using Xunit;

namespace SilverModManager.Games.Generic.Tests;

/// <summary>
/// A game built from a definition record: the identity, stores, art and native-build facts all come from the record,
/// including when they are read through <see cref="IGameData"/>, where default interface members would otherwise win.
/// </summary>
public class DefinedGameTests
{
    private static readonly GameId Id = GameId.From("Tests.Defined");

    private static StubDefinition Definition(RelativePath? native = null) => new()
    {
        GameId = Id,
        DisplayName = "Defined Game",
        NexusModsGameId = Optional<Sdk.NexusModsApi.NexusModsGameId>.None,
        Stores = new StoreIdentifiers(Id) { SteamAppIds = [4242u] },
        Executable = "bin/defined.exe",
        NativeLinuxExecutable = native,
        LocationNames = new Dictionary<string, string> { ["Tests.Docs"] = "Documents" },
        LocationSentenceNames = new Dictionary<string, string> { ["Tests.Docs"] = "the documents folder" },
    };

    private static (StubDefinedGame Game, RecordingArtSource Art) Create(StubDefinition definition)
    {
        var art = new RecordingArtSource();
        var provider = new ServiceCollection().AddSingleton<IGameArtSource>(art).BuildServiceProvider();
        return (new StubDefinedGame(provider, definition), art);
    }

    [Fact]
    public void Identity_ComesFromTheDefinition()
    {
        var definition = Definition();
        var (game, _) = Create(definition);

        IGameData data = game;
        data.GameId.Should().Be(Id);
        data.DisplayName.Should().Be("Defined Game");
        data.NexusModsGameId.HasValue.Should().BeFalse();
        data.StoreIdentifiers.SteamAppIds.Should().Equal(4242u);
        game.Definition.Should().BeSameAs(definition);
    }

    [Fact]
    public void Art_IsLookedUpFromTheDefinitionsSteamIds()
    {
        var (game, art) = Create(Definition());

        var icon = game.IconImage;
        var tile = game.TileImage;

        art.IconIds.Should().Equal(4242u);
        art.TileIds.Should().Equal(4242u);
        icon.Should().BeSameAs(game.IconImage);
        tile.Should().BeSameAs(game.TileImage);
    }

    [Fact]
    public void Installers_AreBuiltLazilyAndOnce()
    {
        var (game, _) = Create(Definition());
        game.InstallersBuilt.Should().Be(0);

        var first = game.LibraryItemInstallers;

        game.LibraryItemInstallers.Should().BeSameAs(first);
        game.InstallersBuilt.Should().Be(1);
    }

    [Fact]
    public void LocationNames_ReadThroughIGameData_AreTheDefinitions()
    {
        var (game, _) = Create(Definition());

        ((IGameData)game).LocationNames.Should().Equal(new Dictionary<string, string> { ["Tests.Docs"] = "Documents" });
        ((IGameData)game).LocationSentenceNames.Should().Equal(new Dictionary<string, string> { ["Tests.Docs"] = "the documents folder" });
    }

    [Fact]
    public void NativeLinuxBuild_ReadThroughIGameData_IsTheDefinitions()
    {
        var (native, _) = Create(Definition("bin/defined.x86_64"));
        var (windows, _) = Create(Definition());

        ((IGameData)native).HasNativeLinuxBuild.Should().BeTrue();
        ((IGameData)native).NativeLinuxPrimaryFile.Should().Be((RelativePath)"bin/defined.x86_64");
        ((IGameData)windows).HasNativeLinuxBuild.Should().BeFalse();
        ((IGameData)windows).NativeLinuxPrimaryFile.Should().BeNull();
    }

    [Fact]
    public void GetPrimaryFile_IsTheDefinitionsExecutable()
    {
        var (game, _) = Create(Definition());

        game.GetPrimaryFile(null!).Should().Be(new GamePath(LocationId.Game, "bin/defined.exe"));
    }

    [Fact]
    public void GetLocations_AreTheDefinitions()
    {
        var (game, _) = Create(Definition());

        game.GetLocations(null!, null!).Should().BeSameAs(StubDefinition.Locations);
    }

    private sealed record StubDefinition : IGameDefinition
    {
        public static readonly ImmutableDictionary<LocationId, AbsolutePath> Locations = ImmutableDictionary<LocationId, AbsolutePath>.Empty;

        public required GameId GameId { get; init; }
        public required string DisplayName { get; init; }
        public required Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId { get; init; }
        public required StoreIdentifiers Stores { get; init; }
        public required RelativePath Executable { get; init; }
        public RelativePath? NativeLinuxExecutable { get; init; }
        public required IReadOnlyDictionary<string, string> LocationNames { get; init; }
        public IReadOnlyDictionary<string, string> LocationSentenceNames { get; init; } = ImmutableDictionary<string, string>.Empty;

        public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) => Locations;
    }

    private sealed class StubDefinedGame(IServiceProvider provider, StubDefinition definition) : DefinedGame<StubDefinition>(provider, definition)
    {
        public int InstallersBuilt { get; private set; }

        protected override ILoadoutSynchronizer CreateSynchronizer(IServiceProvider provider) => throw new NotSupportedException();

        protected override ILibraryItemInstaller[] CreateInstallers(IServiceProvider provider)
        {
            InstallersBuilt++;
            return [];
        }
    }

    private sealed class RecordingArtSource : IGameArtSource
    {
        public ImmutableArray<uint> IconIds { get; private set; }
        public ImmutableArray<uint> TileIds { get; private set; }
        private readonly IStreamFactory _icon = new MemoryStreamFactory("icon.png", new MemoryStream());
        private readonly IStreamFactory _tile = new MemoryStreamFactory("tile.png", new MemoryStream());

        public IStreamFactory Icon(ImmutableArray<uint> steamAppIds)
        {
            IconIds = steamAppIds;
            return _icon;
        }

        public IStreamFactory Tile(ImmutableArray<uint> steamAppIds)
        {
            TileIds = steamAppIds;
            return _tile;
        }
    }
}

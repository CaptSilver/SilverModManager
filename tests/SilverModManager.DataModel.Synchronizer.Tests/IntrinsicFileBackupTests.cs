using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Hashing.xxHash3;
using NexusMods.MnemonicDB.Abstractions;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;
using NexusMods.Games.TestFramework;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// A file the app generates at a path the user already had a file at: the user's copy is kept before the
/// first write, put back when the game is unmanaged, and the app's own output is never mistaken for it.
/// </summary>
public class IntrinsicFileBackupTests(ITestOutputHelper helper) : AIsolatedGameTest<IntrinsicFileBackupTests, IntrinsicStubbedGame>(helper)
{
    private static readonly byte[] UserBytes = "# the user's own list\nMyMod.esp\n"u8.ToArray();

    private AbsolutePath Generated => GameInstallation.Locations.ToAbsolutePath(IntrinsicStubbedGame.GeneratedPath);

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<IntrinsicStubbedGame>()
            .AddUniversalGameLocator<IntrinsicStubbedGame>(Version.Parse("0.0.0"));
    }

    private async Task UserHadAFile()
    {
        Generated.Parent.CreateDirectory();
        await Generated.WriteAllBytesAsync(UserBytes);
    }

    private async Task<Loadout.ReadOnly> Apply(Loadout.ReadOnly loadout) => await Synchronizer.Synchronize(Refresh(loadout));

    [Fact]
    public async Task TheFirstApplyBacksUpTheUsersFileBeforeOverwritingIt()
    {
        await UserHadAFile();
        var loadout = await CreateLoadout();

        await Apply(loadout);

        (await Generated.ReadAllBytesAsync()).Should().NotEqual(UserBytes, "the app now owns the file");
        (await FileStore.HaveFile(UserBytes.xxHash3())).Should().BeTrue("the user's copy has to be kept before it is overwritten");
        GameBackedUpFile.All(Connection.Db).Select(f => f.Hash).Should().Contain(UserBytes.xxHash3(), "the backup is pinned against garbage collection");
    }

    [Fact]
    public async Task UnmanagingPutsTheUsersFileBack()
    {
        await UserHadAFile();
        var loadout = await CreateLoadout();
        await Apply(loadout);

        await LoadoutManager.UnManage(GameInstallation);

        Generated.FileExists.Should().BeTrue("the user had that file before the game was managed");
        (await Generated.ReadAllBytesAsync()).Should().Equal(UserBytes);
    }

    [Fact]
    public async Task UnmanagingRemovesAFileTheUserNeverHad()
    {
        var loadout = await CreateLoadout();
        await Apply(loadout);
        Generated.FileExists.Should().BeTrue();

        await LoadoutManager.UnManage(GameInstallation);

        Generated.FileExists.Should().BeFalse();
    }

    [Fact]
    public async Task ASecondApplyDoesNotBackUpTheAppsOwnFile()
    {
        await UserHadAFile();
        var loadout = await CreateLoadout();
        await Apply(loadout);
        var generated = await Generated.ReadAllBytesAsync();
        var backedUp = GameBackedUpFile.All(Connection.Db).Select(f => f.Hash).ToArray();

        await Apply(loadout);

        (await FileStore.HaveFile(generated.xxHash3())).Should().BeFalse("what the app wrote is regenerated, never kept");
        GameBackedUpFile.All(Connection.Db).Select(f => f.Hash).Should().BeEquivalentTo(backedUp);
    }

    [Fact]
    public async Task ResettingToTheOriginalGameStateRestoresTheUsersFile()
    {
        await UserHadAFile();
        var loadout = await CreateLoadout();
        await Apply(loadout);

        await Synchronizer.ResetToOriginalGameState(GameInstallation, [LocatorId.From("StubbedGameState.zip")]);

        (await Generated.ReadAllBytesAsync()).Should().Equal(UserBytes);
    }
}

/// <summary>
/// A stubbed game whose synchronizer generates one file in its preferences folder.
/// </summary>
public class IntrinsicStubbedGame : IGame, IGameData<IntrinsicStubbedGame>
{
    public static GameId GameId { get; } = GameId.From("IntrinsicStubbedGame");
    public static string DisplayName => "Stubbed Game With A Generated File";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 1);

    public static readonly GamePath GeneratedPath = new(LocationId.Preferences, "generated.cfg");

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [44u],
        GOGProductIds = [44L],
        EGSCatalogItemId = ["epic-game-id-generated-file"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public IntrinsicStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new GeneratingSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult)
    {
        return new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
            { LocationId.Preferences, gameLocatorResult.Path.Combine("preferences") },
        }.ToImmutableDictionary();
    }

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    private sealed class GeneratingSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => new() { [GeneratedPath] = new GeneratedFile() };
    }

    private sealed class GeneratedFile : IIntrinsicFile
    {
        public GamePath Path => GeneratedPath;

        public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
            => await stream.WriteAsync("# generated\n"u8.ToArray());

        public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx) => Task.CompletedTask;
    }
}

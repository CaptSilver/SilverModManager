using System.Collections.Immutable;
using System.Text;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics.Emitters;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Library.Installers;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using Xunit;

namespace SilverModManager.DataModel.Synchronizer.Tests;

/// <summary>
/// A generated file is work for the next apply only when rendering it now gives other bytes than the app wrote last
/// time. The status asks that of the files that declare they render from the loadout alone; the others are always work.
/// </summary>
[Collection(RenderedListingStubbedGame.CollectionName)]
public class ShouldSynchronizeIntrinsicTests : AIsolatedGameTest<ShouldSynchronizeIntrinsicTests, RenderedListingStubbedGame>
{
    public ShouldSynchronizeIntrinsicTests(ITestOutputHelper helper) : base(helper) => RenderedListingStubbedGame.Reset();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddGame<RenderedListingStubbedGame>()
        .AddUniversalGameLocator<RenderedListingStubbedGame>(Version.Parse("0.0.0"));

    private async Task<Loadout.ReadOnly> AppliedLoadout()
    {
        var loadout = await CreateLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"a.mod"], loadout.LoadoutId, "a.mod");
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));
        return Refresh(loadout);
    }

    private (List<PathPartPair> Previous, List<PathPartPair> Disk) States(Loadout.ReadOnly loadout)
    {
        var metadata = loadout.Installation;
        return (Synchronizer.GetLastScannedDiskState(metadata), Synchronizer.GetDiskStateForGame(metadata));
    }

    private async Task<bool> ShouldSynchronize(Loadout.ReadOnly loadout, List<PathPartPair>? previous = null, List<PathPartPair>? disk = null)
    {
        var (lastScanned, onDisk) = States(loadout);
        return await Synchronizer.ShouldSynchronize(loadout, previous ?? lastScanned, disk ?? onDisk);
    }

    private static List<PathPartPair> WithIdentity(List<PathPartPair> state, FileIdentity identity) =>
        state.Select(pair => pair.Path == RenderedListingStubbedGame.ListingPath ? pair with { Part = pair.Part with { Identity = identity } } : pair).ToList();

    [Fact]
    public async Task AnIntrinsicWhoseRenderMatchesWhatWasWrittenIsNoWork()
    {
        var loadout = await AppliedLoadout();

        // A rename changes the loadout and leaves the generated file's bytes as they were.
        using (var tx = Connection.BeginTransaction())
        {
            tx.Add(loadout.Id, Loadout.Name, "renamed");
            await tx.Commit();
        }

        (await ShouldSynchronize(Refresh(loadout))).Should().BeFalse();
        RenderedListingStubbedGame.Renders.Should().BeGreaterThan(0, "the file was rendered to be compared");
    }

    [Fact]
    public async Task AnIntrinsicWhoseRenderDiffersFromWhatWasWrittenIsWork()
    {
        var loadout = await AppliedLoadout();
        RenderedListingStubbedGame.Suffix = "changed";

        (await ShouldSynchronize(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task AnIntrinsicThatDoesNotOptInIsAlwaysWork()
    {
        var loadout = await AppliedLoadout();
        RenderedListingStubbedGame.OptIn = false;
        RenderedListingStubbedGame.Renders = 0;

        (await ShouldSynchronize(loadout)).Should().BeTrue();
        RenderedListingStubbedGame.Renders.Should().Be(0, "a file that may read more than the loadout is not rendered by the status");
    }

    [Fact]
    public async Task ARenderThatThrowsKeepsTheWriteAndTheStatusAnswers()
    {
        var loadout = await AppliedLoadout();
        RenderedListingStubbedGame.ThrowOnRender = true;

        (await ShouldSynchronize(loadout)).Should().BeTrue();
    }

    [Fact]
    public async Task NothingIsRenderedWhenAnotherNodeAlreadyHasWork()
    {
        var loadout = await AppliedLoadout();
        using (var tx = Connection.BeginTransaction())
        {
            await AddModAsync(tx, [(RelativePath)"b.mod"], loadout.LoadoutId, "b.mod");
            await tx.Commit();
        }
        RenderedListingStubbedGame.Renders = 0;

        (await ShouldSynchronize(Refresh(loadout))).Should().BeTrue();
        RenderedListingStubbedGame.Renders.Should().Be(0);
    }

    [Fact]
    public async Task APreviousIdentityOfAnotherKindKeepsTheWrite()
    {
        var loadout = await AppliedLoadout();
        var (previous, disk) = States(loadout);
        var sha1 = FileIdentity.Sha1(Sha1Value.From(new byte[20]));

        (await ShouldSynchronize(loadout, WithIdentity(previous, sha1), WithIdentity(disk, sha1))).Should().BeTrue();
    }

    [Fact]
    public async Task AnIntrinsicChangedOnDiskSinceTheLastWriteIsWorkWithoutRendering()
    {
        var loadout = await AppliedLoadout();
        var (previous, disk) = States(loadout);
        RenderedListingStubbedGame.Renders = 0;

        var other = FileIdentity.XxHash3(Encoding.UTF8.GetBytes("edited by hand").xxHash3());

        (await ShouldSynchronize(loadout, previous, WithIdentity(disk, other))).Should().BeTrue();
        RenderedListingStubbedGame.Renders.Should().Be(0);
    }
}

/// <summary>
/// A stubbed game whose synchronizer writes a file naming every loadout file at the game folder's top level, with
/// switches for what the tests need the file to do.
/// </summary>
public class RenderedListingStubbedGame : IGame, IGameData<RenderedListingStubbedGame>
{
    public const string CollectionName = "RenderedListingStubbedGame";

    public static GameId GameId { get; } = GameId.From("RenderedListingStubbedGame");
    public static string DisplayName => "Stubbed Game With A Rendered Listing";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 3);

    public static readonly GamePath ListingPath = new(LocationId.Preferences, "rendered.cfg");

    public static bool OptIn { get; set; } = true;
    public static bool ThrowOnRender { get; set; }
    public static string Suffix { get; set; } = "";
    public static int Renders { get => _renders; set => _renders = value; }
    private static int _renders;

    public static void Reset()
    {
        OptIn = true;
        ThrowOnRender = false;
        Suffix = "";
        Renders = 0;
    }

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [46u],
        GOGProductIds = [46L],
        EGSCatalogItemId = ["epic-game-id-rendered-listing"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<SilverModManager.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("SilverModManager.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public RenderedListingStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new ListingSynchronizer(_serviceProvider);

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

    private sealed class ListingSynchronizer(IServiceProvider provider) : DefaultSynchronizer(provider)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) =>
            new() { [ListingPath] = OptIn ? new RenderedListing() : new ReadsMoreListing() };
    }

    private class ReadsMoreListing : IIntrinsicFile
    {
        public GamePath Path => ListingPath;

        public async Task Write(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree)
        {
            Interlocked.Increment(ref _renders);
            if (ThrowOnRender) throw new InvalidOperationException("this file cannot be rendered");
            var names = syncTree
                .Where(entry => entry.Key.LocationId == LocationId.Game && entry.Value.HaveLoadout)
                .Select(entry => entry.Key.FileName.ToString())
                .Order(StringComparer.Ordinal);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(string.Concat(names.Select(name => name + "\n")) + Suffix));
        }

        public Task Ingest(Stream stream, Loadout.ReadOnly loadout, Dictionary<GamePath, SyncNode> syncTree, ITransaction tx) => Task.CompletedTask;
    }

    private sealed class RenderedListing : ReadsMoreListing, IIntrinsicFileRenderedFromLoadout;
}

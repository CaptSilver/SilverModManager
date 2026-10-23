using System.Collections.Immutable;
using DynamicData.Kernel;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Abstractions.Diagnostics.Emitters;
using NexusMods.Abstractions.Games;
using NexusMods.Abstractions.Library.Installers;
using NexusMods.Abstractions.Loadouts;
using NexusMods.Abstractions.Loadouts.Deployment;
using NexusMods.Abstractions.Loadouts.Synchronizers;
using NexusMods.Abstractions.Loadouts.Synchronizers.Rules;
using NexusMods.Games.TestFramework;
using NexusMods.Paths;
using NexusMods.Sdk.Games;
using NexusMods.Sdk.IO;
using NexusMods.Sdk.Loadouts;
using NexusMods.StandardGameLocators.TestHelpers;
using Xunit;

namespace NexusMods.DataModel.Synchronizer.Tests;

/// <summary>
/// Games that order their plugins by file modification time hand the synchronizer the times to set. The files
/// get them on the apply that deploys them or moves them, and the disk state records the same times so the next
/// index does not see the files as edited.
/// </summary>
public class TimestampStampingTests : AIsolatedGameTest<TimestampStampingTests, TimestampStubbedGame>
{
    private static readonly DateTimeOffset T0 = new(2024, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly GamePath A = new(LocationId.Game, "plugins/a.esp");
    private static readonly GamePath B = new(LocationId.Game, "plugins/b.esp");
    private static readonly GamePath C = new(LocationId.Game, "plugins/c.esp");

    public TimestampStampingTests(ITestOutputHelper helper) : base(helper) => TimestampStubbedGame.Order = [];

    protected override IServiceCollection AddServices(IServiceCollection services)
    {
        return base.AddServices(services)
            .AddGame<TimestampStubbedGame>()
            .AddUniversalGameLocator<TimestampStubbedGame>(Version.Parse("0.0.0"));
    }

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private DateTimeOffset MtimeOf(GamePath path) => new(OnDisk(path).FileInfo.LastWriteTimeUtc);

    private async Task<Loadout.ReadOnly> LoadoutOn(string backend)
    {
        var loadout = await CreateLoadout();
        if (backend == "copy")
            await SynchronizerService.ChangeDeploymentBackend(GameInstallation, new GamePath(LocationId.Game, ""), DeploymentBackendId.Copy, chosen: true);

        using var tx = Connection.BeginTransaction();
        await AddModAsync(tx, ((GamePath[])[A, B, C]).Select(p => (RelativePath)p.Path), loadout.LoadoutId, "plugins");
        await tx.Commit();
        return Refresh(loadout);
    }

    private IEnumerable<DiskStateEntry.ReadOnly> DiskState(Loadout.ReadOnly loadout) =>
        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation);

    private DateTimeOffset RecordedMtime(Loadout.ReadOnly loadout, GamePath path) =>
        DiskState(loadout).Single(e => (GamePath)e.Path == path).LastModified;

    private void StampWith(params (GamePath Path, int Minutes)[] order) =>
        TimestampStubbedGame.Order = order.Select(o => (o.Path, T0.AddMinutes(o.Minutes))).ToList();

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task ADeployedFileGetsItsTimeAndTheDiskStateRecordsIt(string backend)
    {
        var loadout = await LoadoutOn(backend);
        StampWith((A, 0), (B, 1), (C, 2));

        await Synchronizer.Synchronize(loadout);

        foreach (var (path, expected) in TimestampStubbedGame.Order)
        {
            MtimeOf(path).Should().Be(expected);
            RecordedMtime(loadout, path).Should().Be(expected);
        }
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task AFollowingApplyFindsNoFileEdited(string backend)
    {
        var loadout = await LoadoutOn(backend);
        StampWith((A, 0), (B, 1), (C, 2));
        await Synchronizer.Synchronize(loadout);

        await Synchronizer.Synchronize(Refresh(loadout));

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree.Where(e => e.Key == A || e.Key == B || e.Key == C).Select(e => e.Value.Actions)
            .Should().OnlyContain(actions => !actions.HasFlag(Actions.IngestFromDisk) && !actions.HasFlag(Actions.ExtractToDisk));
        DiskState(loadout).Should().OnlyContain(e => e.LastModified == MtimeOf((GamePath)e.Path));
    }

    [Theory]
    [InlineData("default")]
    [InlineData("copy")]
    public async Task AFileThatIsAlreadyDeployedIsRestampedWhenTheOrderChanges(string backend)
    {
        var loadout = await LoadoutOn(backend);
        StampWith((A, 0), (B, 1), (C, 2));
        await Synchronizer.Synchronize(loadout);

        StampWith((C, 0), (B, 1), (A, 2));
        await Synchronizer.Synchronize(Refresh(loadout));

        foreach (var (path, expected) in TimestampStubbedGame.Order)
        {
            MtimeOf(path).Should().Be(expected);
            RecordedMtime(loadout, path).Should().Be(expected);
        }

        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        tree.Where(e => e.Key == A || e.Key == B || e.Key == C).Select(e => e.Value.Actions)
            .Should().OnlyContain(actions => !actions.HasFlag(Actions.IngestFromDisk));
    }

    [Fact]
    public async Task AGameThatNamesNoTimesLeavesTheFilesAlone()
    {
        var loadout = await LoadoutOn("copy");
        TimestampStubbedGame.Order = [];

        await Synchronizer.Synchronize(loadout);

        OnDisk(A).FileExists.Should().BeTrue();
        RecordedMtime(loadout, A).Should().Be(MtimeOf(A));
    }
}

/// <summary>A game with a plugin folder whose order is whatever the test sets.</summary>
public class TimestampStubbedGame : IGame, IGameData<TimestampStubbedGame>, ITimestampOrderedGame
{
    public static GameId GameId { get; } = GameId.From("TimestampStubbedGame");
    public static string DisplayName => "Stubbed Game With File Times";
    public static Optional<Sdk.NexusModsApi.NexusModsGameId> NexusModsGameId => Sdk.NexusModsApi.NexusModsGameId.From(uint.MaxValue - 4);

    public static List<(GamePath Path, DateTimeOffset Mtime)> Order { get; set; } = [];

    public StoreIdentifiers StoreIdentifiers { get; } = new(GameId)
    {
        SteamAppIds = [47u],
        GOGProductIds = [47L],
        EGSCatalogItemId = ["epic-game-id-file-times"],
    };

    public IStreamFactory IconImage { get; } = new EmbeddedResourceStreamFactory<NexusMods.StandardGameLocators.TestHelpers.StubbedGames.StubbedGame>("NexusMods.StandardGameLocators.TestHelpers.Resources.question_mark_game.png");
    public IStreamFactory TileImage => throw new NotImplementedException("No game image for stubbed game.");

    private readonly IServiceProvider _serviceProvider;
    public TimestampStubbedGame(IServiceProvider provider) => _serviceProvider = provider;

    public GamePath GetPrimaryFile(GameInstallation installation) => new(LocationId.Game, "");

    public ILoadoutSynchronizer Synchronizer => new DefaultSynchronizer(_serviceProvider);

    public ImmutableDictionary<LocationId, AbsolutePath> GetLocations(IFileSystem fileSystem, GameLocatorResult gameLocatorResult) =>
        new Dictionary<LocationId, AbsolutePath>
        {
            { LocationId.Game, gameLocatorResult.Path.Combine("game") },
        }.ToImmutableDictionary();

    public ILibraryItemInstaller[] LibraryItemInstallers => [];
    public IDiagnosticEmitter[] DiagnosticEmitters => [];
    public ISortOrderManager SortOrderManager => new SortOrderManager(_serviceProvider);

    public Task<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>> PluginTimestamps(Loadout.ReadOnly loadout, IReadOnlyDictionary<GamePath, SyncNode> syncTree) =>
        Task.FromResult<IReadOnlyList<(GamePath Path, DateTimeOffset Mtime)>>(Order);
}

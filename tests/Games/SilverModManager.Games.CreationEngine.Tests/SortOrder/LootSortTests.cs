using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SilverModManager.Abstractions.Games;
using SilverModManager.Abstractions.Games.FileHashes;
using SilverModManager.Abstractions.Games.FileHashes.Models;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Games.Bethesda.Loot.Masterlists;
using SilverModManager.Games.CreationEngine.Loot;
using SilverModManager.Games.CreationEngine.SortOrder;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using NexusMods.Paths;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;
using SilverModManager.Games.CreationEngine.Tests.SkyrimSE;
using SilverModManager.MnemonicDB.Abstractions.TxFunctions;

namespace SilverModManager.Games.CreationEngine.Tests.SortOrder;

/// <summary>
/// Sorting a Skyrim SE loadout's plugins with the LOOT port: what the masterlist, the userlist and a refresh do to
/// the stored order, and what the outcome reports.
/// </summary>
public class LootSortTests(ITestOutputHelper helper)
    : AIsolatedGameTest<LootSortTests, CreationEngine.SkyrimSE.SkyrimSE>(helper)
{
    private const string DataFolder = "Data";

    // The prelude defines the default group every plugin belongs to, so a test list only needs its own entries.
    private static readonly string Prelude = new BundledMasterlistStore().PreludeText();

    private const string BAfterA = """
        plugins:
          - name: 'A.esp'
            after: [ 'B.esp' ]
        """;

    private const string Cycle = """
        plugins:
          - name: 'A.esp'
            after: [ 'B.esp' ]
          - name: 'B.esp'
            after: [ 'A.esp' ]
        """;

    /// <summary>Serves the masterlist a test sets, so no test reads the bundled lists or the user's cache.</summary>
    private sealed class FixtureMasterlistStore : IMasterlistStore
    {
        public string Masterlist { get; set; } = "plugins: []\n";
        public string Revision { get; set; } = "fixture 1";
        public LootGame? LastGame { get; private set; }

        public MasterlistContent Current(LootGame game)
        {
            LastGame = game;
            return new MasterlistContent(Masterlist, Prelude, Revision);
        }
    }

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.SkyrimSE.SkyrimSE>(new Version("1.6.1170"), locationsFileSystem: IsolatedKnownFolders.Create)
        .AddSingleton<FixtureMasterlistStore>()
        .AddSingleton<IMasterlistStore>(s => s.GetRequiredService<FixtureMasterlistStore>())
        // Without this the variety would look for a userlist in the user's real data folder.
        .AddSingleton(s => new LootDataDirectory(s.GetRequiredService<TemporaryFileManager>().CreateFolder().Path));

    private FixtureMasterlistStore Store => ServiceProvider.GetRequiredService<FixtureMasterlistStore>();

    private IDb Db => Connection.Db;

    private static SortItemKey<string> Key(string name) => new(name.ToLowerInvariant());

    private async Task SeedSkyrimEsm(Loadout.ReadOnly loadout)
    {
        var local = ((StubbedFileHasherService)ServiceProvider.GetRequiredService<IFileHashesService>()).Local.Connection;
        var install = loadout.Installation;

        var bytes = SkyrimPluginFile.Bytes("Skyrim.esm");
        var hashes = await MultiHasher.HashStream(new MemoryStream(bytes));
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("Skyrim.esm", new MemoryStream(bytes)), FileIdentity.XxHash3(hashes.XxHash3), Size.FromLong(bytes.Length))]);

        using var tx = local.BeginTransaction();
        var relation = new HashRelation.New(tx)
        {
            MinimalHash = hashes.MinimalHash,
            XxHash3 = hashes.XxHash3,
            Sha1 = hashes.Sha1,
            XxHash64 = hashes.XxHash64,
            Md5 = hashes.Md5,
            Size = hashes.Size,
            Crc32 = hashes.Crc32,
        };
        var path = new PathHashRelation.New(tx)
        {
            Path = RelativePath.FromUnsanitizedInput($"{DataFolder}/Skyrim.esm"),
            HashId = relation,
        };
        _ = new LocalBaseline.New(tx)
        {
            Store = install.Store,
            GamePath = install.Path,
            // A baseline counts only for the locator ids it was built for.
            LocatorIds = loadout.LocatorIds.Select(id => id.Value).ToArray(),
            Kind = LocalBaselineKind.Snapshot,
            VersionName = "",
            BuiltAt = DateTimeOffset.UnixEpoch,
            FilesIds = [path.Id],
        };
        await tx.Commit();
    }

    private async Task AddPlugins(Loadout.ReadOnly loadout, params string[] plugins)
    {
        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "Mods");
        foreach (var plugin in plugins)
            _ = AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"{DataFolder}/{plugin}"));
        await tx.Commit();
    }

    /// <summary>A loadout with Skyrim.esm, then A, B and C in that order, all switched on except where a test says otherwise.</summary>
    private async Task<(PluginSortOrderVariety Variety, SortOrderId Id)> Loadout_SkyrimThenABC()
    {
        // Created before the plugins are stored: creating a loadout runs the garbage collector.
        var loadout = await CreateLoadout();
        await SeedSkyrimEsm(loadout);
        await AddPlugins(loadout, "A.esp", "B.esp", "C.esp");
        var variety = new PluginSortOrderVariety(ServiceProvider, Game);
        var id = await variety.GetOrCreateSortOrderFor(loadout.LoadoutId, loadout.LoadoutId);
        await variety.ReconcileSortOrder(id);
        return (variety, id);
    }

    private string[] StoredNames(SortOrderId id) =>
        Connection.Query<string>($"SELECT PluginName FROM plugins.SortOrderItems({Connection}, {id.Value}) ORDER BY SortIndex").ToArray();

    [Fact]
    public async Task SortAsync_PutsAPluginAfterTheOneItsMasterlistEntryNames_AndStoresTheOrder()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        Store.Masterlist = BAfterA;
        StoredNames(id).Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");

        var outcome = await ((ILootSortSource)variety).SortAsync(id);

        outcome.Succeeded.Should().BeTrue();
        outcome.CycleText.Should().BeNull();
        StoredNames(id).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
        Store.LastGame.Should().Be(LootGame.SkyrimSE);
    }

    [Fact]
    public async Task SortAsync_LeavesEverySwitchAsItWas()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        await variety.SetEnabled(id, Key("B.esp"), false);
        Store.Masterlist = BAfterA;

        await ((ILootSortSource)variety).SortAsync(id);

        variety.GetSortOrderItems(id, Db).Where(item => !((ISortItemDetails)item).IsEnabled).Select(item => item.DisplayName)
            .Should().Equal("B.esp");
    }

    [Fact]
    public async Task SortAsync_OnACycle_ChangesNothingAndReturnsTheCycleAsLootWordsIt()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        Store.Masterlist = Cycle;
        var before = StoredNames(id);

        var outcome = await ((ILootSortSource)variety).SortAsync(id);

        outcome.Succeeded.Should().BeFalse();
        outcome.CycleText.Should().Contain("A.esp").And.Contain("B.esp").And.Contain("--[");
        StoredNames(id).Should().Equal(before);
    }

    private const string LockedFile = "Locked.esp";

    /// <summary>A file in the game's Data folder that cannot be opened, so a condition that reads it cannot be decided.</summary>
    private AbsolutePath LockFile()
    {
        var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, $"{DataFolder}/{LockedFile}"));
        path.Parent.CreateDirectory();
        File.WriteAllText(path.ToString(), "locked");
        File.SetUnixFileMode(path.ToString(), UnixFileMode.None);
        return path;
    }

    private static void Unlock(AbsolutePath path) => File.SetUnixFileMode(path.ToString(), UnixFileMode.UserRead | UnixFileMode.UserWrite);

    private static void SkipWhenPermissionsDoNotApply() =>
        Assert.SkipWhen(!OperatingSystem.IsLinux() || Environment.UserName == "root", "Needs permission bits that apply to this user");

    [Fact]
    public async Task SortAsync_ReportsThatOverlapEdgesWereSkippedAndHowManyConditionsWereNotEvaluated()
    {
        SkipWhenPermissionsDoNotApply();
        var (variety, id) = await Loadout_SkyrimThenABC();
        var locked = LockFile();
        try
        {
            Store.Masterlist = $"""
                plugins:
                  - name: 'C.esp'
                    after:
                      - name: 'A.esp'
                        condition: 'checksum("{LockedFile}", 00000001)'
                    tag:
                      - name: 'Relev'
                        condition: 'checksum("{LockedFile}", 00000002)'
                """;
            Store.Revision = "fixture 7";

            var outcome = await ((ILootSortSource)variety).SortAsync(id);

            outcome.Succeeded.Should().BeTrue();
            outcome.OverlapEdgesSkipped.Should().BeTrue();
            outcome.UnevaluatedConditions.Should().Be(2);
            outcome.MasterlistRevision.Should().Be("fixture 7");
        }
        finally
        {
            Unlock(locked);
        }
    }

    [Fact]
    public async Task SortAsync_OnACycle_StillReportsTheDivergencesAndTheRevision()
    {
        SkipWhenPermissionsDoNotApply();
        var (variety, id) = await Loadout_SkyrimThenABC();
        var locked = LockFile();
        try
        {
            Store.Masterlist = Cycle + $"""

                  - name: 'C.esp'
                    after:
                      - name: 'A.esp'
                        condition: 'checksum("{LockedFile}", 00000001)'
                """;
            Store.Revision = "fixture 9";

            var outcome = await ((ILootSortSource)variety).SortAsync(id);

            outcome.Succeeded.Should().BeFalse();
            outcome.OverlapEdgesSkipped.Should().BeTrue();
            outcome.UnevaluatedConditions.Should().Be(1);
            outcome.MasterlistRevision.Should().Be("fixture 9");
        }
        finally
        {
            Unlock(locked);
        }
    }

    /// <summary>Writes a file into the game's Data folder. Done after the loadout exists, so the loadout does not take it in.</summary>
    private async Task PutInDataFolder(string name, byte[] bytes)
    {
        var path = GameInstallation.Locations.ToAbsolutePath(new GamePath(LocationId.Game, $"{DataFolder}/{name}"));
        path.Parent.CreateDirectory();
        await path.WriteAllBytesAsync(bytes);
    }

    private const string BAfterAWhen = """
        plugins:
          - name: 'A.esp'
            after:
              - name: 'B.esp'
                condition: '{0}'
        """;

    private async Task<(string[] Order, int Unevaluated)> SortWith(string condition, Func<PluginSortOrderVariety, SortOrderId, Task>? arrange = null)
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        if (arrange is not null) await arrange(variety, id);
        // A.esp loads first today, so B.esp ahead of it shows the rule was kept.
        Store.Masterlist = string.Format(BAfterAWhen, condition);

        var outcome = await ((ILootSortSource)variety).SortAsync(id);
        outcome.Succeeded.Should().BeTrue();
        return (StoredNames(id), outcome.UnevaluatedConditions);
    }

    [Fact]
    public async Task AConditionalLoadAfter_IsKeptWhenTheFileIsInTheGamesDataFolder()
    {
        var (order, unevaluated) = await SortWith("file(\"marker.TXT\")", (_, _) => PutInDataFolder("Marker.txt", "here"u8.ToArray()));

        order.Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
        unevaluated.Should().Be(0);
    }

    [Fact]
    public async Task AConditionalLoadAfter_IsDroppedWhenTheFileIsNotThere()
    {
        var (order, unevaluated) = await SortWith("file(\"Marker.txt\")");

        order.Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
        unevaluated.Should().Be(0);
    }

    [Fact]
    public async Task AnActiveCondition_FollowsThePluginsSwitch()
    {
        var (withSwitchOn, _) = await SortWith("active(\"B.esp\")");
        withSwitchOn.Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task AnActiveCondition_IsFalseWhenThePluginIsSwitchedOff()
    {
        var (order, unevaluated) = await SortWith("active(\"B.esp\")",
            async (variety, id) => await variety.SetEnabled(id, Key("B.esp"), false));

        order.Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
        unevaluated.Should().Be(0);
    }

    [Fact]
    public async Task AnActiveCondition_CountsTheGamesEarlyLoadingPlugins()
    {
        // Dawnguard.esm is not in this loadout, but the game always loads it.
        var (order, _) = await SortWith("active(\"Dawnguard.esm\")");

        order.Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task AnIsMasterCondition_ReadsTheMasterFlagOfALoadoutPlugin()
    {
        var (masterKept, _) = await SortWith("is_master(\"Skyrim.esm\")");
        masterKept.Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task AnIsMasterCondition_ReadsTheHeaderOfAPluginOnlyTheDataFolderHas()
    {
        var (order, _) = await SortWith("is_master(\"other.esm\")", (_, _) => PutInDataFolder("Other.esm", SkyrimPluginFile.Bytes("Other.esm")));

        order.Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task AnIsMasterCondition_IsFalseForADataFolderFileThatIsNotAPlugin()
    {
        var (order, unevaluated) = await SortWith("is_master(\"Junk.esm\")", (_, _) => PutInDataFolder("Junk.esm", [1, 2, 3]));

        order.Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
        unevaluated.Should().Be(0);
    }

    [Fact]
    public async Task AnIsMasterCondition_IsFalseForANonMaster()
    {
        var (order, _) = await SortWith("is_master(\"C.esp\")");

        order.Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
    }

    [Fact]
    public async Task SortAsync_ReadsTheUserlistFromTheRegisteredLootDataFolder()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var userlist = ServiceProvider.GetRequiredService<LootDataDirectory>().Path / "loot/skyrimse/userlist.yaml";
        userlist.Parent.CreateDirectory();
        await userlist.WriteAllTextAsync("plugins:\n  - name: 'A.esp'\n    after: [ 'C.esp' ]\n");

        var outcome = await ((ILootSortSource)variety).SortAsync(id);

        outcome.Succeeded.Should().BeTrue();
        StoredNames(id).Should().Equal("Skyrim.esm", "C.esp", "A.esp", "B.esp");
    }

    private LootSortService ServiceWith(PluginSortOrderVariety variety, AbsolutePath dataDirectory, Func<LootGame, CancellationToken, Task>? refresh = null, TimeSpan? timeout = null) =>
        new(Game, variety, Connection, Store, dataDirectory, refresh, NullLogger.Instance, timeout);

    [Fact]
    public async Task TheUserlistInTheDataFolder_AddsItsRulesToTheMasterlists()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;
        var userlist = dataDirectory / "loot/skyrimse/userlist.yaml";
        userlist.Parent.CreateDirectory();
        await userlist.WriteAllTextAsync("plugins:\n  - name: 'A.esp'\n    after: [ 'C.esp' ]\n");

        var outcome = await ServiceWith(variety, dataDirectory).SortAsync(id, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        // C now loads before A, and the tie-break for the current order cannot put B ahead of C without a cycle.
        StoredNames(id).Should().Equal("Skyrim.esm", "C.esp", "A.esp", "B.esp");
    }

    [Fact]
    public async Task WithoutAUserlist_TheMasterlistAloneDecides()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;

        var outcome = await ServiceWith(variety, dataDirectory).SortAsync(id, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        StoredNames(id).Should().Equal("Skyrim.esm", "A.esp", "B.esp", "C.esp");
    }

    [Fact]
    public async Task ARefreshRunsBeforeTheMasterlistIsRead()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;
        LootGame? refreshed = null;

        var outcome = await ServiceWith(variety, dataDirectory, (game, _) =>
        {
            refreshed = game;
            Store.Masterlist = BAfterA;
            Store.Revision = "refreshed";
            return Task.CompletedTask;
        }).SortAsync(id, CancellationToken.None);

        refreshed.Should().Be(LootGame.SkyrimSE);
        outcome.MasterlistRevision.Should().Be("refreshed");
        StoredNames(id).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task ARefreshThatTakesTooLong_IsAbandonedAndTheCurrentCopyIsUsed()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;
        Store.Masterlist = BAfterA;

        var outcome = await ServiceWith(variety, dataDirectory, (_, token) => Task.Delay(Timeout.Infinite, token), TimeSpan.FromMilliseconds(50))
            .SortAsync(id, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        StoredNames(id).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task ARefreshThatFails_FallsBackToTheCurrentCopy()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;
        Store.Masterlist = BAfterA;

        var outcome = await ServiceWith(variety, dataDirectory, (_, _) => throw new HttpRequestException("offline"))
            .SortAsync(id, CancellationToken.None);

        outcome.Succeeded.Should().BeTrue();
        StoredNames(id).Should().Equal("Skyrim.esm", "B.esp", "A.esp", "C.esp");
    }

    [Fact]
    public async Task ACancelledSort_IsNotMistakenForARefreshTimeout()
    {
        var (variety, id) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;
        using var cancel = new CancellationTokenSource();

        var sort = ServiceWith(variety, dataDirectory, async (_, token) =>
        {
            await cancel.CancelAsync();
            await Task.Delay(Timeout.Infinite, token);
        }).SortAsync(id, cancel.Token);

        await sort.Awaiting(s => s).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task ASortOrderThatNoLongerExists_IsAnError()
    {
        var (variety, _) = await Loadout_SkyrimThenABC();
        var dataDirectory = TemporaryFileManager.CreateFolder().Path;

        var sort = () => ServiceWith(variety, dataDirectory).SortAsync(SortOrderId.From(1UL), CancellationToken.None);

        await sort.Should().ThrowAsync<InvalidOperationException>();
    }
}

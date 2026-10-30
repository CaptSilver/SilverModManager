using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.DataModel.Diagnostics;
using NexusMods.Hashing.xxHash3;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Games;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Paths;
using SilverModManager.Sdk.Hashes;
using SilverModManager.Games.TestFramework;
using SilverModManager.Games.Witcher.Witcher3;
using SilverModManager.Games.Witcher.Witcher3.Models;
using SilverModManager.Games.Witcher.Witcher3.SortOrder;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.Witcher.Tests.Witcher3;

public class Witcher3SynchronizerTests(ITestOutputHelper helper)
    : AIsolatedGameTest<Witcher3SynchronizerTests, Witcher3Game>(helper)
{
    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddWitcherGames()
        .AddUniversalGameLocator<Witcher3Game>(new Version("4.0"), locationsFileSystem: IsolatedKnownFolders.Create);

    [Fact]
    public async Task AnApplyBuildsTheLoadOrder_SoAReplacedModsSettingsGetsItsNotice()
    {
        var loadout = await CreateLoadout();
        await Synchronizer.Synchronize(Refresh(loadout));

        // The Load Order of a loadout nobody has reconciled yet does not exist; Vortex wrote the file since the last apply.
        var variety = Game.SortOrderManager.GetSortOrderVarieties().OfType<WitcherModSortOrderVariety>().Single();
        await variety.DeleteSortOrder(variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).Value);
        variety.GetSortOrderIdFor(loadout.LoadoutId, Connection.Db).HasValue.Should().BeFalse();
        await GameInstallation.Locations.ToAbsolutePath(WitcherConstants.W3ModsSettings)
            .WriteAllBytesAsync(new UTF8Encoding(false).GetBytes("[mod0000_MergedFiles]\nPriority=1\n[modFromVortex]\nPriority=2\nVK=1\n"));

        await Synchronizer.Synchronize(Refresh(loadout));

        WitcherModsSettingsNotice.All(Connection.Db).Should().ContainSingle();
    }

    /// <summary>What the game's synchronizer did before targets were repaired: same files, no pre-step.</summary>
    private sealed class UnrepairedSynchronizer(IServiceProvider provider, IGame game, ALoadoutSynchronizer real) : WitcherSynchronizer(provider, game)
    {
        public override Dictionary<GamePath, IIntrinsicFile> IntrinsicFiles(Loadout.ReadOnly loadout) => real.IntrinsicFiles(loadout);
    }

    private async Task AddModFile(Loadout.ReadOnly loadout, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = bytes.xxHash3();
        if (!await FileStore.HaveFile(FileIdentity.XxHash3(hash)))
            await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(path, new MemoryStream(bytes)), FileIdentity.XxHash3(hash), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, "mod " + path);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, path), content);
        await tx.Commit();
    }

    private string[] GameFolderNames() =>
        GameInstallation.Locations[LocationId.Game].Path.EnumerateDirectories(recursive: false).Select(d => d.FileName).ToArray();

    [Fact]
    public async Task AnApplyMovesAFileDeployedUnderALowercaseModsFolderIntoMods_AndRemovesTheEmptiedFolder()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modX/content/a.ws", "class A {}");

        // What an earlier build did: the file is deployed under the archive's spelling.
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        GameInstallation.Locations[LocationId.Game].Path.Combine("mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().Contain("mods");

        await Synchronizer.Synchronize(Refresh(loadout));

        GameInstallation.Locations[LocationId.Game].Path.Combine("Mods/modX/content/a.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().Contain("Mods").And.NotContain("mods");
    }

    [Fact]
    public async Task AnApplyRewritesTargetsInAnyCaseOfModsDlcAndBin_ToTheGamesSpelling()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "MODS/modX/content/a.ws", "a");
        await AddModFile(loadout, "Dlc/dlcY/content/b.bundle", "b");
        await AddModFile(loadout, "BIN/x64/c.dll", "c");
        await AddModFile(loadout, "Mods/modZ/content/d.ws", "d");

        await Synchronizer.Synchronize(Refresh(loadout));

        var targets = LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(f => ((GamePath)f.AsLoadoutItemWithTargetPath().TargetPath).ToString()).Order(StringComparer.Ordinal).ToArray();
        targets.Should().Equal("{Game}/Mods/modX/content/a.ws", "{Game}/Mods/modZ/content/d.ws", "{Game}/bin/x64/c.dll", "{Game}/dlc/dlcY/content/b.bundle");
    }

    [Fact]
    public async Task AnApplyMergesLowercaseModsBesideMods_AndKeepsEveryModsFile()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modMenu/content/a.ws", "menu");
        await AddModFile(loadout, "Mods/modSharedutils/content/b.ws", "base");
        await AddModFile(loadout, "mods/modSharedutils/content/c.ws", "more");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        GameFolderNames().Should().Contain("mods").And.Contain("Mods");

        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("Mods/modMenu/content/a.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modSharedutils/content/b.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modSharedutils/content/c.ws").FileExists.Should().BeTrue();
        GameFolderNames().Should().NotContain("mods");
    }

    [Fact]
    public async Task ARepairedFileCanStillBeRemovedByTheNextApply()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modX/content/a.ws", "class A {}");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        await Synchronizer.Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in LoadoutItemGroup.All(Connection.Db).Where(g => g.AsLoadoutItem().Name.StartsWith("mod mods/", StringComparison.Ordinal)))
                tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("Mods/modX/content/a.ws").FileExists.Should().BeFalse();
        GameFolderNames().Should().NotContain("mods");
    }

    [Fact]
    public async Task AModDisabledBeforeTheRepairingApplyIsRemovedByThatApply()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "mods/modX/content/a.ws", "class A {}");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));

        using (var tx = Connection.BeginTransaction())
        {
            foreach (var group in LoadoutItemGroup.All(Connection.Db).Where(g => g.AsLoadoutItem().Name.StartsWith("mod mods/", StringComparison.Ordinal)))
                tx.Add(group.Id, LoadoutItem.Disabled, Null.Instance);
            await tx.Commit();
        }
        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        game.Combine("Mods/modX/content/a.ws").FileExists.Should().BeFalse();
        game.Combine("mods/modX/content/a.ws").FileExists.Should().BeFalse();
    }

    private async Task RecordDiskState(Loadout.ReadOnly loadout, string path, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        using var tx = Connection.BeginTransaction();
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = new GamePath(LocationId.Game, path).ToGamePathParentTuple(loadout.Installation),
            Identity = FileIdentity.XxHash3(bytes.xxHash3()),
            XxHash3 = bytes.xxHash3(),
            Size = Size.FromLong(bytes.Length),
            LastModified = DateTime.UtcNow,
            GameId = loadout.Installation,
        };
        await tx.Commit();
    }

    [Fact]
    public async Task RepairRespellsDiskStateRecordedAfterTheLoadoutSnapshotWasTaken()
    {
        var loadout = await CreateLoadout();
        var snapshot = Refresh(loadout);

        // What the reindex at the start of an apply does: records a file after the caller took its snapshot.
        await RecordDiskState(loadout, "mods/modMenu/content/a.ws", "menu");
        await Witcher3FolderCase.Repair(Connection, snapshot);

        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id)
            .Select(e => ((GamePath)e.Path).ToString()).Where(p => p.StartsWith("{Game}/", StringComparison.Ordinal))
            .Should().Equal("{Game}/Mods/modMenu/content/a.ws");
    }

    [Fact]
    public async Task AnApplyOfAMenuModsLoadoutDeployedUnderLowercaseModsBesideMods_LeavesEverythingUnderModsOnly()
    {
        var loadout = await CreateLoadout();
        for (var i = 0; i < 14; i++) await AddModFile(loadout, $"mods/modMenu{i}/content/a.ws", $"menu {i}");
        await AddModFile(loadout, "Mods/modBase/content/b.ws", "base");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        GameFolderNames().Should().Contain("mods").And.Contain("Mods");

        await Synchronizer.Synchronize(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        GameFolderNames().Should().Contain("Mods").And.NotContain("mods");
        for (var i = 0; i < 14; i++) game.Combine($"Mods/modMenu{i}/content/a.ws").FileExists.Should().BeTrue();
        game.Combine("Mods/modBase/content/b.ws").FileExists.Should().BeTrue();
        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id)
            .Should().OnlyContain(e => !((GamePath)e.Path).ToString().StartsWith("{Game}/mods/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASwitchToALoadoutOfMenuModsDeployedUnderLowercaseModsBesideMods_LeavesEverythingUnderModsOnly()
    {
        var loadout = await CreateLoadout();
        for (var i = 0; i < 14; i++) await AddModFile(loadout, $"mods/modMenu{i}/content/a.ws", $"menu {i}");
        await AddModFile(loadout, "Mods/modBase/content/b.ws", "base");
        await new UnrepairedSynchronizer(ServiceProvider, Game, (ALoadoutSynchronizer)Synchronizer).Synchronize(Refresh(loadout));
        var other = await CreateLoadout();

        await Synchronizer.Synchronize(Refresh(other));
        await LoadoutManager.DeactivateCurrentLoadout(GameInstallation);
        await LoadoutManager.ActivateLoadout(Refresh(loadout));

        var game = GameInstallation.Locations[LocationId.Game].Path;
        GameFolderNames().Should().Contain("Mods").And.NotContain("mods");
        for (var i = 0; i < 14; i++) game.Combine($"Mods/modMenu{i}/content/a.ws").FileExists.Should().BeTrue();
        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation.Id)
            .Should().OnlyContain(e => !((GamePath)e.Path).ToString().StartsWith("{Game}/mods/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALoadoutSwitchBacksUpAFileRecordedAfterTheCallersSnapshotBeforeReplacingIt()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "Mods/modOld/content/old.ws", "new");
        var staleState = GameInstallMetadata.Load(Connection.Db, loadout.Installation.Id);
        var old = GameInstallation.Locations[LocationId.Game].Path.Combine("mods/modOld/content/old.ws");
        old.Parent.CreateDirectory();
        await old.WriteAllTextAsync("old");
        await RecordDiskState(loadout, "mods/modOld/content/old.ws", "old");

        await ((ALoadoutSynchronizer)Synchronizer).BuildProcessRun(Refresh(loadout), staleState, CancellationToken.None);

        // The row recorded after the snapshot is what makes the switch back the old content up before it replaces it.
        var game = GameInstallation.Locations[LocationId.Game].Path;
        (await game.Combine("Mods/modOld/content/old.ws").ReadAllTextAsync()).Should().Be("new");
        game.Combine("mods").DirectoryExists().Should().BeFalse();
        (await FileStore.HaveFile(FileIdentity.XxHash3(Encoding.UTF8.GetBytes("old").xxHash3()))).Should().BeTrue();
    }

    // The files stay on the host's file system; only the located game becomes a Windows build with a prefix.
    private void RunUnderWine()
    {
        var locator = ServiceProvider.GetServices<IGameLocator>().OfType<UniversalStubbedGameLocator<Witcher3Game>>().Single();
        locator.Platform = OSPlatform.Windows;
        locator.WinePrefixPath = TemporaryFileManager.CreateFolder().Path.Combine("pfx");
        GameRegistry.ClearCache();
        GameInstallation = GameRegistry.LocateGameInstallations().Single(i => i.Game is Witcher3Game);
    }

    [Fact]
    public async Task AnApplyLeavesAFileWhoseTwinExistsInModsAndTheCardTellsTheUserToMoveItByHand()
    {
        var loadout = await CreateLoadout();
        await AddModFile(loadout, "Mods/modX/content/a.ws", "from the mod");
        loadout = Refresh(loadout);
        await Synchronizer.Synchronize(loadout);
        var twin = GameInstallation.Locations[LocationId.Game].Path.Combine("mods/modX/content/a.ws");
        twin.Parent.CreateDirectory();
        await twin.WriteAllTextAsync("left behind");

        await Synchronizer.Synchronize(Refresh(loadout));

        twin.FileExists.Should().BeTrue();
        RunUnderWine();
        var tree = await Synchronizer.BuildSyncTree(Refresh(loadout));
        Synchronizer.ProcessSyncTree(tree);
        var cards = new List<Diagnostic>();
        await foreach (var card in new CaseVariantFolderEmitter().Diagnose(Refresh(loadout), tree.ToFrozenDictionary(), CancellationToken.None))
            cards.Add(card);
        var details = cards.Should().ContainSingle().Subject.FormatDetails(new PlainDiagnosticWriter());
        details.Should().Contain("{Game}/mods").And.Contain("{Game}/Mods").And.Contain("Move the files of one folder").And.Contain("`Mods`, `dlc`, `bin`");
    }
}

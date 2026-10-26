using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Files.Diff;
using SilverModManager.Abstractions.Loadouts.Extensions;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.MnemonicDB.Abstractions.ElementComparers;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.Sdk.Trees;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// INI tweak files are inputs to the INI bake. They stay in the loadout, but the game ignores an
/// <c>INI Tweaks</c> folder, so none of them may be written into the game folder.
/// </summary>
public class IniTweaksNotDeployedTests(ITestOutputHelper helper) : AIsolatedGameTest<IniTweaksNotDeployedTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout4.ini");
    private const string TweakText = "[Display]\nfMaxTime=0.1\n";

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private AbsolutePath OnDisk(GamePath path) => GameInstallation.Locations.ToAbsolutePath(path);

    private async Task WriteUserIni()
    {
        OnDisk(MainIni).Parent.CreateDirectory();
        await OnDisk(MainIni).WriteAllBytesAsync("[General]\r\nsLanguage=EN\r\n"u8.ToArray());
    }

    private async Task AddTweak(Loadout.ReadOnly loadout, string group, string folder, string file, bool settingsGroup = false)
    {
        var bytes = Encoding.UTF8.GetBytes(TweakText);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory(file, new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var id = AddEmptyGroup(tx, loadout.LoadoutId, group);
        if (settingsGroup) tx.Add(id.Value, LoadoutItemGroup.GameSettingsGroup, Null.Instance);
        AddFile(tx, loadout.LoadoutId, id, new GamePath(LocationId.Game, $"{folder}/{file}"), TweakText);
        if (!settingsGroup)
        {
            _ = new LoadoutItemGroupPriority.New(tx)
            {
                LoadoutId = loadout.LoadoutId,
                TargetId = id,
                Priority = ConflictPriority.From(10),
            };
        }
        await tx.Commit();
    }

    private IEnumerable<GamePath> LoadoutFilePaths(Loadout.ReadOnly loadout) =>
        LoadoutItem.FindByLoadout(Connection.Db, loadout.LoadoutId).OfTypeLoadoutItemWithTargetPath().OfTypeLoadoutFile()
            .Select(f => (GamePath)f.AsLoadoutItemWithTargetPath().TargetPath);

    private async Task OwnInis(Loadout.ReadOnly loadout, bool value)
    {
        using var tx = Connection.BeginTransaction();
        tx.Add(loadout.Id, Loadout.OwnsGameInis, value);
        await tx.Commit();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATweakFileIsNeverWrittenToTheGameFolder(bool ownsInis)
    {
        var loadout = await CreateLoadout();
        await OwnInis(loadout, ownsInis);
        await WriteUserIni();
        await AddTweak(loadout, "Some mod", "INI Tweaks", "Some mod [Fallout4.ini].ini");
        await AddTweak(loadout, "Game settings", "INI Tweaks", "Loadout [Fallout4.ini].ini", settingsGroup: true);
        await AddTweak(loadout, "Odd case", "ini tweaks", "Odd case [Fallout4.ini].ini");

        await Synchronizer.Synchronize(Refresh(loadout));

        OnDisk(new GamePath(LocationId.Game, "INI Tweaks")).DirectoryExists().Should().BeFalse();
        OnDisk(new GamePath(LocationId.Game, "ini tweaks")).DirectoryExists().Should().BeFalse();
        LoadoutFilePaths(Refresh(loadout)).Should().Contain(p => p.Path.ToString().EndsWith("Some mod [Fallout4.ini].ini"))
            .And.Contain(p => p.Path.ToString().EndsWith("Loadout [Fallout4.ini].ini"), "the loadout keeps its tweaks for the bake and the INI page");
    }

    [Fact]
    public async Task TheBakedIniStillCarriesTheTweak()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();
        await AddTweak(loadout, "Some mod", "INI Tweaks", "Some mod [Fallout4.ini].ini");

        await Synchronizer.Synchronize(Refresh(loadout));

        IniDocument.Parse(await OnDisk(MainIni).ReadAllBytesAsync()).Get("Display", "fMaxTime").Should().Be("0.1");
        OnDisk(new GamePath(LocationId.Game, "INI Tweaks")).DirectoryExists().Should().BeFalse();
    }

    private static readonly GamePath ModTweak = new(LocationId.Game, "INI Tweaks/Some mod [Fallout4.ini].ini");

    /// <summary>
    /// What an apply that still deployed the folder left behind: <paramref name="onDisk"/> in the game folder, and
    /// <paramref name="deployed"/> recorded as what that apply wrote, or nothing recorded when it is null.
    /// </summary>
    private async Task SeedEarlierDeployment(Loadout.ReadOnly loadout, byte[] onDisk, byte[]? deployed)
    {
        OnDisk(ModTweak).Parent.CreateDirectory();
        await OnDisk(ModTweak).WriteAllBytesAsync(onDisk);
        if (deployed is null) return;

        using var tx = Connection.BeginTransaction();
        _ = new DiskStateEntry.New(tx, tx.TempId(DiskStateEntry.EntryPartition))
        {
            Path = ModTweak.ToGamePathParentTuple(loadout.Installation),
            Hash = deployed.xxHash3(),
            Size = Size.FromLong(deployed.Length),
            LastModified = DateTime.UtcNow,
            GameId = loadout.Installation,
        };
        // The apply records its transaction as the last applied state, which is what "previous" reads from.
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadout, loadout.Id);
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastSyncedLoadoutTransaction, EntityId.From(tx.ThisTxId.Value));
        tx.Add(loadout.Installation.Id, GameInstallMetadata.LastScannedDiskStateTransaction, EntityId.From(tx.ThisTxId.Value));
        await tx.Commit();
    }

    // Two applies, because a file left on disk and untracked after the first is indexed again and recorded as
    // deployed by it, which is when the second would delete it.
    private async Task ApplyTwiceAndExpectNoTweakOnDisk(Loadout.ReadOnly loadout)
    {
        for (var i = 0; i < 2; i++)
        {
            await Synchronizer.Synchronize(Refresh(loadout));
            OnDisk(ModTweak).FileExists.Should().BeFalse("apply {0} leaves no tweak in the game folder", i + 1);
        }

        DiskStateEntry.FindByGame(Connection.Db, loadout.Installation).Select(e => (GamePath)e.Path).Should().NotContain(ModTweak);

        var metadata = Refresh(loadout).Installation;
        var diff = Synchronizer.LoadoutToDiskDiff(Refresh(loadout), Synchronizer.GetDiskStateForGame(metadata), Synchronizer.GetLastScannedDiskState(metadata));
        diff.GetAllDescendentFiles().Single(f => f.GamePath() == ModTweak).Item.Value.ChangeType
            .Should().Be(FileChangeType.None, "the tweak file is not a pending deploy");
    }

    [Fact]
    public async Task ATweakFileDeployedByAnEarlierApplyIsRemovedAndNotReportedAsChanged()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();
        await AddTweak(loadout, "Some mod", "INI Tweaks", "Some mod [Fallout4.ini].ini");
        var bytes = Encoding.UTF8.GetBytes(TweakText);
        await SeedEarlierDeployment(loadout, onDisk: bytes, deployed: bytes);

        await ApplyTwiceAndExpectNoTweakOnDisk(loadout);
    }

    [Fact]
    public async Task ATweakFileThePlayerEditedAfterItWasDeployedIsRemovedButItsBytesAreKept()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();
        await AddTweak(loadout, "Some mod", "INI Tweaks", "Some mod [Fallout4.ini].ini");
        var edited = Encoding.UTF8.GetBytes("[Display]\nfMaxTime=0.5\n");
        await SeedEarlierDeployment(loadout, onDisk: edited, deployed: Encoding.UTF8.GetBytes(TweakText));

        await ApplyTwiceAndExpectNoTweakOnDisk(loadout);

        (await FileStore.HaveFile(edited.xxHash3())).Should().BeTrue("the player's edit is backed up before the file is removed");
    }

    [Fact]
    public async Task ATweakFileOnDiskWithNoRecordOfBeingDeployedIsRemovedButItsBytesAreKept()
    {
        var loadout = await CreateLoadout();
        await WriteUserIni();
        await AddTweak(loadout, "Some mod", "INI Tweaks", "Some mod [Fallout4.ini].ini");
        var other = Encoding.UTF8.GetBytes("[Display]\nfMaxTime=0.7\n");
        await SeedEarlierDeployment(loadout, onDisk: other, deployed: null);

        await ApplyTwiceAndExpectNoTweakOnDisk(loadout);

        (await FileStore.HaveFile(other.xxHash3())).Should().BeTrue("bytes the store never had are backed up before the file is removed");
    }
}

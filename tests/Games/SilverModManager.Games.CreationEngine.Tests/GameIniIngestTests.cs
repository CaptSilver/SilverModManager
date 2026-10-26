using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NexusMods.Hashing.xxHash3;
using NexusMods.Paths;
using SilverModManager.Abstractions.Diagnostics;
using SilverModManager.Abstractions.Loadouts;
using SilverModManager.Abstractions.Loadouts.Synchronizers;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Conflicts;
using SilverModManager.Abstractions.Loadouts.Synchronizers.Ini;
using SilverModManager.Games.CreationEngine.Emitters;
using SilverModManager.Games.TestFramework;
using SilverModManager.MnemonicDB.Abstractions;
using SilverModManager.Sdk.FileStore;
using SilverModManager.Sdk.Games;
using SilverModManager.Sdk.Ini;
using SilverModManager.Sdk.IO;
using SilverModManager.Sdk.Loadouts;
using SilverModManager.StandardGameLocators.TestHelpers;

namespace SilverModManager.Games.CreationEngine.Tests;

/// <summary>
/// What an apply does with an owned game INI the game rewrote since the last apply: a setting nothing else owns
/// becomes part of the loadout's copy, a setting the app or a mod tweak owns is put back.
/// </summary>
public class GameIniIngestTests(ITestOutputHelper helper) : AIsolatedGameTest<GameIniIngestTests, CreationEngine.Fallout4.Fallout4>(helper)
{
    private static readonly GamePath MainIni = new(LocationId.Preferences, "Fallout4.ini");
    private static readonly byte[] UserIni = "[Display]\r\niSize W=2560 ; wide\r\niSize H=1440\r\n\r\n[General]\r\nsLanguage=EN\r\n"u8.ToArray();

    protected override IServiceCollection AddServices(IServiceCollection services) => base.AddServices(services)
        .AddCreationEngine()
        .AddUniversalGameLocator<CreationEngine.Fallout4.Fallout4>(new Version("1.10.163"), locationsFileSystem: IsolatedKnownFolders.Create);

    private AbsolutePath Live => GameInstallation.Locations.ToAbsolutePath(MainIni);

    private async Task<Loadout.ReadOnly> LoadoutWithBakedIni()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        return await Synchronizer.Synchronize(Refresh(loadout));
    }

    private LoadoutIniState.ReadOnly State(Loadout.ReadOnly loadout) =>
        LoadoutIniState.FindByLoadout(Connection.Db, loadout.LoadoutId).Single(s => (GamePath)s.Path == MainIni);

    private async Task<byte[]> GameEdits(Action<IniDocument> edit)
    {
        var document = IniDocument.Parse(await Live.ReadAllBytesAsync());
        edit(document);
        return document.ToBytes();
    }

    private async Task Ingest(Loadout.ReadOnly loadout, byte[] live)
    {
        loadout = Refresh(loadout);
        var file = GameInis.IntrinsicFiles((IGameIniFiles)Game, loadout, ServiceProvider).Single(f => f.Path == MainIni);
        using var tx = Connection.BeginTransaction();
        await file.Ingest(new MemoryStream(live), loadout, new Dictionary<GamePath, SyncNode>(), tx);
        await tx.Commit();
    }

    private async Task<IniDocument> Rendered(Loadout.ReadOnly loadout)
    {
        loadout = Refresh(loadout);
        var file = GameInis.IntrinsicFiles((IGameIniFiles)Game, loadout, ServiceProvider).Single(f => f.Path == MainIni);
        using var stream = new MemoryStream();
        await file.Write(stream, loadout, new Dictionary<GamePath, SyncNode>());
        return IniDocument.Parse(stream.ToArray());
    }

    private async Task<EntityId> AddTweakMod(Loadout.ReadOnly loadout, string modName, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        await FileStore.BackupFiles([new ArchivedFileEntry(new MemoryStreamFactory("tweak.ini", new MemoryStream(bytes)), bytes.xxHash3(), Size.FromLong(bytes.Length))]);

        using var tx = Connection.BeginTransaction();
        var group = AddEmptyGroup(tx, loadout.LoadoutId, modName);
        AddFile(tx, loadout.LoadoutId, group, new GamePath(LocationId.Game, $"INI Tweaks/{modName} [Fallout4.ini].ini"), content);
        _ = new LoadoutItemGroupPriority.New(tx)
        {
            LoadoutId = loadout.LoadoutId,
            TargetId = group,
            Priority = ConflictPriority.From(10),
        };
        var result = await tx.Commit();
        return result[group];
    }

    [Fact]
    public async Task ASettingTheGameChangedThatNothingOwnsBecomesPartOfTheBaseline()
    {
        var loadout = await LoadoutWithBakedIni();
        var live = await GameEdits(ini => ini.Set("Display", "iSize W", "1920"));

        await Ingest(loadout, live);

        var state = State(Refresh(loadout));
        var baseline = IniDocument.Parse(await FileStore.Load(state.Baseline));
        baseline.Get("Display", "iSize W").Should().Be("1920");
        baseline.ToText().Should().Contain("iSize W=1920 ; wide", "the line keeps its inline comment");
        baseline.Get("Display", "iSize H").Should().Be("1440");
        (await Rendered(loadout)).Get("Display", "iSize W").Should().Be("1920", "the next write carries the adopted setting");
    }

    [Fact]
    public async Task ASettingTheGameAddedIsAdoptedIntoItsSection()
    {
        var loadout = await LoadoutWithBakedIni();
        var live = await GameEdits(ini => ini.Set("Display", "bFull Screen", "0"));

        await Ingest(loadout, live);

        IniDocument.Parse(await FileStore.Load(State(Refresh(loadout)).Baseline)).Get("Display", "bFull Screen").Should().Be("0");
    }

    [Fact]
    public async Task AdoptingASettingRecordsTheNewBakeSoTheNextIngestDiffsAgainstIt()
    {
        var loadout = await LoadoutWithBakedIni();
        await Ingest(loadout, await GameEdits(ini => ini.Set("Display", "iSize W", "1920")));

        var state = State(Refresh(loadout));
        var baked = await FileStore.Load(LoadoutIniState.LastBakedHash.Get(state));
        IniDocument.Parse(baked).Get("Display", "iSize W").Should().Be("1920");
        baked.Should().Equal((await Rendered(loadout)).ToBytes());
    }

    [Fact]
    public async Task ASettingTheAppManagesIsNotAdoptedAndTheNextWriteResetsIt()
    {
        var loadout = await LoadoutWithBakedIni();
        var before = State(Refresh(loadout));
        var live = await GameEdits(ini => ini.Set("Archive", "bInvalidateOlderFiles", "0"));

        await Ingest(loadout, live);

        var after = State(Refresh(loadout));
        after.Baseline.Should().Be(before.Baseline);
        IniDocument.Parse(await FileStore.Load(after.Baseline)).Get("Archive", "bInvalidateOlderFiles").Should().BeNull();
        (await Rendered(loadout)).Get("Archive", "bInvalidateOlderFiles").Should().Be("1");
    }

    [Fact]
    public async Task ASettingAModTweakOwnsIsNotAdopted()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        await AddTweakMod(loadout, "Wide Screen", "[Display]\r\niSize W=3440\r\n");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        var before = State(Refresh(loadout));
        var live = await GameEdits(ini => ini.Set("Display", "iSize W", "1024"));

        await Ingest(loadout, live);

        var after = State(Refresh(loadout));
        after.Baseline.Should().Be(before.Baseline);
        (await Rendered(loadout)).Get("Display", "iSize W").Should().Be("3440", "the tweak still wins");
    }

    // The bake recorded on the row already describes the write about to run when the ingest looks at it, so the
    // tweak's value in the file must be compared with what was deployed, or it would be adopted as the game's. The
    // game has to change something too, or the file is not ingested at all.
    [Fact]
    public async Task ATweakSwitchedOffIsNotAdoptedWhenTheGameChangedAnotherSetting()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        var tweak = await AddTweakMod(loadout, "Wide Screen", "[Display]\r\niSize W=3440\r\n");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("Display", "iSize W").Should().Be("3440");

        await Live.WriteAllBytesAsync(await GameEdits(ini => ini.Set("Display", "iSize H", "1080")));
        await DisableItem(tweak);
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        var baseline = IniDocument.Parse(await FileStore.Load(State(Refresh(loadout)).Baseline));
        baseline.Get("Display", "iSize H").Should().Be("1080");
        baseline.Get("Display", "iSize W").Should().Be("2560", "the tweak's value is not the game's");
        var live = IniDocument.Parse(await Live.ReadAllBytesAsync());
        live.Get("Display", "iSize W").Should().Be("2560", "the player's own value is back once the tweak is off");
        live.Get("Display", "iSize H").Should().Be("1080");
    }

    [Fact]
    public async Task NothingChangesWhenTheLiveFileIsTheLastBake()
    {
        var loadout = await LoadoutWithBakedIni();
        var before = State(Refresh(loadout));

        await Ingest(loadout, await Live.ReadAllBytesAsync());

        var after = State(Refresh(loadout));
        after.Baseline.Should().Be(before.Baseline);
        LoadoutIniState.LastBakedHash.Get(after).Should().Be(LoadoutIniState.LastBakedHash.Get(before));
    }

    [Fact]
    public async Task AnEmptyLiveFileIngestsNothing()
    {
        var loadout = await LoadoutWithBakedIni();
        var before = State(Refresh(loadout));

        await Ingest(loadout, []);

        State(Refresh(loadout)).Baseline.Should().Be(before.Baseline);
    }

    [Fact]
    public async Task ALiveFileThatIsNotOnDiskIngestsNothing()
    {
        var loadout = await LoadoutWithBakedIni();
        var before = State(Refresh(loadout));
        var file = GameInis.IntrinsicFiles((IGameIniFiles)Game, Refresh(loadout), ServiceProvider).Single(f => f.Path == MainIni);
        var tree = new Dictionary<GamePath, SyncNode> { [MainIni] = new() { SourceItemType = LoadoutSourceItemType.Intrinsic } };

        using var tx = Connection.BeginTransaction();
        await file.Ingest(new MemoryStream(), Refresh(loadout), tree, tx);
        await tx.Commit();

        State(Refresh(loadout)).Baseline.Should().Be(before.Baseline);
    }

    private async Task<List<string>> Notices(Loadout.ReadOnly loadout)
    {
        var found = new List<string>();
        await foreach (var diagnostic in new GameIniChangedByGameEmitter((IGameIniFiles)Game, ServiceProvider).Diagnose(Refresh(loadout), CancellationToken.None))
            found.Add(diagnostic.FormatDetails(new PlainDiagnosticWriter()));
        return found;
    }

    [Fact]
    public async Task ANoticeNamesAManagedSettingTheGameChangedAndSaysItIsReset()
    {
        var loadout = await LoadoutWithBakedIni();
        await Live.WriteAllBytesAsync(await GameEdits(ini => ini.Set("Archive", "bInvalidateOlderFiles", "0")));

        var notice = (await Notices(loadout)).Should().ContainSingle().Subject;

        notice.Should().Contain("Fallout4.ini").And.Contain("bInvalidateOlderFiles").And.Contain("it is reset on the next Apply");
    }

    [Fact]
    public async Task ANoticeNamesTheModWhoseTweakOverridesASettingTheGameChanged()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);
        await AddTweakMod(loadout, "Wide Screen", "[Display]\r\niSize W=3440\r\n");
        loadout = await Synchronizer.Synchronize(Refresh(loadout));
        await Live.WriteAllBytesAsync(await GameEdits(ini => ini.Set("Display", "iSize W", "1024")));

        var notice = (await Notices(loadout)).Should().ContainSingle().Subject;

        notice.Should().Contain("iSize W").And.Contain("overridden by Wide Screen");
    }

    [Fact]
    public async Task NoNoticeForASettingTheApplyAdopts()
    {
        var loadout = await LoadoutWithBakedIni();
        await Live.WriteAllBytesAsync(await GameEdits(ini => ini.Set("Display", "iSize W", "1920")));

        (await Notices(loadout)).Should().BeEmpty();
    }

    [Fact]
    public async Task NoNoticeOnceTheApplyHasRun()
    {
        var loadout = await LoadoutWithBakedIni();
        await Live.WriteAllBytesAsync(await GameEdits(ini => ini.Set("Archive", "bInvalidateOlderFiles", "0")));
        loadout = await Synchronizer.Synchronize(Refresh(loadout));

        (await Notices(loadout)).Should().BeEmpty();
        IniDocument.Parse(await Live.ReadAllBytesAsync()).Get("Archive", "bInvalidateOlderFiles").Should().Be("1");
    }

    [Fact]
    public async Task NoNoticeBeforeTheLoadoutHasBeenApplied()
    {
        var loadout = await CreateLoadout();
        Live.Parent.CreateDirectory();
        await Live.WriteAllBytesAsync(UserIni);

        (await Notices(loadout)).Should().BeEmpty();
    }
}
